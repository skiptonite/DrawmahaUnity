using System.Security.Cryptography;
using System.Text;
using Azure.Data.Tables;
using Azure.Identity;

namespace Drawmaha.Server;

public static class AuditConfiguration
{
    public static HandAuditStore Create(IConfiguration configuration, IHostEnvironment environment)
    {
        string directory = configuration["DRAWMAHA_AUDIT_DIRECTORY"];
        string encodedKey = configuration["DRAWMAHA_AUDIT_KEY"];
        string endpoint = configuration["DRAWMAHA_TABLES_ENDPOINT"];
        byte[] key;
        if (environment.IsProduction() && (string.IsNullOrWhiteSpace(encodedKey) ||
            (string.IsNullOrWhiteSpace(directory) && string.IsNullOrWhiteSpace(endpoint))))
            throw new InvalidOperationException("Production requires DRAWMAHA_AUDIT_KEY and durable audit storage.");
        if (string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(endpoint))
        {
            if (string.IsNullOrWhiteSpace(encodedKey)) throw new InvalidOperationException("Azure audits require DRAWMAHA_AUDIT_KEY.");
            key = Convert.FromBase64String(encodedKey);
            try
            {
                var credentials = new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = configuration["AZURE_CLIENT_ID"] });
                var table = new TableClient(new Uri(endpoint), "handaudits", credentials);
                table.CreateIfNotExists();
                return new HandAuditStore(key, (frame, line) => table.AddEntity(new TableEntity(
                    frame.Stream, frame.Sequence.ToString("D20", System.Globalization.CultureInfo.InvariantCulture))
                    { ["Frame"] = Encoding.UTF8.GetString(line) }));
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        directory = string.IsNullOrWhiteSpace(directory) ? Path.Combine(environment.ContentRootPath, ".drawmaha-audit") : directory;
        if (!string.IsNullOrWhiteSpace(encodedKey)) key = Convert.FromBase64String(encodedKey);
        else
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string keyPath = Path.Combine(directory, "development.key");
            if (!File.Exists(keyPath))
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var file = new FileStream(keyPath, options);
                byte[] generated = RandomNumberGenerator.GetBytes(32);
                try { file.Write(generated); file.Flush(true); }
                finally { CryptographicOperations.ZeroMemory(generated); }
            }
            key = File.ReadAllBytes(keyPath);
        }
        try { return new HandAuditStore(directory, key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
