using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Drawmaha.Network
{
    // Desktop/Android/iOS .NET transport. Unity APIs and JSON parsing stay on the main thread.
    public sealed class MultiplayerClient : MonoBehaviour
    {
        public string Endpoint = "wss://drawmaha.blackmushroom-85b81292.centralus.azurecontainerapps.io/ws";
        public bool Connected { get; private set; }
        public int ServerProtocol { get; private set; }
        public bool SupportsWaitingPreferences { get { return ServerProtocol >= WireProtocol.WaitingPreferences; } }
        public bool SupportsMainBank { get { return ServerProtocol >= WireProtocol.MainBank; } }
        public bool SupportsTableChat { get { return ServerProtocol >= WireProtocol.TableChat; } }
        readonly List<ChatMessageView> chatMessages = new List<ChatMessageView>();
        public IReadOnlyList<ChatMessageView> ChatMessages { get { return chatMessages; } }
        public bool ChatPending { get { return pendingChatId != null; } }
        public event Action<string> ChatConfirmed;
        public string Status { get; private set; } = "Not connected";
        public TableState Table { get; private set; }
        public LobbyRow[] Tables { get; private set; } = new LobbyRow[0];
        public event Action<TableState> StateChanged;
        public event Action<string> Failure;
        public bool CommandPending { get { return pendingId != null; } }
        public float StateReceivedAt { get; private set; }
        readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<string> notices = new ConcurrentQueue<string>();
        readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        ClientWebSocket socket;
        CancellationTokenSource cancel;
        string username, accessKey, token, walletId, pendingId, epoch;
        string pendingChatId, pendingChatText;
        float pendingChatAt;
        int avatar, generation;
        float heartbeat, pendingAt;
        bool wantConnection, connectInProgress;
        float reconnectAt;
        public int BankBalance { get; private set; }
        string TokenKey { get { return LocalTestPlayer.Key("drawmaha.token." + Endpoint); } }
        string WalletKey { get { return LocalTestPlayer.Key("drawmaha.wallet.id." + Endpoint); } }
        string LegacyTokenKey { get { return TokenKey + "." + (username ?? "").ToLowerInvariant(); } }
        string LegacyWalletKey { get { return WalletKey + "." + (username ?? "").ToLowerInvariant(); } }
        public void Connect(string endpoint, string name, int avatarIndex, string invite)
        {
            Disconnect(); ServerProtocol = 0; Table = null; Tables = new LobbyRow[0]; Endpoint = endpoint.Trim(); username = name.Trim(); avatar = avatarIndex; accessKey = invite;
            token = PlayerPrefs.GetString(TokenKey, "");
            walletId = PlayerPrefs.GetString(WalletKey, "");
            if (!Guid.TryParseExact(walletId, "N", out _))
            {
                walletId = LocalTestPlayer.Number == 0 ? PlayerPrefs.GetString(LegacyWalletKey, "") : "";
                if (!Guid.TryParseExact(walletId, "N", out _)) walletId = Guid.NewGuid().ToString("N");
            }
            if (LocalTestPlayer.Number == 0 && string.IsNullOrEmpty(token)) token = PlayerPrefs.GetString(LegacyTokenKey, "");
            PlayerPrefs.SetString(WalletKey, walletId); PlayerPrefs.Save();
            BankBalance = 0; wantConnection = true; BeginConnect();
        }
        void BeginConnect()
        {
            if (connectInProgress) return;
            Uri uri;
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out uri) || (uri.Scheme != "ws" && uri.Scheme != "wss")) { Status = "Use a ws:// or wss:// server URL."; wantConnection = false; return; }
            if (uri.Scheme == "ws" && !uri.IsLoopback) { Status = "Remote servers require wss:// encryption."; wantConnection = false; return; }
            connectInProgress = true; Status = "Connecting…";
            cancel?.Dispose(); cancel = new CancellationTokenSource(); socket = new ClientWebSocket(); socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            int run = ++generation;
            string hello = JsonUtility.ToJson(new Command { type = "hello", username = username, avatar = avatar, accessKey = accessKey, token = token, walletId = walletId });
            _ = ConnectionLoop(socket, cancel.Token, run, hello);
        }
        async Task ConnectionLoop(ClientWebSocket peer, CancellationToken ct, int run, string hello)
        {
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15)); await peer.ConnectAsync(new Uri(Endpoint), timeout.Token);
                }
                await peer.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(hello)), WebSocketMessageType.Text, true, ct);
                byte[] bytes = new byte[65536];
                while (!ct.IsCancellationRequested && peer.State == WebSocketState.Open)
                {
                    int offset = 0; WebSocketReceiveResult result;
                    do
                    {
                        result = await peer.ReceiveAsync(new ArraySegment<byte>(bytes, offset, bytes.Length - offset), ct);
                        if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Connection closed.");
                        if (result.MessageType != WebSocketMessageType.Text) throw new WebSocketException("Unsupported message.");
                        offset += result.Count;
                        if (offset == bytes.Length && !result.EndOfMessage) throw new WebSocketException("Server message too large.");
                    } while (!result.EndOfMessage);
                    if (run == generation) inbox.Enqueue(Encoding.UTF8.GetString(bytes, 0, offset));
                }
            }
            catch (Exception e) when (e is WebSocketException || e is OperationCanceledException || e is ArgumentException)
            {
                if (run == generation && !ct.IsCancellationRequested) notices.Enqueue("Disconnected. Reconnecting…");
            }
            finally { if (run == generation) notices.Enqueue("closed"); peer.Dispose(); }
        }
        void Update()
        {
            string message;
            while (notices.TryDequeue(out message))
            {
                if (message == "closed") { Connected = false; connectInProgress = false; pendingId = pendingChatId = null; reconnectAt = Time.unscaledTime + 3; }
                else Status = message;
            }
            while (inbox.TryDequeue(out message))
            {
                try
                {
                    var packet = JsonUtility.FromJson<Envelope>(message);
                    if (packet.type == "welcome")
                    {
                        bool changed = epoch != null && epoch != packet.epoch;
                        epoch = packet.epoch; token = packet.token; ServerProtocol = packet.protocol; BankBalance = packet.walletBalance;
                        PlayerPrefs.SetString(TokenKey, token);
                        if (LocalTestPlayer.Number == 0) { PlayerPrefs.DeleteKey(LegacyTokenKey); PlayerPrefs.DeleteKey(LegacyWalletKey); }
                        PlayerPrefs.Save();
                        Connected = true; connectInProgress = false;
                        Status = !SupportsWaitingPreferences ? "Server update required for waiting-action preferences." : !SupportsMainBank ? "Server update required for the main bank." : changed ? "Server restarted. Tables were reset." : "Connected";
                        if (changed) { Table = null; chatMessages.Clear(); }
                        heartbeat = Time.unscaledTime + 15;
                    }
                    else if (packet.type == "wallet") BankBalance = packet.walletBalance;
                    else if (packet.type == "lobby") Tables = packet.tables ?? new LobbyRow[0];
                    else if (packet.type == "state")
                    {
                        if (Table == null || Table.id != packet.table?.id) { chatMessages.Clear(); pendingChatId = null; }
                        Table = packet.table; StateReceivedAt = Time.unscaledTime; StateChanged?.Invoke(Table);
                    }
                    else if (packet.type == "chatHistory" && Table != null && packet.tableId == Table.id)
                    {
                        chatMessages.Clear();
                        foreach (var entry in packet.chatMessages ?? new ChatMessageView[0]) AddChat(entry);
                    }
                    else if (packet.type == "chat" && Table != null && packet.tableId == Table.id) AddChat(packet.chat);
                    else if (packet.type == "left") { Table = null; pendingId = pendingChatId = null; chatMessages.Clear(); }
                    else if (packet.type == "ack" && pendingChatId != null && packet.id == pendingChatId)
                    {
                        string sent = pendingChatText; pendingChatId = null; ChatConfirmed?.Invoke(sent);
                    }
                    else if (packet.type == "error" && pendingChatId != null && packet.id == pendingChatId)
                    {
                        pendingChatId = null; Failure?.Invoke(packet.message);
                    }
                    else if (packet.type == "ack" && packet.id == pendingId) pendingId = null;
                    else if (packet.type == "error" && (packet.id == pendingId || string.IsNullOrEmpty(packet.id)))
                    {
                        pendingId = null; Status = packet.message; Failure?.Invoke(packet.message);
                        if (!Connected) { wantConnection = false; connectInProgress = false; }
                    }
                }
                catch (Exception e) { Status = "Invalid server message: " + e.Message; }
            }
            if (wantConnection && !Connected && !connectInProgress && Time.unscaledTime >= reconnectAt) BeginConnect();
            if (Connected && Time.unscaledTime >= heartbeat) { heartbeat = Time.unscaledTime + 15; Send(new Command { type = "ping" }, false); }
            if (pendingId != null && Time.unscaledTime - pendingAt > 8)
            {
                pendingId = null; Status = "No action confirmation. Reconnecting for the current table state.";
                socket?.Abort();
            }
            if (pendingChatId != null && Time.unscaledTime - pendingChatAt > 8)
            {
                pendingChatId = null; Failure?.Invoke("Chat delivery was not confirmed. Check the chat before sending again.");
            }
        }
        void AddChat(ChatMessageView entry)
        {
            if (entry == null || chatMessages.Exists(item => item.sequence == entry.sequence)) return;
            chatMessages.Add(entry);
            if (chatMessages.Count > ChatRules.HistoryLimit) chatMessages.RemoveAt(0);
        }
        public bool SendChat(string text)
        {
            if (!Connected || Table == null || !SupportsTableChat || ChatPending) return false;
            try { text = ChatRules.Validate(text); }
            catch (ArgumentException e) { Failure?.Invoke(e.Message); return false; }
            pendingChatId = Guid.NewGuid().ToString("N"); pendingChatText = text; pendingChatAt = Time.unscaledTime;
            var command = new Command { type = "chat", id = pendingChatId, tableId = Table.id, message = text };
            _ = SendText(JsonUtility.ToJson(command), socket, cancel.Token, generation);
            return true;
        }
        public void Send(Command command, bool track = true)
        {
            bool interruptPending = command.type == "leave";
            if (!Connected || (track && CommandPending && !interruptPending)) return;
            if (track)
            {
                command.id = Guid.NewGuid().ToString("N"); pendingId = command.id; pendingAt = Time.unscaledTime;
                if (Table != null) { command.tableId = Table.id; command.hand = Table.hand; command.revision = Table.revision; }
            }
            _ = SendText(JsonUtility.ToJson(command), socket, cancel.Token, generation);
        }
        public void SendBeforeQuit(Command command)
        {
            if (!Connected || socket == null || Table == null) return;
            command.id = Guid.NewGuid().ToString("N"); command.tableId = Table.id;
            command.hand = Table.hand; command.revision = Table.revision;
            string json = JsonUtility.ToJson(command); ClientWebSocket peer = socket;
            CancellationToken ct = cancel.Token; int run = generation;
            try { Task.Run(() => SendText(json, peer, ct, run)).Wait(500); }
            catch (AggregateException) { }
        }
        async Task SendText(string json, ClientWebSocket peer, CancellationToken ct, int run)
        {
            bool acquired = false;
            try
            {
                await sendGate.WaitAsync(ct); acquired = true;
                if (peer.State == WebSocketState.Open) await peer.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, ct);
            }
            catch (Exception e) when (e is WebSocketException || e is OperationCanceledException || e is ObjectDisposedException) { if (run == generation) notices.Enqueue("Disconnected. Reconnecting…"); }
            finally { if (acquired) sendGate.Release(); }
        }
        public void Disconnect()
        {
            wantConnection = false; generation++; cancel?.Cancel(); socket?.Abort(); socket = null;
            Connected = false; connectInProgress = false; pendingId = pendingChatId = null; chatMessages.Clear();
            string ignored; while (inbox.TryDequeue(out ignored)) { } while (notices.TryDequeue(out ignored)) { }
            Status = "Not connected";
        }
        void OnDestroy() { Disconnect(); cancel?.Dispose(); }
    }
}
