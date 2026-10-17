using System;
using System.Collections.Generic;
using System.Linq;

namespace Drawmaha
{
    // The same checks can run in Unity or in a standalone C# console.
    public static class LogicChecks
    {
        static int assertions;
        static void Check(bool condition, string name)
        {
            assertions++; if (!condition) throw new Exception("FAILED: " + name);
        }
        static void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, name);
        }
        static Card[] Parse(string value)
        {
            return value.Split(' ').Select(s => new Card("23456789TJQKA".IndexOf(s[0]) + 2, "cdhs".IndexOf(s[1]))).ToArray();
        }
        static void Passive(Game g)
        {
            if (g.Stage == Street.Draw) g.Draw(g.Actor, new int[0]); else g.Call(g.Actor);
        }
        static void Until(Game g, Street street)
        {
            int guard = 0;
            while (g.Stage != street && g.Stage != Street.Complete && guard++ < 100) Passive(g);
            Check(g.Stage == street, "Reached " + street);
        }
        static void Replace(Player p, string cards) { p.Cards.Clear(); p.Cards.AddRange(Parse(cards)); }
        public static string Run()
        {
            assertions = 0;
            RankingChecks(); BettingChecks(); SettlementChecks(); SimulationChecks(); SixSeatChecks(); BlindChecks();
            return "PASS: " + assertions + " assertions; all 2,598,960 five-card hands checked; 1,500 randomized hands completed; six-seat draws and blind-orbit checks passed.";
        }
        static void RankingChecks()
        {
            string[] hands = { "Ac Jd 9h 6s 3c", "Ac Ad Jh 9s 3c", "Ac Ad Jh Js 3c", "Ac Ad Ah Js 3c", "9c 8d 7h 6s 5c", "Ac Jc 9c 6c 3c", "Ac Ad Ah Js Jc", "Ac Ad Ah As Jc", "Ac Kc Qc Jc Tc" };
            for (int i = 0; i < hands.Length; i++) Check(HandEvaluator.Five(Parse(hands[i])).Category == i, "Category " + i);
            for (int i = 1; i < hands.Length; i++) Check(HandEvaluator.Five(Parse(hands[i])).Score > HandEvaluator.Five(Parse(hands[i - 1])).Score, "Category ordering " + i);
            Check(HandEvaluator.Five(Parse("Ac 2d 3h 4s 5c")).Score < HandEvaluator.Five(Parse("2c 3d 4h 5s 6c")).Score, "Wheel ranks below six-high straight");
            Check(HandEvaluator.Five(Parse("Ac Ad Kh Qs Jc")).Score > HandEvaluator.Five(Parse("Ac Ad Kh Qs Tc")).Score, "Pair fifth-card kicker");
            Check(HandEvaluator.Five(Parse("Ac Ad Kh Ks 2c")).Score > HandEvaluator.Five(Parse("Ac Ad Qh Qs Jc")).Score, "Two pair lower pair before kicker");
            Check(HandEvaluator.Omaha(Parse("Ac 2d 3h 4s 5d"), Parse("Tc Jc Qc Kc 9c")).Category != 5, "One hole club cannot make Omaha flush");
            Check(HandEvaluator.Omaha(Parse("Ac 2c 3h 4s 5d"), Parse("Tc Jc Qc Kd 9s")).Category == 5, "Two hole clubs make Omaha flush");
            Check(HandEvaluator.Omaha(Parse("2d 3h 7s 8d 9h"), Parse("Tc Jc Qc Kc Ac")).Category < 8, "Cannot play royal flush on board");
            int[] counts = new int[9]; var deck = new List<Card>();
            for (int s = 0; s < 4; s++) for (int r = 2; r <= 14; r++) deck.Add(new Card(r, s));
            var five = new Card[5];
            for (int a = 0; a < 48; a++) for (int b = a + 1; b < 49; b++) for (int c = b + 1; c < 50; c++) for (int d = c + 1; d < 51; d++) for (int e = d + 1; e < 52; e++)
            { five[0] = deck[a]; five[1] = deck[b]; five[2] = deck[c]; five[3] = deck[d]; five[4] = deck[e]; counts[HandEvaluator.Five(five).Category]++; }
            int[] expected = { 1302540, 1098240, 123552, 54912, 10200, 5108, 3744, 624, 40 };
            for (int i = 0; i < 9; i++) Check(counts[i] == expected[i], "Exhaustive category frequency " + i);
        }
        static void BettingChecks()
        {
            var pendingAction = Game.CreateForTesting(16); pendingAction.StartHand();
            int bettor = pendingAction.Actor;
            pendingAction.RaiseTo(bettor, pendingAction.MinimumRaiseTo);
            Check(!pendingAction.IsAwaitingAction(bettor), "Bettor is not awaiting another action while opponents respond");
            Check(pendingAction.IsAwaitingAction(pendingAction.Actor), "Next player remains awaiting action after a bet");

            var g = Game.CreateForTesting(17); g.StartHand();
            Check(g.Dealer == 0 && g.SmallBlindSeat == 1 && g.BigBlindSeat == 2 && g.Actor == 3, "Four-seat blind order");
            Check(g.MaximumRaiseTo(3) == 35 && g.MinimumRaiseTo == 20, "Initial pot-limit bounds");
            Reject(() => g.RaiseTo(3, 36), "Over-pot raise rejected");
            Reject(() => g.RaiseTo(3, 15), "Under-minimum non-all-in rejected");
            Reject(() => g.Call(0), "Out-of-turn action rejected");
            g.Call(3); g.Call(0); g.Call(1);
            Check(g.Actor == 2 && g.ToCall(2) == 0 && g.Stage == Street.Preflop, "Big blind retains option");
            g.Call(2); Check(g.Stage == Street.Flop && g.Actor == 1, "Postflop starts left of button");
            Until(g, Street.Draw);
            Reject(() => g.Draw(g.Actor, new[] { 0, 0 }), "Duplicate discard rejected");
            Reject(() => g.Draw(g.Actor, new[] { 0, 1, 2 }), "More than two discards rejected");
            var before = g.Players[g.Actor].Cards.ToArray(); int first = g.Actor;
            g.Draw(first, new[] { 0, 1 });
            Check(g.Stage == Street.Draw && !before.SequenceEqual(g.Players[first].Cards), "Replacement cards arrive on the player's draw action");
            Check(before.Skip(2).SequenceEqual(g.Players[first].Cards.Skip(2)), "Undiscarded cards remain in place");
            while (g.Stage == Street.Draw) g.Draw(g.Actor, new[] { 0, 1 });
            Check(g.Board.Count == 4, "Full table continues after each player's draw");
            while (g.Stage != Street.Complete) Passive(g);
            Check(g.Players.Sum(p => p.Stack) == 4000, "Five-card draw conserves chips");

            var shortRaise = Game.CreateForTesting(2); shortRaise.Players[1].Stack = 25; shortRaise.StartHand();
            shortRaise.RaiseTo(3, 20); shortRaise.Call(0); shortRaise.RaiseTo(1, 25); shortRaise.Call(2);
            Check(shortRaise.Actor == 3 && !shortRaise.CanRaise(3), "Short all-in does not reopen prior raiser");
            shortRaise.Call(3); Check(!shortRaise.CanRaise(0), "Short all-in does not reopen prior caller"); shortRaise.Call(0);

            var hu = Game.CreateForTesting(3); hu.Players[1].Stack = hu.Players[2].Stack = 0; hu.StartHand();
            Check(hu.SmallBlindSeat == hu.Dealer && hu.Actor == hu.Dealer, "Heads-up button posts small blind and acts first");
            Until(hu, Street.Flop); Check(hu.Actor == hu.BigBlindSeat, "Heads-up big blind acts first postflop");

            var dry = Game.CreateForTesting(7); dry.Players[1].Stack = 5; dry.Players[2].Stack = 10; dry.StartHand();
            dry.Fold(3); Check(!dry.CanRaise(0), "No dry-side-pot raise"); dry.Call(0);
            Check(dry.Stage == Street.Draw, "All-in board auto-runs to draw");
            while (dry.Stage != Street.Complete) Passive(dry);
            Check(dry.Board.Count == 5 && dry.Players.Sum(p => p.Stack) == 2015, "All-in runout and draw conserve chips");
        }
        static void SettlementChecks()
        {
            var g = Game.CreateForTesting(12); g.StartHand(); Until(g, Street.River);
            g.Board.Clear(); g.Board.AddRange(Parse("Ac Kd Qh 7s 2c"));
            Replace(g.Players[0], "Ah Ad As 9d 8d"); // Draw quads, Omaha trip aces.
            Replace(g.Players[1], "Jc Tc 3d 4d 5h"); // Omaha Broadway, weaker draw.
            Replace(g.Players[2], "Kh Ks 6d 6c 8s");
            Replace(g.Players[3], "Qd Qs 5d 5c 9h");
            int[] pre = g.Players.Select(p => p.Stack).ToArray();
            int pot = g.Pot; while (g.Stage != Street.Complete) Passive(g);
            Check(g.LastPot == pot && g.Pot == 0, "Settled pot remains available after completion");
            Check(!g.Results.Any(r => r.StartsWith("Main pot")), "Single pot is not labelled main pot");
            Check(g.Results.Any(r => r.StartsWith("Omaha:")) && g.Results.Any(r => r.StartsWith("Draw:")), "Single-pot hand results use Omaha and Draw labels");
            Check(g.Players[0].Stack - pre[0] == pot / 2, "Draw half awarded to quads");
            Check(g.Players[1].Stack - pre[1] == pot / 2, "Omaha half awarded to Broadway");

            var side = Game.CreateForTesting(18); side.Players[0].Stack = 20; side.StartHand();
            side.RaiseTo(3, 35); side.Call(0); side.Call(1); side.Call(2); Until(side, Street.River);
            side.Board.Clear(); side.Board.AddRange(Parse("Ac Kd Qh 7s 2c"));
            Replace(side.Players[0], "Ah Ad As 9d 8d"); Replace(side.Players[1], "Jc Tc 3d 4d 5h"); Replace(side.Players[2], "Kh Ks 6d 6c 8s"); Replace(side.Players[3], "Qd Qs 5d 5c 9h");
            while (side.Stage != Street.Complete) Passive(side);
            Check(side.Players[0].Stack == 40, "Short stack wins only main draw half");
            Check(side.Results.Any(r => r.StartsWith("Main pot")), "Main pot is labelled when side pots exist");
            Check(side.Results.Any(r => r.StartsWith("Side pot")), "Side pots reported");
            Check(side.Results.Any(r => r.StartsWith("Main pot · Omaha:")) && side.Results.Any(r => r.StartsWith("Side pot 1 · Draw:")), "Layered pot results identify each pot and hand");
            Check(side.Players.Sum(p => p.Stack) == 3020, "Side-pot chip conservation");

            var tie = Game.CreateForTesting(19); tie.SmallBlind = 1; tie.BigBlind = 1; tie.StartHand(); Until(tie, Street.River);
            tie.Board.Clear(); tie.Board.AddRange(Parse("Ac Kd Qh 7s 2c"));
            Replace(tie.Players[0], "Jc Tc 9c 8c 6c"); Replace(tie.Players[1], "Jh Th 9h 8h 6h"); Replace(tie.Players[2], "3d 3c 4d 5d 6d"); Replace(tie.Players[3], "3h 3s 4h 5h 6s");
            int[] old = tie.Players.Select(p => p.Stack).ToArray();
            while (tie.Stage != Street.Complete) Passive(tie);
            Check(tie.Players[0].Stack == old[0] + 2 && tie.Players[1].Stack == old[1] + 2, "Ties split both halves");
            Check(tie.Players[2].Stack == old[2] && tie.Players[3].Stack == old[3], "Flush draw beats paired draw");

            var odd = Game.CreateForTesting(21); odd.SmallBlind = 1; odd.BigBlind = 3; odd.Players[3].Stack = 0; odd.StartHand(); Until(odd, Street.River);
            odd.Board.Clear(); odd.Board.AddRange(Parse("Ac Kd Qh 7s 2c"));
            Replace(odd.Players[0], "Jc Tc 9c 8c 6c"); Replace(odd.Players[1], "Jh Th 9h 8h 6h"); Replace(odd.Players[2], "3d 3c 4d 5d 6d");
            int[] oddBefore = odd.Players.Select(p => p.Stack).ToArray();
            Check(odd.Pot == 9, "Odd pot setup"); while (odd.Stage != Street.Complete) Passive(odd);
            Check(odd.Players[0].Stack - oddBefore[0] == 4 && odd.Players[1].Stack - oddBefore[1] == 5, "Odd pot chip to Omaha, tied odd chip left of button");

            var refund = Game.CreateForTesting(22); refund.Players[0].Stack = 20; refund.StartHand();
            refund.RaiseTo(3, 35); refund.Call(0); refund.Fold(1); refund.Fold(2);
            while (refund.Stage != Street.Complete) Passive(refund);
            Check(refund.Results.Any(r => r == "Uncalled 15 returned to Player4."), "Uncalled excess returned at showdown");
            Check(refund.Players.Sum(p => p.Stack) == 3020, "Refund plus folded dead money conserves chips");

            var fold = Game.CreateForTesting(1); fold.StartHand(); fold.Fold(3); fold.Fold(0); fold.Fold(1);
            Check(fold.Stage == Street.Complete && fold.Players[2].Stack == 1005 && fold.Pot == 0, "Uncontested pot settled once");
            Check(fold.LastPot == 15 && fold.Players[2].Payout == 15 && fold.Players[2].Scoop, "Uncontested winner receives scoop and settled pot is retained");

        }
        static void SimulationChecks()
        {
            for (int seed = 0; seed < 1000; seed++)
            {
                var random = new Random(seed + 4000); var g = Game.CreateForTesting(seed);
                foreach (var p in g.Players) p.Stack = random.Next(1, 301);
                int total = g.Players.Sum(p => p.Stack); g.StartHand(); int actions = 0;
                while (g.Stage != Street.Complete)
                {
                    Check(++actions < 150, "Simulation terminates seed " + seed);
                    Check(g.Actor >= 0 && g.Actor < 4, "Valid actor");
                    int seat = g.Actor;
                    if (g.Stage == Street.Draw)
                    {
                        g.Draw(seat, Enumerable.Range(0, 5).Where(i => random.NextDouble() < 0.6).Take(2).ToArray());
                    }
                    else if (random.NextDouble() < 0.12) g.Fold(seat);
                    else if (g.CanRaise(seat) && random.NextDouble() < 0.35)
                    {
                        int max = g.MaximumRaiseTo(seat), min = Math.Min(g.MinimumRaiseTo, max); g.RaiseTo(seat, random.Next(min, max + 1));
                    }
                    else g.Call(seat);
                    Check(g.Players.All(p => p.Stack >= 0), "Nonnegative stacks");
                    Check(g.Players.Sum(p => p.Stack) + g.Pot == total, "Chip conservation at each action");
                    var visible = g.Players.SelectMany(p => p.Cards).Concat(g.Board).ToArray();
                    Check(visible.Distinct().Count() == visible.Length, "No duplicated live cards");
                }
                Check(g.Players.Sum(p => p.Stack) == total, "Final chip conservation");
            }
            for (int seed = 0; seed < 20; seed++)
            {
                var g = Game.CreateForTesting(seed, 100);
                for (int n = 0; n < 30 && g.Players.Count(p => p.Stack > 0) > 1; n++)
                {
                    g.StartHand(); int actions = 0;
                    while (g.Stage != Street.Complete) { Check(++actions < 150, "Multi-hand session terminates"); Passive(g); }
                    Check(g.Players.Sum(p => p.Stack) == 400, "Multi-hand session conserves chips");
                }
            }
        }
        static void SixSeatChecks()
        {
            for (int seed = 0; seed < 500; seed++)
            {
                var random = new Random(seed + 811); var g = Game.CreateForTesting(seed, 100, 6); g.StartHand();
                int guard = 0; var discarded = new Dictionary<int, Card[]>();
                while (g.Stage != Street.Complete)
                {
                    Check(++guard < 200, "Six-seat hand terminates");
                    if (g.Stage == Street.Draw)
                    {
                        int seat = g.Actor;
                        var indices = seed < 100 ? Enumerable.Range(0, 2).ToArray() : Enumerable.Range(0, 5).Where(i => random.NextDouble() < 0.7).Take(2).ToArray();
                        discarded[seat] = indices.Select(i => g.Players[seat].Cards[i]).ToArray(); g.Draw(seat, indices);
                        if (g.Stage != Street.Draw)
                            foreach (var pair in discarded) Check(!g.Players[pair.Key].Cards.Intersect(pair.Value).Any(), "Cannot redraw own discards");
                    }
                    else if (seed >= 100 && random.NextDouble() < 0.15) g.Fold(g.Actor);
                    else if (seed >= 100 && g.CanRaise(g.Actor) && random.NextDouble() < 0.3) g.RaiseTo(g.Actor, g.MaximumRaiseTo(g.Actor));
                    else g.Call(g.Actor);
                    var cards = g.Players.SelectMany(p => p.Cards).Concat(g.Board).ToArray();
                    Check(cards.Distinct().Count() == cards.Length, "Six-seat fixed-deck live cards unique");
                    Check(g.Players.Sum(p => p.Stack) + g.Pot == 600, "Six-seat chip conservation");
                }
            }
            var uncontested = Game.CreateForTesting(4, 100, 6); uncontested.StartHand();
            while (uncontested.Stage != Street.Complete) uncontested.Fold(uncontested.Actor);
            Check(!uncontested.ShowdownReached, "Uncontested wins never trigger reveal");
        }
        static void BlindChecks()
        {
            var orbit = new BlindOrbit(); bool[] seats = { true, false, true, false, true, false };
            var entries = Enumerable.Repeat(EntryMode.Ready, 6).ToArray();
            Check(orbit.Plan(new[] { true, false, false, false, false, false }, entries) == null, "A lone player waits without starting a hand");
            var first = orbit.Plan(seats, entries); Check(first.Dealer == 0 && first.SmallBlind == 2 && first.BigBlind == 4, "Initial cash blind positions"); orbit.Commit(first);
            seats[1] = true; entries[1] = EntryMode.PostBigBlind;
            var join = orbit.Plan(seats, entries);
            Check(join.BigBlind == 0 && join.SmallBlind == 4 && join.Dealer == 2, "Joining does not reset incumbent blinds");
            Check(join.Active[1] && join.EntryPosts.ContainsKey(1), "Eligible newcomer may post"); orbit.Commit(join); entries[1] = EntryMode.Ready;
            var next = orbit.Plan(seats, entries); Check(next.BigBlind == 1 && next.SmallBlind == 0, "Blind orbit progresses through new seat");
            var blocked = new BlindOrbit(); blocked.Commit(first); seats[1] = false; seats[3] = true; entries[3] = EntryMode.PostBigBlind;
            var protectedSeat = blocked.Plan(seats, entries); Check(!protectedSeat.Active[3], "No entry between button and BB");
            entries[3] = EntryMode.WaitForBigBlind; bool admitted = false;
            for (int n = 0; n < 5; n++) { var p = blocked.Plan(seats, entries); if (p.BigBlind == 3) { Check(p.Active[3] && !p.EntryPosts.ContainsKey(3), "Waiting seat pays natural BB once"); admitted = true; break; } blocked.Commit(p); }
            Check(admitted, "Waiting seat eventually reaches BB");
            var dead = new BlindOrbit(); dead.Commit(first); bool[] missing = { true, false, true, false, false, false };
            var hu = dead.Plan(missing, Enumerable.Repeat(EntryMode.Ready, 6).ToArray()); Check(hu.BigBlind == 0 && hu.SmallBlind == 2 && hu.Dealer == 2, "Heads-up conversion advances BB");
            var multiDead = new BlindOrbit(); multiDead.Commit(first); missing[5] = true;
            var pDead = multiDead.Plan(missing, Enumerable.Repeat(EntryMode.Ready, 6).ToArray()); Check(pDead.BigBlind == 5 && pDead.SmallBlind == -1, "Departed SB is dead, not reassigned"); multiDead.Commit(pDead);
            var afterDead = multiDead.Plan(missing, Enumerable.Repeat(EntryMode.Ready, 6).ToArray()); Check(afterDead.Dealer == 4 && afterDead.SmallBlind == 5 && afterDead.BigBlind == 0, "Dead button preserves physical position");
            var rotation = new BlindOrbit(); bool[] full = Enumerable.Repeat(true, 6).ToArray();
            var seen = new List<int>();
            for (int n = 0; n < 12; n++) { var p = rotation.Plan(full, Enumerable.Repeat(EntryMode.Ready, 6).ToArray()); seen.Add(p.BigBlind); rotation.Commit(p); }
            Check(seen.Take(6).Distinct().Count() == 6 && seen.Skip(6).Distinct().Count() == 6, "Each seat owes one natural BB per complete fixed-seat orbit");
        }
    }
}
