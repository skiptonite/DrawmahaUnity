# Drawmaha — multiplayer Unity client and Azure server

Six human seats per table. No bots. The server shuffles, deals, validates actions, controls turn order, handles blinds and entry, and awards pots. A Unity client receives only its own private cards until a real showdown.

The server uses cryptographic Fisher–Yates shuffles and a fixed deck per hand. Encrypted, tamper-evident hand audits are required for multiplayer. Local development creates an ignored audit folder automatically; production uses durable Azure Table Storage and a separate audit-key secret. See [randomness, audit configuration and verification](RNG_AND_AUDITS.md).

Table chat appears at the bottom left, matching the betting panel. Send with Enter or the Send button; messages allow up to 200 Unicode characters (an emoji surrogate pair counts as one). Chat is visible only to seated players at that table, retains the latest 30 messages across hands/reconnects, and clears when the table becomes empty. The server supplies the sender name, rejects empty/control-character messages, and limits each session to five messages per ten seconds. Chat does not change betting revisions or turn deadlines. Both server and client need protocol 8 for chat. Run `python Verification/local_multiplayer_check.py --chat` after building the server to verify the feature locally.

## Quick local start

1. Install **Unity 6** and the **.NET 10 SDK**. Add this project folder through Unity Hub.
2. From the project folder, start the server:

   ```powershell
   $env:ASPNETCORE_ENVIRONMENT = "Development"
   dotnet run --project Server/Drawmaha.Server.csproj --urls http://localhost:8080
   ```

   No invite code is required. Legacy `DRAWMAHA_ACCESS_KEY` settings are ignored by the updated server.
3. In Unity, choose **Tools → Drawmaha → Open Table**, then press Play.
4. On first launch, choose a display name and select **Enter lobby**. The server-confirmed name and avatar are saved, and later launches connect automatically. Click your avatar/name in the top-right corner to edit your profile in the lobby or at a table; saving keeps your wallet, chips and seat. The default server is Azure. To use a local server, pass `-drawmaha-server ws://127.0.0.1:8080/ws` when launching the client.
5. Build a Windows client using **Tools → Drawmaha → Build Windows Client**. To test two players on one computer, use **Tools → Drawmaha → Testing → Build and Launch Two Windows Players**.
6. With two seated, connected players, the first hand starts automatically. Later hands start six seconds after settlement. New arrivals wait for the next eligible hand.

Each guest wallet starts with 10,000 play chips by default. The bank is tied to one locally saved guest identity per server and app installation; the display name can change without creating another wallet. Joining deducts the table buy-in; leaving returns the remaining stack. The bank is stored on the server, not in PlayerPrefs. Existing installations migrate the wallet for the display name entered at first connect after updating. There is no in-game reset option. Clearing saved client data or reinstalling may still create a new guest identity, so this is not a strong one-person/one-account guarantee.

## Two players on one computer

Two ordinary launches share the normal player's saved wallet/reconnect token. Different display names alone do not create independent players. Test profiles give each window its own saved name, wallet and reconnect token without touching the normal profile. Profiles retain their identity between launches. They use the same server validation and permissions as any other guest, and are not bots.

- **Easiest:** choose **Tools → Drawmaha → Testing → Build and Launch Two Windows Players** in Unity. It builds the current client and opens two windowed copies, initially prefilled as Test Player 1 and Test Player 2. Confirm the name once in each window; saved profiles connect automatically on later launches. Join the same table. Both use Azure by default. Names and avatars can be changed through each window's top-right profile button.
- **Already built:** choose **Tools → Drawmaha → Testing → Launch Two Windows Players**, or run `./Verification/launch-two-clients.ps1` from PowerShell.
- **Local server:** start the Development server from step 2 above, then run `./Verification/launch-two-clients.ps1 -LocalServer`. No invite code or Azure deployment is needed.
- **Editor plus one build:** stop Play mode, choose **Tools → Drawmaha → Testing → Editor Identity → Test Player 1**, then press Play. Launch the standalone client with `-drawmaha-test-player 2`. Choose **Normal Player** in that menu to switch the editor back afterward. Do not use the same test profile in both windows.

Manual Windows launch example (run each command once):

```powershell
Start-Process ./Builds/Windows/Drawmaha.exe -ArgumentList '-drawmaha-test-player 1 -screen-fullscreen 0 -screen-width 960 -screen-height 540'
Start-Process ./Builds/Windows/Drawmaha.exe -ArgumentList '-drawmaha-test-player 2 -screen-fullscreen 0 -screen-width 960 -screen-height 540'
```

Profiles 1–6 are supported for testing a full table; profile 0 is the existing normal player. Names and avatars are saved separately for each profile; table/card/background preferences remain shared. The game already runs in the background, so switching between windows does not stop play or pause turn timers. Rebuild older executables before using these options.

For a friend on another computer, build the matching client with **Tools → Drawmaha → Build Windows Client** or **Tools → Drawmaha → Build macOS Client**. For macOS, install **macOS Build Support** for this Unity Editor in Unity Hub first. The macOS build outputs `Builds/macOS/Drawmaha.app` and creates a ready-to-upload `Builds/Drawmaha-macOS.zip` with executable permissions preserved. Attach that ZIP to a GitHub Release. On first launch, macOS may warn that the app is from an unidentified developer; for a private test, Control-click the app, choose **Open**, then confirm. Your friend chooses a display name once; no invite is required. The client defaults to the deployed Azure server. Remote connections require `wss://`; unencrypted `ws://` is allowed only for loopback testing. See [AZURE_SETUP.md](AZURE_SETUP.md) for server deployment and updates.

Profile editing and no-invite onboarding require the updated protocol-9 server and a rebuilt client. Names must be 2–20 letters/numbers/spaces/underscores/hyphens, starting with a letter or number, and unique among current server sessions. Rejected edits are not saved; profile changes are separate from betting commands and do not reset turn timers. Private/public table creation requires protocol 10. A temporary network reconnect still restores an unfinished seat when available. Run `python Verification/local_multiplayer_check.py --profile` to check the server flow.

## Lobby tables

The lobby lists active tables and starts empty. Anyone can create a table; creation defaults to **private**, and the creator is seated immediately at the minimum buy-in. Public tables are an explicit option and do not require a code. Private tables remain listed, but joining requires the creator's 8–24 character invite code. Codes are case-insensitive, hashed on the server, checked before wallet debit, and never included in lobby messages.

Creators configure the table name, blinds, and minimum/maximum buy-in. Blinds must be positive with the big blind larger than the small blind and no more than 100,000. Buy-in limits must be at least one big blind, minimum no greater than maximum, and maximum no more than 100,000,000. Players choose an amount in that range when joining; all values are play chips and are debited from the main bank. Tables have six seats, the server allows up to 100 active tables, and an empty table is removed. Table settings and seats are in memory and are lost when the server restarts. New guest wallets receive 10,000 chips by default; configure `DRAWMAHA_STARTING_BANK` to change this.

## Gameplay retained

Five hole cards; pot-limit betting preflop and after flop, turn, and river; one draw of 0–2 cards after **flop betting**, with replacements dealt immediately. The final hole cards are used for both halves. Omaha high requires exactly two hole cards and three board cards; draw high uses all five private cards. Ranking, raise limits, short all-in reopening, side pots, ties, uncalled refunds and odd-chip payout conventions are retained.

The four-seat engine has been extended to accept up to six seats and a server-supplied blind/deal plan. When replacements exhaust the stub, discards and burns are reshuffled; a player cannot receive their own discarded cards during that draw. Existing four-seat dealing remains unchanged when the stub has enough cards.

## Casino-style table entry

There is no single universal casino policy. This project implements the posting/waiting rules in [Robert's Rules of Poker: Buttons & Blinds](https://www.pokerzone.com/rules/buttons-and-blinds/), with a **dead button** and natural big-blind orbit:

- Join during an existing hand: reserve a seat, receive no cards, contribute no chips to that hand.
- **Wait for big blind**: be dealt in when the natural big blind reaches your seat. This is the casino entry wait, not waiting to receive the dealer button.
- **Post big blind**: post one live big blind on the next eligible hand. No extra entry post if you are naturally the big blind. Cannot post on the button or in the clockwise interval between button and big blind.
- New arrivals do not cause existing players' button/blind positions to be recalculated during the hand. The next natural big blind advances from the previous big blind, preserving incumbent obligations. A departed small blind may be dead rather than reassigned.
- Heads-up: button posts the small blind; the big blind continues advancing. Growing from heads-up retains the old blind positions.
- A reconnect within 90 seconds retains seat, chips, identity, and the current hand. If the player was skipped during an offline blind turn, entry waits for the natural big blind; posting early also collects an owed small blind as dead money.
- Leave during a hand: fold at the player's next betting turn; stand pat if a draw is pending. An all-in hand remains eligible. Cash out and release the seat at settlement/the hand boundary. Seats also release after disconnect grace or a zero stack.

A player may pay an **entry** big blind and then a **natural** big blind soon afterward; that is not a repeat caused by resetting the orbit. This is not configurable to every cardroom's house rules. See `Core/BlindOrbit.cs` and the tests for the exact policy.

## Presentation

- Overlapping, slightly rotated hole cards above every occupied player panel.
- Circular profile picture/initial, username, chips below the username, action text, dealer marker, and pulsing turn outline with a countdown.
- Opponent card **identities are absent from network packets** before showdown. Remaining hands reveal at showdown; folded hands and uncontested survivors stay hidden from opponents.
- Undealt board positions are empty. Private cards and board cards animate from the dealer area one at a time. All-in runouts animate the full board before revealing showdown hands.
- Choose the background and poker table independently from **Settings**. The defaults are `BeachBackground` and `BeachPokerTable`.

To add options, place background images under `Assets/Resources/Backgrounds` and poker-table images under `Assets/Resources/PokerTables`. The Settings selectors load Texture2D assets from those folders. Background images fill the screen; table images are centered using their source aspect ratio, so transparent margins are useful for oval-table artwork. Avatar indices 0–7 correspond to lobby choices 1–8; without profile pictures, the client shows colored circles with initials.

This uses a scaled IMGUI interface to preserve the original project's dependency-free setup. Desktop landscape is the primary target. Unity Editor rendering, standalone builds, mobile input, and IL2CPP have not been verified here. `ClientWebSocket` does not provide a WebGL/browser transport in this version.

## Multiplayer behavior and limits

The server uses .NET 10 and ordinary ASP.NET Core WebSockets. There is no game-host player; all table mutations occur on the server under one lock. Clients submit an action ID, hand number, and expected revision. Duplicate accepted commands do not apply twice; stale/out-of-turn/illegal commands are rejected. Reconnect restores current state without resending an unconfirmed action. Timers default to 30 seconds; timeout is check if free, fold when facing a bet, or stand pat during the draw. These deterministic fallbacks are not AI players.

The small private-play server holds tables and sessions **in memory**. Guest wallets and their chip ledger are stored in Azure Table Storage. At each completed hand, the server checkpoints each seated player's stack; after a restart it refunds the last checkpoint to the bank because the table itself cannot be recovered. Chips won or lost during an unfinished hand are rolled back to that checkpoint. Keep maximum replicas at one and use single revision mode; multiple independent table engines are not supported. Guest identities are local bearer IDs, not authenticated accounts, and can be lost with client data. Account recovery, multi-replica routing, chat, tournaments and real-money operation are outside this version.

## Checks

```powershell
dotnet run --project Verification/EngineChecks.csproj
```

Or run **Tools → Drawmaha → Run Logic Checks** in Unity. The suite exhaustively checks all 2,598,960 five-card hands, the original betting/payout regressions, 1,500 randomized four/six-seat hands, six full draws, and blind/entry rotation. The same core sources are compiled into the server and Unity client.

The real-socket smoke test is `Verification/multiplayer_smoke.py`. See [Verification/RESULTS.md](Verification/RESULTS.md) for the actual validation and how to rerun it.
