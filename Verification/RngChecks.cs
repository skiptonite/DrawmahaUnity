using System.Security.Cryptography;
using System.Text.Json;
using Drawmaha;
using Drawmaha.Server;

static class RngChecks
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Reject(Action action, string message)
    {
        try { action(); } catch { checks++; return; }
        throw new Exception(message);
    }
    sealed class CountingRandom : IRandomSource
    {
        public int Count;
        public int NextBelow(int limit) { Check(limit == 52 - Count, "Shuffle bounds"); Count++; return limit - 1; }
    }
    sealed class InvalidRandom : IRandomSource { public int NextBelow(int limit) => limit; }
    sealed class Capture : IHandAudit
    {
        public readonly List<HandAuditEvent> Events = new();
        public bool Fail;
        public void Record(HandAuditEvent entry) { if (Fail) throw new IOException("test"); Events.Add(entry); }
    }

    static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "stats")
            {
                int samples = args.Length == 2 ? int.Parse(args[1]) : 100000;
                if (samples < 10000 || samples > 1000000) throw new ArgumentException("Use 10000 to 1000000 decks.");
                var positions = new int[52, 52]; var adjacent = new int[52, 52]; var random = new SecureShuffleRandom();
                int[] previous = null; long repeats = 0;
                for (int sample = 0; sample < samples; sample++)
                {
                    var deck = new Deck(random);
                    var ids = Enumerable.Range(0, 52).Select(_ => deck.Take().GetHashCode()).ToArray();
                    if (ids.Distinct().Count() != 52) throw new Exception("Invalid permutation.");
                    for (int i = 0; i < 52; i++)
                    {
                        positions[i, ids[i]]++;
                        if (i > 0) adjacent[ids[i - 1], ids[i]]++;
                        if (previous != null && previous[i] == ids[i]) repeats++;
                    }
                    previous = ids;
                }
                double expected = samples / 52.0, total = 0, pairs = 0;
                var perPosition = new double[52];
                for (int i = 0; i < 52; i++) for (int j = 0; j < 52; j++)
                {
                    double delta = positions[i, j] - expected;
                    perPosition[i] += delta * delta / expected; total += delta * delta / expected;
                    if (i != j) { delta = adjacent[i, j] - expected; pairs += delta * delta / expected; }
                }
                Console.WriteLine(JsonSerializer.Serialize(new { decks = samples, positionChiSquare = total,
                    perPositionChiSquare = perPosition, adjacencyChiSquare = pairs,
                    consecutiveDeckSamePositionMatches = repeats, expectedSamePositionMatches = samples - 1,
                    note = "Diagnostics only. Cells within a deck are dependent; this is not a certification or a p-value." }));
                return 0;
            }
            if (args.Length > 0 && args[0] == "verify")
            {
                if (args.Length < 2 || args.Length > 3 || (args.Length == 3 && args[2] != "--allow-open"))
                    throw new ArgumentException("Usage: verify <audit.jsonl> [--allow-open]");
                string encoded = Environment.GetEnvironmentVariable("DRAWMAHA_AUDIT_KEY");
                byte[] key = string.IsNullOrWhiteSpace(encoded)
                    ? File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "development.key"))
                    : Convert.FromBase64String(encoded);
                try
                {
                    int hands = AuditReplay.Verify(HandAuditStore.ReadVerified(args[1], key), args.Length != 3);
                    Console.WriteLine($"Verified authenticated records and replayed {hands} settled hands. " +
                        (args.Length == 3 ? "Open-stream inspection: completeness is not asserted." : "Stream is closed."));
                    Console.WriteLine("Archive this file SHA-256 independently: " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))));
                }
                finally { CryptographicOperations.ZeroMemory(key); }
                return 0;
            }
            Run(); Console.WriteLine($"PASS: {checks} RNG, fixed-deck, audit authentication and replay checks."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
    }

    static void Run()
    {
        var count = new CountingRandom(); var deck = new Deck(count);
        var cards = Enumerable.Range(0, 52).Select(_ => deck.Take()).ToArray();
        Check(count.Count == 51 && cards.Distinct().Count() == 52 && deck.Remaining == 0, "Full deck permutation");
        Reject(() => deck.Take(), "Deck exhaustion must throw");
        Reject(() => deck.Burn(), "Burn exhaustion must throw");
        Check(count.Count == 51, "No additional shuffle on exhaustion");
        Reject(() => new Deck(null), "Null randomness rejected");
        Reject(() => new Deck(new InvalidRandom()), "Out-of-range randomness rejected");

        var capture = new Capture(); var game = new Game(new DeterministicTestRandom(123), 100, 6, capture);
        game.StartHand();
        while (game.Stage != Street.Complete)
            if (game.Stage == Street.Draw) game.Draw(game.Actor, new[] { 0, 1 }); else game.Call(game.Actor);
        Check(capture.Events.Count(e => e.Kind == "shuffle") == 1, "Only one shuffle");
        Check(capture.Events.Count(e => e.Kind == "deal") == 47 && capture.Events.Count(e => e.Kind == "burn") == 3, "Maximum legal hand consumes 50 cards");
        var consumed = capture.Events.Where(e => e.Kind == "deal" || e.Kind == "burn").ToArray();
        var shuffled = capture.Events.Single(e => e.Kind == "shuffle").Cards;
        Check(consumed.Select(e => e.Position).SequenceEqual(Enumerable.Range(0, 50)), "Monotonic deck positions");
        Check(consumed.Select(e => e.Cards[0]).SequenceEqual(shuffled.Take(50)), "Fixed shuffle consumed unchanged");
        var burns = consumed.Where(e => e.Kind == "burn").Select(e => e.Position).ToArray();
        Check(burns.SequenceEqual(new[] { 30, 46, 48 }), "Correct flop/turn/river burns");
        Check(game.Players.Sum(p => p.Stack) == 600, "Settlement conservation");
        capture.Fail = true;
        Reject(() => game.StartHand(), "Audit failure must throw");
        Check(game.Faulted, "Audit failure freezes game");
        Reject(() => game.StartHand(), "Faulted game cannot restart");

        var actionAudit = new Capture(); var actionGame = new Game(new SecureShuffleRandom(), 100, 6, actionAudit);
        actionGame.StartHand();
        int actor = actionGame.Actor;
        float stack = actionGame.Players[actor].Stack, pot = actionGame.Pot;
        actionAudit.Fail = true;
        Reject(() => actionGame.Call(actor), "Failed action audit throws before payment");
        Check(actionGame.Faulted && actionGame.Players[actor].Stack == stack && actionGame.Pot == pot, "Failed recording prevents financial action");
        Reject(() => actionGame.Fold(actor), "Frozen game rejects further actions");
        TimeSpan compensated = TimeSpan.Zero;
        var timed = new DeadlineAwareAudit(actionAudit, delay => compensated += delay);
        Reject(() => timed.Record(new HandAuditEvent()), "Timed sink propagates failures");
        Check(compensated > TimeSpan.Zero, "Storage delays compensate clocks even on failure");

        string directory = Path.Combine(Path.GetTempPath(), "drawmaha-rng-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            string path;
            using (var store = new HandAuditStore(directory, key))
            {
                path = store.FilePath;
                var replayGame = new Game(new SecureShuffleRandom(), 1000, 6, store.ForTable("test"));
                for (int hand = 0; hand < 8; hand++)
                {
                    var setup = new HandSetup { Dealer = hand % 6, SmallBlind = (hand + 1) % 6, BigBlind = (hand + 2) % 6, Active = Enumerable.Repeat(true, 6).ToArray() };
                    setup.EntryPosts[(hand + 3) % 6] = 10; setup.DeadPosts[(hand + 4) % 6] = 5;
                    replayGame.StartHand(setup);
                    bool raised = false;
                    while (replayGame.Stage != Street.Complete)
                    {
                        if (hand == 0) replayGame.Fold(replayGame.Actor);
                        else if (replayGame.Stage == Street.Draw) replayGame.Draw(replayGame.Actor, new[] { 0, 1 });
                        else if (!raised && replayGame.CanRaise(replayGame.Actor)) { replayGame.RaiseTo(replayGame.Actor, replayGame.MinimumRaiseTo); raised = true; }
                        else replayGame.Call(replayGame.Actor);
                    }
                }
                // Another table and an interrupted hand share the same authenticated stream.
                var interrupted = new Game(new SecureShuffleRandom(), 1000, 6, store.ForTable("other"));
                interrupted.StartHand(); interrupted.RecordInterruption("test-shutdown");
            }
            var records = HandAuditStore.ReadVerified(path, key).ToArray();
            Check(AuditReplay.Verify(records) == 8, "Encrypted records round-trip and replay");
            Check(records.Single(r => r.Kind == "shuffle" && r.Table == "other").Event.Cards.Distinct().Count() == 52, "Immutable card deserialization");
            Reject(() => HandAuditStore.ReadVerified(path, RandomNumberGenerator.GetBytes(32)).ToArray(), "Wrong key rejected");
            var lines = File.ReadAllLines(path);
            string changed = Path.Combine(directory, "changed.jsonl");
            var frame = JsonSerializer.Deserialize<AuditFrame>(lines[2], HandAuditStore.Json);
            frame.Ciphertext[0] ^= 1;
            var altered = (string[])lines.Clone(); altered[2] = JsonSerializer.Serialize(frame, HandAuditStore.Json);
            File.WriteAllLines(changed, altered);
            Reject(() => HandAuditStore.ReadVerified(changed, key).ToArray(), "Ciphertext modification rejected");
            File.WriteAllLines(changed, lines.Where((_, i) => i != 3));
            Reject(() => HandAuditStore.ReadVerified(changed, key).ToArray(), "Deleted middle record rejected");
            altered = (string[])lines.Clone(); (altered[2], altered[3]) = (altered[3], altered[2]);
            File.WriteAllLines(changed, altered);
            Reject(() => HandAuditStore.ReadVerified(changed, key).ToArray(), "Reordered records rejected");
            File.WriteAllLines(changed, lines.Take(lines.Length - 1));
            Reject(() => AuditReplay.Verify(HandAuditStore.ReadVerified(changed, key)), "Missing closing marker rejected");
            // Even a new valid encrypted stream cannot hide an altered deal from replay.
            records.First(r => r.Kind == "deal").Event.Cards[0] = new Card(14, 3);
            records.First(r => r.Kind == "deal").Event.Position = 51;
            Reject(() => AuditReplay.Verify(records), "Incorrect dealing rejected by replay");

            bool writeFails = false;
            using var failingStore = new HandAuditStore(key, (_, _) => { if (writeFails) throw new IOException("simulated durable storage failure"); });
            var failedGame = new Game(new SecureShuffleRandom(), 100, 6, failingStore.ForTable("failure"));
            writeFails = true;
            Reject(() => failedGame.StartHand(), "Durable sink failure propagates");
            Check(failedGame.Faulted, "Durable sink failure freezes table");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            // Only files in this newly created, uniquely named test directory are removed.
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
