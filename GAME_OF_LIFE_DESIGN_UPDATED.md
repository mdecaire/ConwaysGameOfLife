# Conway's Game of Life API
## Architecture decisions and implementation-planning handoff

**Project:** NetDocuments coding exercise | **Target:** C# / .NET 8 (`net8.0`) | **Document status:** Design baseline for Codex planning, not authorization to implement

> **Instructions for Codex:** Read this document in full. Produce a phased, reviewable implementation and testing plan based on the approved decisions below. **Do not write application code until the developer has reviewed and approved the plan.** Do not silently change decisions, treat provisional choices as final, or infer that deferred features are required. Highlight ambiguities, inconsistencies, and proposed modifications separately. This document is the design context, not a request to create a framework or introduce extra infrastructure.

### Planning collaboration contract

1. Distinguish the exercise's **explicit requirements**, our **approved design decisions**, **documented assumptions**, **provisional choices**, **deferred options**, and **unresolved details**.
2. Map each of the four required endpoints to design components, persistence behavior, acceptance tests, and potential failure modes. Treat historical retrieval as deferred, not in initial scope.
3. Propose a minimal, maintainable .NET 8 architecture with a pure Game of Life engine, separable persistence, and thin HTTP endpoints. Prefer established libraries and built-in .NET facilities; no paid services, trials, or external infrastructure.
4. Recommend incremental test-driven development: review candidate tests, challenge coverage and edge cases, then implement small red-green-refactor steps. Stop for developer review at agreed checkpoints. Do not silently implement the entire system.
5. Include a benchmark and decision-gate plan for board-size ceilings, sparse/dense performance, JSON persistence costs, and cycle-detection algorithms. Avoid prematurely hard-coding a 1,024-by-1,024 production maximum.
6. Respect the approved internal-only final-state computation budget (actual generation-engine calls), the locked configurable idempotency-retention default of 24 hours from successful transaction commit, and lookup-first final-state memoization. Flag only still-open details, such as retry/timeout values and algorithm selection; do not reopen the retention default without evidence and explicit developer approval.
7. Present a coherent plan first, not source code. Quality, correctness, resilience, and discussion-ready tradeoffs matter more than elaborate architecture.

---

## A. Exercise requirements and constraints

**Objective:** Implement a production-conscious RESTful API for Conway's Game of Life in C# using .NET 8 (`net8.0`).

**Required functional behavior:**

1. **Upload Board State:** Accept a new board state (2D grid of cells); return a unique identifier for the stored board.
2. **Get Next State:** Given a board ID, return the next generation state of the board.
3. **Get N States Ahead:** Given a board ID and an integer N, return the board state after N generations.
4. **Get Final State:** Return a stable conclusion when the state stops changing or enters a repeating cycle; if a conclusion is not found within a reasonable number of iterations, return an appropriate error.

The exercise says endpoints are required **at a minimum**. We considered a fifth endpoint for historical lookup and **deferred it** to avoid unneeded replay work and operational complexity.

**Other explicit exercise constraints:** Board states must survive application restarts or crashes; design should be clean, modular, testable, validated, and resilient; no authentication or authorization required. Evaluated for correctness, code quality, architecture, production readiness, and ability to explain decisions. The exercise indicates roughly **4-5 hours of implementation effort** and favors quality and thoughtful design over perfect or overly complex solutions.

**Additional approved delivery constraint:** The project must be runnable and testable for free from a cloned repository, with no subscription, trial account, hosted cache/database, secrets, or external connection strings. Use local SQLite. Check in the code and repeatable database setup/migrations, not a generated user-data database.

**Critical interpretive distinction:** The requirements use the English verbs “Get Next State” and “Get N States Ahead”; they do not mandate HTTP `GET`. The approved contract uses `POST` for state-changing advancement, and `GET` for non-advancing retrieval/search.

---

## B. Decision register

The labels below reflect decisions made together. “Approved provisionally” means the direction is agreed but measurements or implementation review can change a detail. Historical draft text that was later superseded is *not* an additional requirement.

### Decision 1: Upload grid and internal representation
**Status: Approved**

- API accepts a nonempty **rectangular 2D boolean JSON grid** under `cells`: `true` = alive, `false` = dead. Reject null cells and jagged rows.
- Width and height are derived from the uploaded grid and retained separately.
- Zero-based coordinates; `(0, 0)` is top-left, with `X` indexing columns and `Y` indexing rows (`cells[y][x]`).
- Game engine works with living-cell coordinates in `HashSet<(int X, int Y)>`. Absence from the set means dead; dead cells are not stored as coordinates.
- Keep the HTTP request/response representation, game-domain representation, and storage encoding independent.

**Rationale:** Familiar 2D API, efficient sparse computation. **Tradeoff:** Hash sets can consume more memory than a bitset when very dense.

**Tests:** Valid rectangles, empty/fully populated boards, correct orientation and coordinate mapping, reject empty/jagged/null/malformed grids, and round-trip reconstruction into original dimensions.

### Decision 2: Finite, nonwrapping boundaries
**Status: Approved**

- A board has fixed finite dimensions for its lifetime.
- No wrapping across opposite edges. Out-of-bounds positions are treated as dead; births occur only in bounds.

**Documented assumption:** Boundary behavior was not specified. **Alternatives considered:** Toroidal wrapping and an unbounded expanding universe. The original theoretical infinite universe is not identical to this bounded interpretation.

**Tests:** Corner/edge neighbor counts, gliders reaching boundaries, 1x1 and other small board shapes, dimensions unchanged after advancement.

### Decision 3: Board-size safeguards and benchmarking
**Status: Approved provisionally, pending performance testing**

- **1,024 x 1,024 is the baseline size we expect to support and benchmark, not the final production ceiling.** The earlier draft's “initial maximum 1,024” wording was superseded by this clarification.
- Test well above that baseline at multiple dimensions and densities. Final maximum input dimensions and payload limits must be **configurable** and justified with actual hardware/performance results.
- Minimum allowed grid is 1x1. Reject requests outside *the ultimately configured operational limits*.
- Benchmark engine CPU time, allocations and peak memory, incoming JSON parsing, outbound JSON rendering/size, persistence storage size and serialization, and request latency.

**Documented assumption:** The assignment provides no maximum dimensions. Sparse internal representation alone cannot make an enormous **dense 2D JSON upload/response** practical.

**Tests:** Boundary of configured limits, one cell over, large sparse/dense/random boards, request and response size, restart/recovery as size scales.

### Decision 4: Stateful advancement (required endpoint #2)
**Status: Approved; earlier document's “proposed” label is obsolete**

- Board ID identifies a **stateful simulation** beginning at generation 0, not merely a fixed immutable snapshot.
- `Next State` computes from the last committed generation, advances by exactly one, persists the new state and generation together, and returns that generation.
- Repeated *distinct* Next requests advance sequentially. An identical starting configuration produces the same next configuration (algorithmic determinism); repeating an advance request is not generally read-only or idempotent unless an idempotency key is reused.
- Persist successfully committed progress so restart/crash can recover the last successful advancement. Only respond with success when the required write commits.

**Documented assumption:** “Get Next State” plus persistence is interpreted as advancing the stored board. It is not an explicit statement in the assignment; the API exposes the chosen behavior clearly.

**Tests:** Known pattern progression, sequential calls, persisted generation increments, restart recovery, failed writes, correct dimensions and response, simultaneous advances.

### Decision 5: Durable store versus optional caching; self-contained execution
**Status: Approved**

- **SQLite is the source of truth** for initial state, current committed state, generation, and successful final-state discovery.
- An in-memory cache may speed up repeated calculations but is **not** durable and must never be the only copy of promised state.
- **Do not implement `IMemoryCache` initially.** Defer unless benchmarks show a worthwhile benefit. No Redis, hosted cache, or subscription.
- Cache eviction, service restart, or absence of a cache must not change correctness.

**Tests:** Correctness with cache absent/cleared (if subsequently implemented), SQLite recovery, no successful response on failed write.

### Decision 6: Advance versus preview (required endpoints #2 and #3)
**Status: Approved**

- **Next:** Compute one new state from current, commit it, and increment `CurrentGeneration` by one.
- **N Ahead:** Compute a **preview** N steps after the *persisted current generation*; return it without changing current state or generation.
- A preview observes a consistent starting snapshot even if another request advances the board concurrently.
- Previously computed intermediate states need not be persisted. Generation caching was considered but **deferred** under Decision 5.

**Tests:** Preview of N then Next advances only one, previews repeat deterministically from the same committed snapshot, concurrent preview/advance consistency, persistence unmodified.

### Decision 7: Relative N semantics and zero
**Status: Approved**

- `n` means the number of generations **ahead of the committed current generation**, not an absolute generation index.
- `n = 0` returns the current state and current absolute generation, with no write.
- E.g., when current generation is 5: `preview?n=0` returns 5; `preview?n=3` returns 8.

**Documented assumption:** The specification does not explicitly identify N's reference point. Relative future preview is supported by the phrase “N States Ahead.” Historical navigation was considered but deliberately deferred (Decision 10).

**Tests:** N = 0, positive N, generation labels, repeated previews, invalid/oversized N.

### Decision 8: Stable outcomes and cycles (required endpoint #4)
**Status: Approved; simplified final-state representative selection explicitly approved**

- Both a fixed point and an oscillator count as a **successful final-state search conclusion**.
- **Stable:** A configuration repeats after one generation, cycle length 1.
- **Cycle:** A previously encountered configuration repeats after two or more generations, cycle length > 1.
- The primary outcome is whether the simulation stabilizes or enters a repeating cycle. **Approved representative convention:** for stabilization, return the verified stable configuration; for oscillation, return any verified configuration belonging to the detected repeating cycle. No canonical or first-repeated configuration is required, and no reconstruction solely to select such a representative is required.
- Preserve the existing response fields, including `generation`, `startingGeneration`, `conclusionKind` (`Stable` or `Cycle`), `cycleLength`, and `iterationsUsed`. `generation` must label the actual returned configuration on the trajectory from `startingGeneration`; `cycleLength` must be the verified period; `iterationsUsed` counts every actual generation-engine call, including additional detection/verification calculations. It is not a generation offset.
- Different cycle-detection algorithms may return different valid cycle configurations and generation labels. Require correct cycle membership and accurate metadata for each result, not identical representatives across algorithms. This does not relax the unchanged persisted-result replay requirement in Decision 11.
- A cycle is not misrepresented as a single permanently frozen board.
- Verify equality exactly before declaring a cycle; fingerprints alone can collide.

**Documented interpretation:** We read the assignment's wording about states that no longer change or cycle as including oscillating states among successful conclusions.

**Rationale:** Stabilization or oscillation is the meaningful final outcome; representative selection and generation labels are supporting metadata. Requiring a particular representative adds algorithmic complexity without improving that outcome. Allow any verified representative while retaining exact correctness and reporting.

**Tests:** Still-life block, empty stable board, 2-period blinker, longer-period oscillators, transient then stable/cycle, hash collision simulation when applicable, and limit-edge behavior. For each algorithm, independently verify stable/cycle membership, the exact period, the returned configuration at its claimed absolute generation, and actual engine-call accounting. Accept different valid oscillator phases and generation labels across algorithms; reject transient configurations, mislabeled phases, incorrect periods, and fingerprint-only matches. No assertion requires a canonical or first-repeated representative.

### Decision 9: Internal final-state computation budget and cancellation
**Status: Approved; replaces the earlier caller-defined `maxIterations` proposal**

- The Final State endpoint **does not accept `maxIterations` or any other caller-selected iteration budget**. The assignment calls for an error after a reasonable amount of work but does not require exposing this control to API consumers.
- Enforce **one configurable server-side maximum** on the number of actual calls to the generation engine during a *single final-state search*. The initial value of **10,000** is provisional and must be benchmarked. Count every generation calculation used by the chosen cycle-detection algorithm, including extra calculations required by Brent to determine a period or representative result.
- **Check the remaining budget before every generation-engine call**, including every additional Brent calculation or collision-verification calculation. If the number of calls already made equals the cap, do not make another call. A conclusion proven using exactly the allowed number of calls succeeds; return `422` only when another calculation is required to establish the conclusion and would exceed the cap. Never perform an extra call and reject afterward. This clarifies the approved budget rule and requires no new infrastructure or abstractions.
- If a valid conclusion cannot be established without exceeding this computation budget, return the approved `422 Unprocessable Content` response. Different correct cycle-detection algorithms may reach a conclusion at different work counts near the cap; this is an accepted consequence of an **internal resource budget**, not a client-controlled promise about logical generations.
- Report `iterationsUsed` as **actual generation-engine calculations performed during the successful discovery**, not the difference between absolute generation labels. Do not confuse this with the absolute result generation or cycle length.
- Apply the work limit to **new calculations**. A previously persisted successful conclusion is returned by the lookup-first path in Decision 11 without running the engine or reapplying the current configured work limit.
- Pass ASP.NET Core request cancellation (e.g., `RequestAborted`) through expensive calculation. Check for cancellation at least between generation calls and, when practical, within costly steps. Searches never advance the board. Failure or cancellation observed before commit must leave no final-state result; cancellation or response loss after a successful commit cannot undo the saved conclusion (see Decision 14).
- Keep Final State synchronous for the initial build. Background workers, durable jobs, and polling were considered but deferred until performance data justifies them.

**Documented assumption:** The specification does not define a precise "reasonable" iteration count. A documented configurable internal computation cap gives the service a bounded, testable failure condition without adding public API complexity.

**Tests:** Use independently established expected configurations and periods (for example, hand-verified block and blinker fixtures), not expected results generated by the engine under test. For each candidate algorithm, assert successful discovery exactly at its required call limit, accurate `iterationsUsed`, and `422` when the next required calculation would exceed a smaller cap; assert that the forbidden extra call never occurs and exhaustion persists no result. Include additional Brent calculations in these counts and boundary cases. If fingerprint detection is selected, force equal fingerprints for unequal configurations and verify that exact equality prevents a false stable/cycle conclusion. Retain still-life discovery in one engine call, longer oscillators, cancellation, and the absence of public `maxIterations` behavior.

### Decision 10: Historical-generation endpoint
**Status: Deferred from the initial implementation (previously approved, now explicitly descoped)**

- **Do not implement** `GET /api/boards/{id}/generations/{generation}` in the take-home solution.
- Implement only the four endpoints required by the exercise. `N States Ahead` remains a non-mutating preview relative to the current committed generation, not a historical lookup.
- Continue storing the uploaded initial board and the last committed board for the approved persistence design. Do not calculate or permanently retain every intermediate generation for historical navigation.
- Consider a historical-generation route as a potential future enhancement only if a real requirement warrants the added cost.

**Rationale:** Replaying from generation 0 to an arbitrary old generation can become expensive and introduces extra response, resource-limiting, and recovery tests. The feature is not required for the coding exercise, so deferring it is a deliberate scope decision.

**Tests:** No historical endpoint is expected in the initial route contract. Confirm the four required endpoints work; no historical replay functionality or replay-limit implementation is required.

### Decision 11: On-demand, lookup-first final-state persistence
**Status: Approved; updated after adversarial review**

- `FinalStateResults` holds **at most one successful computed conclusion per board**. A board upload, Next State call, and N-ahead preview **must not** precompute, store, or invalidate a final-state result.
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

### Decision 12: Optimistic concurrency and safe idempotent retries
**Status: Approved; duplicate-key retry behavior resolved; configurable 24-hour retention default locked**

- Calculate Next outside database write transactions. Use an optimistic update against the expected current generation, committing the next state and incremented generation atomically in SQLite.
- For **distinct** concurrent Next requests, a generation conflict causes the loser to reload the latest state, calculate again, and retry up to a configurable bounded count. Ordering of concurrent requests is not guaranteed. Exhausted conflicts return `409 Conflict`.
- `Idempotency-Key` remains **optional** for state-changing Next requests. Clients needing safe retransmission should provide a unique key per logical advancement. Repeating the same key for that board within its documented retention period must replay the **original response**, not advance again.
- **Check for an existing idempotency record before beginning calculation. On every conflict/retry path, recheck whether that same key has committed before recalculating or attempting another update.** Distinguish an existing duplicate key from a conflict caused by a *different* request.
- The database enforces uniqueness on `(BoardId, Key)` and stores the record plus the committed Next response in **the same transaction** as the board state/generation advancement. Handle two in-flight identical-key requests so **only one advancement occurs**, the loser loads the winning record, and both get the same successful result. Handle a commit-before-response-loss via durable replay after restart.
- **Idempotency retention has a locked, approved default of 24 hours measured from successful transaction commit.** The duration remains configurable and must be documented for callers. Before expiration, the same key for the same board replays the original committed response without another advancement. At or after expiration, the key may request a new advancement. Expired records may be cleaned up transactionally during subsequent keyed requests; no background cleanup service is required. Requests without keys are not retry-idempotent.
- **Rationale and change control:** 24 hours provides a practical initial retry window without additional infrastructure or indefinite retention. Reconsider this decision only when testing, performance measurements, or operational evidence demonstrates a problem, and only after an explicit proposal and developer approval.
- Distinguish expected-generation conflicts, temporary SQLite contention, known duplicate-key constraints, transaction failures, unexpected failures, and cancellation using Decision 14. A failed expected-generation update requires a new authoritative snapshot before recalculation. Dispose of failed transactions before retrying, recheck the key before every retry/recalculation, and never blindly repeat an advancement after an ambiguous commit outcome.
- Advancing a board **does not invalidate FinalStateResults** (Decision 11). No Redis, locks outside SQLite, or distributed services are needed.

**Documented assumption:** Multiple distinct Next requests each request one new advancement from the latest committed state. Retransmissions with the same key represent one logical request, not multiple clicks.

**Tests:** Two concurrent distinct keys yield two valid consecutive generations; duplicate concurrent keys yield exactly one generation and identical responses; recheck on every optimistic retry; prove recalculation from the winner's committed state after an optimistic conflict; response replay after later advancements and after restart; response loss and ambiguous outcomes around commit; atomic rollback of current state/generation/idempotency data; classified SQLite contention and bounded exhaustion; cancellation and sanitized unexpected failures; replay immediately before expiry, safe reuse exactly at/after expiry, and concurrent duplicate/reuse/cleanup interleavings; retention starts at successful commit rather than request/calculation start; no FinalStateResults invalidation on Next. Use real file-backed SQLite connections and deterministic synchronization/fault injection.

### Decision 13: Cycle-detection algorithm selection
**Status: Provisional, pending comparative benchmarking**

Two candidates:

1. **Fingerprint dictionary:** Track canonical/order-independent fingerprints associated with generation indexes. Usually advances one generation per engine call. Verify exact state equality on any candidate repeated fingerprint to prevent hash-collision false positives. Reconstruction for collision verification, if used, also consumes engine calls and counts toward the budget.
2. **Brent's cycle-finding algorithm:** Requires only a constant additional number of board states and exact comparisons, but may make extra generation-engine calls to detect or characterize a cycle.

- Compare correctness on the same deterministic input boards, total `CalculateNextGeneration` calls, time, peak memory, and accurate result metadata. Under the **approved representative convention**, each algorithm may return any verified cycle member (or the verified stable state), with its own correct absolute generation and actual call count. Require the same verified period for the same eventual cycle, not identical representative configurations, generation labels, or work counts. Do not add canonical/first-repeat reconstruction work merely to align algorithms.
- **The computation-budget rule is settled** (Decision 9): Count **all actual engine calls**, including Brent's additional passes, with a provisional configurable 10,000-call server cap. Do not treat the count as the absolute generation offset.
- Two algorithms can legitimately report success versus `422` differently near the internal cap when one consumes more work. Ensure the selected implementation obeys its configured limit and never reports a conclusion it did not actually prove within the budget.
- Benchmark sparse, dense, transient, still-life, and periodic patterns. Select one algorithm based on correctness and measured performance, then obtain the developer's approval before finalizing. Algorithm selection remains provisional; the any-verified-representative convention is already approved and is not a benchmark decision.

**Documented assumption:** The exercise mandates correct cycle detection, not a particular algorithm. No caller-defined iteration parameter exists.

### Decision 14: SQLite schema, storage, transactions, and codecs
**Status: Approved provisionally, pending implementation and performance testing**

**Three planned tables:**

- `Boards`: `Id` (GUID), `Width`, `Height`, `InitialState`, `CurrentState`, `CurrentGeneration`.
- `FinalStateResults`: Unique `BoardId` (FK), `StartingGeneration`, `ResultGeneration`, `ResultState`, `CycleLength`, `IterationsUsed`, and an optional explicit conclusion kind. **No requirement to match a later current generation** when reading a successful conclusion.
- `IdempotencyRecords`: Unique `(BoardId, Key)`, committed result generation, saved original response, and durable commit-relative retention/expiry metadata. The approved default is **24 hours from successful transaction commit**, not from request start or an earlier record-creation timestamp. Review the exact timestamp/expiry capture mechanism before implementing keyed advancement; it must meet this contract without changing the locked default.

**Persistence and encoding:**

- Persist the original board and last committed board, but not intermediate generations. Only Final State endpoint may produce a durable `FinalStateResults` record, and only after success.
- **Approved simplification:** Use built-in System.Text.Json to serialize living-cell coordinates as JSON for SQLite persistence. Keep width and height as separate database fields and reconstruct `HashSet<(int X, int Y)>` on deserialization. Use a minimal coordinate data type; no custom converters, serialization frameworks, or unnecessary interfaces.
- Custom binary encoding and alternative-codec implementation/benchmarks are **deferred unless actual performance measurements demonstrate that JSON serialization or storage is a meaningful bottleneck**. Their presence in earlier plans does not authorize this work. Retain all other architectural decisions.
- Create schema reproducibly from checked-in code; no external database service.

**Atomicity and concurrency:**

- Perform generation calculations outside database transactions. Commit the current board's state and generation together using an optimistic expected-generation check, plus the successful Next request's idempotency record if a key was provided.
- On an optimistic Next generation conflict, reload and retry within the configured bound. Handle SQLite locking separately under the failure policy below. **On a duplicate key**, replay the winning stored original response instead of advancing a second time (Decision 12).
- **Do not delete or invalidate FinalStateResults during Next**. A successful final-state result is an eventual deterministic outcome of the board's trajectory, not a materialized version of the current generation.
- For Final State, check for a stored result **before simulation**, insert only after successful computation, and enforce unique `BoardId`. On simultaneous first-time calculations, the losing insert reads the existing winner's record and returns that persisted result. A concurrent Next advancement does not invalidate the final conclusion.
- Return success only after required writes commit or authoritative verification establishes that the relevant write committed. Failed or exhausted Final State calculations and cancellation observed before commit do not create records. Cancellation or HTTP response loss after a successful commit does not undo it.

**Approved failure-handling requirements (developer clarification; architecture unchanged):**

| Failure type | Required handling |
| --- | --- |
| Optimistic generation conflict | A conditional update that affects no row is not a SQLite locking exception. End the failed attempt, recheck the retained idempotency key, reload the latest committed board, recalculate, and retry within the configured bound. If the board no longer exists, use the existing `404` convention. Genuine concurrency retry exhaustion remains `409`. |
| SQLite busy/locked condition | Classify `SqliteException.SqliteErrorCode` and `SqliteExtendedErrorCode` together with the operation and connection state. Temporary lock contention is distinct from an expected-generation conflict. Account for provider busy waits; do not retry every busy/locked exception indiscriminately. |
| Known idempotency uniqueness conflict | End/roll back the losing transaction, then retrieve the winning retained `(BoardId, Key)` record using a fresh connection and replay its original response. The losing board update must not survive. A uniqueness exception alone is not proof that the matching record exists. |
| Other constraint failure | Do not interpret foreign-key, check, not-null, or unrelated unique/primary-key failures as duplicate requests. Handle these as unexpected database failures unless an existing documented domain/HTTP rule specifically applies. |
| Transaction failure before commit | Roll back the whole advancement: current state, generation increment, and optional idempotency response form one atomic unit. A statement failure does not justify retaining earlier writes. Dispose of readers, commands, transactions, and connections; cleanup failures must not obscure the original failure or lead to reuse of the connection. |
| Unexpected database failure | Do not blindly retry I/O, corruption, full/read-only database, invalid schema, or other unclassified failures. Return sanitized `500` ProblemDetails when a response is possible; expose no SQL, database path, provider exception text, or stack trace. Keep diagnostic details in server logs with a correlation identifier. |
| Cancellation | Observe request cancellation before calculations, attempts, database operations, and commit. Stop new work/retries and safely clean up an active transaction. Cancellation/disconnection does not establish whether a commit occurred and cannot reverse a completed commit. No new cancellation status code is introduced. |

**Proposed implementation details — pending developer review:**

- Keep this logic in `BoardService` and `SqliteBoardStore` with normal .NET exception handling and small operation-specific methods. No generic retry framework, distributed locks, external service, or background retry worker. Use private-cache, separate connections per attempt; do not share active connections/readers across concurrent requests. Prefer a short non-deferred write transaction after calculation, so the in-transaction key check, conditional update, and idempotency insert are protected without retaining a read transaction during simulation.
- Use one bounded operation retry loop with a configurable retry count and finite database-wait budget. Check the retained key before every retry and before every recalculation, and again inside the write transaction. Before every retry, finish cleanup, open a fresh connection/transaction, and reload authoritative state; never carry a failed attempt's board snapshot into a new calculation.
- Let Microsoft.Data.Sqlite perform its built-in ordinary busy/locked waits using finite `DefaultTimeout`/`CommandTimeout` settings, including implicit commands such as transaction start. Do not add a second sleep/backoff loop that restarts a full provider timeout after ordinary contention times out. All attempts, commands, and recovery lookups consume the same bounded operation budget; account for provider timeout granularity when documenting the effective bound. Retry counts, timeouts, and the total wait bound remain configurable and benchmark-gated; no numeric defaults are selected here.
- Distinguish ordinary `SQLITE_BUSY` (including known transient recovery/timeout variants) from `SQLITE_BUSY_SNAPSHOT`, which requires ending the stale transaction and restarting with fresh reads rather than waiting inside it. An ordinary provider busy-timeout expiry ends the contention path instead of granting another full wait window. `SQLITE_LOCKED` commonly reflects a same-connection/shared-cache resource conflict; with separate private-cache connections, treat it as an unexpected resource-lifetime defect unless a concrete transient case is demonstrated and explicitly approved for bounded retry. Do not enable shared cache merely to make it retryable.
- Recognize `SQLITE_CONSTRAINT_UNIQUE`/`SQLITE_CONSTRAINT_PRIMARYKEY` only in the known idempotency-insert context and confirm the matching committed record before replay. Do not use `INSERT OR REPLACE`, overwrite the winner, or ignore all constraint failures. For a competing `FinalStateResults` insert, apply the already-approved unique-BoardId winner lookup without recalculating or invalidating the result.
- If `Commit` throws, determine whether the transaction remains active; SQLite can leave it active after an error. End/roll back any still-active attempt and dispose of the connection before recovery. For an uncertain keyed outcome, a fresh authoritative read of the matching retained response proves an advancement committed and permits replay. An unreadable database is not evidence that the record is absent. Verified absence after the original attempt is definitively ended permits a new attempt only for an otherwise eligible transient failure, within the original budget and while not canceled; every attempt still rechecks the key transactionally. Unexpected commit errors are not made retryable by a missing record.
- If the outcome cannot be verified, do not claim success or automatically advance again; return sanitized `500` if possible. Without an idempotency key, a later generation cannot identify which request committed, so never automatically repeat an ambiguously committed Next. A response failure after a confirmed commit is a transport failure, not permission to rerun database work. For upload/final-result recovery, verify the already assigned unique board/result identity rather than infer success from unrelated progress.
- Microsoft.Data.Sqlite's async ADO.NET calls execute synchronously. Do not promise immediate cancellation merely because a token was passed to an async method. Check cancellation around synchronous operations, keep provider waits finite, and measure the selected provider version's cancellation behavior. If cancellation arrives during an operation, clean up when control returns; do not schedule another attempt. Recovery for a disconnected request can occur on the client's next retained-key retry; do not launch background work or reuse the canceled request token as the only means of transaction cleanup.

**Proposed HTTP clarification requiring developer approval:** Keep `409` for genuine optimistic-concurrency exhaustion and `500` for unexpected/unverifiable database outcomes. Propose applying the existing `409` concurrency-exhaustion convention also to positively identified temporary SQLite contention after its bounded wait/restart budget is exhausted. This extends the mapping explicitly; it is not yet approved. Do not silently introduce `503`, map every SQLite exception to `409`, or apply `422` to database failures.

**Provider references:** [Microsoft.Data.Sqlite errors and timeouts](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors), [transaction behavior](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions), [async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async), [SQLite result codes](https://sqlite.org/rescode.html), and [SQLite transaction/commit behavior](https://sqlite.org/lang_transaction.html). The retry policy above is a proposed application design based on these provider behaviors, not a claim that SQLite supplies an end-to-end retry guarantee.

**Tests and benchmarks:** JSON coordinate round-trip for empty/populated boards and clear failure for invalid stored JSON; durable board progress across crashes; atomic Next/idempotency rollback; duplicate-key simultaneous requests and replay; final result lookup-first and zero engine calls on hit; concurrent first-time final searches (one saved result), Next/Final interleavings, restart reuse, response serialization, database size, and JSON serialization/storage costs.

### Decision 15: HTTP API, validation, and error convention
**Status: Approved, subject to performance testing and selected algorithm**

| HTTP method | Route | Effect |
| --- | --- | --- |
| `POST` | `/api/boards` | Upload initial grid and create a new simulation ID; do not compute final state |
| `POST` | `/api/boards/{id}/next` | Advance once and commit the new current state |
| `GET` | `/api/boards/{id}/preview?n=3` | Return N generations ahead of current without committing or computing final state |
| `GET` | `/api/boards/{id}/final-state` | **Look up a stored successful conclusion first**; compute/persist it only on a miss; do not advance the board |

**Deferred:** Historical retrieval `GET /api/boards/{id}/generations/{generation}` is **not** implemented (Decision 10). The public Final State API has **no `maxIterations` query parameter**; its computation cap is an internal configuration (Decision 9).

**Upload example:**

```json
{
  "cells": [
    [false, true, false],
    [false, true, false],
    [false, true, false]
  ]
}
```

**Common board-state response shape:**

```json
{
  "boardId": "00000000-0000-0000-0000-000000000000",
  "generation": 3,
  "width": 3,
  "height": 3,
  "cells": [
    [false, true, false],
    [false, true, false],
    [false, true, false]
  ]
}
```

- Upload returns `201 Created` with the new board ID and generation 0. The older plan's `Location` pointing to `/generations/0` is **superseded** because that route is deferred. If a `Location` header is included, use a supported live-state URI such as `/api/boards/{id}/preview?n=0`, which represents the **current** board and does not guarantee immutable access to generation 0 later. Exact header convention can be reviewed with the HTTP tests.
- Next and preview use the common state DTO with absolute generation numbers. Final State retains the existing fields `generation`, `startingGeneration`, `conclusionKind`, `cycleLength`, and `iterationsUsed` alongside the board configuration. Return the verified stable configuration or any verified member of the repeating cycle, with accurate absolute generation, period, and **actual engine calls used when the stored result was discovered**. No canonical or first-repeated representative is required. Preserve saved responses unchanged and do not imply that the cached representative is always later than today's current generation.
- Validate nonempty rectangular boolean grids; reject `null`, jagged and malformed JSON, invalid IDs, and out-of-range dimensions. Limits and body-size safeguards are configurable; **1,024 x 1,024 remains a testing baseline, not a final ceiling**.
- **Resource-protection verification:** Configure Kestrel's request-body limit before body reading/JSON binding, using existing ASP.NET Core facilities. For a known oversized `Content-Length`, reject before deserializing the grid; retain server enforcement for chunked/unknown-length bodies, stopping when the limit is crossed without buffering the full upload. Some bounded reading/parsing is unavoidable for unknown-length bodies; do not promise zero allocation. Verify these paths with actual Kestrel, not only TestServer. Numeric payload, dimension, and cell-count limits remain provisional/configurable pending benchmarks; small test overrides are not production defaults.
- Preview `n` is an integer in range 0 through the provisional configurable maximum 10,000. `n=0` returns the current state without a write.
- Final State uses a **server-configured computation budget only**, initially proposed at 10,000 actual engine calls; on exhaustion return `422 Unprocessable Content`. It has no public budget parameter.
- Return standardized ASP.NET Core `ProblemDetails` errors without internal stack traces; propagate cancellation for expensive computations.
- **ProblemDetails verification:** A `413` status does not itself guarantee a ProblemDetails body. Use the existing `AddProblemDetails`, status-code pages, and exception handling where applicable; preserve a body-read `BadHttpRequestException`'s `413` rather than converting it to `400`/`500`. Test supported JSON `Accept` headers, absent `Accept`, and unsupported media preferences in Production configuration. Require sanitized `application/problem+json` with body `status: 413` on the application's supported JSON error paths. Server-level rejections outside the app pipeline or a disconnected/started response cannot be assumed to support that envelope; record the observed boundary and bring any contract exception to developer review. Do not claim universal consistency before real-Kestrel tests execute. [.NET 8 body-binding source](https://github.com/dotnet/aspnetcore/blob/v8.0.0/src/Http/Http.Extensions/src/RequestDelegateFactory.cs), [ASP.NET Core ProblemDetails guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-8.0).

| HTTP status | Chosen convention |
| --- | --- |
| `200 OK` | Successful retrieval/preview/advance or final-state conclusion |
| `201 Created` | Upload succeeded |
| `400 Bad Request` | Invalid board content/shape or invalid supplied parameters |
| `404 Not Found` | Unknown board ID |
| `409 Conflict` | Concurrency retries exhausted or an incompatible idempotency-key reuse |
| `413 Content Too Large` | Request exceeds configured payload limit |
| `422 Unprocessable Content` | Final State needs another calculation to prove a conclusion, but that call would exceed its internal budget |
| `500 Internal Server Error` | Unexpected server failure |

- A caller can receive no HTTP response after disconnect/cancellation. Only successfully committed writes are reported as successful.
- Database errors follow Decision 14: approved optimistic retry exhaustion is `409`, unexpected or unverifiable failures use sanitized `500`, and cancellation introduces no new status. Extending `409` to verified SQLite contention exhaustion is explicitly proposed for developer approval; the approved status table above is unchanged.
- Benchmark large response serialization/size separately from engine execution and persistence; coordinate JSON persistence does not make dense rectangular API JSON cheap.
- No authentication or authorization, as specified. No historical endpoint and no general-purpose cache in the initial build.

**Required API tests:** Exactly four routes; consistent DTO and error semantics; invalid/empty/jagged/null/big inputs; unknown IDs; correct relative N semantics and nonmutating preview; upload and Next do not create or invalidate a final result; first Final State computes and persists; subsequent call returns stored result **without calling the engine**, including after restart/Next; internal-budget exhaustion returns `422` without storing; idempotent concurrent Next requests; concurrent Final State calls; rollback, cancellation, and large JSON response behavior.

## C. Test strategy to turn into a reviewed implementation plan

The decisions above name required tests. Consolidate them into a focused suite, **not one redundant test per decision paragraph**. Challenge assertions against realistic incorrect implementations before approving each TDD slice.

1. **Pure engine unit tests:** Moore-neighborhood rules (birth at 3, survival at 2/3, death otherwise); all-dead, block, blinker, and other known patterns; sparse coordinates, 1x1 / 1xN / Nx1 / asymmetric grids, boundaries, immutable input, and exact expected configurations derived independently.
2. **Cycle and internal-budget tests:** Stable period 1, oscillators of period >1, transient then cycle, exact collision-safe equality, cancellation, success and exhaustion at the **actual generation-engine call count** boundary. Independently verify that each returned representative belongs to the eventual stable state/cycle, matches its claimed generation, and reports the exact period and engine-call count. Compare candidates using identical input boards, while accepting different valid cycle phases/generation labels and different actual work counts. Reject transient representatives, inaccurate labels/periods, and uncounted extra calculations; do not assert identical or canonical representatives across algorithms.
3. **SQLite integration tests:** Upload/reload, durable current progress, atomic Next/idempotency commit and rollback, retry/replay after restart and response loss, ambiguous commit verification, first Final State **lookup before calculate**, one successful saved result, zero engine calls on cache hit, reuse after Next and restart, and no result from failed calculation or cancellation observed before commit. Inject a database failure between the board update and idempotency insert and assert all three persisted values remain unchanged. Cover cancellation during database work, rollback/cleanup failure handling, unrelated constraints, and sanitized unexpected errors. All database guarantees require real file-backed SQLite connections, not mocks alone.
4. **HTTP integration tests:** Four routes, real serializer and validation, exact status and `ProblemDetails`, relative N=0 and future previews, final-state response snapshot semantics, cancellation, no bogus historical route or public `maxIterations` behavior, and response size. Add the focused resource/validation cases below; TestServer alone is insufficient evidence for Kestrel request-size enforcement.
5. **Concurrency tests:** Barrier-synchronize distinct-key requests reading the same generation; assert one loser reloads/recalculates and the two successful responses represent consecutive correct generations. Identical keys advance **once**, replay identical original responses, and recheck on every retry. Exercise the known unique-key-conflict recovery path and prove unrelated constraint failures are not replayed. Hold a real writer lock to exercise provider busy waiting, release it deterministically for success, and hold it through configured exhaustion for the proposed error path. Reproduce stale-snapshot busy and same-connection locked cases to test their different classifications. Test expiry immediately before/at/after the commit-relative retention boundary, concurrent expired-key reuse, and transactional cleanup races. Two first-time Final State requests save one winner and return its record. Concurrent Final State calculation and Next advancement never corrupt current progress or invalidate a discovered eventual conclusion. Use deterministic barriers/events, an injected clock, and controlled SQLite/test-harness faults; do not use sleeps to arrange races. Finite watchdogs may detect hangs, but normal tests must not assert benchmark latency targets.
6. **Benchmarks:** At least 1,024 x 1,024 and larger if feasible; 0%, sparse, mixed, dense input; generation, cycle detection, JSON persistence serialization, SQLite read/write, JSON request/response sizes, time/allocations/peak memory, contention, and cancellation responsiveness. Benchmarks inform limits and algorithm choice, not truth of functional tests.

**Deferred tests:** No history-generation reconstruction or historical replay limit tests are required while Decision 10 is deferred.

**Focused HTTP validation/resource tests (proposed acceptance details):**

- Run a small group in the existing integration-test project against the production app composition hosted by real Kestrel on an ephemeral loopback port, using normal `HttpClient`, the same middleware, and temporary file-backed SQLite. Keep WebApplicationFactory/TestServer for ordinary HTTP tests; no additional project, server framework, proxy, or external infrastructure is needed.
- For configured body limit `L`, construct valid UTF-8 JSON bodies of exactly `L-1`, `L`, and `L+1` bytes using harmless whitespace padding and boards within dimension/cell limits. Below/at the limit, accept valid uploads; above it, return `413`. Cover known `Content-Length` and HTTP/1.1 chunked/unknown-length uploads. Test application early rejection and Kestrel's native enforcement independently so an application guard cannot mask a missing server limit. Configure any per-request size feature before reads, not in an endpoint filter that runs after JSON binding.
- Send very wide single-row boards and large sparse/dense rectangles within and just outside configured dimension/cell limits. Include the 1,024 x 1,024 support baseline in large-board integration cases, with adequate test-configured payload/dimension limits; use small overrides for routine byte-boundary cases. Keep body size within its limit when testing dimension validation (`400`), and dimensions valid when testing payload rejection (`413`). Assert orientation/round-trip correctness, no arithmetic overflow in size validation, and no persisted board on rejection. Benchmark larger practical workloads separately; the baseline is not a production ceiling.
- For oversized uploads, verify rejection before full grid materialization/domain mapping or SQLite writes. Use narrowly scoped test observations of body-read/binding entry where needed; a `413` assertion alone does not prove resource protection. Known-length rejection should precede JSON parsing; streamed requests may parse a bounded prefix but must not buffer/materialize the entire oversized grid. Measure allocation/peak-memory effects separately under the existing benchmark plan instead of asserting arbitrary memory/timing thresholds in ordinary tests.
- Preview `n` validation: missing/empty, malformed noninteger, negative, above configured maximum, numeric parse overflow, and duplicate values (including equal duplicates). Proposed clarification: require one unambiguous value and return existing `400` ProblemDetails for these invalid requests. Seed a real persisted generation near its representable maximum, then use an otherwise valid `n` whose addition would overflow: check before engine work, return proposed `400`, and leave state unchanged; `n=0` at the maximum remains valid. Do not wrap generation labels or return `500`/final-search `422` for this validation failure.
- Cancel real expensive generation work through request cancellation, covering preview and an uncached final-state search. Coordinate entry/cancellation with the existing test controls, verify the server observes cancellation and stops further calculations, and verify no current-state mutation or final-result write before commit. Do not treat a canceled client await as proof of server cancellation or require a particular response after disconnect. Include cancellation checks within a costly engine call as well as between calls; measure latency separately.

**Kestrel/413 evidence status:** The .NET 8 Kestrel source checks an oversized declared length when body reading starts, and Minimal API binding preserves the resulting HTTP status. ProblemDetails still depends on middleware, negotiation, and response state. Actual runtime behavior for this app is unverified until the planned tests run; preserve the approved `413` convention and review any observed envelope limitation rather than silently weaken it. [.NET 8 Kestrel body-size check](https://github.com/dotnet/aspnetcore/blob/v8.0.0/src/Servers/Kestrel/Core/src/Internal/Http/Http1ContentLengthMessageBody.cs), [server error-handling boundary](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-8.0#server-exception-handling).

Use an independent correctness oracle where possible. Assert exact cell configurations, persisted generation and idempotency records, and engine **call counts**, not merely non-null responses, success status, or mocks. Before each implementation cycle, conduct a targeted adversarial review and stop for developer approval.

## D. Unresolved or conditional details: ask in the plan, do not silently decide

1. **Dimension and payload limits:** Initial configuration before performance evidence. 1,024 x 1,024 is the baseline to support/test, **not** a permanent maximum.
2. **Internal Final State cap:** The unit is **decided**: actual generation-engine calls, without any public override. The numeric value (proposed 10,000) remains provisional until benchmarked.
3. **Cycle detection algorithm:** Fingerprint dictionary versus Brent; compare period accuracy, memory, time, extra engine calls, and each result's representative/generation correctness before selection. The any-verified-state convention is approved; matching algorithm representatives or reconstructing a canonical/first-repeated state is not a selection requirement.
4. **Persistence serialization (resolved):** Minimal System.Text.Json coordinate serialization is approved; width/height remain separate. Custom binary encoding and alternative-codec comparisons are deferred unless measurements demonstrate a meaningful JSON serialization or storage bottleneck. No codec-selection gate is required for the JSON implementation.
5. **Commit-relative retention metadata:** The configurable **24-hour default from successful commit is locked**, as are pre-expiry replay, reuse at/after expiry, and transactional cleanup without a background service. Review only the durable timestamp/expiry capture mechanism; do not substitute request-start or pre-commit insertion time for the approved commit anchor.
6. **SQLite retry implementation and tuning:** Review the proposed mechanics/classification in Decision 14 and benchmark the configurable retry count, provider timeout, and effective total wait bound. The proposed `409` mapping for identified SQLite contention exhaustion requires explicit developer approval. Retry eligible concurrency failures only; unexpected failures and uncertain commits are not blind-retry cases. No numeric values are locked here.
7. **HTTP Location header:** The former immutable generation-0 resource is deferred. If present, use a supported live-current URI; do not promise historical access. Finalize the header convention in API tests.
8. **Optional cache:** Do not implement `IMemoryCache` initially; `FinalStateResults` is approved *durable memoization*, not a general cache.
9. **Performance fallback:** Do not silently change the approved rectangular JSON API or introduce extra services if dense boards strain resources. Present measured tradeoffs for developer approval.

**Resolved and out of scope:** No caller `maxIterations`; no historical route or historical replay work-limit decision; no final-result invalidation on Next; duplicate-key recheck on every retry/recalculation is required. Idempotency retention defaults to **24 hours from successful commit**, remains configurable, replays before expiry, permits reuse at/after expiry, and allows transactional cleanup without background infrastructure. These are approved decisions, not open questions for Codex to re-decide. Future retention changes require evidence and explicit developer approval.

**Approved final-state representative selection:** Return the verified stable configuration or any verified configuration in the detected repeating cycle, with the existing response fields and accurate generation, period, and actual engine-call count. No canonical/first-repeat requirement or cross-algorithm representative equality applies. The first persisted conclusion and its metadata remain unchanged on reuse.

## E. Suggested Codex deliverable: plan only

Respond with:

- A brief **requirements-to-decisions traceability matrix for the four required endpoints only**. Mark historical retrieval explicitly deferred.
- An implementation plan in small reviewable phases with minimum project boundaries and concrete acceptance criteria, **without implementation code**.
- A test-first sequence that targets the resolved adversarial risks: concurrent duplicate keys, counted cycle-detection work, lookup-first final-state reuse, concurrent first-time final calculations and Next interleavings.
- A practical SQLite schema/transaction plan: atomic board advancement + optional idempotency insert, explicit duplicate-key recheck/replay, and separate on-demand final-result insertion with unique board ID and **no Next-driven invalidation**.
- A focused benchmark strategy for board sizing, persistence encoding, and cycle-detection algorithm selection with actual engine-call accounting.
- A short list of genuinely unresolved values and response conventions. **Stop after presenting the plan and wait for developer approval.**

**Guardrail:** No extra external services, no large generic frameworks, no background worker implementation, no speculative in-memory caching, no historical endpoint, no caller-defined final-state budget, and no writing code before the plan is approved.
