using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using Drawmaha.Network;
using UnityEngine;

static class ClientProfileChecks
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Set(MultiplayerClient client, string name, object value) => typeof(MultiplayerClient).GetField(name, Private).SetValue(client, value);
    static T Get<T>(MultiplayerClient client, string name) => (T)typeof(MultiplayerClient).GetField(name, Private).GetValue(client);
    static void Receive(MultiplayerClient client, Envelope packet)
    {
        Get<ConcurrentQueue<string>>(client, "inbox").Enqueue(JsonUtility.ToJson(packet));
        typeof(MultiplayerClient).GetMethod("Update", Private).Invoke(client, null);
    }
    static MultiplayerClient NewClient(string name)
    {
        var client = new MultiplayerClient();
        // Invalid URI avoids starting real sockets; subsequent packets are controlled below.
        client.Connect("test-endpoint", name, 2);
        Set(client, "socket", new ClientWebSocket());
        Set(client, "cancel", new CancellationTokenSource());
        return client;
    }
    static Envelope Welcome(string name, string epoch = "epoch1") => new() {
        type = "welcome", username = name, avatar = 2, token = "test-token", epoch = epoch,
        protocol = WireProtocol.Current, walletBalance = 10000
    };
    static int Main()
    {
        try
        {
            const string nameKey = "drawmaha.username";
            var client = NewClient("First Player");
            Check(!PlayerPrefs.HasKey(nameKey), "Attempted connection must not persist an unconfirmed display name");
            int changed = 0, errors = 0, gameErrors = 0;
            client.ProfileChanged += () => changed++;
            client.ProfileFailure += _ => errors++;
            client.Failure += _ => gameErrors++;
            Receive(client, Welcome("First Player"));
            Check(PlayerPrefs.GetString(nameKey) == "First Player" && PlayerPrefs.GetInt("drawmaha.avatar") == 2, "Welcome persists canonical name/avatar");
            Check(changed == 1 && client.Connected && !client.ProfilePending, "Welcome confirms the profile");
            string wallet = PlayerPrefs.GetString("drawmaha.wallet.id.test-endpoint");
            string token = PlayerPrefs.GetString("drawmaha.token.test-endpoint");
            Set(client, "pendingId", "betting-command");
            Check(client.UpdateProfile("Taken Name", 4), "Profile request starts independently of a pending game action");
            string id = Get<string>(client, "pendingProfileId");
            Check(PlayerPrefs.GetString(nameKey) == "First Player", "Unconfirmed edit is not saved");
            Receive(client, new Envelope { type = "error", id = id, message = "That display name is in use." });
            Check(PlayerPrefs.GetString(nameKey) == "First Player" && client.PlayerName == "First Player", "Rejected name preserves the saved profile");
            Check(errors == 1 && gameErrors == 0 && client.CommandPending && !client.ProfilePending, "Profile errors leave betting commands alone");
            Check(client.UpdateProfile("New Player", 6), "Second profile request starts");
            id = Get<string>(client, "pendingProfileId");
            Receive(client, new Envelope { type = "profile", id = "unrelated", username = "Ignore Me", avatar = 0 });
            Check(client.PlayerName == "First Player" && client.ProfilePending, "Unrelated profile response ignored");
            Receive(client, new Envelope { type = "profile", id = id, username = "New Player", avatar = 6 });
            Check(client.PlayerName == "New Player" && PlayerPrefs.GetString(nameKey) == "New Player" && PlayerPrefs.GetInt("drawmaha.avatar") == 6,
                "Confirmed edit updates runtime and saved profile");
            Check(PlayerPrefs.GetString("drawmaha.wallet.id.test-endpoint") == wallet && PlayerPrefs.GetString("drawmaha.token.test-endpoint") == token,
                "Editing name does not replace wallet or reconnect token");
            Set(client, "pendingId", null);
            var reopened = NewClient(PlayerPrefs.GetString(nameKey));
            Check(reopened.PlayerName == "New Player", "Later launch can load the saved name without asking again");
            Receive(reopened, Welcome("New Player"));
            Receive(reopened, new Envelope { type = "lobby", tables = new[] { new LobbyRow { id = "micro" } } });
            Check(reopened.Connected && reopened.Table == null && reopened.Tables.Length == 1, "Returning player reaches the lobby");
            reopened.Disconnect();
            client.Disconnect();
            typeof(LocalTestPlayer).GetProperty("Number").GetSetMethod(true).Invoke(null, new object[] { 2 });
            var testPlayer = NewClient("Test Two");
            Receive(testPlayer, Welcome("Test Two"));
            Check(PlayerPrefs.GetString(LocalTestPlayer.Key(nameKey)) == "Test Two" && PlayerPrefs.GetString(nameKey) == "New Player",
                "Test slot persists separately from normal player");
            testPlayer.Disconnect();
            Console.WriteLine($"PASS: {checks} real-client profile persistence and confirmation checks using isolated test preferences.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
