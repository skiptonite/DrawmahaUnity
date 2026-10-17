# Saved profiles and no-invite onboarding — 2026-10-17

- Removed the invite UI/server gate, including the production invite requirement. Existing invite-key environment settings are ignored; the updater removes that setting. Reconnect tokens continue to authenticate wallet/session continuity.
- First launch asks only for a display name. Only a server-confirmed name/avatar is saved; returning profiles connect automatically. Names/avatars remain separate for local test-player slots.
- Added the top-right avatar/name button and a profile window for changing name/avatar. Inspected both in the actual Unity Game view. UI compiled with no errors, and the Windows build succeeded with zero errors.
- **31 server profile assertions passed:** no-invite access, validation, duplicate names, lobby/table edits, authoritative avatar/name confirmation, duplicate command protection, unchanged chips/turn state, chat naming and reconnect identity.
- **14 real-client profile checks passed** with isolated test-only Unity preferences and controlled server packets: confirmed persistence, rejected/unconfirmed edits, independent betting/profile requests, reopening from a saved name, lobby reception, and test-slot isolation. The harness does not modify real player preferences.
- Regressions passed: **1,840 multiplayer assertions**, **58 chat assertions**, **81 RNG/audit checks**, and **113,729 core assertions**. The multiplayer audit replayed four settled hands.
- Azure scripts parsed successfully but were not deployed. Profile editing requires protocol 9 and the rebuilt client. Local checks: `python Verification/local_multiplayer_check.py --profile` and `dotnet run --project Verification/ClientProfileChecks.csproj`.

# Local test-player profiles — 2026-10-17

- Verified in Unity that the normal wallet key stays unchanged, profiles 1 and 2 use different keys, command-line profile selection overrides the editor default, and invalid profile numbers are rejected.
- Windows client rebuilt successfully with zero errors. Test launcher script parsed successfully. The two interactive windows were not automatically launched or connected to Azure.
- Use **Tools → Drawmaha → Testing → Launch Two Windows Players** with the rebuilt client, or the build-and-launch menu for future source changes. See the two-player section in README.md for local-server and Editor-plus-build options.

# Table chat verification — 2026-10-17

- Server build: zero warnings/errors. Unity Editor compiled the chat scripts; the input limiter and Unicode counter ran successfully inside Unity.
- Inspected the actual Game view: chat occupies the bottom-left corner and matches the betting panel's dimensions, background and rounded styling. It includes wrapped, scrollable messages, an input field, a 200-character counter, and a Send button. Enter submits while the chat field is focused.
- **58 chat integration assertions passed** against a fresh local server: table/lobby isolation, server-controlled sender identity, empty/oversized/control-character rejection, exactly 200 ASCII characters and emoji accepted, stale game revisions allowed for chat, duplicate command IDs, burst limiting and recovery, history on reconnect, latest-30 retention, and history clearing on an empty table.
- Multiplayer regression: **1,840 assertions passed**, and its encrypted audit replayed four settled hands. RNG/audit regression: **81 checks passed**. Core regression: **113,729 assertions passed**.
- Test commands: `python Verification/local_multiplayer_check.py --chat` and `python Verification/local_multiplayer_check.py`, after building the server. Both use temporary local server processes; no Azure deployment was performed.
- Chat requires protocol 8 on the server/client. Production storage behavior is unchanged: chat is bounded, in-memory table history, separate from encrypted card audits.

# RNG and audit verification — 2026-10-17

- Server build: zero warnings and zero errors on .NET 10.
- Core suite: **113,729 assertions**, exhaustive evaluation of all 2,598,960 five-card hands and 1,500 simulated hands.
- RNG/audit suite: **81 checks** covering shuffle bounds, fixed-deck exhaustion, maximum 50-card consumption, burns, encryption round-trip, incorrect keys, altered/deleted/reordered records, missing closing markers, replay mismatch, failure before payment, frozen games, and deadline compensation.
- Six-client multiplayer smoke test: **1,840 assertions**, including authorization, joins, rejected illegal actions, duplicate commands, two-card draws, showdown, reconnects, wallet transfers and per-recipient card privacy.
- The real multiplayer server's encrypted audit authenticated successfully and replayed **four settled hands**. The local runner deliberately terminates its child server, so this inspection uses `--allow-open` and does not assert stream completeness. Closed-stream validation is covered by the standalone audit suite.
- Statistical diagnostic: **100,000 decks** from the production random source; all decks were valid permutations. Aggregate card-position chi-square 2727.91168; adjacency chi-square 2606.09352; consecutive-deck same-position matches 100362 (expectation 99999). These are diagnostic observations, not a certification or a statistical proof of fairness.
- Azure PowerShell scripts parsed successfully. Azure deployment, remote Table Storage persistence, Unity Editor compilation and independent lab certification were not performed.

Run `python Verification/local_multiplayer_check.py` after building the server to repeat the isolated local multiplayer check and audit replay. See [RNG_AND_AUDITS.md](../RNG_AND_AUDITS.md) for the remaining commands and deployment settings.

---

# Historical verification — multiplayer update

The following notes predate the current two-discard limit and fixed-deck implementation. Claims about five-card draws and recycling describe the older implementation.

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
