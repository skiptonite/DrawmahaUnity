# Drawmaha — multiplayer Unity client and Azure server

Six human seats per table. No bots. The server shuffles, deals, validates actions, controls turn order, handles blinds and entry, and awards pots. A Unity client receives only its own private cards until a real showdown.

## Quick local start

1. Install **Unity 6** and the **.NET 10 SDK**. Add this project folder through Unity Hub.
2. From the project folder, start the server:

   ```powershell
   $env:ASPNETCORE_ENVIRONMENT = "Development"
   dotnet run --project Server/Drawmaha.Server.csproj --urls http://localhost:8080
   ```

   A development server permits an empty invite key. To require one, set `DRAWMAHA_ACCESS_KEY` before starting it. Production refuses to start without a key of at least 16 characters.
3. In Unity, choose **Tools → Drawmaha → Open Table**, then press Play.
4. On the connect screen, enter a username and invite code. The default server address is `ws://localhost:8080/ws`; after connecting, choose a table in the lobby.
5. Build a Windows client using **Tools → Drawmaha → Build Windows Client** and run a second instance with a **different username**. Saved reconnect identities are separate for each username/server combination.
6. With two seated, connected players, the first hand starts automatically. Later hands start six seconds after settlement. New arrivals wait for the next eligible hand.

For a friend on another computer, deploy the server as described in [AZURE_SETUP.md](AZURE_SETUP.md). Configure the generated `wss://` address as the client build's default endpoint before distributing it; players only enter their username and invite code. Remote connections require `wss://`; unencrypted `ws://` is allowed only for loopback testing. An Azure deployment has not been performed for you.

## Lobby tables

| Table | Blinds | Starting buy-in | Maximum seats |
| --- | ---: | ---: | ---: |
| The Green Room | 1 / 2 | 200 | 6 |
| Drawmaha Social | 5 / 10 | 1,000 | 6 |
| The High Table | 25 / 50 | 5,000 | 6 |

All values are play chips. Buy-ins are fixed at 100 big blinds. Change the rows in `Server/Hub.cs`. Clients read the lobby configuration from the server.

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

The small private-play server holds tables and sessions **in memory**. Restarting or redeploying it resets all tables, stacks, and sessions. Minimum one replica avoids deliberate scale-to-zero shutdowns, but it does **not** make state durable. Keep maximum replicas at one and use single revision mode; sticky sessions alone cannot make multiple independent table engines agree. Drain tables before deployments. Persistent accounts, player-uploaded avatars, wallets, cross-restart recovery, multi-replica routing, chat, tournaments and real-money operation are outside this version.

## Checks

```powershell
dotnet run --project Verification/EngineChecks.csproj
```

Or run **Tools → Drawmaha → Run Logic Checks** in Unity. The suite exhaustively checks all 2,598,960 five-card hands, the original betting/payout regressions, 1,500 randomized four/six-seat hands, six full draws, and blind/entry rotation. The same core sources are compiled into the server and Unity client.

The real-socket smoke test is `Verification/multiplayer_smoke.py`. See [Verification/RESULTS.md](Verification/RESULTS.md) for the actual validation and how to rerun it.
