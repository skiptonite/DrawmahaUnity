using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Drawmaha.Network;

namespace Drawmaha.Server;

public sealed class CryptoRandom : Random
{
    public override int Next(int maxValue) => RandomNumberGenerator.GetInt32(maxValue);
    public override int Next(int minValue, int maxValue) => RandomNumberGenerator.GetInt32(minValue, maxValue);
}
public sealed class Session
{
    public string Token, Name;
    public int Avatar, Seat = -1, DisconnectedHand, LockedDrawHand = -1, AutoCallAmount = -1;
    public int[] LockedDiscards;
    public Peer Peer;
    public Room Room;
    public DateTime LastSeen;
    public bool Leaving, MissedSmall, MissedBig;
    public bool LeaveRequested;
    public bool CheckFold;
    public EntryMode Entry;
    public readonly Queue<string> Commands = new();
    public readonly HashSet<string> Seen = new();
}
public sealed class Room
{
    public readonly string Id, Name;
    public readonly Game Game;
    public readonly Session[] Seats = new Session[6];
    public readonly bool[] ReservedSeats = new bool[6];
    public readonly BlindOrbit Orbit = new();
    public readonly List<HandHistoryView> CompletedHands = new();
    public long Revision;
    public DateTime Deadline, NextHand;
    public bool AnnouncedComplete;
    public int ArchivedHandNumber = -1;
    public int BuyIn => Game.BigBlind * 100;
    public Room(string id, string name, int small, int big)
    {
        Id = id; Name = name; Game = new Game(new CryptoRandom(), 0, 6) { SmallBlind = small, BigBlind = big };
    }
    public LobbyRow Lobby() => new() { id = Id, name = Name, smallBlind = Game.SmallBlind, bigBlind = Game.BigBlind, buyIn = BuyIn, occupied = Seats.Count(s => s != null) + ReservedSeats.Count(reserved => reserved), available = Enumerable.Range(0, Seats.Length).Count(i => Seats[i] == null && !ReservedSeats[i]), maxSeats = 6 };
    public TableState View(Session viewer, DateTime now)
    {
        int own = viewer?.Seat ?? -1;
        int livePlayers = Game.Players.Count(p => p.InHand && !p.Folded);
        return new TableState
        {
            id = Id, name = Name, yourSeat = own, hand = Game.HandNumber, revision = Revision,
            stage = Game.Stage.ToString(), dealer = Game.Dealer, smallBlindSeat = Game.SmallBlindSeat, bigBlindSeat = Game.BigBlindSeat,
            smallBlind = Game.SmallBlind, bigBlind = Game.BigBlind, pot = Game.Pot > 0 ? Game.Pot : Game.LastPot, actor = Game.Actor,
            call = own >= 0 && Game.IsBetting ? Game.ToCall(own) : 0,
            minRaise = Game.MinimumRaiseTo, maxRaise = own >= 0 && Game.IsBetting ? Game.MaximumRaiseTo(own) : 0,
            canRaise = own >= 0 && Game.CanRaise(own), showdown = Game.ShowdownReached,
            deadlineUnixMs = Game.Actor >= 0 ? new DateTimeOffset(Deadline, TimeSpan.Zero).ToUnixTimeMilliseconds() : 0,
            serverUnixMs = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            board = Game.Board.Select(ToCard).ToArray(), handHistory = CompletedHands.AsEnumerable().Reverse().ToArray(),
            results = Game.Results.ToArray(), history = Game.Log.TakeLast(35).Reverse().ToArray(),
            players = Enumerable.Range(0, 6).Select(i =>
            {
                Session s = Seats[i]; var p = Game.Players[i];
                bool needsRebuy = s != null && p.Stack == 0 && (Game.Stage == Street.Complete || !p.InHand);
                bool reveal = s != null && ((i == own && livePlayers > 1) || (Game.ShowdownReached && p.InHand && !p.Folded));
                return new SeatView {
                    seat = i, occupied = s != null, username = s?.Name ?? "Open seat", avatar = s?.Avatar ?? 0,
                    stack = s == null ? 0 : p.Stack, bet = s == null ? 0 : p.Bet, count = s != null && p.InHand ? p.Cards.Count : 0,
                    cards = reveal ? p.Cards.Select(ToCard).ToArray() : Array.Empty<CardView>(),
                    connected = s?.Peer != null, inHand = s != null && p.InHand, folded = p.Folded, drawCount = p.DrawCount,
                    winner = Game.Stage == Street.Complete && p.Payout > 0, scoop = Game.Stage == Street.Complete && p.Scoop,
                    needsRebuy = needsRebuy,
                    drawLocked = Game.Stage == Street.Draw && s?.LockedDrawHand == Game.HandNumber,
                    queuedDrawCount = i == own && s?.LockedDrawHand == Game.HandNumber ? s.LockedDiscards?.Length ?? 0 : 0,
                    checkFold = i == own && s?.CheckFold == true,
                    callQueued = i == own && s != null && s.AutoCallAmount > 0 && s.AutoCallAmount == (Game.IsBetting ? Game.ToCall(i) : 0),
                    callAmount = i == own && Game.IsBetting ? Game.ToCall(i) : 0,
                    waitingToAct = i == own && Game.IsAwaitingAction(i),
                    entryMode = s?.Entry.ToString() ?? "", leaving = i == own && s?.Leaving == true,
                    status = s == null ? "Available" : s.Peer == null ? "Disconnected" : needsRebuy ? "Rebuy needed" : !p.InHand ? (s.Entry == EntryMode.PostBigBlind ? "Post next eligible hand" : s.Entry == EntryMode.WaitForBigBlind ? "Waiting for big blind" : "Ready for next hand") : p.Folded ? (p.Action == "Auto-fold" ? "Auto-fold" : "Folded") : p.Stack == 0 ? "All in" : p.Action
                };
            }).ToArray()
        };
    }
    internal static CardView ToCard(Card c) => new() { rank = c.Rank, suit = c.Suit };
}

// One lock serializes every mutation; socket writes happen through each peer's queue.
public sealed class Hub
{
    readonly object gate = new();
    public readonly Room[] Rooms = { new("micro", "The Green Room", 1, 2), new("standard", "Drawmaha Social", 5, 10), new("deep", "The High Table", 25, 50) };
    readonly Dictionary<string, Session> sessions = new();
    public readonly string Epoch = Guid.NewGuid().ToString("N");
    readonly string accessKey;
    readonly int actionSeconds, showdownSeconds, reconnectSeconds;
    int connections;
    public Hub(string accessKey = null, int actionSeconds = 30, int showdownSeconds = 6, int reconnectSeconds = 90)
    {
        this.accessKey = accessKey ?? Environment.GetEnvironmentVariable("DRAWMAHA_ACCESS_KEY") ?? "";
        this.actionSeconds = Setting("DRAWMAHA_ACTION_SECONDS", actionSeconds, 2, 120);
        this.showdownSeconds = Setting("DRAWMAHA_SHOWDOWN_SECONDS", showdownSeconds, 1, 30);
        this.reconnectSeconds = Setting("DRAWMAHA_RECONNECT_SECONDS", reconnectSeconds, 2, 600);
        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production" && this.accessKey.Length < 16) throw new InvalidOperationException("Production requires a DRAWMAHA_ACCESS_KEY of at least 16 characters.");
    }
    static int Setting(string name, int fallback, int min, int max) => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? Math.Clamp(value, min, max) : fallback;
    public bool TryReserveConnection() { lock (gate) { if (connections >= 64) return false; connections++; return true; } }
    public void ReleaseConnection() { lock (gate) connections--; }
    public void Connect(Peer peer, Command hello)
    {
        lock (gate)
        {
            if (accessKey.Length > 0 && !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(accessKey), System.Text.Encoding.UTF8.GetBytes(hello.accessKey ?? ""))) throw new InvalidOperationException("Incorrect invite key.");
            Session s;
            if (!string.IsNullOrEmpty(hello.token) && sessions.TryGetValue(hello.token, out s))
            {
                s.Peer?.Stop();
                if (s.Room != null && !s.Room.Game.Players[s.Seat].InHand && (s.MissedBig || s.MissedSmall)) s.Entry = EntryMode.WaitForBigBlind;
            }
            else
            {
                if (sessions.Count >= 128) throw new InvalidOperationException("Session capacity reached. Try again later.");
                string name = (hello.username ?? "").Trim();
                if (!Regex.IsMatch(name, @"^[a-zA-Z0-9][a-zA-Z0-9 _-]{1,19}$")) throw new ArgumentException("Username: 2–20 letters, numbers, spaces, _ or -.");
                if (sessions.Values.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("That username is in use.");
                s = new Session { Name = name, Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Avatar = Math.Clamp(hello.avatar, 0, 7) };
                sessions.Add(s.Token, s);
            }
            s.Peer = peer; s.LastSeen = DateTime.UtcNow; peer.Session = s;
            peer.Send(new Envelope { type = "welcome", token = s.Token, epoch = Epoch, protocol = WireProtocol.Current });
            peer.Send(new Envelope { type = "lobby", tables = Rooms.Select(r => r.Lobby()).ToArray() });
            if (s.Room != null) Changed(s.Room, DateTime.UtcNow);
        }
    }
    public void Handle(Peer peer, Command command)
    {
        lock (gate)
        {
            var s = peer.Session;
            if (s == null || s.Peer != peer) return;
            DateTime now = DateTime.UtcNow; s.LastSeen = now;
            while (peer.Requests.Count > 0 && (now - peer.Requests.Peek()).TotalSeconds > 10) peer.Requests.Dequeue();
            if (peer.Requests.Count >= 30) { peer.Send(new Envelope { type = "error", message = "Too many requests. Wait a few seconds.", id = command?.id }); return; }
            peer.Requests.Enqueue(now);
            if (command == null) return;
            if (command.type == "ping") { peer.Send(new Envelope { type = "pong" }); return; }
            if (command.type == "lobby") { peer.Send(new Envelope { type = "lobby", tables = Rooms.Select(r => r.Lobby()).ToArray() }); return; }
            try
            {
                if (string.IsNullOrEmpty(command.id) || command.id.Length > 64) throw new ArgumentException("A command ID is required.");
                if (s.Seen.Contains(command.id)) { peer.Send(new Envelope { type = "ack", id = command.id }); if (s.Room != null) peer.Send(new Envelope { type = "state", table = s.Room.View(s, now) }); return; }
                if (command.type == "join") Join(s, command, now);
                else
                {
                    var room = s.Room ?? throw new InvalidOperationException("Join a table first.");
                    if (command.tableId != room.Id) throw new InvalidOperationException("Wrong table.");
                    if (command.type == "leave")
                    {
                        Player player = room.Game.Players[s.Seat];
                        bool handInProgress = room.Game.Stage != Street.Waiting && room.Game.Stage != Street.Complete;
                        if (handInProgress && player.InHand) { s.Leaving = true; s.LeaveRequested = true; }
                        else Remove(s, handInProgress);
                        Changed(room, now); LobbyChanged();
                    }
                    else if (command.type == "rebuy")
                    {
                        Player player = room.Game.Players[s.Seat];
                        if (player.Stack != 0 || (player.InHand && room.Game.Stage != Street.Complete)) throw new InvalidOperationException("Rebuy is available between hands.");
                        player.Stack = room.BuyIn; player.Action = "Rebuy";
                        Changed(room, now); LobbyChanged();
                    }
                    else if (command.type == "lockDraw")
                    {
                        if (command.hand != room.Game.HandNumber) throw new InvalidOperationException("Hand changed; review the latest state and try again.");
                        Player player = room.Game.Players[s.Seat];
                        if (room.Game.Stage != Street.Draw || !player.InHand || player.Folded) throw new InvalidOperationException("Draw choices can only be locked during the draw round.");
                        if (s.LockedDrawHand == room.Game.HandNumber)
                        {
                            s.LockedDrawHand = -1; s.LockedDiscards = null;
                            player.Action = "Waiting to draw";
                        }
                        else
                        {
                            if (player.Action == "Stand pat" || player.Action.StartsWith("Draw ", StringComparison.Ordinal))
                                throw new InvalidOperationException("Your draw is already complete.");
                            int[] discards = command.discards ?? Array.Empty<int>();
                            Game.ValidateDraw(discards);
                            s.LockedDrawHand = room.Game.HandNumber; s.LockedDiscards = (int[])discards.Clone();
                            player.Action = discards.Length == 0 ? "Stand pat queued" : "Draw queued";
                        }
                        ResolveLockedDraws(room, now); Changed(room, now);
                    }
                    else if (command.type == "checkFold")
                    {
                        Player player = room.Game.Players[s.Seat];
                        if (!room.Game.IsAwaitingAction(s.Seat) || !player.CanAct || command.hand != room.Game.HandNumber)
                            throw new InvalidOperationException("Check/Fold can only be set while waiting for your betting turn.");
                        s.CheckFold = command.enabled;
                        if (command.enabled) s.AutoCallAmount = -1;
                        if (command.enabled && room.Game.Actor == s.Seat) ResolveAutomaticActions(room, now);
                        Changed(room, now);
                    }
                    else if (command.type == "autoCall")
                    {
                        Player player = room.Game.Players[s.Seat];
                        int callAmount = room.Game.IsBetting && player.CanAct ? room.Game.ToCall(s.Seat) : 0;
                        if (!room.Game.IsAwaitingAction(s.Seat) || !player.CanAct || command.hand != room.Game.HandNumber || callAmount <= 0 || command.callAmount != callAmount)
                            throw new InvalidOperationException("Call amount changed or your turn has started.");
                        s.AutoCallAmount = command.enabled ? callAmount : -1;
                        if (command.enabled) s.CheckFold = false;
                        if (command.enabled && room.Game.Actor == s.Seat) ResolveAutomaticActions(room, now);
                        Changed(room, now);
                    }
                    else if (command.type == "entry")
                    {
                        if (room.Game.Players[s.Seat].InHand && room.Game.Stage != Street.Complete) throw new InvalidOperationException("Entry preference applies between hands.");
                        s.Entry = ParseEntry(command.entryMode); Changed(room, now);
                    }
                    else
                    {
                        if (s.Leaving && !s.LeaveRequested) throw new InvalidOperationException("You are leaving after this hand.");
                        if (command.revision != room.Revision || command.hand != room.Game.HandNumber) throw new InvalidOperationException("Table changed; review the latest state and try again.");
                        if (room.Game.Actor != s.Seat) throw new InvalidOperationException("Not your turn.");
                        switch (command.type)
                        {
                            case "fold": room.Game.Fold(s.Seat); break;
                            case "call": room.Game.Call(s.Seat); break;
                            case "raise": room.Game.RaiseTo(s.Seat, command.raiseTo); break;
                            case "draw": room.Game.Draw(s.Seat, command.discards ?? Array.Empty<int>()); break;
                            default: throw new ArgumentException("Unknown command.");
                        }
                        AfterAction(room, now);
                        ResolveLockedDraws(room, now);
                        ResolveAutomaticActions(room, now);
                        Changed(room, now);
                    }
                }
                s.Seen.Add(command.id); s.Commands.Enqueue(command.id);
                if (s.Commands.Count > 64) s.Seen.Remove(s.Commands.Dequeue());
                peer.Send(new Envelope { type = "ack", id = command.id });
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                peer.Send(new Envelope { type = "error", message = e.Message, id = command.id });
                if (s.Room != null) peer.Send(new Envelope { type = "state", table = s.Room.View(s, now) });
            }
        }
    }
    static EntryMode ParseEntry(string entry) => entry == "post" ? EntryMode.PostBigBlind : entry == "wait" ? EntryMode.WaitForBigBlind : throw new ArgumentException("Choose post or wait.");
    void Join(Session s, Command command, DateTime now)
    {
        if (s.Room != null) throw new InvalidOperationException("Leave your current table first.");
        var entry = ParseEntry(command.entryMode);
        var room = Rooms.FirstOrDefault(r => r.Id == command.tableId) ?? throw new ArgumentException("Unknown table.");
        int seat = -1;
        for (int i = 0; i < room.Seats.Length; i++) if (room.Seats[i] == null && !room.ReservedSeats[i]) { seat = i; break; }
        if (seat < 0) throw new InvalidOperationException("Table is full.");
        s.Room = room; s.Seat = seat; s.Entry = entry; s.Leaving = s.LeaveRequested = false; s.MissedBig = s.MissedSmall = false;
        s.CheckFold = false; s.AutoCallAmount = -1; s.LockedDrawHand = -1; s.LockedDiscards = null;
        room.Seats[seat] = s; var p = room.Game.Players[seat]; p.Name = s.Name; p.Stack = room.BuyIn; p.Bet = p.Contributed = p.DrawCount = p.Payout = 0; p.Folded = p.InHand = p.Scoop = false; p.Action = ""; p.Cards.Clear();
        room.Game.Note(s.Name + " joins seat " + (seat + 1) + (entry == EntryMode.PostBigBlind ? " · post big blind" : " · wait for big blind"));
        Changed(room, now); LobbyChanged();
    }
    void Remove(Session s, bool reserveSeat = false)
    {
        Room room = s.Room; if (room == null) return;
        room.ReservedSeats[s.Seat] = reserveSeat;
        room.Seats[s.Seat] = null; var p = room.Game.Players[s.Seat]; p.Stack = 0; p.Cards.Clear(); p.InHand = false;
        if (!room.Seats.Any(seat => seat != null)) { room.CompletedHands.Clear(); room.ArchivedHandNumber = -1; }
        s.Room = null; s.Seat = -1; s.Leaving = s.LeaveRequested = false;
        s.CheckFold = false; s.AutoCallAmount = -1; s.LockedDrawHand = -1; s.LockedDiscards = null;
        s.Peer?.Send(new Envelope { type = "left" });
    }
    public void Disconnect(Peer peer)
    {
        lock (gate)
        {
            var s = peer.Session; if (s == null || s.Peer != peer) return;
            s.Peer = null; s.LastSeen = DateTime.UtcNow; s.DisconnectedHand = s.Room?.Game.HandNumber ?? 0;
            if (s.Room != null) Changed(s.Room, DateTime.UtcNow);
        }
    }
    void AfterAction(Room room, DateTime now)
    {
        room.Deadline = now.AddSeconds(actionSeconds);
        if (room.Game.Stage == Street.Complete)
        {
            ArchiveCompletedHand(room);
            if (!room.AnnouncedComplete) { room.AnnouncedComplete = true; room.NextHand = now.AddSeconds(showdownSeconds); }
        }
    }
    static void ArchiveCompletedHand(Room room)
    {
        Game game = room.Game;
        if (game.Stage != Street.Complete || room.ArchivedHandNumber == game.HandNumber) return;
        var archived = new HandHistoryView
        {
            hand = game.HandNumber,
            showdown = game.ShowdownReached,
            board = game.Board.Select(Room.ToCard).ToArray(),
            players = Enumerable.Range(0, game.Players.Length)
                .Where(seat => game.Players[seat].InHand)
                .Select(seat =>
                {
                    Player player = game.Players[seat];
                    bool revealed = game.ShowdownReached && !player.Folded;
                    return new HandHistoryPlayerView
                    {
                        username = player.Name,
                        avatar = room.Seats[seat]?.Avatar ?? 0,
                        folded = player.Folded,
                        revealed = revealed,
                        cards = revealed ? player.Cards.Select(Room.ToCard).ToArray() : Array.Empty<CardView>()
                    };
                }).ToArray()
        };
        room.CompletedHands.Add(archived);
        if (room.CompletedHands.Count > 20) room.CompletedHands.RemoveAt(0);
        room.ArchivedHandNumber = game.HandNumber;
    }
    void ResolveLockedDraws(Room room, DateTime now)
    {
        while (room.Game.Stage == Street.Draw && room.Game.Actor >= 0)
        {
            int seat = room.Game.Actor; Session actor = room.Seats[seat];
            if (actor == null || actor.LockedDrawHand != room.Game.HandNumber) return;
            int[] discards = actor.LockedDiscards ?? Array.Empty<int>();
            actor.LockedDrawHand = -1; actor.LockedDiscards = null;
            room.Game.Draw(seat, discards); AfterAction(room, now);
        }
    }
    void ClearStaleAutoCalls(Room room)
    {
        foreach (Session session in room.Seats.Where(s => s != null))
        {
            if (session.AutoCallAmount <= 0) continue;
            Player player = room.Game.Players[session.Seat];
            if (!room.Game.IsBetting || !player.CanAct || room.Game.ToCall(session.Seat) != session.AutoCallAmount)
                session.AutoCallAmount = -1;
        }
    }
    void ResolveAutomaticActions(Room room, DateTime now)
    {
        ClearStaleAutoCalls(room);
        while (room.Game.IsBetting && room.Game.Actor >= 0)
        {
            int seat = room.Game.Actor;
            Session actor = room.Seats[seat];
            if (actor == null) return;
            int callAmount = room.Game.ToCall(seat);
            if (actor.CheckFold)
            {
                actor.CheckFold = false;
                if (callAmount > 0)
                {
                    room.Game.Fold(seat);
                    room.Game.Players[seat].Action = "Auto-fold";
                }
                else
                {
                    room.Game.Call(seat);
                    room.Game.Players[seat].Action = "Auto-check";
                }
            }
            else if (callAmount > 0 && actor.AutoCallAmount == callAmount)
            {
                actor.AutoCallAmount = -1;
                room.Game.Call(seat);
                room.Game.Players[seat].Action = "Auto-call";
            }
            else return;
            AfterAction(room, now);
            ClearStaleAutoCalls(room);
        }
        if (!room.Game.IsBetting)
            foreach (Session session in room.Seats.Where(s => s != null)) session.CheckFold = false;
    }
    public void Tick(DateTime now)
    {
        lock (gate)
        {
            foreach (var room in Rooms)
            {
                foreach (var s in room.Seats.Where(s => s != null && s.Peer == null && (now - s.LastSeen).TotalSeconds >= reconnectSeconds)) s.Leaving = true;
                if (room.Game.Actor >= 0)
                {
                    var actor = room.Seats[room.Game.Actor];
                    if (actor == null || (actor.Leaving && !actor.LeaveRequested) || now >= room.Deadline)
                    {
                        // Deterministic timeout policy, not an AI opponent.
                        if (room.Game.Stage == Street.Draw) room.Game.Draw(room.Game.Actor, Array.Empty<int>());
                        else if (room.Game.ToCall(room.Game.Actor) > 0) room.Game.Fold(room.Game.Actor);
                        else room.Game.Call(room.Game.Actor);
                        AfterAction(room, now);
                        ResolveLockedDraws(room, now);
                        ResolveAutomaticActions(room, now);
                        Changed(room, now);
                    }
                }
                bool boundary = room.Game.Stage == Street.Waiting || (room.Game.Stage == Street.Complete && now >= room.NextHand);
                if (!boundary) continue;
                bool removed = false;
                foreach (var s in room.Seats.Where(s => s != null && s.Leaving).ToArray()) { Remove(s); removed = true; }
                Array.Clear(room.ReservedSeats, 0, room.ReservedSeats.Length);
                bool[] available = room.Seats.Select((s, i) => s != null && s.Peer != null && room.Game.Players[i].Stack > 0).ToArray();
                if (available.Count(player => player) < 2)
                {
                    bool reset = room.Game.Stage == Street.Complete;
                    if (reset) room.Game.ResetCompletedHandForWaiting();
                    if (removed || reset) Changed(room, now);
                    if (removed) LobbyChanged();
                    continue;
                }
                if (removed) { Changed(room, now); LobbyChanged(); }
                EntryMode[] entries = room.Seats.Select(s => s?.Entry ?? EntryMode.WaitForBigBlind).ToArray();
                var plan = room.Orbit.Plan(available, entries); if (plan == null) continue;
                // Track obligations for disconnected incumbents skipped by this rotation.
                if (room.Orbit.Started)
                {
                    int last = room.Orbit.LastBigBlind;
                    if (last >= 0 && room.Seats[last]?.Peer == null && room.Seats[last]?.Entry == EntryMode.Ready) room.Seats[last].MissedSmall = true;
                    for (int step = 1; step <= 6; step++)
                    {
                        int i = (last + step + 6) % 6; if (i == plan.BigBlind) break;
                        if (room.Seats[i] != null && room.Seats[i].Peer == null && room.Seats[i].Entry == EntryMode.Ready) room.Seats[i].MissedBig = true;
                    }
                }
                foreach (int seat in plan.EntryPosts.Keys.ToArray()) plan.EntryPosts[seat] = room.Game.BigBlind;
                for (int i = 0; i < 6; i++)
                {
                    if (plan.Active[i])
                    {
                        var s = room.Seats[i];
                        if (s.MissedSmall && s.Entry == EntryMode.PostBigBlind && i != plan.BigBlind) plan.DeadPosts[i] = room.Game.SmallBlind;
                        s.Entry = EntryMode.Ready; s.MissedSmall = s.MissedBig = false;
                    }
                }
                room.Game.StartHand(plan); room.Orbit.Commit(plan); room.AnnouncedComplete = false;
                AfterAction(room, now); Changed(room, now);
            }
            foreach (var pair in sessions.Where(p => p.Value.Peer == null && p.Value.Room == null && (now - p.Value.LastSeen).TotalMinutes > 20).ToArray()) sessions.Remove(pair.Key);
        }
    }
    void Changed(Room room, DateTime now)
    {
        room.Revision++;
        foreach (var s in room.Seats) if (s?.Peer != null) s.Peer.Send(new Envelope { type = "state", table = room.View(s, now) });
    }
    void LobbyChanged() { var rows = Rooms.Select(r => r.Lobby()).ToArray(); foreach (var s in sessions.Values) s.Peer?.Send(new Envelope { type = "lobby", tables = rows }); }
}
