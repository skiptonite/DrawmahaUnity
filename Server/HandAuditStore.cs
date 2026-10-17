using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Drawmaha;

namespace Drawmaha.Server;

// Linked into the offline verifier too. Never logs private audit contents.
public sealed class HandAuditStore : IDisposable
{
    public static readonly JsonSerializerOptions Json = CreateJson();
    static JsonSerializerOptions CreateJson()
    {
        var json = new JsonSerializerOptions { IncludeFields = true, IgnoreReadOnlyProperties = true };
        json.Converters.Add(new AuditCardConverter());
        json.Converters.Add(new AuditSetupConverter());
        return json;
    }
    readonly object gate = new();
    readonly FileStream file;
    readonly Action<AuditFrame, byte[]> persist;
    readonly byte[] key;
    readonly string stream = Guid.NewGuid().ToString("N");
    readonly string build = GetBuildId();
    static string GetBuildId()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return version + "/sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
    }
    long sequence;
    string previous = "";
    bool failed, disposed;
    public string FilePath { get; }

    public HandAuditStore(string directory, byte[] masterKey)
    {
        if (masterKey == null || masterKey.Length != 32) throw new ArgumentException("Audit key must contain 32 bytes.");
        directory = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        key = DeriveKey(masterKey, stream);
        FilePath = Path.Combine(directory, $"audit-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{stream}.jsonl");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        file = new FileStream(FilePath, options);
        persist = (_, line) => { file.Write(line); file.WriteByte((byte)'\n'); file.Flush(flushToDisk: true); };
        try { Append("", null, "stream-open"); }
        catch { file.Dispose(); CryptographicOperations.ZeroMemory(key); throw; }
    }

    public HandAuditStore(byte[] masterKey, Action<AuditFrame, byte[]> durableAppend)
    {
        if (masterKey == null || masterKey.Length != 32) throw new ArgumentException("Audit key must contain 32 bytes.");
        persist = durableAppend ?? throw new ArgumentNullException(nameof(durableAppend));
        key = DeriveKey(masterKey, stream);
        try { Append("", null, "stream-open"); }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
    }

    public static byte[] DeriveKey(byte[] masterKey, string streamId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, Encoding.UTF8.GetBytes(streamId), "Drawmaha audit v1"u8.ToArray());
    public static byte[] AssociatedData(AuditFrame frame) => Encoding.UTF8.GetBytes(
        FormattableString.Invariant($"{frame.Version}|{frame.Stream}|{frame.Sequence}|{frame.Previous}"));

    public IHandAudit ForTable(string table) => new TableAudit(this, table);
    sealed class TableAudit(HandAuditStore store, string table) : IHandAudit
    { public void Record(HandAuditEvent entry) => store.Append(table, entry, entry.Kind); }

    void Append(string table, HandAuditEvent entry, string kind)
    {
        lock (gate)
        {
            if (failed || disposed) throw new IOException("Secure audit storage unavailable.");
            byte[] plaintext = null;
            try
            {
                var record = new AuditRecord { Utc = DateTimeOffset.UtcNow, Build = build, Table = table,
                    HandId = entry == null ? null : $"{stream}/{table}/{entry.Hand}", Kind = kind, Event = entry };
                plaintext = JsonSerializer.SerializeToUtf8Bytes(record, Json);
                var frame = new AuditFrame { Version = 1, Stream = stream, Sequence = sequence, Previous = previous,
                    Nonce = new byte[12], Ciphertext = new byte[plaintext.Length], Tag = new byte[16] };
                BinaryPrimitives.WriteInt64BigEndian(frame.Nonce.AsSpan(4), sequence);
                using (var aes = new AesGcm(key, 16))
                    aes.Encrypt(frame.Nonce, plaintext, frame.Ciphertext, frame.Tag, AssociatedData(frame));
                byte[] line = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
                persist(frame, line);
                previous = Convert.ToHexString(SHA256.HashData(line)); sequence = checked(sequence + 1);
            }
            catch { failed = true; throw; }
            finally { if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            try { if (!failed) Append("", null, "stream-close"); }
            finally { disposed = true; file?.Dispose(); CryptographicOperations.ZeroMemory(key); }
        }
    }

    public static IEnumerable<AuditRecord> ReadVerified(string path, byte[] masterKey)
    {
        string expectedPrevious = "", expectedStream = null;
        long expectedSequence = 0;
        byte[] readKey = null;
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                var frame = JsonSerializer.Deserialize<AuditFrame>(line, Json) ?? throw new InvalidDataException("Missing audit frame.");
                if (frame.Version != 1 || frame.Sequence != expectedSequence || frame.Previous != expectedPrevious ||
                    frame.Stream == null || (expectedStream != null && frame.Stream != expectedStream))
                    throw new InvalidDataException("Audit sequence or chain mismatch.");
                if (readKey == null) { expectedStream = frame.Stream; readKey = DeriveKey(masterKey, expectedStream); }
                byte[] plaintext = new byte[frame.Ciphertext.Length];
                AuditRecord record;
                try
                {
                    using var aes = new AesGcm(readKey, 16);
                    aes.Decrypt(frame.Nonce, frame.Ciphertext, frame.Tag, plaintext, AssociatedData(frame));
                    record = JsonSerializer.Deserialize<AuditRecord>(plaintext, Json) ?? throw new InvalidDataException("Missing audit record.");
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
                expectedPrevious = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
                expectedSequence++;
                yield return record;
            }
            if (expectedSequence == 0) throw new InvalidDataException("Empty audit stream.");
        }
        finally { if (readKey != null) CryptographicOperations.ZeroMemory(readKey); }
    }
}

public sealed class AuditFrame
{
    public int Version;
    public string Stream, Previous;
    public long Sequence;
    public byte[] Nonce, Ciphertext, Tag;
}
public sealed class AuditRecord
{
    public DateTimeOffset Utc;
    public string Build, Table, HandId, Kind;
    public HandAuditEvent Event;
}

sealed class AuditCardConverter : JsonConverter<Card>
{
    public override Card Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return new Card(doc.RootElement.GetProperty("Rank").GetInt32(), doc.RootElement.GetProperty("Suit").GetInt32());
    }
    public override void Write(Utf8JsonWriter writer, Card value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteNumber("Rank", value.Rank); writer.WriteNumber("Suit", value.Suit); writer.WriteEndObject();
    }
}
sealed class AuditSetupConverter : JsonConverter<HandSetup>
{
    public override HandSetup Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader); var root = doc.RootElement;
        var setup = new HandSetup { Dealer = root.GetProperty("Dealer").GetInt32(),
            SmallBlind = root.GetProperty("SmallBlind").GetInt32(), BigBlind = root.GetProperty("BigBlind").GetInt32(),
            Active = root.GetProperty("Active").Deserialize<bool[]>(options) };
        foreach (var post in root.GetProperty("EntryPosts").EnumerateObject()) setup.EntryPosts.Add(int.Parse(post.Name, System.Globalization.CultureInfo.InvariantCulture), post.Value.GetSingle());
        foreach (var post in root.GetProperty("DeadPosts").EnumerateObject()) setup.DeadPosts.Add(int.Parse(post.Name, System.Globalization.CultureInfo.InvariantCulture), post.Value.GetSingle());
        return setup;
    }
    public override void Write(Utf8JsonWriter writer, HandSetup value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteNumber("Dealer", value.Dealer);
        writer.WriteNumber("SmallBlind", value.SmallBlind); writer.WriteNumber("BigBlind", value.BigBlind);
        writer.WritePropertyName("Active"); JsonSerializer.Serialize(writer, value.Active, options);
        writer.WritePropertyName("EntryPosts"); JsonSerializer.Serialize(writer, value.EntryPosts, options);
        writer.WritePropertyName("DeadPosts"); JsonSerializer.Serialize(writer, value.DeadPosts, options);
        writer.WriteEndObject();
    }
}
