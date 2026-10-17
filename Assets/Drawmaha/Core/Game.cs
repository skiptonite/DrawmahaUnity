using System;
using System.Collections.Generic;
using System.Linq;

namespace Drawmaha
{
    public sealed class HandSetup
    {
        public int Dealer, SmallBlind, BigBlind;
        public bool[] Active;
        public readonly Dictionary<int, float> EntryPosts = new Dictionary<int, float>();
        public readonly Dictionary<int, float> DeadPosts = new Dictionary<int, float>();
    }
    public enum Street { Waiting, Preflop, Flop, Draw, Turn, River, Complete }
    public sealed class Player
    {
        public string Name;
        public float Stack, Bet, Contributed, Payout;
        public int DrawCount;
        public bool Folded, InHand;
        public bool Scoop;
        public readonly List<Card> Cards = new List<Card>();
        public string Action = "";
        public Player(string name, float stack) { Name = name; Stack = stack; }
        public bool CanAct { get { return InHand && !Folded && Stack > 0; } }
    }

    // Pure C#: independent of scenes, UI, Unity APIs, and bot decisions.
    public sealed class Game
    {
        public readonly Player[] Players;
        public readonly List<Card> Board = new List<Card>();
        public readonly List<string> Log = new List<string>();
        public readonly List<string> Results = new List<string>();
        public Street Stage { get; private set; }
        public int Actor { get; private set; } = -1;
        public int Dealer { get; private set; } = -1;
        public int SmallBlindSeat { get; private set; }
        public int BigBlindSeat { get; private set; }
        public float CurrentBet { get; private set; }
        public float LastRaise { get; private set; }
        public int HandNumber { get; private set; }
        public bool ShowdownReached { get; private set; }
        public float LastPot { get; private set; }
        public bool IsAwaitingAction(int seat) => IsBetting && seat >= 0 && seat < Players.Length && pending.Contains(seat) && Players[seat].CanAct;
        float contestablePot;
        public float SmallBlind = 5, BigBlind = 10;
        public float Pot { get { return RoundChips(Players.Sum(p => p.Contributed)); } }
        public bool IsBetting { get { return Stage == Street.Preflop || Stage == Street.Flop || Stage == Street.Turn || Stage == Street.River; } }
        readonly IRandomSource random;
        readonly IHandAudit audit;
        public bool Faulted { get; private set; }
        Deck deck;
        readonly HashSet<int> pending = new HashSet<int>();
        readonly float[] actedAt;

        public static Game CreateForTesting(int seed, float startingStack = 1000, int seatCount = 4)
        { return new Game(new DeterministicTestRandom(seed), startingStack, seatCount); }
        public Game(IRandomSource shuffleRandom, float startingStack = 0, int seatCount = 6, IHandAudit audit = null)
        {
            if (seatCount < 2 || seatCount > 6) throw new ArgumentOutOfRangeException("seatCount");
            random = shuffleRandom ?? throw new ArgumentNullException("shuffleRandom");
            this.audit = audit;
            Players = Enumerable.Range(0, seatCount).Select(i => new Player("Player" + (i + 1), startingStack)).ToArray();
            actedAt = new float[Players.Length];
        }
        void RequireHealthy()
        { if (Faulted) throw new InvalidOperationException("Table paused: secure hand recording failed."); }
        void Audit(string kind, int seat = -1, float amount = 0, int[] indices = null,
            Card[] cards = null, int position = -1, HandSetup setup = null, string detail = null)
        {
            if (audit == null) return;
            RequireHealthy();
            try
            {
                audit.Record(new HandAuditEvent {
                    Kind = kind, Hand = HandNumber, Stage = Stage.ToString(), Seat = seat, Amount = amount,
                    Indices = indices == null ? null : (int[])indices.Clone(), Cards = cards, Position = position,
                    Setup = setup, Detail = detail, Board = Board.ToArray(), Dealer = Dealer,
                    SmallBlindSeat = SmallBlindSeat, BigBlindSeat = BigBlindSeat, SmallBlind = SmallBlind,
                    BigBlind = BigBlind, Actor = Actor, CurrentBet = CurrentBet, LastRaise = LastRaise,
                    Players = Players.Select((p, i) => new AuditPlayer { Seat = i, Name = p.Name, Stack = p.Stack,
                        Bet = p.Bet, Contributed = p.Contributed, Payout = p.Payout, InHand = p.InHand,
                        Folded = p.Folded, Cards = p.Cards.ToArray() }).ToArray()
                });
            }
            catch (Exception e) { Faulted = true; throw new AuditFailureException(e); }
        }
        public void RecordInterruption(string reason)
        { if (!Faulted && Stage != Street.Waiting && Stage != Street.Complete) Audit("interrupted", detail: reason); }
        public void RecordSeatUpdate(int seat, string reason)
        { if (Stage != Street.Waiting && Stage != Street.Complete) Audit("seat-update", seat, detail: reason); }
        int Next(int from, Func<int, bool> predicate)
        {
            for (int n = 1; n <= Players.Length; n++) { int i = (from + n + Players.Length) % Players.Length; if (predicate(i)) return i; }
            return -1;
        }
        public void Note(string message) { Log.Add(message); if (Log.Count > 160) Log.RemoveAt(0); }
        public void StartHand() { StartHand(null); }
        public void ResetCompletedHandForWaiting()
        {
            if (Stage != Street.Complete) throw new InvalidOperationException("Only a completed hand can be reset.");
            Board.Clear(); Results.Clear(); pending.Clear(); Actor = -1; Stage = Street.Waiting;
            CurrentBet = 0; LastRaise = BigBlind; LastPot = contestablePot = 0; ShowdownReached = false;
            foreach (var player in Players)
            {
                player.Bet = player.Contributed = player.Payout = 0;
                player.DrawCount = 0;
                player.Folded = player.InHand = player.Scoop = false; player.Cards.Clear(); player.Action = "Waiting";
            }
        }
        public void StartHand(HandSetup setup)
        {
            RequireHealthy();
            if (Stage != Street.Waiting && Stage != Street.Complete) throw new InvalidOperationException("Finish the current hand first.");
            if (Players.Count(p => p.Stack > 0) < 2) throw new InvalidOperationException("Start a new table: fewer than two funded players.");
            if (setup != null && (setup.Active == null || setup.Active.Length != Players.Length || setup.Active.Count(a => a) < 2 || setup.BigBlind < 0 || setup.BigBlind >= Players.Length || !setup.Active[setup.BigBlind])) throw new ArgumentException("Invalid hand setup.");
            HandNumber++; ShowdownReached = false; LastPot = contestablePot = 0; Board.Clear(); Results.Clear(); pending.Clear();
            for (int i = 0; i < Players.Length; i++)
            {
                var p = Players[i]; p.InHand = p.Stack > 0 && (setup == null || setup.Active[i]); p.Folded = false; p.Bet = p.Contributed = p.Payout = 0; p.DrawCount = 0; p.Scoop = false; p.Cards.Clear(); p.Action = p.InHand ? "" : "Waiting";
            }
            Dealer = setup == null ? Next(Dealer, i => Players[i].InHand) : setup.Dealer;
            SmallBlindSeat = setup == null ? (Players.Count(p => p.InHand) == 2 ? Dealer : Next(Dealer, i => Players[i].InHand)) : setup.SmallBlind;
            BigBlindSeat = setup == null ? Next(SmallBlindSeat, i => Players[i].InHand) : setup.BigBlind;
            Stage = Street.Preflop; Actor = -1; CurrentBet = 0; LastRaise = BigBlind;
            Audit("hand-start", setup: setup);
            deck = new Deck(random, (kind, cards, position) => Audit(kind, cards: cards, position: position));
            int seat = Dealer;
            for (int r = 0; r < 5; r++) for (int n = 0; n < Players.Count(p => p.InHand); n++)
            {
                seat = Next(seat, i => Players[i].InHand); Players[seat].Cards.Add(deck.Take());
            }
            if (SmallBlindSeat >= 0 && Players[SmallBlindSeat].InHand) { Pay(SmallBlindSeat, SmallBlind); Players[SmallBlindSeat].Action = "Small blind"; }
            Pay(BigBlindSeat, BigBlind); Players[BigBlindSeat].Action = "Big blind";
            if (setup != null) foreach (var post in setup.EntryPosts)
                if (post.Key != BigBlindSeat && post.Key != SmallBlindSeat && Players[post.Key].InHand) { Pay(post.Key, post.Value); Players[post.Key].Action = "Entry big blind"; }
            if (setup != null) foreach (var post in setup.DeadPosts)
                if (Players[post.Key].InHand)
                {
                    float paid = RoundChips(Math.Min(post.Value, Players[post.Key].Stack));
                    Players[post.Key].Stack = RoundChips(Players[post.Key].Stack - paid);
                    Players[post.Key].Contributed = RoundChips(Players[post.Key].Contributed + paid);
                }
            CurrentBet = BigBlind; LastRaise = BigBlind; Stage = Street.Preflop;
            Note("Hand " + HandNumber + " · button at seat " + (Dealer + 1) + ".");
            BeginRound(BigBlindSeat, false);
        }
        void Pay(int seat, float amount)
        {
            var p = Players[seat]; float paid = RoundChips(Math.Min(amount, p.Stack));
            p.Stack = RoundChips(p.Stack - paid);
            p.Bet = RoundChips(p.Bet + paid);
            p.Contributed = RoundChips(p.Contributed + paid);
        }
        public float ToCall(int seat) { return RoundChips(Math.Min(Players[seat].Stack, Math.Max(0, CurrentBet - Players[seat].Bet))); }
        public float MinimumRaiseTo { get { return RoundChips(CurrentBet + LastRaise); } }
        public float MaximumRaiseTo(int seat) { return RoundChips(Math.Min(Players[seat].Bet + Players[seat].Stack, CurrentBet + Pot + Math.Max(0, CurrentBet - Players[seat].Bet))); }
        public bool CanRaise(int seat)
        {
            if (!IsBetting || Actor != seat || !Players[seat].CanAct || Players.Count(p => p.CanAct) < 2) return false;
            if (actedAt[seat] >= 0 && CurrentBet - actedAt[seat] < LastRaise) return false;
            float max = MaximumRaiseTo(seat);
            return max > CurrentBet && (max >= MinimumRaiseTo || max == Players[seat].Bet + Players[seat].Stack);
        }
        void RequireTurn(int seat)
        {
            RequireHealthy();
            if (Actor != seat || !IsBetting || !Players[seat].CanAct) throw new InvalidOperationException("Not this player's betting turn.");
        }
        public void Fold(int seat)
        {
            RequireTurn(seat); Audit("fold", seat);
            Players[seat].Folded = true; Players[seat].Action = "Fold"; Note(Players[seat].Name + " folds."); FinishAction(seat);
        }
        public void Call(int seat)
        {
            RequireTurn(seat); Audit("call", seat, ToCall(seat));
            float amount = ToCall(seat); Pay(seat, amount); actedAt[seat] = CurrentBet;
            Players[seat].Action = amount == 0 ? "Check" : "Call " + amount;
            if (Players[seat].Stack == 0) Players[seat].Action += " · all in";
            Note(Players[seat].Name + " · " + Players[seat].Action); FinishAction(seat);
        }
        public void RaiseTo(int seat, float total)
        {
            RequireTurn(seat);
            if (float.IsNaN(total) || float.IsInfinity(total)) throw new ArgumentException("Enter a finite raise amount.");
            total = RoundChips(total);
            if (!CanRaise(seat) || total <= CurrentBet || total > MaximumRaiseTo(seat)) throw new ArgumentException("Illegal pot-limit raise.");
            bool allIn = total == Players[seat].Bet + Players[seat].Stack;
            if (total < MinimumRaiseTo && !allIn) throw new ArgumentException("Raise below the minimum.");
            Audit("raise", seat, total);
            float increase = total - CurrentBet; bool fullRaise = increase >= LastRaise;
            Pay(seat, total - Players[seat].Bet); CurrentBet = total;
            if (fullRaise) LastRaise = increase;
            actedAt[seat] = CurrentBet;
            foreach (int i in Enumerable.Range(0, Players.Length))
                if (i != seat && Players[i].CanAct && (fullRaise || Players[i].Bet < CurrentBet)) pending.Add(i);
            Players[seat].Action = "Raise to " + total + (allIn ? " · all in" : ""); Note(Players[seat].Name + " · " + Players[seat].Action); FinishAction(seat);
        }
        void FinishAction(int seat)
        {
            pending.Remove(seat); pending.RemoveWhere(i => !Players[i].CanAct);
            var live = Enumerable.Range(0, Players.Length).Where(i => Players[i].InHand && !Players[i].Folded).ToArray();
            if (live.Length == 1)
            {
                float amount = Pot; contestablePot = amount;
                Players[live[0]].Stack = RoundChips(Players[live[0]].Stack + amount);
                Players[live[0]].Payout = RoundChips(Players[live[0]].Payout + amount);
                Results.Add(Players[live[0]].Name + " wins " + amount + " uncontested."); Complete(); return;
            }
            // An unmatched lone stack still gets its call/fold decision; no dry-side-pot bets.
            if (Players.Count(p => p.CanAct) == 1) pending.RemoveWhere(i => Players[i].Bet >= CurrentBet);
            Actor = Next(seat, i => pending.Contains(i));
            if (Actor < 0) Advance();
        }
        void BeginRound(int from, bool reset)
        {
            if (reset) { foreach (var p in Players) p.Bet = 0; CurrentBet = 0; LastRaise = BigBlind; }
            pending.Clear();
            for (int i = 0; i < Players.Length; i++) { actedAt[i] = -1; if (Players[i].CanAct) pending.Add(i); }
            if (pending.Count == 1) pending.RemoveWhere(i => Players[i].Bet >= CurrentBet);
            Actor = Next(from, i => pending.Contains(i)); if (Actor < 0) Advance();
        }
        void DealBoard(int count) { deck.Burn(); for (int n = 0; n < count; n++) Board.Add(deck.Take()); }
        void Advance()
        {
            if (Stage == Street.Preflop) { DealBoard(3); Stage = Street.Flop; Note("Flop: " + string.Join(" ", Board)); BeginRound(Dealer, true); }
            else if (Stage == Street.Flop)
            {
                Stage = Street.Draw; pending.Clear();
                for (int i = 0; i < Players.Length; i++) if (Players[i].InHand && !Players[i].Folded) pending.Add(i);
                Actor = Next(Dealer, i => pending.Contains(i)); Note("Draw: announce 0–2 discards. Replacements are dealt immediately.");
            }
            else if (Stage == Street.Turn) { DealBoard(1); Stage = Street.River; Note("River: " + Board[4]); BeginRound(Dealer, true); }
            else if (Stage == Street.River) Showdown();
        }
        public void Draw(int seat, int[] indices)
        {
            RequireHealthy();
            if (Stage != Street.Draw || Actor != seat) throw new InvalidOperationException("Not this player's draw turn.");
            ValidateDraw(indices);
            Card[] forbidden = indices.Select(i => Players[seat].Cards[i]).ToArray();
            Audit("draw", seat, indices: indices, cards: forbidden);
            foreach (int index in indices) Players[seat].Cards[index] = deck.Take();
            Players[seat].DrawCount = indices.Length; Players[seat].Action = indices.Length == 0 ? "Stand pat" : "Draw " + indices.Length;
            Note(Players[seat].Name + " · " + Players[seat].Action); pending.Remove(seat); Actor = Next(seat, i => pending.Contains(i));
            if (Actor >= 0) return;
            DealBoard(1); Stage = Street.Turn; Note("Turn: " + Board[3]); BeginRound(Dealer, true);
        }
        public static void ValidateDraw(int[] indices)
        {
            if (indices == null || indices.Length > 2 || indices.Distinct().Count() != indices.Length || indices.Any(i => i < 0 || i >= 5))
                throw new ArgumentException("Draw up to two distinct cards.");
        }
        void Complete()
        {
            foreach (var message in Results) Note(message);
            LastPot = Pot;
            foreach (var p in Players) { p.Scoop = p.Payout > 0 && p.Payout == contestablePot; p.Bet = 0; p.Contributed = 0; }
            Stage = Street.Complete; Actor = -1; pending.Clear();
            Audit("settled", detail: string.Join(" | ", Results));
        }
        void Showdown()
        {
            ShowdownReached = true;
            var hole = Players.Select(p => p.InHand && !p.Folded ? HandEvaluator.Five(p.Cards).Score : -1).ToArray();
            var omaha = Players.Select(p => p.InHand && !p.Folded ? HandEvaluator.Omaha(p.Cards, Board).Score : -1).ToArray();
            var levels = Players.Select(p => p.Contributed).Where(c => c > 0).Distinct().OrderBy(c => c).ToArray();
            int contestableLevels = levels.Count(level => Players.Count(p => p.Contributed >= level) > 1);
            float previous = 0; int number = 0;
            foreach (float level in levels)
            {
                int[] contributors = Enumerable.Range(0, Players.Length).Where(i => Players[i].Contributed >= level).ToArray();
                float amount = RoundChips((level - previous) * contributors.Length); previous = level;
                if (contributors.Length == 1)
                {
                    Players[contributors[0]].Stack = RoundChips(Players[contributors[0]].Stack + amount);
                    Results.Add("Uncalled " + amount + " returned to " + Players[contributors[0]].Name + "."); continue;
                }
                int[] eligible = contributors.Where(i => Players[i].InHand && !Players[i].Folded).ToArray();
                if (eligible.Length == 0) throw new InvalidOperationException("A pot has no eligible winner.");
                contestablePot += amount;
                string label = contestableLevels > 1
                    ? (number == 0 ? "Main pot · " : "Side pot " + number + " · ")
                    : "";
                number++;
                int amountCents = ToCents(amount);
                Award((amountCents + 1) / 2 / 100f, eligible, omaha, label + "Omaha");
                Award(amountCents / 2 / 100f, eligible, hole, label + "Draw");
            }
            Complete();
        }
        void Award(float amount, int[] eligible, int[] scores, string label)
        {
            int best = eligible.Max(i => scores[i]); var winners = eligible.Where(i => scores[i] == best).OrderBy(i => (i - Dealer - 1 + Players.Length) % Players.Length).ToArray();
            int amountCents = ToCents(amount), shareCents = amountCents / winners.Length, remainderCents = amountCents % winners.Length;
            for (int n = 0; n < winners.Length; n++)
            {
                float payout = (shareCents + (n < remainderCents ? 1 : 0)) / 100f;
                Players[winners[n]].Stack = RoundChips(Players[winners[n]].Stack + payout);
                Players[winners[n]].Payout = RoundChips(Players[winners[n]].Payout + payout);
            }
            Results.Add(label + ": " + string.Join(" + ", winners.Select(i => Players[i].Name)) + " win " + amount + ".");
        }
        static float RoundChips(float amount) => (float)Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        static int ToCents(float amount) => (int)Math.Round(amount * 100, MidpointRounding.AwayFromZero);
    }
}
