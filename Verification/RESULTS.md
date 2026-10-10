# Verification performed — multiplayer update

Date: 2026-10-06 (UTC)

## Passed

- **Server compilation:** .NET SDK 10.0.103 / .NET 10 target, zero warnings and zero errors.
- **Core suite:** 114,187 assertions passed. Every one of the 2,598,960 five-card hands matched the expected category distribution. The original Omaha, betting, payout, heads-up, side-pot, odd-chip and refund regression cases passed.
- **Simulation:** 1,500 randomized four/six-seat hands terminated with unique live cards, nonnegative stacks and chip conservation. Includes 100 six-seat hands where all six players drew five cards, with successful deck recycling and no player receiving their own discards.
- **Blind rotation:** first-hand positions, live entry posts, protected-position entry rejection, waiting for the natural big blind, incumbent blind preservation when a seat joins, dead small blind/dead button, heads-up conversion, and one natural BB per full six-seat orbit passed.
- **Live multiplayer:** six concurrent, real WebSocket clients connected to the compiled server. The smoke test passed **1,491 assertions** covering private invite enforcement, lobby capacity, automatic two-player start, no mid-hand admission, unchanged current-hand blinds, rejected seventh seat, out-of-turn/stale/over-pot rejection, uncontested privacy, six-player full draws and chip conservation, showdown-only opponent reveal, duplicate command IDs, reconnect identity/seat preservation, and leaving.
- Private-card checks ran against every recipient snapshot in that test: opponent cards were absent before showdown and folded hands remained absent afterward.
- The project ZIP was checked for byte-for-byte agreement with the packaged source files.

The socket assertion count can vary with the number/timing of broadcasts while retaining the same checked conditions.

## Rerun engine checks

```powershell
dotnet run --project Verification/EngineChecks.csproj
```

In Unity, use **Tools → Drawmaha → Run Logic Checks**.

## Rerun live socket checks

Use a fresh development server with the test invite. In one PowerShell terminal:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:DRAWMAHA_ACCESS_KEY = "test-invite-key-123456"
$env:DRAWMAHA_SHOWDOWN_SECONDS = "1"
$env:DRAWMAHA_RECONNECT_SECONDS = "3"
dotnet run --project Server/Drawmaha.Server.csproj --urls http://localhost:8080
```

In another terminal:

```powershell
python -m pip install websockets
python Verification/multiplayer_smoke.py ws://localhost:8080/ws
```

The test deliberately uses fresh usernames and an empty server. Restart the development server before rerunning it. It is a protocol test harness, not a game bot shipped in the client/server.

## Not verified here

Unity Editor was unavailable. The Unity-specific transport/UI/editor scripts have not been compiled inside Unity or played there. Their visual appearance, standalone Windows build, mobile behavior, and IL2CPP compatibility still require editor/device verification.

Azure resources were not created, Azure CLI deployment was not executed, and the Docker image was not built/run in this environment. The deployment configuration follows current Microsoft documentation. Live tables and guest identities are in memory and reset when the server process restarts.
