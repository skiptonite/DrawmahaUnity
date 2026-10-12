using System;
using System.Collections.Concurrent;
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
        string username, accessKey, token, pendingId, epoch;
        int avatar, generation;
        float heartbeat, pendingAt;
        bool wantConnection, connectInProgress;
        float reconnectAt;
        string TokenKey { get { return "drawmaha.token." + Endpoint + "." + (username ?? "").ToLowerInvariant(); } }
        public void Connect(string endpoint, string name, int avatarIndex, string invite)
        {
            Disconnect(); Table = null; Tables = new LobbyRow[0]; Endpoint = endpoint.Trim(); username = name.Trim(); avatar = avatarIndex; accessKey = invite;
            token = PlayerPrefs.GetString(TokenKey, ""); wantConnection = true; BeginConnect();
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
            string hello = JsonUtility.ToJson(new Command { type = "hello", username = username, avatar = avatar, accessKey = accessKey, token = token });
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
                if (message == "closed") { Connected = false; connectInProgress = false; pendingId = null; reconnectAt = Time.unscaledTime + 3; }
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
                        epoch = packet.epoch; token = packet.token; PlayerPrefs.SetString(TokenKey, token); PlayerPrefs.Save();
                        Connected = true; connectInProgress = false; Status = changed ? "Server restarted. Tables were reset." : "Connected";
                        if (changed) Table = null;
                        heartbeat = Time.unscaledTime + 15;
                    }
                    else if (packet.type == "lobby") Tables = packet.tables ?? new LobbyRow[0];
                    else if (packet.type == "state") { Table = packet.table; StateReceivedAt = Time.unscaledTime; StateChanged?.Invoke(Table); }
                    else if (packet.type == "left") { Table = null; pendingId = null; }
                    else if (packet.type == "ack" && packet.id == pendingId) pendingId = null;
                    else if (packet.type == "error")
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
        }
        public void Send(Command command, bool track = true)
        {
            if (!Connected || (track && CommandPending)) return;
            if (track)
            {
                command.id = Guid.NewGuid().ToString("N"); pendingId = command.id; pendingAt = Time.unscaledTime;
                if (Table != null) { command.tableId = Table.id; command.hand = Table.hand; command.revision = Table.revision; }
            }
            _ = SendText(JsonUtility.ToJson(command), socket, cancel.Token, generation);
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
        public void ForgetSession(string endpoint = null, string name = null)
        {
            if (endpoint != null) Endpoint = endpoint.Trim();
            if (name != null) username = name.Trim();
            PlayerPrefs.DeleteKey(TokenKey); token = ""; Table = null; Disconnect();
        }
        public void Disconnect()
        {
            wantConnection = false; generation++; cancel?.Cancel(); socket?.Abort(); socket = null;
            Connected = false; connectInProgress = false; pendingId = null;
            string ignored; while (inbox.TryDequeue(out ignored)) { } while (notices.TryDequeue(out ignored)) { }
            Status = "Not connected";
        }
        void OnDestroy() { Disconnect(); cancel?.Dispose(); }
    }
}
