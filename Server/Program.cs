using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Drawmaha.Server;
using Drawmaha.Network;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8192);
builder.Services.AddSingleton<WalletStore>();
builder.Services.AddSingleton<HandAuditStore>(services => AuditConfiguration.Create(
    services.GetRequiredService<IConfiguration>(), services.GetRequiredService<IHostEnvironment>()));
builder.Services.AddSingleton<Hub>();
builder.Services.AddHostedService<TableTicker>();
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.MapGet("/health", () => Results.Ok(new { status = "ok", protocol = WireProtocol.Current }));
app.MapGet("/", () => Results.Text("Drawmaha server · connect a Unity client to /ws"));
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    var hub = context.RequestServices.GetRequiredService<Hub>();
    if (!hub.TryReserveConnection()) { context.Response.StatusCode = 503; return; }
    Peer peer = null;
    WebSocket socket = null;
    try
    {
        socket = await context.WebSockets.AcceptWebSocketAsync();
        peer = new Peer(socket, context.RequestAborted);
        var send = peer.SendLoop();
        // Identity/invite belongs in the first frame, never a URL or log.
        using var helloDeadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        helloDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        var hello = await peer.Receive(helloDeadline.Token);
        if (hello == null || hello.type != "hello") throw new InvalidOperationException("Send hello first.");
        hub.Connect(peer, hello);
        while (!context.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var command = await peer.Receive(context.RequestAborted);
            if (command == null) break;
            hub.Handle(peer, command);
        }
        peer.Stop(); await send;
    }
    catch (Exception e) when (e is WebSocketException || e is OperationCanceledException || e is JsonException || e is InvalidOperationException || e is ArgumentException)
    {
        // No frame bodies, private cards, usernames, or tokens are logged.
        if (peer != null)
        {
            peer.Send(new Envelope { type = "error", message = e is JsonException ? "Invalid JSON frame." : e.Message });
            await Task.Delay(150); peer.Stop();
        }
    }
    finally { if (peer != null) { peer.Stop(); hub.Disconnect(peer); } socket?.Dispose(); hub.ReleaseConnection(); }
});
app.Run();

namespace Drawmaha.Server
{
    public sealed class Peer
    {
        public readonly WebSocket Socket;
        readonly CancellationTokenSource stop;
        readonly Channel<string> output = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        public Session Session;
        public readonly Queue<DateTime> Requests = new();
        public static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
        public Peer(WebSocket socket, CancellationToken aborted) { Socket = socket; stop = CancellationTokenSource.CreateLinkedTokenSource(aborted); }
        public void Send(Envelope envelope)
        {
            if (!output.Writer.TryWrite(JsonSerializer.Serialize(envelope, Json))) Stop(); // slow clients reconnect; never grow unbounded
        }
        public async Task SendLoop()
        {
            try
            {
                await foreach (var message in output.Reader.ReadAllAsync(stop.Token))
                    await Socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, stop.Token);
            }
            catch (Exception e) when (e is WebSocketException || e is OperationCanceledException || e is ObjectDisposedException) { }
        }
        public async Task<Command> Receive(CancellationToken token)
        {
            byte[] buffer = new byte[8192]; int offset = 0;
            while (true)
            {
                var result = await Socket.ReceiveAsync(new ArraySegment<byte>(buffer, offset, buffer.Length - offset), token);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("Text frames only.");
                offset += result.Count;
                if (result.EndOfMessage) return JsonSerializer.Deserialize<Command>(buffer.AsSpan(0, offset), Json);
                if (offset >= buffer.Length) throw new InvalidOperationException("Frame exceeds 8 KiB.");
            }
        }
        public void Stop() { stop.Cancel(); output.Writer.TryComplete(); Socket.Abort(); }
    }
    public sealed class TableTicker : BackgroundService
    {
        readonly Hub hub;
        public TableTicker(Hub hub) { this.hub = hub; }
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            hub.RecordShutdown();
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(stoppingToken)) hub.Tick(DateTime.UtcNow);
        }
    }
}
