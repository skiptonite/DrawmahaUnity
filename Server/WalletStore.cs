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
    readonly float startingBalance;

    public WalletStore(IConfiguration configuration)
    {
        startingBalance = float.TryParse(configuration["DRAWMAHA_STARTING_BANK"], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float configuredBalance)
            && !float.IsNaN(configuredBalance) && !float.IsInfinity(configuredBalance)
            ? Math.Clamp(configuredBalance, 0, 100000000)
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

    public float GetBalance(string walletId)
    {
        lock (gate) return ReadBalance(GetWallet(walletId));
    }

    public (bool Enabled, float BigBlinds, bool AlwaysMax) GetAutoTopUp(string walletId)
    {
        lock (gate)
        {
            TableEntity wallet = GetWallet(walletId);
            bool enabled = wallet.TryGetValue("AutoTopUpEnabled", out object enabledValue) && enabledValue is bool enabledFlag && enabledFlag;
            bool alwaysMax = wallet.TryGetValue("AlwaysTopUpMax", out object maxValue) && maxValue is bool maxFlag && maxFlag;
            float bigBlinds = wallet.TryGetValue("AutoTopUpBigBlinds", out object amountValue)
                ? RoundChips(Convert.ToSingle(amountValue, System.Globalization.CultureInfo.InvariantCulture)) : 100;
            return (enabled, bigBlinds, alwaysMax);
        }
    }

    public void SetAutoTopUp(string walletId, float bigBlinds, bool enabled, bool alwaysMax)
    {
        if (float.IsNaN(bigBlinds) || float.IsInfinity(bigBlinds) || bigBlinds < 0 || bigBlinds > 1000000)
            throw new ArgumentException("Auto top-up must be configured in a valid number of big blinds.");
        lock (gate)
        {
            TableEntity wallet = GetWallet(walletId);
            wallet["AutoTopUpEnabled"] = enabled;
            wallet["AutoTopUpBigBlinds"] = (double)Math.Round(bigBlinds, 2, MidpointRounding.AwayFromZero);
            wallet["AlwaysTopUpMax"] = enabled && alwaysMax;
            Save(wallet, 0, null);
        }
    }

    public void BuyIn(string walletId, string tableId, float amount)
    {
        lock (gate)
        {
            TableEntity wallet = GetWallet(walletId);
            if (!string.IsNullOrEmpty(ReadTableId(wallet))) throw new InvalidOperationException("Your chips are already reserved at a table.");
            float balance = ReadBalance(wallet);
            if (balance < amount) throw new InvalidOperationException("Not enough chips in your main bank for this buy-in.");
            wallet["Balance"] = (double)RoundChips(balance - amount);
            wallet["TableId"] = tableId;
            wallet["TableStack"] = (double)amount;
            Save(wallet, -amount, "buy-in");
        }
    }

    public void Rebuy(string walletId, string tableId, float amount, string reason = "rebuy")
    {
        lock (gate)
        {
            TableEntity wallet = GetWallet(walletId);
            RequireTable(wallet, tableId);
            float balance = ReadBalance(wallet);
            if (balance < amount) throw new InvalidOperationException("Not enough chips in your main bank for a rebuy.");
            wallet["Balance"] = (double)RoundChips(balance - amount);
            wallet["TableStack"] = (double)RoundChips(ReadTableStack(wallet) + amount);
            Save(wallet, -amount, reason);
        }
    }

    public void CheckpointStack(string walletId, string tableId, float stack)
    {
        lock (gate)
        {
            TableEntity wallet = GetWallet(walletId);
            RequireTable(wallet, tableId);
            if (ReadTableStack(wallet) == stack) return;
            wallet["TableStack"] = (double)RoundChips(stack);
            Save(wallet, 0, null);
        }
    }

    public float CashOut(string walletId, string tableId, float stack)
    {
        lock (gate)
        {
            TableEntity wallet = GetWallet(walletId);
            RequireTable(wallet, tableId);
            wallet["Balance"] = (double)RoundChips(ReadBalance(wallet) + stack);
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

    void Save(TableEntity wallet, float ledgerAmount, string reason)
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
                float stack = ReadTableStack(wallet);
                wallet["Balance"] = (double)RoundChips(ReadBalance(wallet) + stack);
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

    static TableEntity NewWallet(string walletId, float startingBalance) => new(walletId, WalletRow)
    {
        ["Balance"] = (double)startingBalance,
        ["TableId"] = "",
        ["TableStack"] = 0d
    };

    static TableEntity NewLedger(string walletId, float amount, string reason) => new(walletId, "ledger-" + Guid.NewGuid().ToString("N"))
    {
        ["Amount"] = (double)amount,
        ["Reason"] = reason
    };

    static float ReadBalance(TableEntity wallet) => RoundChips(Convert.ToSingle(wallet["Balance"], System.Globalization.CultureInfo.InvariantCulture));
    static float ReadTableStack(TableEntity wallet) => RoundChips(Convert.ToSingle(wallet["TableStack"], System.Globalization.CultureInfo.InvariantCulture));
    static float RoundChips(float amount) => (float)Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    static string ReadTableId(TableEntity wallet) => wallet["TableId"] as string ?? "";

    static void RequireTable(TableEntity wallet, string tableId)
    {
        if (!string.Equals(ReadTableId(wallet), tableId, StringComparison.Ordinal))
            throw new InvalidOperationException("Your bank no longer has chips reserved at this table.");
    }

    static InvalidOperationException StorageUnavailable(Exception exception) =>
        new("The bank is temporarily unavailable. No transfer was completed; please try again.", exception);
}