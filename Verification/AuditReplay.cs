using System.Text.Json;
using Drawmaha;
using Drawmaha.Server;

// Verifies dealing, burns, betting/draw legality and settlement using the recorded deck.
// Does not claim that replay proves the original random source was unbiased.
public static class AuditReplay
{
    sealed class RecordedRandom : IRandomSource
    {
        readonly Queue<int> choices = new();
        public void Load(Card[] target)
        {
            if (choices.Count != 0 || target == null || target.Length != 52 || target.Distinct().Count() != 52 ||
                target.Any(c => c.Rank < 2 || c.Rank > 14 || c.Suit < 0 || c.Suit > 3))
                throw new InvalidDataException("Invalid recorded deck.");
            var working = Enumerable.Range(0, 52).Select(i => new Card(i % 13 + 2, i / 13)).ToArray();
            for (int i = 51; i > 0; i--)
            {
                int j = Array.FindIndex(working, 0, i + 1, c => c.Equals(target[i]));
                if (j < 0) throw new InvalidDataException("Invalid deck permutation.");
                choices.Enqueue(j); (working[i], working[j]) = (working[j], working[i]);
            }
        }
        public int NextBelow(int limit) => choices.Count > 0 ? choices.Dequeue() : throw new InvalidDataException("Unexpected shuffle.");
    }
    sealed class ComparisonAudit : IHandAudit
    {
        public readonly List<HandAuditEvent> Expected = new();
        public int Position;
        public void Record(HandAuditEvent actual)
        {
            if (Position >= Expected.Count) throw new InvalidDataException("Replay produced an unexpected event.");
            var expected = Expected[Position++];
            // Human-readable result text is not authoritative; numeric settlement is compared below.
            string detail = actual.Detail; actual.Detail = expected.Detail;
            bool equal = JsonSerializer.Serialize(actual, HandAuditStore.Json) == JsonSerializer.Serialize(expected, HandAuditStore.Json);
            actual.Detail = detail;
            if (!equal) throw new InvalidDataException($"Replay mismatch at event {Position}: {expected.Kind}.");
        }
    }

    public static int Verify(IEnumerable<AuditRecord> records, bool requireClosed = true)
    {
        var all = records.ToArray();
        if (all.Length < 1 || all[0].Kind != "stream-open" || all[0].Event != null)
            throw new InvalidDataException("Missing stream-open marker.");
        bool closed = all[^1].Kind == "stream-close" && all[^1].Event == null;
        if (requireClosed && !closed) throw new InvalidDataException("Stream is open, interrupted, or truncated; no closing marker.");
        if (all.Skip(1).Take(all.Length - (closed ? 2 : 1)).Any(r => r.Event == null || r.Kind != r.Event.Kind ||
            string.IsNullOrEmpty(r.Table) || !r.HandId.EndsWith($"/{r.Table}/{r.Event.Hand}", StringComparison.Ordinal)))
            throw new InvalidDataException("Invalid event metadata.");
        int settled = 0;
        foreach (var table in all.Where(r => r.Event != null).GroupBy(r => r.Table))
        {
            var audit = new ComparisonAudit(); audit.Expected.AddRange(table.Select(r => r.Event));
            var random = new RecordedRandom();
            var game = new Game(random, 0, audit.Expected[0].Players.Length, audit);
            bool interrupted = false;
            while (audit.Position < audit.Expected.Count)
            {
                int before = audit.Position;
                var entry = audit.Expected[before];
                if (interrupted) throw new InvalidDataException("Game continued after interruption.");
                // Names can change between commands; financial/card state cannot.
                for (int i = 0; i < game.Players.Length; i++) game.Players[i].Name = entry.Players[i].Name;
                switch (entry.Kind)
                {
                    case "hand-start":
                        // Buy-ins, departures and rebuys occur outside hands.
                        if (game.Stage != Street.Waiting && game.Stage != Street.Complete) throw new InvalidDataException("Overlapping hands.");
                        for (int i = 0; i < game.Players.Length; i++) game.Players[i].Stack = entry.Players[i].Stack;
                        game.SmallBlind = entry.SmallBlind; game.BigBlind = entry.BigBlind;
                        if (before + 1 >= audit.Expected.Count || audit.Expected[before + 1].Kind != "shuffle")
                            throw new InvalidDataException("Missing shuffle.");
                        random.Load(audit.Expected[before + 1].Cards);
                        game.StartHand(entry.Setup);
                        break;
                    case "fold": game.Fold(entry.Seat); break;
                    case "call": game.Call(entry.Seat); break;
                    case "raise": game.RaiseTo(entry.Seat, entry.Amount); break;
                    case "draw": game.Draw(entry.Seat, entry.Indices); break;
                    case "seat-update":
                        var player = game.Players[entry.Seat]; var changed = entry.Players[entry.Seat];
                        if (player.InHand || changed.InHand || changed.Cards.Length != 0 || changed.Stack < 0)
                            throw new InvalidDataException("Seat update changed an active hand.");
                        player.Stack = changed.Stack; player.Bet = changed.Bet; player.Contributed = changed.Contributed;
                        player.Payout = changed.Payout; player.Folded = changed.Folded; player.Cards.Clear();
                        game.RecordSeatUpdate(entry.Seat, entry.Detail);
                        break;
                    case "interrupted": game.RecordInterruption(entry.Detail); interrupted = true; break;
                    default: throw new InvalidDataException($"Unexpected event: {entry.Kind}.");
                }
                if (audit.Position <= before) throw new InvalidDataException("Replay did not advance.");
            }
            if (requireClosed && game.Stage != Street.Complete && !interrupted)
                throw new InvalidDataException("Active hand lacks an interruption marker.");
            settled += audit.Expected.Count(e => e.Kind == "settled");
        }
        return settled;
    }
}
