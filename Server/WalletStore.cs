using Azure;
using Azure.Data.Tables;
using Azure.Identity;

namespace Drawmaha.Server;

public sealed class WalletStore
{
    const string WalletRow = "wallet";
    readonly object gate = new();
    readonly Dictionary<string, TableEntity> memory = new(StringComparer.Ordinal);
    readonly TableClient table;
    readonly int startingBalance;

    public WalletStore(IConfiguration configuration)
    {
        startingBalance = int.TryParse(configuration["DRAWMAHA_STARTING_BANK"], out int configuredBalance)
            ? Math.Clamp(configuredBalance, 0, int.MaxValue)
            : 10000;
        string endpoint = configuration["DRAWMAHA_TABLES_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production")
                throw new InvalidOperationException("Production requires DRAWMAHA_TABLES_ENDPOINT.");
            return;
        }

        var credentialOptions = new DefaultAzureCredentialOptions { ManagedIdentityClientId = configuration["AZURE_CLIENT_ID"] };
        table = new TableClient(new Uri(endpoint), "playerwallets", new DefaultAzureCredential(credentialOptions));
        table.CreateIfNotExists();
        RecoverOpenTables();
    }

    public int GetBalance(string walletId)
    {
        lock (gate) return ReadBalance(GetWallet(walletId));
    }

    public void BuyIn(string walletId, string tableId, int amount)
    {
        lock (gate)
        {
            TableEntity wallet = GetWallet(walletId);
            if (!string.IsNullOrEmpty(ReadTableId(wallet))) throw new InvalidOperationException("Your chips are already reserved at a table.");
            int balance = ReadBalance(wallet);
            if (balance < amount) throw new InvalidOperationException("Not enough chips in your main bank for this buy-in.");
            wallet["Balance"] = balance - amount;
            wallet["TableId"] = tableId;
            wallet["TableStack"] = amount;
            Save(wallet, -amount, "buy-in");
        }
    }

    public void Rebuy(string walletId, string tableId, int amount)
    {
        lock (gate)
        {
            TableEntity wallet = GetWallet(walletId);
            RequireTable(wallet, tableId);
            int balance = ReadBalance(wallet);
            if (balance < amount) throw new InvalidOperationException("Not enough chips in your main bank for a rebuy.");
            wallet["Balance"] = balance - amount;
            wallet["TableStack"] = checked(ReadTableStack(wallet) + amount);
            Save(wallet, -amount, "rebuy");
        }
    }

    public void CheckpointStack(string walletId, string tableId, int stack)
    {
        lock (gate)
        {
            TableEntity wallet = GetWallet(walletId);
            RequireTable(wallet, tableId);
            if (ReadTableStack(wallet) == stack) return;
            wallet["TableStack"] = stack;
            Save(wallet, 0, null);
        }
    }

    public int CashOut(string walletId, string tableId, int stack)
    {
        lock (gate)
        {
            TableEntity wallet = GetWallet(walletId);
            RequireTable(wallet, tableId);
            wallet["Balance"] = checked(ReadBalance(wallet) + stack);
            wallet["TableId"] = "";
            wallet["TableStack"] = 0;
            Save(wallet, stack, "cash-out");
            return ReadBalance(wallet);
        }
    }

    TableEntity GetWallet(string walletId)
    {
        if (table == null)
        {
            if (memory.TryGetValue(walletId, out TableEntity existing)) return existing;
            var created = NewWallet(walletId, startingBalance);
            memory.Add(walletId, created);
            return created;
        }

        try
        {
            NullableResponse<TableEntity> response = table.GetEntityIfExists<TableEntity>(walletId, WalletRow);
            if (response.HasValue) return response.Value;
            TableEntity created = NewWallet(walletId, startingBalance);
            try
            {
                table.SubmitTransaction(new[]
                {
                    new TableTransactionAction(TableTransactionActionType.Add, created),
                    new TableTransactionAction(TableTransactionActionType.Add, NewLedger(walletId, startingBalance, "guest-starting-bank"))
                });
                return GetWallet(walletId);
            }
            catch (RequestFailedException exception) when (exception.Status == 409) { return GetWallet(walletId); }
        }
        catch (Exception exception) when (exception is RequestFailedException || exception is AuthenticationFailedException)
        {
            throw StorageUnavailable(exception);
        }
    }

    void Save(TableEntity wallet, int ledgerAmount, string reason)
    {
        if (table == null)
        {
            memory[wallet.PartitionKey] = wallet;
            return;
        }

        try
        {
            var actions = new List<TableTransactionAction>
            {
                new(TableTransactionActionType.UpdateReplace, wallet, wallet.ETag)
            };
            if (reason != null)
                actions.Add(new TableTransactionAction(TableTransactionActionType.Add, NewLedger(wallet.PartitionKey, ledgerAmount, reason)));
            table.SubmitTransaction(actions);
        }
        catch (Exception exception) when (exception is RequestFailedException || exception is AuthenticationFailedException)
        {
            throw StorageUnavailable(exception);
        }
    }

    void RecoverOpenTables()
    {
        try
        {
            foreach (TableEntity wallet in table.Query<TableEntity>(filter: "RowKey eq 'wallet'"))
            {
                if (string.IsNullOrEmpty(ReadTableId(wallet))) continue;
                int stack = ReadTableStack(wallet);
                wallet["Balance"] = checked(ReadBalance(wallet) + stack);
                wallet["TableId"] = "";
                wallet["TableStack"] = 0;
                Save(wallet, stack, "server-restart-refund");
            }
        }
        catch (Exception exception) when (exception is RequestFailedException || exception is AuthenticationFailedException)
        {
            throw StorageUnavailable(exception);
        }
    }

    static TableEntity NewWallet(string walletId, int startingBalance) => new(walletId, WalletRow)
    {
        ["Balance"] = startingBalance,
        ["TableId"] = "",
        ["TableStack"] = 0
    };

    static TableEntity NewLedger(string walletId, int amount, string reason) => new(walletId, "ledger-" + Guid.NewGuid().ToString("N"))
    {
        ["Amount"] = amount,
        ["Reason"] = reason
    };

    static int ReadBalance(TableEntity wallet) => Convert.ToInt32(wallet["Balance"]);
    static int ReadTableStack(TableEntity wallet) => Convert.ToInt32(wallet["TableStack"]);
    static string ReadTableId(TableEntity wallet) => wallet["TableId"] as string ?? "";

    static void RequireTable(TableEntity wallet, string tableId)
    {
        if (!string.Equals(ReadTableId(wallet), tableId, StringComparison.Ordinal))
            throw new InvalidOperationException("Your bank no longer has chips reserved at this table.");
    }

    static InvalidOperationException StorageUnavailable(Exception exception) =>
        new("The bank is temporarily unavailable. No transfer was completed; please try again.", exception);
}