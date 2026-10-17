using System.Security.Cryptography;
using System.Text;
using Drawmaha.Network;

namespace Drawmaha.Server;

public sealed class Session
{
    public string Token, Name, WalletId, PendingRoomId;
    public int Avatar, Seat = -1, DisconnectedHand, LockedDrawHand = -1;
    public int PendingSeat = -1;
    public float AutoCallAmount = -1, PendingAddOn, AutoTopUpBigBlinds;
    public bool AutoTopUpEnabled, AlwaysTopUpMax;
    public DateTime PendingRoomExpires;
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
    public readonly Queue<DateTime> ChatSentAt = new();
}
public sealed class Room
{
    public readonly string Id, Name;
    public readonly bool IsPublic;
    public readonly byte[] InviteCodeHash;
    public readonly float MinBuyIn, MaxBuyIn;
    public readonly Game Game;
    public readonly Session[] Seats = new Session[6];
    public readonly bool[] ReservedSeats = new bool[6];
    public readonly BlindOrbit Orbit = new();
    public readonly List<HandHistoryView> CompletedHands = new();
    public readonly List<ChatMessageView> ChatMessages = new();
    public long ChatSequence;
    public long Revision;
    public DateTime Deadline, NextHand;
    public bool AnnouncedComplete;
    public int ArchivedHandNumber = -1;
    public float BuyIn => MinBuyIn;
    public Room(string id, string name, float small, float big, float minBuyIn, float maxBuyIn, bool isPublic, byte[] inviteCodeHash, IHandAudit audit)
    {
        Id = id; Name = name; MinBuyIn = minBuyIn; MaxBuyIn = maxBuyIn; IsPublic = isPublic; InviteCodeHash = inviteCodeHash;
        Game = new Game(new SecureShuffleRandom(), 0, 6,
            audit ?? throw new ArgumentNullException(nameof(audit))) { SmallBlind = small, BigBlind = big };
    }
    public LobbyRow Lobby() => new() { id = Id, name = Name, smallBlind = Game.SmallBlind, bigBlind = Game.BigBlind, buyIn = MinBuyIn,
        minBuyIn = MinBuyIn, maxBuyIn = MaxBuyIn, isPublic = IsPublic,
        occupied = Seats.Count(s => s != null) + ReservedSeats.Count(reserved => reserved),
        available = Enumerable.Range(0, Seats.Length).Count(i => Seats[i] == null && !ReservedSeats[i]), maxSeats = 6 };
    public TableState View(Session viewer, DateTime now)
    {
        int own = viewer?.Seat ?? -1;
        int livePlayers = Game.Players.Count(p => p.InHand && !p.Folded);
        return new TableState
        {
            id = Id, name = Name, yourSeat = own, hand = Game.HandNumber, revision = Revision,
            stage = Game.Stage.ToString(), dealer = Game.Dealer, smallBlindSeat = Game.SmallBlindSeat, bigBlindSeat = Game.BigBlindSeat,
            smallBlind = Game.SmallBlind, bigBlind = Game.BigBlind, minBuyIn = MinBuyIn, maxBuyIn = MaxBuyIn,
            isPublic = IsPublic,
            pot = Game.Pot > 0 ? Game.Pot : Game.LastPot, actor = Game.Actor,
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
                    pendingAddOn = i == own ? s?.PendingAddOn ?? 0 : 0,
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
    readonly WalletStore wallets;
    readonly HandAuditStore auditStore;
    public readonly List<Room> Rooms = new();
    readonly Dictionary<string, Session> sessions = new();
    public readonly string Epoch = Guid.NewGuid().ToString("N");
    readonly int actionSeconds, showdownSeconds, reconnectSeconds;
    int connections;
    public Hub(WalletStore wallets, HandAuditStore audit, int actionSeconds = 30, int showdownSeconds = 6, int reconnectSeconds = 90)
    {
        this.wallets = wallets; auditStore = audit;
        this.actionSeconds = Setting("DRAWMAHA_ACTION_SECONDS", actionSeconds, 2, 120);
        this.showdownSeconds = Setting("DRAWMAHA_SHOWDOWN_SECONDS", showdownSeconds, 1, 30);
        this.reconnectSeconds = Setting("DRAWMAHA_RECONNECT_SECONDS", reconnectSeconds, 2, 600);
    }
    IHandAudit TimedAudit(IHandAudit sink) => new DeadlineAwareAudit(sink, delay =>
    {
        // Audit writes run under gate: no table can process commands during this delay.
        foreach (var room in Rooms)
            if (room.Game.Actor >= 0) room.Deadline = room.Deadline.Add(delay);
    });
    static int Setting(string name, int fallback, int min, int max) => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? Math.Clamp(value, min, max) : fallback;
    public bool TryReserveConnection() { lock (gate) { if (connections >= 64) return false; connections++; return true; } }
    public void ReleaseConnection() { lock (gate) connections--; }
    public void Connect(Peer peer, Command hello)
    {
        lock (gate)
        {
            if (hello.protocol < WireProtocol.Current) throw new InvalidOperationException("Update the game to connect to this server.");
            string name = DisplayNameRules.Validate(hello.username);
            if (!Guid.TryParseExact(hello.walletId, "N", out Guid parsedWalletId)) throw new ArgumentException("A valid guest wallet identity is required.");
            string walletId = parsedWalletId.ToString("N");
            float walletBalance = wallets.GetBalance(walletId);
            Session s;
            if (!string.IsNullOrEmpty(hello.token) && sessions.TryGetValue(hello.token, out s))
            {
                if (s.WalletId != walletId) throw new InvalidOperationException("This reconnect token belongs to a different wallet.");
                if (sessions.Values.Any(p => p != s && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("That display name is in use.");
                s.Peer?.Stop();
                if (s.Room != null && !s.Room.Game.Players[s.Seat].InHand && (s.MissedBig || s.MissedSmall)) s.Entry = EntryMode.WaitForBigBlind;
            }
            else
            {
                if (sessions.Count >= 128) throw new InvalidOperationException("Session capacity reached. Try again later.");
                if (sessions.Values.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("That display name is in use.");
                s = new Session { Name = name, WalletId = walletId, Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Avatar = Math.Clamp(hello.avatar, 0, 7) };
                sessions.Add(s.Token, s);
            }
            if (s.Name != name)
            {
                string previousName = s.Name;
                s.Name = name;
                if (s.Room != null)
                {
                    s.Room.Game.Players[s.Seat].Name = name;
                    s.Room.Game.Note(previousName + " changed display name to " + name + ".");
                }
            }
            s.Peer = peer; s.LastSeen = DateTime.UtcNow; peer.Session = s;
            var topUpSettings = wallets.GetAutoTopUp(walletId);
            s.AutoTopUpEnabled = topUpSettings.Enabled;
            s.AutoTopUpBigBlinds = topUpSettings.BigBlinds;
            s.AlwaysTopUpMax = topUpSettings.AlwaysMax;
            peer.Send(new Envelope { type = "welcome", token = s.Token, epoch = Epoch, protocol = WireProtocol.Current,
                walletBalance = walletBalance, username = s.Name, avatar = s.Avatar,
                autoTopUpEnabled = s.AutoTopUpEnabled, autoTopUpBigBlinds = s.AutoTopUpBigBlinds, alwaysTopUpMax = s.AlwaysTopUpMax });
            peer.Send(new Envelope { type = "lobby", tables = Rooms.Select(r => r.Lobby()).ToArray() });
            if (s.Room != null) { Changed(s.Room, DateTime.UtcNow); SendChatHistory(s); }
            else if (s.PendingRoomId != null)
            {
                Room pendingRoom = Rooms.FirstOrDefault(room => room.Id == s.PendingRoomId);
                if (pendingRoom != null)
                {
                    TableState preview = pendingRoom.View(null, DateTime.UtcNow); preview.buyInReserved = true;
                    peer.Send(new Envelope { type = "state", table = preview });
                }
            }
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
                if (s.Seen.Contains(command.id))
                {
                    if (command.type == "profile") SendProfile(s, command.id);
                    peer.Send(new Envelope { type = "ack", id = command.id,
                        tableId = command.type == "create" ? s.PendingRoomId ?? command.tableId : null });
                    if (s.Room != null) peer.Send(new Envelope { type = "state", table = s.Room.View(s, now) });
                    return;
                }
                if (command.type == "profile") UpdateProfile(s, command, now);
                else if (command.type == "create") CreateTable(s, command, now);
                else if (command.type == "join") Join(s, command, now);
                else if (command.type == "cancelCreate")
                {
                    if (!CancelPendingCreate(s)) throw new InvalidOperationException("There is no created table reservation to cancel.");
                }
                else
                {
                    var room = s.Room ?? throw new InvalidOperationException("Join a table first.");
                    if (room.Game.Faulted) throw new InvalidOperationException("Table paused: secure hand recording failed.");
                    if (command.tableId != room.Id) throw new InvalidOperationException("Wrong table.");
                    if (command.type == "chat") SendChat(s, command.message, now);
                    else if (command.type == "leave")
                    {
                        Player player = room.Game.Players[s.Seat];
                        bool handInProgress = room.Game.Stage != Street.Waiting && room.Game.Stage != Street.Complete;
                        if (handInProgress && player.InHand) { s.Leaving = true; s.LeaveRequested = true; }
                        else Remove(s, handInProgress);
                        RemoveEmptyRooms();
                        Changed(room, now); LobbyChanged();
                    }
                    else if (command.type == "rebuy")
                    {
                        Player player = room.Game.Players[s.Seat];
                        if (player.Stack != 0 || (player.InHand && room.Game.Stage != Street.Complete)) throw new InvalidOperationException("Rebuy is available between hands.");
                        if (room.BuyIn > room.MaxBuyIn - player.Stack) throw new InvalidOperationException("The table maximum buy-in has been reached.");
                        wallets.Rebuy(s.WalletId, room.Id, room.BuyIn);
                        player.Stack = room.BuyIn; player.Action = "Rebuy";
                        room.Game.RecordSeatUpdate(s.Seat, "rebuy");
                        WalletChanged(s);
                        Changed(room, now); LobbyChanged();
                    }
                    else if (command.type == "addOn")
                    {
                        if (!IsValidChipAmount(command.addOnAmount) || command.addOnAmount <= 0)
                            throw new ArgumentException("Choose a positive add-on amount with up to two decimal places.");
                        Player player = room.Game.Players[s.Seat];
                        float capacity = Math.Max(0, room.MaxBuyIn - player.Stack - s.PendingAddOn);
                        float amount = RoundChips(Math.Min(command.addOnAmount, Math.Min(capacity, wallets.GetBalance(s.WalletId))));
                        if (amount <= 0) throw new InvalidOperationException("There is no add-on available for your stack and bank balance.");
                        s.PendingAddOn = RoundChips(s.PendingAddOn + amount);
                        room.Game.Note(s.Name + " queued an add-on for the next hand.");
                        Changed(room, now);
                    }
                    else if (command.type == "autoTopUp")
                    {
                        if (!IsValidChipAmount(command.autoTopUpBigBlinds) || command.autoTopUpBigBlinds > 1000000
                            || (command.enabled && !command.alwaysTopUpMax && command.autoTopUpBigBlinds <= 0))
                            throw new ArgumentException("Auto top-up must use a valid number of big blinds.");
                        float bigBlinds = RoundChips(command.autoTopUpBigBlinds);
                        bool alwaysMax = command.enabled && command.alwaysTopUpMax;
                        wallets.SetAutoTopUp(s.WalletId, bigBlinds, command.enabled, alwaysMax);
                        s.AutoTopUpEnabled = command.enabled;
                        s.AutoTopUpBigBlinds = bigBlinds;
                        s.AlwaysTopUpMax = alwaysMax;
                        peer.Send(new Envelope { type = "topUpSettings", autoTopUpEnabled = s.AutoTopUpEnabled,
                            autoTopUpBigBlinds = bigBlinds, alwaysTopUpMax = alwaysMax });
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
                        float callAmount = room.Game.IsBetting && player.CanAct ? room.Game.ToCall(s.Seat) : 0;
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
                            case "raise":
                                if (!IsValidChipAmount(command.raiseTo)) throw new ArgumentException("Raise amounts must use up to two decimal places.");
                                room.Game.RaiseTo(s.Seat, command.raiseTo); break;
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
                peer.Send(new Envelope { type = "ack", id = command.id, tableId = command.type == "create" ? command.tableId : null });
            }
            catch (AuditFailureException)
            {
                if (s.Room != null) NotifyAuditFailure(s.Room);
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                peer.Send(new Envelope { type = "error", message = e.Message, id = command.id });
                if (s.Room != null) peer.Send(new Envelope { type = "state", table = s.Room.View(s, now) });
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Unexpected table command failure: " + e.GetType().Name);
                peer.Send(new Envelope { type = "error", message = "The server could not complete that request.", id = command.id });
            }
        }
    }
    static void SendProfile(Session session, string commandId) => session.Peer.Send(new Envelope {
        type = "profile", id = commandId, username = session.Name, avatar = session.Avatar
    });
    void UpdateProfile(Session session, Command command, DateTime now)
    {
        string name = DisplayNameRules.Validate(command.username);
        if (sessions.Values.Any(other => other != session && other.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("That display name is in use.");
        if (command.avatar < 0 || command.avatar > 7) throw new ArgumentException("Choose a valid avatar.");
        string previousName = session.Name;
        session.Name = name; session.Avatar = command.avatar;
        if (session.Room != null)
        {
            session.Room.Game.Players[session.Seat].Name = name;
            if (previousName != name) session.Room.Game.Note(previousName + " changed display name to " + name + ".");
            BroadcastState(session.Room, now);
        }
        SendProfile(session, command.id);
    }
    static string ValidateTableName(string value)
    {
        string name = (value ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9][a-zA-Z0-9 _-]{2,31}$"))
            throw new ArgumentException("Table names must be 3–32 letters, numbers, spaces, _ or -, starting with a letter or number.");
        return name;
    }
    static string ValidateInviteCode(string value)
    {
        string code = (value ?? "").Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"^[A-Z0-9-]{8,24}$"))
            throw new ArgumentException("Invite codes must be 8–24 letters, numbers, or hyphens.");
        return code;
    }
    static byte[] HashInviteCode(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));
    static void ValidateTableSettings(Command command)
    {
        if (!IsValidChipAmount(command.smallBlind) || !IsValidChipAmount(command.bigBlind)
            || command.smallBlind <= 0 || command.bigBlind <= command.smallBlind || command.bigBlind > 100000)
            throw new ArgumentException("Choose positive blinds with up to two decimal places, where the big blind is larger and no more than 100,000.");
        if (!IsValidChipAmount(command.minBuyIn) || !IsValidChipAmount(command.maxBuyIn))
            throw new ArgumentException("Buy-ins must use valid amounts with up to two decimal places.");
        if (command.minBuyIn < command.bigBlind || command.maxBuyIn < command.minBuyIn || command.maxBuyIn > 100000000)
            throw new ArgumentException("Buy-ins must be at least one big blind, with the maximum no lower than the minimum and no more than 100,000,000.");
    }
    static bool IsValidChipAmount(float amount) => !float.IsNaN(amount) && !float.IsInfinity(amount)
        && amount >= 0 && Math.Abs(amount * 100 - Math.Round(amount * 100)) < 0.001;
    static float RoundChips(float amount) => (float)Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    void CreateTable(Session session, Command command, DateTime now)
    {
        if (session.Room != null || session.PendingRoomId != null) throw new InvalidOperationException("Leave your current table before creating another.");
        if (Rooms.Count >= 100) throw new InvalidOperationException("The server has reached its table limit. Try again later.");
        string name = ValidateTableName(command.tableName);
        ValidateTableSettings(command);
        command.smallBlind = RoundChips(command.smallBlind);
        command.bigBlind = RoundChips(command.bigBlind);
        command.minBuyIn = RoundChips(command.minBuyIn);
        command.maxBuyIn = RoundChips(command.maxBuyIn);
        byte[] codeHash = null;
        if (!command.isPublic) codeHash = HashInviteCode(ValidateInviteCode(command.inviteCode));
        if (wallets.GetBalance(session.WalletId) < command.minBuyIn)
            throw new InvalidOperationException("Your main bank does not have enough chips for this table's minimum buy-in.");
        string id = Guid.NewGuid().ToString("N");
        Room room = new(id, name, command.smallBlind, command.bigBlind, command.minBuyIn, command.maxBuyIn,
            command.isPublic, codeHash, TimedAudit(auditStore.ForTable(id)));
        Rooms.Add(room);
        room.ReservedSeats[0] = true;
        session.PendingRoomId = id; session.PendingSeat = 0; session.PendingRoomExpires = now.AddMinutes(5);
        command.tableId = id;
        LobbyChanged();
        TableState preview = room.View(null, now); preview.buyInReserved = true;
        session.Peer?.Send(new Envelope { type = "state", table = preview });
    }
    bool CancelPendingCreate(Session session)
    {
        if (session.PendingRoomId == null) return false;
        Room room = Rooms.FirstOrDefault(candidate => candidate.Id == session.PendingRoomId);
        if (room != null && session.PendingSeat >= 0 && session.PendingSeat < room.ReservedSeats.Length)
            room.ReservedSeats[session.PendingSeat] = false;
        session.PendingRoomId = null; session.PendingSeat = -1; session.PendingRoomExpires = default;
        RemoveEmptyRooms();
        if (room != null) LobbyChanged();
        session.Peer?.Send(new Envelope { type = "left" });
        return true;
    }
    static EntryMode ParseEntry(string entry) => entry == "post" ? EntryMode.PostBigBlind : entry == "wait" ? EntryMode.WaitForBigBlind : throw new ArgumentException("Choose post or wait.");
    static void SendChatHistory(Session session) => session.Peer?.Send(new Envelope {
        type = "chatHistory", tableId = session.Room.Id, chatMessages = session.Room.ChatMessages.ToArray()
    });
    static void SendChat(Session sender, string text, DateTime now)
    {
        string message = ChatRules.Validate(text);
        while (sender.ChatSentAt.Count > 0 && (now - sender.ChatSentAt.Peek()).TotalSeconds >= 10) sender.ChatSentAt.Dequeue();
        if (sender.ChatSentAt.Count >= 5) throw new InvalidOperationException("Chat is moving too quickly. Please wait a few seconds.");
        sender.ChatSentAt.Enqueue(now);
        Room room = sender.Room;
        var entry = new ChatMessageView { sequence = ++room.ChatSequence,
            sentUnixMs = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeMilliseconds(), username = sender.Name, message = message };
        room.ChatMessages.Add(entry);
        if (room.ChatMessages.Count > ChatRules.HistoryLimit) room.ChatMessages.RemoveAt(0);
        foreach (var session in room.Seats)
            session?.Peer?.Send(new Envelope { type = "chat", tableId = room.Id, chat = entry });
        // Chat deliberately does not mutate the game revision, actor or action deadline.
    }
    void Join(Session s, Command command, DateTime now)
    {
        if (s.Room != null) throw new InvalidOperationException("Leave your current table first.");
        var entry = ParseEntry(command.entryMode);
        var room = Rooms.FirstOrDefault(r => r.Id == command.tableId) ?? throw new ArgumentException("Unknown table.");
        if (s.PendingRoomId != null && s.PendingRoomId != room.Id)
            throw new InvalidOperationException("Accept or cancel your created table before joining another.");
        if (room.Game.Faulted) throw new InvalidOperationException("Table paused: secure hand recording failed.");
        if (!room.IsPublic && !CryptographicOperations.FixedTimeEquals(room.InviteCodeHash, HashInviteCode(ValidateInviteCode(command.inviteCode))))
            throw new InvalidOperationException("Incorrect invite code.");
        if (!IsValidChipAmount(command.buyInAmount))
            throw new ArgumentException("Buy-ins must use valid amounts with up to two decimal places.");
        command.buyInAmount = RoundChips(command.buyInAmount);
        if (command.buyInAmount < room.MinBuyIn || command.buyInAmount > room.MaxBuyIn)
            throw new ArgumentException("Choose a buy-in within this table's minimum and maximum.");
        int seat = s.PendingRoomId == room.Id && s.PendingSeat >= 0 && room.ReservedSeats[s.PendingSeat] ? s.PendingSeat : -1;
        for (int i = 0; seat < 0 && i < room.Seats.Length; i++) if (room.Seats[i] == null && !room.ReservedSeats[i]) { seat = i; break; }
        if (seat < 0) throw new InvalidOperationException("Table is full.");
        wallets.BuyIn(s.WalletId, room.Id, command.buyInAmount);
        if (s.PendingRoomId == room.Id)
        {
            room.ReservedSeats[seat] = false;
            s.PendingRoomId = null; s.PendingSeat = -1; s.PendingRoomExpires = default;
        }
        s.Room = room; s.Seat = seat; s.Entry = entry; s.Leaving = s.LeaveRequested = false; s.MissedBig = s.MissedSmall = false;
        s.CheckFold = false; s.AutoCallAmount = -1; s.LockedDrawHand = -1; s.LockedDiscards = null;
        room.Seats[seat] = s; var p = room.Game.Players[seat]; p.Name = s.Name; p.Stack = command.buyInAmount;
        p.Bet = p.Contributed = p.Payout = 0; p.DrawCount = 0; p.Folded = p.InHand = p.Scoop = false; p.Action = ""; p.Cards.Clear();
        room.Game.Note(s.Name + " joins seat " + (seat + 1) + (entry == EntryMode.PostBigBlind ? " · post big blind" : " · wait for big blind"));
        room.Game.RecordSeatUpdate(seat, "join");
        WalletChanged(s);
        Changed(room, now); LobbyChanged();
        SendChatHistory(s);
    }
    void Remove(Session s, bool reserveSeat = false)
    {
        Room room = s.Room; if (room == null) return;
        Player player = room.Game.Players[s.Seat];
        float balance = wallets.CashOut(s.WalletId, room.Id, player.Stack);
        room.ReservedSeats[s.Seat] = reserveSeat;
        room.Seats[s.Seat] = null; player.Stack = 0; player.Cards.Clear(); player.InHand = false;
        room.Game.RecordSeatUpdate(s.Seat, "leave");
        if (!room.Seats.Any(seat => seat != null)) { room.CompletedHands.Clear(); room.ArchivedHandNumber = -1; room.ChatMessages.Clear(); }
        s.Room = null; s.Seat = -1; s.Leaving = s.LeaveRequested = false;
        s.CheckFold = false; s.AutoCallAmount = -1; s.LockedDrawHand = -1; s.LockedDiscards = null;
        s.Peer?.Send(new Envelope { type = "wallet", walletBalance = balance });
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
        room.Deadline = DateTime.UtcNow.AddSeconds(actionSeconds);
        if (room.Game.Stage == Street.Complete)
        {
            CheckpointStacks(room);
            ArchiveCompletedHand(room);
            if (!room.AnnouncedComplete) { room.AnnouncedComplete = true; room.NextHand = DateTime.UtcNow.AddSeconds(showdownSeconds); }
        }
    }
    void CheckpointStacks(Room room)
    {
        for (int seat = 0; seat < room.Seats.Length; seat++)
        {
            Session session = room.Seats[seat];
            if (session != null) wallets.CheckpointStack(session.WalletId, room.Id, room.Game.Players[seat].Stack);
        }
    }
    bool CanStartWithQueuedFunding(Room room)
    {
        int eligible = 0;
        for (int seat = 0; seat < room.Seats.Length; seat++)
        {
            Session session = room.Seats[seat];
            if (session?.Peer == null || session.Leaving) continue;
            Player player = room.Game.Players[seat];
            if (player.Stack > 0 || (room.MaxBuyIn > player.Stack && wallets.GetBalance(session.WalletId) > 0
                && (session.PendingAddOn > 0 || session.AutoTopUpEnabled))) eligible++;
        }
        return eligible >= 2;
    }
    void ApplyQueuedFunding(Room room)
    {
        for (int seat = 0; seat < room.Seats.Length; seat++)
        {
            Session session = room.Seats[seat];
            if (session?.Peer == null || session.Leaving) continue;
            Player player = room.Game.Players[seat];
            float maximum = Math.Max(0, room.MaxBuyIn - player.Stack);
            float targetStack = player.Stack + Math.Min(session.PendingAddOn, maximum);
            if (session.AutoTopUpEnabled)
            {
                float target = session.AlwaysTopUpMax ? room.MaxBuyIn
                    : Math.Min(room.MaxBuyIn, session.AutoTopUpBigBlinds * room.Game.BigBlind);
                targetStack = Math.Max(targetStack, target);
            }
            float amount = RoundChips(Math.Min(Math.Max(0, targetStack - player.Stack),
                Math.Min(maximum, wallets.GetBalance(session.WalletId))));
            if (amount <= 0) { session.PendingAddOn = 0; continue; }
            try { wallets.Rebuy(session.WalletId, room.Id, amount, session.AutoTopUpEnabled ? "auto-top-up" : "add-on"); }
            catch (InvalidOperationException) { continue; }
            session.PendingAddOn = 0;
            player.Stack = RoundChips(player.Stack + amount);
            room.Game.RecordSeatUpdate(seat, "add-on");
            WalletChanged(session);
        }
    }
    void WalletChanged(Session session)
    {
        session.Peer?.Send(new Envelope { type = "wallet", walletBalance = wallets.GetBalance(session.WalletId) });
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
            float callAmount = room.Game.ToCall(seat);
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
            bool expiredCreate = false;
            foreach (Session session in sessions.Values.Where(s => s.PendingRoomId != null && s.PendingRoomExpires <= now).ToArray())
            {
                Room pendingRoom = Rooms.FirstOrDefault(room => room.Id == session.PendingRoomId);
                if (pendingRoom != null && session.PendingSeat >= 0 && session.PendingSeat < pendingRoom.ReservedSeats.Length)
                    pendingRoom.ReservedSeats[session.PendingSeat] = false;
                session.PendingRoomId = null; session.PendingSeat = -1; session.PendingRoomExpires = default;
                expiredCreate = true;
            }
            if (expiredCreate) { RemoveEmptyRooms(); LobbyChanged(); }
            foreach (var room in Rooms.ToArray())
            {
                if (room.Game.Faulted) continue;
                try
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
                for (int seat = 0; seat < room.ReservedSeats.Length; seat++)
                    if (!sessions.Values.Any(session => session.PendingRoomId == room.Id && session.PendingSeat == seat && session.PendingRoomExpires > now))
                        room.ReservedSeats[seat] = false;
                if (CanStartWithQueuedFunding(room)) ApplyQueuedFunding(room);
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
                catch (AuditFailureException)
                {
                    NotifyAuditFailure(room);
                }
            }
            if (RemoveEmptyRooms()) LobbyChanged();
            foreach (var pair in sessions.Where(p => p.Value.Peer == null && p.Value.Room == null && (now - p.Value.LastSeen).TotalMinutes > 20).ToArray()) sessions.Remove(pair.Key);
        }
    }
    public void RecordShutdown()
    {
        lock (gate)
            foreach (var room in Rooms)
                try { room.Game.RecordInterruption("server-shutdown"); }
                catch (AuditFailureException) { /* The table is already paused. */ }
    }
    static void NotifyAuditFailure(Room room)
    {
        foreach (var session in room.Seats)
            session?.Peer?.Send(new Envelope { type = "error", message = "Table paused: secure hand recording failed." });
    }
    bool RemoveEmptyRooms() => Rooms.RemoveAll(room => !room.Seats.Any(session => session != null) && !room.ReservedSeats.Any(reserved => reserved)) > 0;
    void Changed(Room room, DateTime now)
    {
        room.Revision++;
        BroadcastState(room, now);
    }
    static void BroadcastState(Room room, DateTime now)
    {
        foreach (var s in room.Seats) if (s?.Peer != null) s.Peer.Send(new Envelope { type = "state", table = room.View(s, DateTime.UtcNow) });
    }
    void LobbyChanged() { var rows = Rooms.Select(r => r.Lobby()).ToArray(); foreach (var s in sessions.Values) s.Peer?.Send(new Envelope { type = "lobby", tables = rows }); }
}
