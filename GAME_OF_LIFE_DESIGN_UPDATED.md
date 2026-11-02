# Conway's Game of Life API
## Architecture decisions and design reference

**Project:** NetDocuments coding exercise | **Target:** C# / .NET 8 (`net8.0`)

This reference records the exercise requirements, selected architecture, assumptions, rationale, and testing considerations. `README.md` is the primary reference for running and using the application. `PLAN.md` groups behavioral slices and acceptance evidence; `PERFORMANCE.md` records measurements and operational limitations.

The design favors a pure engine, thin HTTP endpoints, direct local SQLite persistence, and built-in .NET facilities. The four assignment operations are represented by five routes to distinguish relative preview from absolute reconstruction. Performance work uses representative measurements rather than competing algorithms, codecs, or permanent benchmark infrastructure.

## A. Exercise requirements and constraints

**Objective:** Implement a production-conscious RESTful API for Conway's Game of Life in C# using .NET 8 (`net8.0`).

**Required functional behavior:**

1. **Upload Board State:** Accept a new board state (2D grid of cells); return a unique identifier for the stored board.
2. **Get Next State:** Given a board ID, return the next generation state of the board.
3. **Get N States Ahead:** Given a board ID and an integer N, return the board state after N generations.
4. **Get Final State:** Return a stable conclusion when the state stops changing or enters a repeating cycle; if a conclusion is not found within a reasonable number of iterations, return an appropriate error.

The exercise requires endpoints **at a minimum** and does not explicitly define N's starting point after advancement. The API supports both interpretations with separate relative-preview and absolute-generation GET routes. This adds a fifth route without per-generation storage or snapshot tables.

**Other explicit exercise constraints:** Board states must survive application restarts or crashes; design should be clean, modular, testable, validated, and resilient; no authentication or authorization required. Evaluated for correctness, code quality, architecture, production readiness, and ability to explain decisions. The exercise indicates roughly **4-5 hours of implementation effort** and favors quality and thoughtful design over perfect or overly complex solutions.

**Additional approved delivery constraint:** The project must be runnable and testable for free from a cloned repository, with no subscription, trial account, hosted cache/database, secrets, or external connection strings. Use local SQLite. Check in the code and simple reproducible schema initialization, not a generated user-data database. No database migration framework is required or approved.

**Critical interpretive distinction:** The requirements use the English verbs “Get Next State” and “Get N States Ahead”; they do not mandate HTTP `GET`. The approved contract uses `POST` for state-changing advancement, and `GET` for non-advancing retrieval/search.

---

## B. Decision register

The decisions below distinguish explicit requirements from documented assumptions and selected tradeoffs. Historical alternatives explain the choices; they are not additional requirements. Provisional operational values reflect workload-dependent tuning.

### Decision 1: Upload grid and internal representation

- API accepts a nonempty **rectangular 2D boolean JSON grid** under `cells`: `true` = alive, `false` = dead. Reject null cells and jagged rows.
- Width and height are derived from the uploaded grid and retained separately.
- Zero-based coordinates; `(0, 0)` is top-left, with `X` indexing columns and `Y` indexing rows (`cells[y][x]`).
- Game engine works with living-cell coordinates in `HashSet<(int X, int Y)>`. Absence from the set means dead; dead cells are not stored as coordinates.
- Keep the HTTP request/response representation, game-domain representation, and storage encoding independent.

**Rationale:** Familiar 2D API, efficient sparse computation. **Tradeoff:** Hash sets can consume more memory than a bitset when very dense.

**Tests:** Valid rectangles, empty/fully populated boards, correct orientation and coordinate mapping, reject empty/jagged/null/malformed grids, and round-trip reconstruction into original dimensions.

### Decision 2: Finite, nonwrapping boundaries

- A board has fixed finite dimensions for its lifetime.
- No wrapping across opposite edges. Out-of-bounds positions are treated as dead; births occur only in bounds.

**Documented assumption:** Boundary behavior was not specified. **Alternatives considered:** Toroidal wrapping and an unbounded expanding universe. The original theoretical infinite universe is not identical to this bounded interpretation.

**Tests:** Corner/edge neighbor counts, gliders reaching boundaries, 1x1 and other small board shapes, dimensions unchanged after advancement.

### Decision 3: Board-size safeguards and performance review

- **1,024 x 1,024 is the baseline size we expect to support and test, not the final production ceiling.** The earlier draft's “initial maximum 1,024” wording was superseded by this clarification.
- Test representative boards, including reasonably large boards and the support baseline, without requiring exhaustive size/density sweeps. Preserve the existing configurable request-size safeguard. Any additional dimension or cell-count limit requires measured need and developer review informed by actual application behavior and the lightweight performance sanity check.
- Minimum allowed grid is 1x1. Structural validation and configured request-size enforcement apply.
- **Scope boundary:** Do not add `MaxCells` or an arbitrary board-dimension ceiling without measured need and developer approval. The suggested 4,194,304-cell value has no measurement basis or .NET constraint behind it and is not an approved default. Preserve existing structural validation and Kestrel request-size safeguards; transport verification uses real Kestrel. This scope boundary does not remove the final-search computation budget.
- Use simple measurements and existing .NET tooling to check generation speed, cycle detection, memory consumption, JSON serialization, and persistence for meaningful performance problems. Identify an actual bottleneck and propose targeted improvements for developer review before changing the implementation; no dedicated benchmark framework or competing implementation is required.

**Documented assumption:** The assignment provides no maximum dimensions. Sparse internal representation alone cannot make an enormous **dense 2D JSON upload/response** practical.

**Tests:** Existing request-size boundaries, large sparse/dense/random boards, request and response size, restart/recovery as size scales. Add dimension/cell-count boundary tests only if those limits are subsequently approved.

### Decision 4: Stateful advancement (required endpoint #2)

- Board ID identifies a **stateful simulation** beginning at generation 0, not merely a fixed immutable snapshot.
- `Next State` computes from the last committed generation, advances by exactly one, persists the new state and generation together, and returns that generation.
- Each Next request requests one advancement from the current committed state. Concurrent successful requests commit consecutive generations without lost updates. An identical starting configuration produces the same next configuration (algorithmic determinism), but repeating an advance request can advance again, including after a client retransmission or response loss. There is no client request-token, deduplication, or original-response replay contract.
- Persist successfully committed progress so restart/crash can recover the last successful advancement. Only respond with success when the required write commits.

**Documented assumption:** “Get Next State” plus persistence is interpreted as advancing the stored board. It is not an explicit statement in the assignment; the API exposes the chosen behavior clearly.

**Tests:** Known pattern progression, sequential calls, persisted generation increments, restart recovery, failed writes, correct dimensions and response, simultaneous advances.

### Decision 5: Durable store versus optional caching; self-contained execution

- **SQLite is the source of truth** for current committed state, generation, and successful final-state discovery. The uploaded board starts as current state at generation 0. The Boards row additionally retains immutable InitialState for absolute reconstruction; restart recovery still uses current state/generation.
- An in-memory cache may speed up repeated calculations but is **not** durable and must never be the only copy of promised state.
- **No `IMemoryCache` is used.** An additional cache requires measured benefit. No Redis, hosted cache, or subscription.
- Cache eviction, service restart, or absence of a cache must not change correctness.

**Tests:** Correctness with cache absent/cleared (if subsequently implemented), SQLite recovery, no successful response on failed write.

### Decision 6: Advance versus preview (required endpoints #2 and #3)

- **Next:** Compute one new state from current, commit it, and increment `CurrentGeneration` by one.
- **N Ahead:** Compute a **preview** N steps after the *persisted current generation*; return it without changing current state or generation.
- A preview observes a consistent starting snapshot even if another request advances the board concurrently.
- Previously computed intermediate states need not be persisted. Generation caching was considered but is outside the selected scope (Decision 5).

**Tests:** Preview of N then Next advances only one, previews repeat deterministically from the same committed snapshot, concurrent preview/advance consistency, persistence unmodified.

### Decision 7: Relative stepsAhead semantics and zero

- `stepsAhead` means the number of generations **ahead of the committed current generation**, not an absolute generation index.
- `stepsAhead=0` returns current cells and absolute generation with no write and no engine calls. Positive values use the existing engine from one committed in-memory snapshot. Reject currentGeneration + stepsAhead overflow before engine work; zero remains valid at the maximum current generation.
- E.g., when current generation is 5: `states-ahead?stepsAhead=0` returns 5; `states-ahead?stepsAhead=3` returns 8.

**Documented assumption:** The specification does not explicitly identify N's reference point. Relative future preview is supported by the phrase “N States Ahead.” The absolute-generation route separately addresses reconstruction from upload (Decision 10).

**Tests:** N = 0, positive N, generation labels, repeated previews, invalid/unrepresentable N, and current-generation-plus-offset overflow.

### Decision 8: Stable outcomes and cycles (required endpoint #4)

- Both a fixed point and an oscillator count as a **successful final-state search conclusion**.
- **Stable:** A configuration repeats after one generation, cycle length 1.
- **Cycle:** A previously encountered configuration repeats after two or more generations, cycle length > 1.
- The primary outcome is whether the simulation stabilizes or enters a repeating cycle. **Representative convention:** for stabilization, return the verified stable configuration; for oscillation, return any verified configuration belonging to the detected repeating cycle. No canonical or first-repeated configuration is required, and no reconstruction solely to select such a representative is required.
- Preserve the existing response fields, including `generation`, `startingGeneration`, `conclusionKind` (`Stable` or `Cycle`), `cycleLength`, and `iterationsUsed`. `generation` must label the actual returned configuration on the trajectory from `startingGeneration`; `cycleLength` must be the verified period; `iterationsUsed` counts every actual generation-engine call, including additional detection/verification calculations. It is not a generation offset.
- Preserve correct cycle membership and accurate metadata for the dictionary-based implementation. The representative need not be canonical; this does not relax the unchanged persisted-result replay requirement in Decision 11.
- A cycle is not misrepresented as a single permanently frozen board.
- Verify equality exactly before declaring a cycle; fingerprints alone can collide.

**Documented interpretation:** We read the assignment's wording about states that no longer change or cycle as including oscillating states among successful conclusions.

**Rationale:** Stabilization or oscillation is the meaningful final outcome; representative selection and generation labels are supporting metadata. Requiring a particular representative adds algorithmic complexity without improving that outcome. Allow any verified representative while retaining exact correctness and reporting.

**Tests:** Still-life block, empty stable board, 2-period blinker, longer-period oscillators, transient then stable/cycle, hash collision simulation when applicable, and limit-edge behavior. Independently verify stable/cycle membership, the exact period, the returned configuration at its claimed absolute generation, and actual engine-call accounting. Accept any verified cycle member with its correct generation label; reject transient configurations, mislabeled phases, incorrect periods, and fingerprint-only matches. No assertion requires a canonical or first-repeated representative.

### Decision 9: Internal final-state computation budget and cancellation

- The Final State endpoint **does not accept `maxIterations` or any other caller-selected iteration budget**. The assignment calls for an error after a reasonable amount of work but does not require exposing this control to API consumers.
- Enforce **one internal maximum** on the number of actual calls to the generation engine during a *single final-state search*. The default is provisionally **10,000**, with an internal-only budget overload for focused tests and no public override. The call cap does not bound bytes retained by dictionary history. Count every generation calculation, including any collision-verification calculation; dictionary-based detection is the approved implementation.
- **Check the remaining budget before every generation-engine call**, including any collision-verification calculation. If the number of calls already made equals the cap, do not make another call. A conclusion proven using exactly the allowed number of calls succeeds; return `422` only when another calculation is required to establish the conclusion and would exceed the cap. Never perform an extra call and reject afterward. This clarifies the approved budget rule and requires no new infrastructure or abstractions.
- If a valid conclusion cannot be established without exceeding this computation budget, return the approved `422 Unprocessable Content` response. This is an **internal resource budget**, not a client-controlled promise about logical generations.
- Report `iterationsUsed` as **actual generation-engine calculations performed during the successful discovery**, not the difference between absolute generation labels. Do not confuse this with the absolute result generation or cycle length.
- Apply the work limit to **new calculations**. A previously persisted successful conclusion is returned by the lookup-first path in Decision 11 without running the engine or reapplying the current configured work limit.
- Pass ASP.NET Core request cancellation (e.g., `RequestAborted`) through expensive calculation. Check for cancellation at least between generation calls and, when practical, within costly steps. Searches never advance the board. Failure or cancellation observed before commit must leave no final-state result; cancellation or response loss after a successful commit cannot undo the saved conclusion (see Decision 14).
- Final State is synchronous. Background workers, durable jobs, and polling were considered but require measured need before expanding the synchronous design.

**Documented assumption:** The specification does not define a precise "reasonable" iteration count. A documented internal computation cap gives the service a bounded, testable failure condition without adding public API complexity.

**Tests:** Use independently established expected configurations and periods (for example, hand-verified block and blinker fixtures), not expected results generated by the engine under test. Assert successful discovery exactly at its required call limit, accurate `iterationsUsed`, and `422` when the next required calculation would exceed a smaller cap; assert that the forbidden extra call never occurs and exhaustion persists no result. If fingerprints are used as dictionary keys, force equal fingerprints for unequal configurations and verify that exact equality prevents a false stable/cycle conclusion. Retain still-life discovery in one engine call, longer oscillators, cancellation, and the absence of public `maxIterations` behavior.

### Decision 10: Absolute-generation retrieval from immutable initial state

- `GET /api/boards/{id}/generations/{generationNumber}` is provided alongside `GET /api/boards/{id}/states-ahead?stepsAhead=N`.
- `generationNumber` is a nonnegative absolute index from upload. Always calculate from InitialState, even when current progress is ahead of, equal to, or behind the target. Return the exact cells with response `generation` equal to generationNumber.
- generationNumber=0 returns original uploaded cells with zero engine calls, including after current cells change and SQLite reopens. Both GETs return 404 for unknown boards and never mutate initial/current state or generation, persist derived results, or calculate/store/invalidate final-state results.
- With current generation 5, preview offsets 0 and 3 return generations 5 and 8; absolute targets 0, 3, and 8 reconstruct those generations from upload.
- Boards has one immutable InitialState column. Upload writes InitialState = CurrentState and CurrentGeneration = 0 in one successful insert. Next never changes InitialState. Reuse the engine, JSON coordinate codec, and cancellation support. Retrieval has no numeric computation cap; the final-state budget is separate.
- No initial-state/snapshot tables, per-generation persistence, caches, speculative shortcuts/checkpoints, new codecs, migration frameworks, or endpoint-specific computation frameworks.

**Database caveat:** CREATE TABLE IF NOT EXISTS does not upgrade an incompatible older table. Use a fresh file for disposable development data; retaining non-disposable data requires an explicit migration policy. Original cells cannot be inferred from advanced current state. InitialState enables absolute reconstruction; restart recovery uses committed current state/generation.

**Tests:** Durable identical initial/current upload values; original cells after progress and reopen; zero calls for zero; independent positive fixtures with current progress greater/equal/less than target; unchanged stored values; unknown/invalid IDs and quantities; generation-addition overflow rejected before calculation. Milestones 4a-4f in PLAN.md group this evidence. Duplicate-query semantics are unspecified.

### Decision 11: On-demand, lookup-first final-state persistence

- `FinalStateResults` holds **at most one successful computed conclusion per board**. A board upload, Next State call, relative preview, and absolute-generation retrieval **must not** precompute, store, or invalidate a final-state result.
- On **every** `GET /api/boards/{id}/final-state` request, **first check SQLite for an existing successful result by `BoardId`**, before invoking the generation engine.
- **Cache hit:** Return the stored response directly, with **zero generation calculations**. The result is reusable across application restarts and subsequent Next State advancements.
- **Cache miss:** Read a consistent snapshot of the board's *current committed generation* and compute forward from that snapshot within Decision 9's internal work limit. Do not advance or modify the committed board.
- **Successful calculation:** Persist the conclusion in `FinalStateResults` and return it. A failed, exhausted, or canceled calculation must not create or replace a result. Cancellation after a successful persistence commit does not remove the committed result.
- **Next State does not invalidate** `FinalStateResults`. Conway's deterministic rules mean every later generation of this simulation reaches the same eventual fixed point or periodic orbit, even if a cycle's displayed representative configuration differs by phase.
- Persist an explicit representative configuration, its `ResultGeneration` (absolute generation label **from the snapshot used for the first successful calculation**), `StartingGeneration`, `CycleLength`, and `IterationsUsed` (actual engine calls). Derive or include `Stable` versus `Cycle` in the response. The saved representative generation **is not promised to be in the future of a later current generation**; it describes the discovery performed on the first successful call. Do not silently re-label old results as newly computed ones.
- Select the representative using Decision 8's approved any-verified-state convention. Once the first successful conclusion is persisted, return its configuration and all saved metadata unchanged on every subsequent request, including after advancement/restart; do not select a new cycle phase or reset `IterationsUsed` to zero on a lookup hit. Zero new engine calls does not alter the saved discovery count.
- Use a **unique `BoardId` constraint** in `FinalStateResults`. Two concurrent first-time requests may both miss and compute. Only one successful record wins; the other must read and return that persisted record rather than overwrite it or return contradictory representative metadata. Avoiding duplicate computation while both are in flight is optional; correctness and first-result reuse are required.
- Because the conclusion is board-wide and permanently reusable, a concurrent Next advancement does **not** make an otherwise successful final-state result stale. No starting-generation guard or Next-driven invalidation is needed for this chosen semantics.

**Rationale:** Compute only on explicit demand, once a success is available; reuse a deterministic eventual outcome independently of ordinary board progression. No general-purpose cache infrastructure is needed.

**Tests:** No result exists immediately after upload, Next, or preview; first Final State call calculates and persists one result; second call performs **zero** engine calculations and returns the exact saved representative and metadata; reuse is unchanged after restart and further Next calls; unsuccessful/canceled searches leave no record; simultaneous first calls may calculate different valid representatives but produce only one persisted winner, and both return that winner's configuration and metadata; an in-flight Final State calculation may safely finish while Next advances; stable and oscillating results accurately describe their original discovery snapshot.

### Decision 12: Optimistic concurrency and bounded Next retries

- Calculate Next outside database write transactions. Use an optimistic update against the expected current generation, committing the next state and incremented generation atomically in SQLite.
- For concurrent Next requests, a generation conflict causes the loser to reload the latest authoritative state, calculate again, and retry up to a configurable bounded count. Ordering of concurrent requests is not guaranteed. Exhausted optimistic conflicts return `409 Conflict`.
- Treat every client Next request as a separate advancement request. Do not introduce request tokens, deduplication records, saved Next responses, expiration metadata, or a replacement retransmission contract.
- Distinguish expected-generation conflicts, genuine SQLite contention, transaction failures, unexpected failures, and cancellation using Decision 14. Dispose failed resources before retrying; a failed expected-generation update always requires a fresh authoritative snapshot and recalculation.
- Roll back the entire state/generation change on pre-commit failure. Success requires a confirmed commit. Never automatically repeat an ambiguously committed advancement; a later generation does not prove which request committed. A confirmed commit remains durable even if its HTTP response is lost.
- Advancing a board **does not invalidate FinalStateResults** (Decision 11). No Redis, locks outside SQLite, or distributed services are needed.

**Rationale:** The assignment requires durable progress and correct concurrent advancement. Keep those guarantees without adding an additional client-facing retransmission contract.

**Tests:** Concurrent requests reading the same generation produce consecutive correct committed states; prove that the optimistic loser reloads/recalculates from the winner's committed state. Verify bounded optimistic retry exhaustion, durable progress after restart, atomic rollback of state and generation, classified/bounded SQLite contention, sanitized unexpected errors, cancellation/resource cleanup, and no automatic re-advancement after response loss or an uncertain commit. Verify that Next preserves saved FinalStateResults. Use real file-backed SQLite connections and deterministic synchronization with narrowly scoped rollback/contention fault injection.

### Decision 13: Dictionary-based cycle detection

- Use a straightforward dictionary-based cycle-detection algorithm, retaining previously encountered board states and their generation indexes. Verify actual board-state equality before identifying a cycle; a matching fingerprint alone is insufficient.
- Preserve accurate stable/cycle conclusions, cycle lengths, generation labels, cancellation, and the approved any-verified-representative convention. Do not reconstruct a canonical or first-repeated representative solely for presentation.
- **The computation-budget rule is unchanged** (Decision 9): Check before every engine call, count all actual calls, and enforce the configurable internal cap (provisionally 10,000). Never report a conclusion that was not proved within the budget.
- Do not implement Brent's algorithm or competing cycle-detection implementations solely for performance comparison. Consider Brent only if actual measurements demonstrate a significant performance or memory problem, then propose a targeted change for developer review before implementation.
- Use the final lightweight performance sanity check to identify actual problems. No mandatory algorithm comparison, performance-selection gate, temporary benchmark project, or dedicated benchmarking framework is required.

**Documented assumption:** The exercise mandates correct cycle detection, not speculative optimization. No caller-defined iteration parameter exists.

### Decision 14: SQLite schema, storage, transactions, and codecs

**Two tables:**

- `Boards`: `Id` (GUID), `Width`, `Height`, immutable `InitialState`, `CurrentState`, and `CurrentGeneration`.
- `FinalStateResults` (Slice 8): Unique `BoardId` (FK), `StartingGeneration`, `ResultGeneration`, `ResultState`, `CycleLength`, `IterationsUsed`, and an optional explicit conclusion kind. **No requirement to match a later current generation** when reading a successful conclusion.

**Persistence and encoding:**

- Upload starts at generation 0 and writes identical initial/current states in one insert. Retain initial state unchanged for absolute reconstruction, with no intermediate generations. Every successful Next atomically persists only current state/generation. Relative preview reads the current pair together in one query and computes from that in-memory snapshot outside database resources. Only Final State endpoint may produce a durable `FinalStateResults` record, and only after success.
- **Persistence encoding:** Use built-in System.Text.Json to serialize living-cell coordinates as JSON for SQLite persistence. Keep width and height as separate database fields and reconstruct `HashSet<(int X, int Y)>` on deserialization. Use a minimal coordinate data type; no custom converters, serialization frameworks, or unnecessary interfaces.
- Custom binary encoding and alternative-codec implementation/benchmarks are **deferred unless actual performance measurements demonstrate that JSON serialization or storage is a meaningful bottleneck**. The simple JSON codec remains selected unless measured evidence justifies a targeted alternative.
- Use Microsoft.Data.Sqlite directly with parameterized SQL and simple reproducible schema initialization from checked-in code. Do not introduce Entity Framework Core, Dapper, other ORMs, or database migration frameworks without explicit approval; no external database service.
- Preserve separate connections per operation and the approved WAL, foreign-key enforcement, and `synchronous=FULL` durability settings.

**Atomicity and concurrency:**

- Perform generation calculations outside database transactions. Commit the current board's state and generation together using an optimistic expected-generation check.
- On an optimistic Next generation conflict, reload authoritative state, recalculate, and retry within the configured bound. Handle SQLite locking separately under the failure policy below (Decision 12).
- **Do not delete or invalidate FinalStateResults during Next**. A successful final-state result is an eventual deterministic outcome of the board's trajectory, not a materialized version of the current generation.
- For Final State, check for a stored result **before simulation**, insert only after successful computation, and enforce unique `BoardId`. On simultaneous first-time calculations, the losing insert reads the existing winner's record and returns that persisted result. A concurrent Next advancement does not invalidate the final conclusion.
- Return success only after required writes commit or authoritative verification establishes that the relevant write committed. Failed or exhausted Final State calculations and cancellation observed before commit do not create records. Cancellation or HTTP response loss after a successful commit does not undo it.

**Failure-handling requirements:**

| Failure type | Required handling |
| --- | --- |
| Optimistic generation conflict | A conditional update that affects no row is not a SQLite locking exception. End the failed attempt, reload the latest authoritative board, recalculate, and retry within the configured bound. If the board no longer exists, use the existing `404` convention. Genuine concurrency retry exhaustion remains `409`. |
| SQLite busy/locked condition | Classify `SqliteException.SqliteErrorCode` and `SqliteExtendedErrorCode` together with the operation and connection state. Temporary lock contention is distinct from an expected-generation conflict. Account for provider busy waits; do not retry every busy/locked exception indiscriminately. |
| Constraint failure | Handle foreign-key, check, not-null, and unexpected unique/primary-key failures as database errors. The known FinalStateResults uniqueness conflict follows the saved-winner rule in Decision 11; unrelated constraints must not be mistaken for that conflict. |
| Transaction failure before commit | Roll back the whole advancement: current state and generation increment form one atomic unit. A statement failure does not justify retaining earlier writes. Dispose of readers, commands, transactions, and connections; cleanup failures must not obscure the original failure or lead to reuse of the connection. |
| Unexpected database failure | Do not blindly retry I/O, corruption, full/read-only database, invalid schema, or other unclassified failures. Return sanitized `500` ProblemDetails when a response is possible; expose no SQL, database path, provider exception text, or stack trace. Keep diagnostic details in server logs with a correlation identifier. |
| Cancellation | Observe request cancellation before calculations, attempts, database operations, and commit. Stop new work/retries and safely clean up an active transaction. Cancellation/disconnection does not establish whether a commit occurred and cannot reverse a completed commit. No new cancellation status code is introduced. |

**Storage mechanics:**

BoardService owns bounded optimistic-conflict retries; SqliteBoardStore owns SQL, transaction boundaries, provider waiting, and cleanup. A verified zero-row conditional update is the sole application retry condition. Busy/locked and other provider exceptions propagate to sanitized `500`.

- Keep this logic in `BoardService` and `SqliteBoardStore` with normal .NET exception handling and small operation-specific methods. No generic retry framework, distributed locks, external service, or background retry worker. Use private-cache, separate connections per attempt; do not share active connections/readers across concurrent requests. Prefer a short non-deferred write transaction after calculation, protecting the conditional state/generation update without retaining a read transaction during simulation.
- Use one bounded optimistic-conflict retry loop with a configurable attempt count and finite provider waiting. Before every eligible retry, finish cleanup, open a fresh connection/transaction, reload authoritative state, and recalculate; never carry a failed attempt's board snapshot into a new calculation.
- Let Microsoft.Data.Sqlite perform its built-in ordinary busy/locked waits using finite `DefaultTimeout`/`CommandTimeout` settings, including implicit commands such as transaction start. Do not add a second sleep/backoff loop that restarts a full provider timeout after ordinary contention times out. Next defaults provisionally to three total attempts and uses positive finite provider Default Timeout (default 30 seconds), including implicit commands. PLAN.md accounts conservatively for command-wait windows; timeout polling can overshoot and this is not a hard request deadline. No second busy-retry loop restarts a provider wait window.
- Distinguish ordinary `SQLITE_BUSY` (including known transient recovery/timeout variants) from `SQLITE_BUSY_SNAPSHOT`, which requires ending the stale transaction and restarting with fresh reads rather than waiting inside it. An ordinary provider busy-timeout expiry ends the contention path instead of granting another full wait window. `SQLITE_LOCKED` commonly reflects a same-connection/shared-cache resource conflict; with separate private-cache connections, treat it as an unexpected resource-lifetime defect unless a concrete transient case is demonstrated and explicitly approved for bounded retry. Do not enable shared cache merely to make it retryable.
- For a competing `FinalStateResults` insert, recognize the known unique-BoardId conflict, end the losing attempt, and read the saved winner without recalculating or invalidating it. Do not use `INSERT OR REPLACE`, overwrite the winner, or ignore unrelated constraint failures.
- If `Commit` throws, determine whether the transaction remains active; SQLite can leave it active after an error. End/roll back any still-active attempt and dispose of the connection. An uncertain Next commit outcome must not trigger automatic re-advancement; return sanitized `500` when its outcome cannot be established. Unexpected commit errors are nonretryable.
- A later generation cannot identify which request committed. A response failure after a confirmed commit is a transport failure, not permission to rerun database work. For upload/final-result recovery, verify the already assigned unique board/result identity rather than infer success from unrelated progress; an unavailable read does not establish absence or success.
- Microsoft.Data.Sqlite's async ADO.NET calls execute synchronously. Do not promise immediate cancellation merely because a token was passed to an async method. Check cancellation around synchronous operations, keep provider waits finite, and measure the selected provider version's cancellation behavior. If cancellation arrives during an operation, clean up when control returns; do not schedule another attempt. Preserve confirmed commits, do not launch background work, and do not reuse the canceled request token as the only means of transaction cleanup.

**HTTP failure mapping:** Genuine optimistic retry exhaustion returns `409`; SQLite busy/locked exceptions and unexpected or unverifiable persistence outcomes return sanitized `500`. Extending `409` to temporary SQLite contention was considered but is outside the contract. Database errors do not use final-search `422`, and cancellation introduces no new status.

**Provider references:** [Microsoft.Data.Sqlite errors and timeouts](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors), [transaction behavior](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions), [async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async), [SQLite result codes](https://sqlite.org/rescode.html), and [SQLite transaction/commit behavior](https://sqlite.org/lang_transaction.html). The policy accounts for provider behavior; SQLite does not supply an end-to-end application retry guarantee.

**Tests and performance review:** JSON coordinate round-trip for empty/populated boards and clear failure for invalid stored JSON; durable board progress across restarts/crashes; atomic Next state/generation rollback; concurrent Next without lost updates and with authoritative reload/recalculation; bounded contention handling; no automatic re-advancement after an uncertain commit or response loss; final result lookup-first and zero engine calls on hit; concurrent first-time final searches (one saved result), Next/Final interleavings, restart reuse, response serialization, database size, and JSON serialization/storage costs.

### Decision 15: HTTP API, validation, and error convention

| HTTP method | Route | Effect |
| --- | --- | --- |
| `POST` | `/api/boards` | Persist identical initial/current states at generation 0; return `201`, unique ID, common state data, and live-current Location; do not compute final state |
| `POST` | `/api/boards/{id}/next` | Advance once and commit the new current state |
| `GET` | `/api/boards/{id}/states-ahead?stepsAhead=3` | Return three steps ahead of one committed current snapshot, without writes or final-state computation |
| `GET` | `/api/boards/{id}/generations/{generationNumber}` | Reconstruct absolute generation from InitialState, without writes or final-state computation |
| `GET` | `/api/boards/{id}/final-state` | **Look up a stored successful conclusion first**; compute/persist it only on a miss; do not advance the board |

**Generation contract:** Relative query naming is `stepsAhead`; the absolute path parameter is `generationNumber`. Common response `generation` remains an absolute label. The public Final State API still has no `maxIterations` query parameter; its budget remains internal (Decision 9).

**Upload example:**

```json
{
  "cells": [
    [false, true, false, false],
    [true, false, false, true]
  ]
}
```

**Common board-state response shape (upload example, HTTP `201 Created`):**

```json
{
  "boardId": "00000000-0000-0000-0000-000000000000",
  "generation": 0,
  "width": 4,
  "height": 2,
  "cells": [
    [false, true, false, false],
    [true, false, false, true]
  ]
}
```

- Upload returns `201 Created` with the generated board ID, correct dimensions, generation 0, cells using the common state response, and `Location: /api/boards/{id}/states-ahead?stepsAhead=0`. This URI represents the **current** board and does not guarantee immutable access to generation 0 later; the absolute-generation URI denotes an immutable logical generation and does not replace the live-current Location resource.
- Next, relative preview, and absolute retrieval use the common state DTO with absolute generation numbers. Final State retains the existing fields `generation`, `startingGeneration`, `conclusionKind`, `cycleLength`, and `iterationsUsed` alongside the board configuration. Return the verified stable configuration or any verified member of the repeating cycle, with accurate absolute generation, period, and **actual engine calls used when the stored result was discovered**. No canonical or first-repeated representative is required. Preserve saved responses unchanged and do not imply that the cached representative is always later than today's current generation.
- Validate nonempty rectangular boolean grids; reject `null`, jagged and malformed JSON and invalid IDs. Preserve existing request-size safeguards; additional dimension/cell-count caps remain deferred under Decision 3. **1,024 x 1,024 remains a testing baseline, not a final ceiling**.
- **Resource-protection verification:** Configure Kestrel's request-body limit before body reading/JSON binding, using existing ASP.NET Core facilities. For a known oversized `Content-Length`, reject before deserializing the grid; retain server enforcement for chunked/unknown-length bodies, stopping when the limit is crossed without buffering the full upload. Some bounded reading/parsing is unavoidable for unknown-length bodies; do not promise zero allocation. Verify these paths with actual Kestrel, not only TestServer. Kestrel request-size configuration provides the payload safeguard; no additional dimension/cell-count cap is configured. Small test overrides are not production defaults. Native chunked accounting includes framing overhead.
- Preview `stepsAhead` and absolute `generationNumber` are nonnegative integer quantities. Both zero cases perform no engine calls or writes. Positive values reuse the engine and request cancellation without a numeric computation cap. The 10,000-call final-search budget does not apply to retrieval.
- Final State uses a **internal computation budget only**, provisionally defaulting to 10,000 actual engine calls; on exhaustion return `422 Unprocessable Content`. It has no public budget parameter.
- Return standardized ASP.NET Core `ProblemDetails` errors without internal stack traces; propagate cancellation for expensive computations.
- **ProblemDetails verification:** A `413` status does not itself guarantee a ProblemDetails body. Use the existing `AddProblemDetails`, status-code pages, and exception handling where applicable; preserve a body-read `BadHttpRequestException`'s `413` rather than converting it to `400`/`500`. Test supported JSON `Accept` headers, absent `Accept`, and unsupported media preferences in Production configuration. Require sanitized `application/problem+json` with body `status: 413` on the application's supported JSON error paths. Server-level rejections outside the app pipeline or a disconnected/started response cannot be assumed to support that envelope; supported JSON paths are verified on real Kestrel, while unsupported Accept and rejections outside middleware retain the envelope limitations above. [.NET 8 body-binding source](https://github.com/dotnet/aspnetcore/blob/v8.0.0/src/Http/Http.Extensions/src/RequestDelegateFactory.cs), [ASP.NET Core ProblemDetails guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-8.0).

| HTTP status | Chosen convention |
| --- | --- |
| `200 OK` | Successful retrieval/states-ahead/advance or final-state conclusion |
| `201 Created` | Upload succeeded |
| `400 Bad Request` | Invalid board content/shape or invalid supplied parameters |
| `404 Not Found` | Unknown board ID |
| `409 Conflict` | Verified optimistic concurrency retries exhausted; SQLite busy/locked uses sanitized `500` |
| `413 Content Too Large` | Request exceeds configured payload limit |
| `422 Unprocessable Content` | Final State needs another calculation to prove a conclusion, but that call would exceed its internal budget |
| `500 Internal Server Error` | Unexpected server failure |

- A caller can receive no HTTP response after disconnect/cancellation. Only successfully committed writes are reported as successful.
- Database errors follow Decision 14: approved optimistic retry exhaustion is `409`, unexpected or unverifiable failures use sanitized `500`, and cancellation introduces no new status. The considered extension of `409` to SQLite contention is outside the contract.
- Check large response serialization/size separately during the lightweight performance review from engine execution and persistence; coordinate JSON persistence does not make dense rectangular API JSON cheap.
- No authentication or authorization, as specified. The API includes absolute-generation reconstruction from InitialState and no general-purpose cache.

**Required API tests:** Five routes under the documented contract; consistent DTO and error semantics; invalid/empty/jagged/null/big inputs; unknown IDs; correct relative stepsAhead and absolute generationNumber semantics, zero calls for both zero cases, and nonmutating retrieval; upload and Next do not create or invalidate a final result; first Final State computes and persists; subsequent call returns stored result **without calling the engine**, including after restart/Next; internal-budget exhaustion returns `422` without storing; concurrent Next requests commit consecutive correct generations without lost updates; concurrent Final State calls; rollback, cancellation, and large JSON response behavior.

## C. Test strategy and acceptance criteria

Use focused coverage rather than one redundant test per decision paragraph. Unit tests establish pure rules and discovery; real file-backed SQLite establishes persistence/transaction behavior; real Kestrel establishes transport enforcement. Numbered slices in PLAN.md group related behavior.

### Slice 3: Durable board upload

The eleven slices in PLAN.md group engine/codec behavior, upload, relative and absolute retrieval, atomic/concurrent Next, bounded discovery, durable/concurrent final results, HTTP resources, and performance verification. The upload boundary covers:

1. Accept a valid rectangular boolean grid through `POST /api/boards`.
2. Validate basic board structure and configured resource safeguards.
3. Convert the request into the existing `BoardState` representation.
4. Persist the board in SQLite at generation 0 using the existing System.Text.Json living-cell coordinate serialization.
5. Return `201 Created` with the generated board ID and common state response data; include the live-current Location header.
6. Reopen the database and reconstruct the stored board correctly.
7. Return an appropriate sanitized error and no successful upload response when persistence fails.

Upload writes only `Boards`, including immutable `InitialState`, current state, dimensions, and generation zero, using parameterized Microsoft.Data.Sqlite and the existing JSON coordinate codec. Keep initialization simple and reproducible; no migration framework, general cache, or automatic final-state discovery is needed. Structural validation and native request-size safeguards apply.

**Focused Slice 3 acceptance tests:**

1. Valid asymmetric upload returns `201`, a unique board ID, correct dimensions/cells, generation 0, and the live-current Location.
2. Invalid requests are rejected, including null, empty, jagged, and malformed grids; cover configured basic safeguards.
3. After closing and reopening real file-backed SQLite, retrieve through the storage layer and reconstruct identical dimensions, living cells, and generation. Reconstruction can be verified through the store and read endpoints.
4. A failed database write returns an appropriate error, never a successful upload response.

### Cross-slice test strategy

The coverage below includes durable initial/current identity, unchanged InitialState after Next, live-current Location, atomic advancement, saved conclusions, cancellation, transport protection, and exact final-search budgets.

1. **Pure engine unit tests:** Moore-neighborhood rules (birth at 3, survival at 2/3, death otherwise); all-dead, block, blinker, and other known patterns; sparse coordinates, 1x1 / 1xN / Nx1 / asymmetric grids, boundaries, immutable input, and exact expected configurations derived independently.
2. **Cycle and internal-budget tests:** Stable period 1, oscillators of period >1, transient then cycle, exact collision-safe equality, cancellation, success and exhaustion at the **actual generation-engine call count** boundary. Independently verify that each returned representative belongs to the eventual stable state/cycle, matches its claimed generation, and reports the exact period and engine-call count. Reject transient representatives, inaccurate labels/periods, and uncounted extra calculations; do not require a canonical representative or a competing algorithm.
3. **SQLite integration tests:** Upload/reload, durable current progress across restarts, atomic Next state/generation commit and rollback, no automatic re-advancement after response loss or an ambiguous commit, first Final State **lookup before calculate**, one successful saved result, zero engine calls on cache hit, reuse after Next and restart, and no result from failed calculation or cancellation observed before commit. Inject a database failure after the conditional board update but before commit and assert that both state and generation roll back. Cover cancellation during database work, rollback/cleanup failure handling, unrelated constraints, and sanitized unexpected errors. All database guarantees require real file-backed SQLite connections, not mocks alone.
4. **HTTP integration tests:** Five routes under the documented contract, real serializer and validation, exact status and `ProblemDetails`, relative stepsAhead=0/positive previews, absolute generationNumber=0/positive reconstruction from InitialState, final-state response snapshot semantics, cancellation, unchanged InitialState after Next, no public `maxIterations` behavior, and response size. Add the focused resource/validation cases below; TestServer alone is insufficient evidence for Kestrel request-size enforcement.
5. **Concurrency tests:** Barrier-synchronize Next requests reading the same generation; assert one loser reloads authoritative state and recalculates, and that the two successful responses represent consecutive correct committed generations without lost updates. Test bounded optimistic-conflict exhaustion. Hold a real writer lock to exercise provider busy waiting, release it deterministically for success, and hold it through configured exhaustion for sanitized `500`. Stale-snapshot busy and same-connection locked cases are exploratory provider investigations, distinct from ordinary writer contention. Two first-time Final State requests save one winner and return its record; unrelated constraint failures remain errors. Concurrent Final State calculation and Next advancement never corrupt current progress or invalidate a discovered eventual conclusion. Use deterministic barriers/events and narrowly scoped SQLite/test-harness faults; do not use sleeps to arrange races or build a general fault-injection framework. Finite watchdogs may detect hangs, but normal tests must not assert performance latency targets. Account for provider timeout behavior separately from verified generation conflicts.
6. **Final performance sanity check:** Use representative boards, including reasonably large boards and the 1,024 x 1,024 support/test baseline, with simple measurements and existing .NET tooling. Check generation speed, dictionary-based cycle detection, memory, JSON serialization, and persistence only to identify meaningful performance problems. No exhaustive workload sweep, comparative benchmark, dedicated framework, or alternative implementation is required. Propose targeted improvements for developer review only if actual measurements justify them; performance observations do not establish functional correctness.

**Retrieval evidence:** Durable identical initial/current upload values; accurate labels and unchanged storage for both GETs; zero engine calls for zero; independent positive fixtures with current progress greater/equal/less than target; original cells after progress and reopen. Retrieval has no numeric computation cap.

**Focused HTTP validation/resource acceptance criteria:**

- Run a small group in the existing integration-test project against the production app composition hosted by real Kestrel on an ephemeral loopback port, using normal `HttpClient`, the same middleware, and temporary file-backed SQLite. Keep WebApplicationFactory/TestServer for ordinary HTTP tests; no additional project, server framework, proxy, or external infrastructure is needed.
- For configured body limit `L`, construct valid UTF-8 JSON bodies of exactly `L-1`, `L`, and `L+1` bytes using harmless whitespace padding and structurally valid boards. Below/at the limit, accept valid uploads; above it, return `413`. Known Content-Length cases use payload bytes; HTTP/1.1 chunked/unknown-length cases use transmitted bytes including framing. Native framing accounting is retained. Test application early rejection and Kestrel's native enforcement independently so an application guard cannot mask a missing server limit. Configure any per-request size feature before reads, not in an endpoint filter that runs after JSON binding.
- Send very wide single-row boards and large sparse/dense rectangles, including the 1,024 x 1,024 support baseline, with an adequate test-configured request-size limit; use small overrides for routine byte-boundary cases. Dimension/cell-count boundary cases apply only when such limits exist; keep body size within its limit when testing such validation (`400`) and isolate it from payload rejection (`413`). Assert orientation/round-trip correctness, no arithmetic overflow in size validation, and no persisted board on rejection. Check larger practical workloads where useful during the lightweight performance review; the baseline is not a production ceiling.
- For oversized uploads, verify rejection before full grid materialization/domain mapping or SQLite writes. Use narrowly scoped test observations of body-read/binding entry where needed; a `413` assertion alone does not prove resource protection. Known-length rejection should precede JSON parsing; streamed requests may parse a bounded prefix but must not buffer/materialize the entire oversized grid. Measure allocation/peak-memory effects separately during the lightweight performance review instead of asserting arbitrary memory/timing thresholds in ordinary tests.
- Preview `stepsAhead` validation: focused missing/empty, malformed noninteger, negative, and numeric parse overflow cases using standard ASP.NET Core Minimal API binding. Duplicate-query semantics, including identical repeated values, are unspecified; no additional matrix is required. Add custom parsing only if an actual reviewed contract requirement demonstrably demands it. Seed a real persisted generation near its representable maximum, then use an otherwise valid `stepsAhead` whose addition would overflow: check before engine work, return `400`, and leave state unchanged; `stepsAhead=0` at the maximum remains valid. Do not wrap generation labels or return `500`/final-search `422` for this validation failure.
- Cancel real expensive generation work through request cancellation, covering relative preview, absolute-generation retrieval, and an uncached final-state search. Coordinate entry/cancellation with the existing test controls, verify the server observes cancellation and stops further calculations, and verify no current-state mutation or final-result write before commit. Do not treat a canceled client await as proof of server cancellation or require a particular response after disconnect. Include cancellation checks within a costly engine call as well as between calls; measure latency separately.

**Kestrel/413 evidence:** Kestrel checks oversized declared length when reading starts, and Minimal API binding preserves the HTTP status. Real ASP.NET Core 8.0.33 tests verified `413` ProblemDetails on supported JSON paths. Unsupported Accept can use a middleware fallback; server-level rejections outside middleware and disconnected/started responses have no universal envelope guarantee. [.NET 8 Kestrel body-size check](https://github.com/dotnet/aspnetcore/blob/v8.0.0/src/Servers/Kestrel/Core/src/Internal/Http/Http1ContentLengthMessageBody.cs), [server error-handling boundary](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-8.0#server-exception-handling).

Use an independent correctness oracle. Assert exact configurations, persisted state/generation, saved metadata, and actual engine calls rather than non-null results, success status, or mocks alone. Deterministic synchronization and focused failure injection should target concrete failure modes without a general fault framework.

## D. Operational boundaries and alternatives

1. **Board and payload limits:** Structural validation and native Kestrel request-size enforcement are retained. No additional `MaxCells` or dimension ceiling is configured; 1,024 x 1,024 is a support/test baseline, not a permanent maximum. The earlier 4,194,304-cell suggestion had no measured basis. Additional limits require evidence of need, not an assumed .NET ceiling.
2. **Computation limits:** Final State defaults provisionally to 10,000 actual engine calls, with an internal-only budget override and no public query override. Relative and absolute retrieval have no numeric computation cap. Large valid requests can occupy CPU/request threads until cancellation; a server-controlled timeout is an optional production safeguard, not an implemented limit, and cannot immediately interrupt synchronous SQLite waiting.
3. **Cycle detection:** Dictionary-based detection with exact equality is selected. Retained history can grow with the number and population of distinct states; a call budget is not a byte bound. Brent or another detector requires evidence of a significant performance/memory problem; no algorithm-selection gate is required.
4. **Persistence encoding:** System.Text.Json living-coordinate serialization keeps width/height separate. Dense JSON reconstruction has measured allocation and execution costs. Alternative codecs or custom binary formats require a demonstrated bottleneck rather than speculative implementation.
5. **SQLite waits and retries:** Next defaults provisionally to three total optimistic attempts and reloads/recalculates only after a zero-row conditional update. Positive finite provider `Default Timeout` (default 30 seconds) handles ordinary waiting, including implicit transaction commands. Busy/locked exceptions return sanitized `500`; the considered `409` extension is outside the contract. Synchronous waits may delay cancellation. See PLAN.md for conservative command-wait accounting and exploratory failure scenarios.
6. **Location and saved results:** Upload Location resolves to live current state. Absolute generation zero remains the original upload. `FinalStateResults` is durable memoization, not a general-purpose cache, and Next never invalidates it.
7. **Performance and scope:** The paired direct-neighbor-counting optimization reduced cumulative allocations while retaining the representation and rules; detailed historical and follow-up measurements remain separate in PERFORMANCE.md. Cumulative allocations are not peak/retained memory or evidence of a leak. Measurements do not establish arbitrary-density, generation-count, or concurrent-load capacity. Rectangular JSON payloads, local SQLite, and the three-project solution remain the selected scope.

No client request-token, deduplication, saved Next-response, expiration contract, per-generation storage, general memory cache, background worker, or external service is required. Each Next request is a separate advancement. Atomic durable state/generation updates, authoritative reload/recalculation, rollback, sanitized errors, cancellation/resource cleanup, and exact bounded final-state discovery remain required.
