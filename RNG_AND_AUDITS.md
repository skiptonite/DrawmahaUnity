# Randomness and hand audits

The multiplayer server uses `SecureShuffleRandom`, a narrow `IRandomSource` implementation backed by .NET `RandomNumberGenerator.GetInt32`. Bounded sampling avoids modulo bias. There is no application-managed seed. Each hand creates a new 52-card deck and performs 51 Fisher–Yates choices. The deck stays fixed, including during draws. Six players drawing at most two cards consume at most 50 cards including three burns; exhaustion throws instead of recycling. Blocking audit-write time is added back to active tables' action deadlines; new turns start their clocks after processing.

Reproducible tests must explicitly use `Game.CreateForTesting(seed)` or `DeterministicTestRandom`. There is no default seeded constructor and no inheritance from `System.Random` in the production random source.

## What is recorded

Every table requires a server-only audit sink. Records include globally unique stream/table/hand IDs, UTC timestamps, assembly informational version (including the source revision when supplied by the build) and the server assembly's SHA-256, initial stacks and seating/blind setup, the complete shuffled deck, each deal and burn with its position, accepted betting/draw actions, discards, and numeric settlement snapshots. Mid-hand changes to inactive seats are recorded too. No credentials, wallet identities, or reconnect tokens are included.

Shuffle/deal and action records are persisted before the corresponding operation proceeds. A settlement snapshot is persisted before the server checkpoints stacks and broadcasts the result. A recording failure permanently freezes the affected game instance; it cannot resume, rebuy, leave, or start another hand. Other tables will also pause on their next attempted audit if the shared sink has failed. Investigate storage and recover through a controlled restart; do not silently retry or replace the failed hand. Wallet recovery continues to use the last completed-hand checkpoint.

Graceful shutdown records interruptions and a stream-close marker. A crash may leave a partially written record or an open stream. Such a stream is incomplete, not certified as a complete history. Some final records may describe an operation intended immediately before the crash; do not assume every recorded intent was applied or shown to players.

## Protection and storage

Records use AES-256-GCM authenticated encryption. HKDF-SHA-256 derives a separate key for each random stream ID; a sequence nonce never repeats within that stream. Version, stream ID, sequence number, and previous-record SHA-256 are authenticated as associated data. Sequence and chain verification detect modifications, insertion, duplication, reordering and removal of interior records without the key. Closing markers help identify incomplete exports.

This is tamper-evident storage, not a proof against an operator who controls both the application and its encryption key. Whole-stream deletion or removal of an entire history cannot be detected from that history alone. Archive stream inventories and final file hashes with an independently controlled, append-only/immutable backup. Restrict production secret access; use Key Vault-backed Container Apps secrets when practical. Keep old keys when rotating and record which streams used each key.

Production requires `DRAWMAHA_AUDIT_KEY`, a Base64-encoded 32-byte random secret. Do not put it in source, ordinary logs, command-line verifier arguments, or a distributed Unity build.

By default, when `DRAWMAHA_TABLES_ENDPOINT` is configured, encrypted records go into the **handaudits** Azure table using the existing managed identity. Each server process gets its own partition (`PartitionKey` = stream ID); `RowKey` is a 20-digit sequence; `Frame` contains the encrypted JSON record. Writes use insert, never upsert. Completed streams remain across restarts. The current deployment role can also read/delete table data: restrict operational access and maintain independent backups. Per-record synchronous writes prioritize durability; measure latency, action timing and transaction costs before increasing table traffic.

Alternatively, explicitly set `DRAWMAHA_AUDIT_DIRECTORY` to use JSON Lines files on a durable, access-restricted volume. Files are created exclusively, flushed to disk for every record, and never reopened for append after restart. Unix-created directories/files have owner-only modes. Existing directory permissions are not changed; configure Windows ACLs and mounted-volume access separately. Do not point a production container at ephemeral storage.

For local development with no storage endpoint, records and a generated `development.key` are placed in `.drawmaha-audit` under the server content root. That directory is ignored by Git. Keeping this key beside records is a development convenience, not a production key-management arrangement.

## Verify and replay

Run the correctness/authentication suite:

```powershell
dotnet run --project Verification/RngChecks.csproj
dotnet run --project Verification/EngineChecks.csproj
python Verification/local_multiplayer_check.py
```

Check a closed local audit file:

```powershell
dotnet run --project Verification/RngChecks.csproj -- verify <absolute-audit-file.jsonl>
```

The verifier reads `DRAWMAHA_AUDIT_KEY` from the environment, or the local `development.key` beside the file. It authenticates and checks every record, then reconstructs the shuffle choices from each recorded permutation and replays actions through the engine, comparing card positions, burns, private state, and settlement snapshots. It prints counts and a file fingerprint, never card contents or keys. Use the matching source/build version when replaying older histories.

Use `--allow-open` only for inspecting an active or crashed stream. It does not assert completeness. A partial JSON record or a partially recorded engine operation can still fail replay; investigate rather than discarding the failing suffix.

For Azure, export **all** entities from one `handaudits` partition (including every continuation page), sort by `RowKey`, and write each `Frame` string as one UTF-8 line, without reformatting the frame JSON. Use a read-only auditor identity. Verify the resulting file with the corresponding key. Do not expose decrypted records or distribute folded cards to players.

## Statistical diagnostics

```powershell
dotnet run --project Verification/RngChecks.csproj -- stats 100000
```

This samples the actual production random source and reports card-position chi-square statistics, ordered-adjacency statistics and same-position matches between consecutive decks. It checks permutation integrity. Statistical values are diagnostics, not a pass/fail security certificate: entries within a shuffled deck are dependent, and repeated testing needs a prespecified analysis plan. Use controlled raw shuffle samples rather than player-selected showdown hands. A lab should establish formal test thresholds and independent production monitoring.

## Release and external assurance

The Azure deployment script creates an `audit-key` Container Apps secret; the update script adds one to older deployments and preserves it across revisions. Storage setup provisions `handaudits`. Updates still require an empty-table maintenance window. These changes do not deploy anything automatically.

Before making claims of external certification, commission an independent lab to assess the RNG, scaling, shuffle, dealing/privacy integration, deployment identity and access controls. No external certification is included here. Optional quantum hardware, public deck commitments, and multiparty randomness protocols remain separate projects requiring their own design and review; no public fairness-proof claim is made by these audit records.
