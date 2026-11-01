# Conway's Game of Life API
## Architecture decisions and implementation-planning handoff

**Project:** NetDocuments coding exercise | **Target:** C# / .NET 8 (`net8.0`) | **Document status:** Design baseline for Codex planning, not authorization to implement

**Current milestone:** Slices 1–3 are complete. Restart Slice 4 with the proposed generation-contract revision from the supplied `SLICE_4_GENERATION_CONTRACT_PLAN.md`, retaining 11 master slices and checkpoints 4a-4f in `PLAN.md`. This documentation-only diff awaits review and authorizes no production-code, test, dependency, or schema changes. The proposed contract replaces historical-retrieval deferral only after approval; unrelated approved decisions remain unchanged.

> **Instructions for Codex:** Read this document in full. Produce a phased, reviewable implementation and testing plan based on the approved decisions below. **Do not write application code until the developer has reviewed and approved the plan.** Do not silently change decisions, treat provisional choices as final, or infer that deferred features are required. Highlight ambiguities, inconsistencies, and proposed modifications separately. This document is the design context, not a request to create a framework or introduce extra infrastructure.

### Planning collaboration contract

1. Distinguish the exercise's **explicit requirements**, our **approved design decisions**, **documented assumptions**, **provisional choices**, **deferred options**, and **unresolved details**.
2. Map the four assignment operations to the proposed five-route contract, including relative preview and absolute-generation retrieval, with persistence, tests, and failure modes. Keep this Slice 4 revision pending documentation review.
3. Propose a minimal, maintainable .NET 8 architecture with a pure Game of Life engine, separable persistence, and thin HTTP endpoints. Prefer established libraries and built-in .NET facilities; no paid services, trials, or external infrastructure.
4. Recommend incremental test-driven development: review candidate tests, challenge coverage and edge cases, then implement small red-green-refactor steps. Stop for developer review at agreed checkpoints. Do not silently implement the entire system.
5. Include a lightweight final performance sanity check of the working application, covering representative boards, generation speed, dictionary-based cycle detection, memory, JSON serialization, and SQLite persistence. No comparative benchmark gate or dedicated framework is required. Avoid prematurely hard-coding a 1,024-by-1,024 production maximum; propose optimizations only for measured bottlenecks.
6. Respect the approved internal-only final-state computation budget (actual generation-engine calls), atomic durable advancement with optimistic concurrency, and lookup-first final-state memoization. Flag only still-open details, such as retry/timeout values and evidence for additional operational limits.
7. Present a coherent plan first, not source code. Quality, correctness, resilience, and discussion-ready tradeoffs matter more than elaborate architecture.

---

## A. Exercise requirements and constraints

**Objective:** Implement a production-conscious RESTful API for Conway's Game of Life in C# using .NET 8 (`net8.0`).

**Required functional behavior:**

1. **Upload Board State:** Accept a new board state (2D grid of cells); return a unique identifier for the stored board.
2. **Get Next State:** Given a board ID, return the next generation state of the board.
3. **Get N States Ahead:** Given a board ID and an integer N, return the board state after N generations.
4. **Get Final State:** Return a stable conclusion when the state stops changing or enters a repeating cycle; if a conclusion is not found within a reasonable number of iterations, return an appropriate error.

The exercise requires endpoints **at a minimum** and does not explicitly define N's starting point after advancement. The proposed Slice 4 revision supports both interpretations with separate relative-preview and absolute-generation GET routes. This adds a fifth route without per-generation storage or snapshot tables.

**Other explicit exercise constraints:** Board states must survive application restarts or crashes; design should be clean, modular, testable, validated, and resilient; no authentication or authorization required. Evaluated for correctness, code quality, architecture, production readiness, and ability to explain decisions. The exercise indicates roughly **4-5 hours of implementation effort** and favors quality and thoughtful design over perfect or overly complex solutions.

**Additional approved delivery constraint:** The project must be runnable and testable for free from a cloned repository, with no subscription, trial account, hosted cache/database, secrets, or external connection strings. Use local SQLite. Check in the code and simple reproducible schema initialization, not a generated user-data database. No database migration framework is required or approved.

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

### Decision 3: Board-size safeguards and performance review
**Status: Approved provisionally, pending performance testing**

- **1,024 x 1,024 is the baseline size we expect to support and test, not the final production ceiling.** The earlier draft's “initial maximum 1,024” wording was superseded by this clarification.
- Test representative boards, including reasonably large boards and the support baseline, without requiring exhaustive size/density sweeps. Preserve the existing configurable request-size safeguard. Any additional dimension or cell-count limit requires measured need and developer review informed by actual application behavior and the lightweight performance sanity check.
- Minimum allowed grid is 1x1. Reject requests outside *the ultimately configured operational limits*.
- **Approved deferral:** Do not add `MaxCells` or an arbitrary board-dimension ceiling without measured need and developer approval. The suggested 4,194,304-cell value has no measurement basis or .NET constraint behind it and is not an approved default. Preserve existing structural validation and Kestrel request-size safeguards; verify transport behavior in Slice 10. This deferral does not remove the final-search computation budget.
- Use simple measurements and existing .NET tooling to check generation speed, cycle detection, memory consumption, JSON serialization, and persistence for meaningful performance problems. Identify an actual bottleneck and propose targeted improvements for developer review before changing the implementation; no dedicated benchmark framework or competing implementation is required.

**Documented assumption:** The assignment provides no maximum dimensions. Sparse internal representation alone cannot make an enormous **dense 2D JSON upload/response** practical.

**Tests:** Existing request-size boundaries, large sparse/dense/random boards, request and response size, restart/recovery as size scales. Add dimension/cell-count boundary tests only if those limits are subsequently approved.

### Decision 4: Stateful advancement (required endpoint #2)
**Status: Approved; earlier document's “proposed” label is obsolete**

- Board ID identifies a **stateful simulation** beginning at generation 0, not merely a fixed immutable snapshot.
- `Next State` computes from the last committed generation, advances by exactly one, persists the new state and generation together, and returns that generation.
- Each Next request requests one advancement from the current committed state. Concurrent successful requests commit consecutive generations without lost updates. An identical starting configuration produces the same next configuration (algorithmic determinism), but repeating an advance request can advance again, including after a client retransmission or response loss. There is no client request-token, deduplication, or original-response replay contract.
- Persist successfully committed progress so restart/crash can recover the last successful advancement. Only respond with success when the required write commits.

**Documented assumption:** “Get Next State” plus persistence is interpreted as advancing the stored board. It is not an explicit statement in the assignment; the API exposes the chosen behavior clearly.

**Tests:** Known pattern progression, sequential calls, persisted generation increments, restart recovery, failed writes, correct dimensions and response, simultaneous advances.

### Decision 5: Durable store versus optional caching; self-contained execution
**Status: Approved**

- **SQLite is the source of truth** for current committed state, generation, and successful final-state discovery. The uploaded board starts as current state at generation 0. Proposed Slice 4a additionally retains immutable InitialState in the same row for absolute reconstruction; restart recovery still uses current state/generation.
- An in-memory cache may speed up repeated calculations but is **not** durable and must never be the only copy of promised state.
- **Do not implement `IMemoryCache` initially.** Defer unless actual measurements show a worthwhile benefit. No Redis, hosted cache, or subscription.
- Cache eviction, service restart, or absence of a cache must not change correctness.

**Tests:** Correctness with cache absent/cleared (if subsequently implemented), SQLite recovery, no successful response on failed write.

### Decision 6: Advance versus preview (required endpoints #2 and #3)
**Status: Approved**

- **Next:** Compute one new state from current, commit it, and increment `CurrentGeneration` by one.
- **N Ahead:** Compute a **preview** N steps after the *persisted current generation*; return it without changing current state or generation.
- A preview observes a consistent starting snapshot even if another request advances the board concurrently.
- Previously computed intermediate states need not be persisted. Generation caching was considered but **deferred** under Decision 5.

**Tests:** Preview of N then Next advances only one, previews repeat deterministically from the same committed snapshot, concurrent preview/advance consistency, persistence unmodified.

### Decision 7: Relative stepsAhead semantics and zero
**Status: Relative semantics approved; stepsAhead naming proposed in the Slice 4 documentation revision**

- `stepsAhead` means the number of generations **ahead of the committed current generation**, not an absolute generation index.
- `stepsAhead=0` returns current cells and absolute generation with no write and no engine calls. Positive values use the existing engine from one committed in-memory snapshot. Reject currentGeneration + stepsAhead overflow before engine work; zero remains valid at the maximum current generation.
- E.g., when current generation is 5: `preview?stepsAhead=0` returns 5; `preview?stepsAhead=3` returns 8.

**Documented assumption:** The specification does not explicitly identify N's reference point. Relative future preview is supported by the phrase “N States Ahead.” The proposed absolute-generation route separately addresses reconstruction from upload (Decision 10).

**Tests:** N = 0, positive N, generation labels, repeated previews, invalid/oversized N.

### Decision 8: Stable outcomes and cycles (required endpoint #4)
**Status: Approved; simplified final-state representative selection explicitly approved**

- Both a fixed point and an oscillator count as a **successful final-state search conclusion**.
- **Stable:** A configuration repeats after one generation, cycle length 1.
- **Cycle:** A previously encountered configuration repeats after two or more generations, cycle length > 1.
- The primary outcome is whether the simulation stabilizes or enters a repeating cycle. **Approved representative convention:** for stabilization, return the verified stable configuration; for oscillation, return any verified configuration belonging to the detected repeating cycle. No canonical or first-repeated configuration is required, and no reconstruction solely to select such a representative is required.
- Preserve the existing response fields, including `generation`, `startingGeneration`, `conclusionKind` (`Stable` or `Cycle`), `cycleLength`, and `iterationsUsed`. `generation` must label the actual returned configuration on the trajectory from `startingGeneration`; `cycleLength` must be the verified period; `iterationsUsed` counts every actual generation-engine call, including additional detection/verification calculations. It is not a generation offset.
- Preserve correct cycle membership and accurate metadata for the dictionary-based implementation. The representative need not be canonical; this does not relax the unchanged persisted-result replay requirement in Decision 11.
- A cycle is not misrepresented as a single permanently frozen board.
- Verify equality exactly before declaring a cycle; fingerprints alone can collide.

**Documented interpretation:** We read the assignment's wording about states that no longer change or cycle as including oscillating states among successful conclusions.

**Rationale:** Stabilization or oscillation is the meaningful final outcome; representative selection and generation labels are supporting metadata. Requiring a particular representative adds algorithmic complexity without improving that outcome. Allow any verified representative while retaining exact correctness and reporting.

**Tests:** Still-life block, empty stable board, 2-period blinker, longer-period oscillators, transient then stable/cycle, hash collision simulation when applicable, and limit-edge behavior. Independently verify stable/cycle membership, the exact period, the returned configuration at its claimed absolute generation, and actual engine-call accounting. Accept any verified cycle member with its correct generation label; reject transient configurations, mislabeled phases, incorrect periods, and fingerprint-only matches. No assertion requires a canonical or first-repeated representative.

### Decision 9: Internal final-state computation budget and cancellation
**Status: Approved; replaces the earlier caller-defined `maxIterations` proposal**

- The Final State endpoint **does not accept `maxIterations` or any other caller-selected iteration budget**. The assignment calls for an error after a reasonable amount of work but does not require exposing this control to API consumers.
- Enforce **one configurable server-side maximum** on the number of actual calls to the generation engine during a *single final-state search*. The initial value of **10,000** remains provisional and subject to developer review informed by the lightweight performance sanity check. Count every generation calculation, including any collision-verification calculation; dictionary-based detection is the approved implementation.
- **Check the remaining budget before every generation-engine call**, including any collision-verification calculation. If the number of calls already made equals the cap, do not make another call. A conclusion proven using exactly the allowed number of calls succeeds; return `422` only when another calculation is required to establish the conclusion and would exceed the cap. Never perform an extra call and reject afterward. This clarifies the approved budget rule and requires no new infrastructure or abstractions.
- If a valid conclusion cannot be established without exceeding this computation budget, return the approved `422 Unprocessable Content` response. This is an **internal resource budget**, not a client-controlled promise about logical generations.
- Report `iterationsUsed` as **actual generation-engine calculations performed during the successful discovery**, not the difference between absolute generation labels. Do not confuse this with the absolute result generation or cycle length.
- Apply the work limit to **new calculations**. A previously persisted successful conclusion is returned by the lookup-first path in Decision 11 without running the engine or reapplying the current configured work limit.
- Pass ASP.NET Core request cancellation (e.g., `RequestAborted`) through expensive calculation. Check for cancellation at least between generation calls and, when practical, within costly steps. Searches never advance the board. Failure or cancellation observed before commit must leave no final-state result; cancellation or response loss after a successful commit cannot undo the saved conclusion (see Decision 14).
- Keep Final State synchronous for the initial build. Background workers, durable jobs, and polling were considered but deferred until performance data justifies them.

**Documented assumption:** The specification does not define a precise "reasonable" iteration count. A documented configurable internal computation cap gives the service a bounded, testable failure condition without adding public API complexity.

**Tests:** Use independently established expected configurations and periods (for example, hand-verified block and blinker fixtures), not expected results generated by the engine under test. Assert successful discovery exactly at its required call limit, accurate `iterationsUsed`, and `422` when the next required calculation would exceed a smaller cap; assert that the forbidden extra call never occurs and exhaustion persists no result. If fingerprints are used as dictionary keys, force equal fingerprints for unequal configurations and verify that exact equality prevents a false stable/cycle conclusion. Retain still-life discovery in one engine call, longer oscillators, cancellation, and the absence of public `maxIterations` behavior.

### Decision 10: Absolute-generation retrieval from immutable initial state
**Status: Proposed Slice 4 revision for documentation review; replaces prior deferral only after approval**

- Add `GET /api/boards/{id}/generations/{generationNumber}` alongside `GET /api/boards/{id}/preview?stepsAhead=N`.
- `generationNumber` is a nonnegative absolute index from upload. Initially always calculate from InitialState, even when current progress is ahead of, equal to, or behind the target. Return the exact cells with response `generation` equal to generationNumber.
- generationNumber=0 returns original uploaded cells with zero engine calls, including after current cells change and SQLite reopens. Both GETs return 404 for unknown boards and never mutate initial/current state or generation, persist derived results, or calculate/store/invalidate final-state results.
- With current generation 5, preview offsets 0 and 3 return generations 5 and 8; absolute targets 0, 3, and 8 reconstruct those generations from upload.
- Add only one immutable InitialState column to Boards. Upload writes InitialState = CurrentState and CurrentGeneration = 0 in one successful insert. Next never changes InitialState. Reuse the engine, JSON coordinate codec, cancellation support, and provisional configurable computation-budget policy; no arbitrary new cap.
- No initial-state/snapshot tables, per-generation persistence, caches, speculative shortcuts/checkpoints, new codecs, migration frameworks, or endpoint-specific computation frameworks.

**Database caveat:** Assume disposable development databases and rebuild them for the new schema. CREATE TABLE IF NOT EXISTS does not upgrade an existing table. Original state cannot be inferred from an advanced current state. If non-disposable data must be retained, stop for a migration policy before schema changes; never fabricate initial states. InitialState enables absolute reconstruction, not basic restart recovery. No database is deleted/rebuilt by this documentation step.

**Tests and review:** Durable identical initial/current upload state; original cells after changed current state and reopen; zero calls for zero; independent positive fixtures with current progress greater/equal/less than target; unchanged persistent values; consistent unknown/invalid/excessive handling. Numeric budgets, unresolved status/duplicate-query details, database retention exceptions, checkpoints 4a-4f, and this documentation diff require review before the relevant implementation. Keep 11 master slices and a separate developer checkpoint after each GREEN.

### Decision 11: On-demand, lookup-first final-state persistence
**Status: Approved; updated after adversarial review**

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
**Status: Approved; simplified client contract**

- Calculate Next outside database write transactions. Use an optimistic update against the expected current generation, committing the next state and incremented generation atomically in SQLite.
- For concurrent Next requests, a generation conflict causes the loser to reload the latest authoritative state, calculate again, and retry up to a configurable bounded count. Ordering of concurrent requests is not guaranteed. Exhausted optimistic conflicts return `409 Conflict`.
- Treat every client Next request as a separate advancement request. Do not introduce request tokens, deduplication records, saved Next responses, expiration metadata, or a replacement retransmission contract.
- Distinguish expected-generation conflicts, genuine SQLite contention, transaction failures, unexpected failures, and cancellation using Decision 14. Dispose failed resources before retrying; a failed expected-generation update always requires a fresh authoritative snapshot and recalculation.
- Roll back the entire state/generation change on pre-commit failure. Success requires a confirmed commit. Never automatically repeat an ambiguously committed advancement; a later generation does not prove which request committed. A confirmed commit remains durable even if its HTTP response is lost.
- Advancing a board **does not invalidate FinalStateResults** (Decision 11). No Redis, locks outside SQLite, or distributed services are needed.

**Rationale:** The assignment requires durable progress and correct concurrent advancement. Keep those guarantees without adding an additional client-facing retransmission contract.

**Tests:** Concurrent requests reading the same generation produce consecutive correct committed states; prove that the optimistic loser reloads/recalculates from the winner's committed state. Verify bounded optimistic retry exhaustion, durable progress after restart, atomic rollback of state and generation, classified/bounded SQLite contention, sanitized unexpected errors, cancellation/resource cleanup, and no automatic re-advancement after response loss or an uncertain commit. Verify that Next preserves saved FinalStateResults. Use real file-backed SQLite connections and deterministic synchronization with narrowly scoped rollback/contention fault injection.

### Decision 13: Dictionary-based cycle detection
**Status: Approved; comparative algorithm selection is superseded**

- Use a straightforward dictionary-based cycle-detection algorithm, retaining previously encountered board states and their generation indexes. Verify actual board-state equality before identifying a cycle; a matching fingerprint alone is insufficient.
- Preserve accurate stable/cycle conclusions, cycle lengths, generation labels, cancellation, and the approved any-verified-representative convention. Do not reconstruct a canonical or first-repeated representative solely for presentation.
- **The computation-budget rule is unchanged** (Decision 9): Check before every engine call, count all actual calls, and enforce the configurable internal cap (provisionally 10,000). Never report a conclusion that was not proved within the budget.
- Do not implement Brent's algorithm or competing cycle-detection implementations solely for performance comparison. Consider Brent only if actual measurements demonstrate a significant performance or memory problem, then propose a targeted change for developer review before implementation.
- Use the final lightweight performance sanity check to identify actual problems. No mandatory algorithm comparison, performance-selection gate, temporary benchmark project, or dedicated benchmarking framework is required.

**Documented assumption:** The exercise mandates correct cycle detection, not speculative optimization. No caller-defined iteration parameter exists.

### Decision 14: SQLite schema, storage, transactions, and codecs
**Status: Approved provisionally, pending implementation and performance testing**

**Two planned tables, introduced incrementally:**

- `Boards` (only table until Slice 8): Slice 3 columns `Id` (GUID), `Width`, `Height`, `CurrentState`, `CurrentGeneration`; proposed Slice 4a adds immutable `InitialState`.
- `FinalStateResults` (Slice 8): Unique `BoardId` (FK), `StartingGeneration`, `ResultGeneration`, `ResultState`, `CycleLength`, `IterationsUsed`, and an optional explicit conclusion kind. **No requirement to match a later current generation** when reading a successful conclusion.

**Persistence and encoding:**

- Upload starts at generation 0; proposed Slice 4a writes identical initial/current states in one insert. Retain initial state unchanged for absolute reconstruction, with no intermediate generations. Every successful Next atomically persists only current state/generation. Relative preview reads the current pair together in one query and computes from that in-memory snapshot outside database resources. Only Final State endpoint may produce a durable `FinalStateResults` record, and only after success, starting in Slice 8.
- **Approved simplification:** Use built-in System.Text.Json to serialize living-cell coordinates as JSON for SQLite persistence. Keep width and height as separate database fields and reconstruct `HashSet<(int X, int Y)>` on deserialization. Use a minimal coordinate data type; no custom converters, serialization frameworks, or unnecessary interfaces.
- Custom binary encoding and alternative-codec implementation/benchmarks are **deferred unless actual performance measurements demonstrate that JSON serialization or storage is a meaningful bottleneck**. Their presence in earlier plans does not authorize this work. Retain all other architectural decisions.
- Use Microsoft.Data.Sqlite directly with parameterized SQL and simple reproducible schema initialization from checked-in code. Create only `Boards` in Slice 3; defer `FinalStateResults` until Slice 8. Do not introduce Entity Framework Core, Dapper, other ORMs, or database migration frameworks without explicit approval; no external database service.
- Preserve separate connections per operation and the approved WAL, foreign-key enforcement, and `synchronous=FULL` durability settings.

**Atomicity and concurrency:**

- Perform generation calculations outside database transactions. Commit the current board's state and generation together using an optimistic expected-generation check.
- On an optimistic Next generation conflict, reload authoritative state, recalculate, and retry within the configured bound. Handle SQLite locking separately under the failure policy below (Decision 12).
- **Do not delete or invalidate FinalStateResults during Next**. A successful final-state result is an eventual deterministic outcome of the board's trajectory, not a materialized version of the current generation.
- For Final State, check for a stored result **before simulation**, insert only after successful computation, and enforce unique `BoardId`. On simultaneous first-time calculations, the losing insert reads the existing winner's record and returns that persisted result. A concurrent Next advancement does not invalidate the final conclusion.
- Return success only after required writes commit or authoritative verification establishes that the relevant write committed. Failed or exhausted Final State calculations and cancellation observed before commit do not create records. Cancellation or HTTP response loss after a successful commit does not undo it.

**Approved failure-handling requirements (developer clarification; architecture unchanged):**

| Failure type | Required handling |
| --- | --- |
| Optimistic generation conflict | A conditional update that affects no row is not a SQLite locking exception. End the failed attempt, reload the latest authoritative board, recalculate, and retry within the configured bound. If the board no longer exists, use the existing `404` convention. Genuine concurrency retry exhaustion remains `409`. |
| SQLite busy/locked condition | Classify `SqliteException.SqliteErrorCode` and `SqliteExtendedErrorCode` together with the operation and connection state. Temporary lock contention is distinct from an expected-generation conflict. Account for provider busy waits; do not retry every busy/locked exception indiscriminately. |
| Constraint failure | Handle foreign-key, check, not-null, and unexpected unique/primary-key failures as database errors. The known FinalStateResults uniqueness conflict follows the saved-winner rule in Decision 11; unrelated constraints must not be mistaken for that conflict. |
| Transaction failure before commit | Roll back the whole advancement: current state and generation increment form one atomic unit. A statement failure does not justify retaining earlier writes. Dispose of readers, commands, transactions, and connections; cleanup failures must not obscure the original failure or lead to reuse of the connection. |
| Unexpected database failure | Do not blindly retry I/O, corruption, full/read-only database, invalid schema, or other unclassified failures. Return sanitized `500` ProblemDetails when a response is possible; expose no SQL, database path, provider exception text, or stack trace. Keep diagnostic details in server logs with a correlation identifier. |
| Cancellation | Observe request cancellation before calculations, attempts, database operations, and commit. Stop new work/retries and safely clean up an active transaction. Cancellation/disconnection does not establish whether a commit occurred and cannot reverse a completed commit. No new cancellation status code is introduced. |

**Proposed implementation details — pending developer review:**

These retry and concurrency mechanics apply in later slices. Slice 3 implements only minimal upload persistence, resource cleanup, and sanitized failure handling with no success on a failed write. Do not scaffold future service/storage methods, retry frameworks, concurrency coordination, caching, or speculative optimizations in Slice 3.

- Keep this logic in `BoardService` and `SqliteBoardStore` with normal .NET exception handling and small operation-specific methods. No generic retry framework, distributed locks, external service, or background retry worker. Use private-cache, separate connections per attempt; do not share active connections/readers across concurrent requests. Prefer a short non-deferred write transaction after calculation, protecting the conditional state/generation update without retaining a read transaction during simulation.
- Use one bounded operation retry loop with a configurable retry count and finite database-wait budget. Before every eligible retry, finish cleanup, open a fresh connection/transaction, reload authoritative state, and recalculate; never carry a failed attempt's board snapshot into a new calculation.
- Let Microsoft.Data.Sqlite perform its built-in ordinary busy/locked waits using finite `DefaultTimeout`/`CommandTimeout` settings, including implicit commands such as transaction start. Do not add a second sleep/backoff loop that restarts a full provider timeout after ordinary contention times out. All attempts, commands, and recovery lookups consume the same bounded operation budget; account for provider timeout granularity when documenting the effective bound. Retry counts, timeouts, and the total wait bound remain configurable and subject to the SQLite contention/timeout correctness review; no numeric defaults are selected here.
- Distinguish ordinary `SQLITE_BUSY` (including known transient recovery/timeout variants) from `SQLITE_BUSY_SNAPSHOT`, which requires ending the stale transaction and restarting with fresh reads rather than waiting inside it. An ordinary provider busy-timeout expiry ends the contention path instead of granting another full wait window. `SQLITE_LOCKED` commonly reflects a same-connection/shared-cache resource conflict; with separate private-cache connections, treat it as an unexpected resource-lifetime defect unless a concrete transient case is demonstrated and explicitly approved for bounded retry. Do not enable shared cache merely to make it retryable.
- For a competing `FinalStateResults` insert, recognize the known unique-BoardId conflict, end the losing attempt, and read the saved winner without recalculating or invalidating it. Do not use `INSERT OR REPLACE`, overwrite the winner, or ignore unrelated constraint failures.
- If `Commit` throws, determine whether the transaction remains active; SQLite can leave it active after an error. End/roll back any still-active attempt and dispose of the connection. An uncertain Next commit outcome must not trigger automatic re-advancement; return sanitized `500` when its outcome cannot be established. Unexpected commit errors are nonretryable.
- A later generation cannot identify which request committed. A response failure after a confirmed commit is a transport failure, not permission to rerun database work. For upload/final-result recovery, verify the already assigned unique board/result identity rather than infer success from unrelated progress; an unavailable read does not establish absence or success.
- Microsoft.Data.Sqlite's async ADO.NET calls execute synchronously. Do not promise immediate cancellation merely because a token was passed to an async method. Check cancellation around synchronous operations, keep provider waits finite, and measure the selected provider version's cancellation behavior. If cancellation arrives during an operation, clean up when control returns; do not schedule another attempt. Preserve confirmed commits, do not launch background work, and do not reuse the canceled request token as the only means of transaction cleanup.

**Proposed HTTP clarification requiring developer approval:** Keep `409` for genuine optimistic-concurrency exhaustion and `500` for unexpected/unverifiable database outcomes. Propose applying the existing `409` concurrency-exhaustion convention also to positively identified temporary SQLite contention after its bounded wait/restart budget is exhausted. This extends the mapping explicitly; it is not yet approved. Do not silently introduce `503`, map every SQLite exception to `409`, or apply `422` to database failures.

**Provider references:** [Microsoft.Data.Sqlite errors and timeouts](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors), [transaction behavior](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions), [async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async), [SQLite result codes](https://sqlite.org/rescode.html), and [SQLite transaction/commit behavior](https://sqlite.org/lang_transaction.html). The retry policy above is a proposed application design based on these provider behaviors, not a claim that SQLite supplies an end-to-end retry guarantee.

**Tests and performance review:** JSON coordinate round-trip for empty/populated boards and clear failure for invalid stored JSON; durable board progress across restarts/crashes; atomic Next state/generation rollback; concurrent Next without lost updates and with authoritative reload/recalculation; bounded contention handling; no automatic re-advancement after an uncertain commit or response loss; final result lookup-first and zero engine calls on hit; concurrent first-time final searches (one saved result), Next/Final interleavings, restart reuse, response serialization, database size, and JSON serialization/storage costs.

### Decision 15: HTTP API, validation, and error convention
**Status: Approved; configurable limits remain subject to performance review**

| HTTP method | Route | Effect |
| --- | --- | --- |
| `POST` | `/api/boards` | Slice 3: persist uploaded grid as current state at generation 0; return `201` and a unique board ID with common state data; no Location requirement until Slice 4; do not compute final state |
| `POST` | `/api/boards/{id}/next` | Advance once and commit the new current state |
| `GET` | `/api/boards/{id}/preview?stepsAhead=3` | Return three steps ahead of one committed current snapshot, without writes or final-state computation |
| `GET` | `/api/boards/{id}/generations/{generationNumber}` | Proposed: reconstruct absolute generation from InitialState, without writes or final-state computation |
| `GET` | `/api/boards/{id}/final-state` | **Look up a stored successful conclusion first**; compute/persist it only on a miss; do not advance the board |

**Proposed contract revision:** Relative query naming is `stepsAhead`; the absolute path parameter is `generationNumber`. Common response `generation` remains an absolute label. The public Final State API still has no `maxIterations` query parameter; its budget remains internal (Decision 9).

**Upload example:**

```json
{
  "cells": [
    [false, true, false, false],
    [true, false, false, true]
  ]
}
```

**Common board-state response shape (Slice 3 upload example, HTTP `201 Created`; no Location header required):**

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

- Slice 3 upload returns `201 Created` with the generated board ID, correct dimensions, generation 0, and cells using the common board-state response. No `Location` header is required in Slice 3. In Slice 4, when preview becomes available, add `Location: /api/boards/{id}/preview?stepsAhead=0` to upload responses and verify that it resolves. This URI represents the **current** board and does not guarantee immutable access to generation 0 later; the absolute-generation URI denotes an immutable logical generation and does not replace the live-current Location resource.
- Next, relative preview, and absolute retrieval use the common state DTO with absolute generation numbers. Final State retains the existing fields `generation`, `startingGeneration`, `conclusionKind`, `cycleLength`, and `iterationsUsed` alongside the board configuration. Return the verified stable configuration or any verified member of the repeating cycle, with accurate absolute generation, period, and **actual engine calls used when the stored result was discovered**. No canonical or first-repeated representative is required. Preserve saved responses unchanged and do not imply that the cached representative is always later than today's current generation.
- Validate nonempty rectangular boolean grids; reject `null`, jagged and malformed JSON and invalid IDs. Preserve existing request-size safeguards; additional dimension/cell-count caps remain deferred under Decision 3. **1,024 x 1,024 remains a testing baseline, not a final ceiling**.
- **Resource-protection verification:** Configure Kestrel's request-body limit before body reading/JSON binding, using existing ASP.NET Core facilities. For a known oversized `Content-Length`, reject before deserializing the grid; retain server enforcement for chunked/unknown-length bodies, stopping when the limit is crossed without buffering the full upload. Some bounded reading/parsing is unavoidable for unknown-length bodies; do not promise zero allocation. Verify these paths with actual Kestrel, not only TestServer. Numeric payload, dimension, and cell-count limits remain provisional/configurable pending developer review informed by the lightweight performance sanity check; small test overrides are not production defaults.
- Preview `stepsAhead` and absolute `generationNumber` are nonnegative integer quantities. Both zero cases perform no engine calls or writes. Positive values reuse the engine and provisional configurable computation-budget policy. No budget setting currently exists in code; 10,000 is not a finalized cap. Review configuration and unresolved HTTP details before relevant positive-work cycles; introduce no arbitrary new limit.
- Final State uses a **server-configured computation budget only**, initially proposed at 10,000 actual engine calls; on exhaustion return `422 Unprocessable Content`. It has no public budget parameter.
- Return standardized ASP.NET Core `ProblemDetails` errors without internal stack traces; propagate cancellation for expensive computations.
- **ProblemDetails verification:** A `413` status does not itself guarantee a ProblemDetails body. Use the existing `AddProblemDetails`, status-code pages, and exception handling where applicable; preserve a body-read `BadHttpRequestException`'s `413` rather than converting it to `400`/`500`. Test supported JSON `Accept` headers, absent `Accept`, and unsupported media preferences in Production configuration. Require sanitized `application/problem+json` with body `status: 413` on the application's supported JSON error paths. Server-level rejections outside the app pipeline or a disconnected/started response cannot be assumed to support that envelope; record the observed boundary and bring any contract exception to developer review. Do not claim universal consistency before real-Kestrel tests execute. [.NET 8 body-binding source](https://github.com/dotnet/aspnetcore/blob/v8.0.0/src/Http/Http.Extensions/src/RequestDelegateFactory.cs), [ASP.NET Core ProblemDetails guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-8.0).

| HTTP status | Chosen convention |
| --- | --- |
| `200 OK` | Successful retrieval/preview/advance or final-state conclusion |
| `201 Created` | Upload succeeded |
| `400 Bad Request` | Invalid board content/shape or invalid supplied parameters |
| `404 Not Found` | Unknown board ID |
| `409 Conflict` | Optimistic concurrency retries exhausted; SQLite-contention exhaustion mapping remains proposed for review |
| `413 Content Too Large` | Request exceeds configured payload limit |
| `422 Unprocessable Content` | Final State needs another calculation to prove a conclusion, but that call would exceed its internal budget |
| `500 Internal Server Error` | Unexpected server failure |

- A caller can receive no HTTP response after disconnect/cancellation. Only successfully committed writes are reported as successful.
- Database errors follow Decision 14: approved optimistic retry exhaustion is `409`, unexpected or unverifiable failures use sanitized `500`, and cancellation introduces no new status. Extending `409` to verified SQLite contention exhaustion is explicitly proposed for developer approval; the approved status table above is unchanged.
- Check large response serialization/size separately during the lightweight performance review from engine execution and persistence; coordinate JSON persistence does not make dense rectangular API JSON cheap.
- No authentication or authorization, as specified. The proposed build includes absolute-generation reconstruction from InitialState and no general-purpose cache.

**Required API tests:** Five routes under the proposed contract; consistent DTO and error semantics; invalid/empty/jagged/null/big inputs; unknown IDs; correct relative stepsAhead and absolute generationNumber semantics, zero calls for both zero cases, and nonmutating retrieval; upload and Next do not create or invalidate a final result; first Final State computes and persists; subsequent call returns stored result **without calling the engine**, including after restart/Next; internal-budget exhaustion returns `422` without storing; concurrent Next requests commit consecutive correct generations without lost updates; concurrent Final State calls; rollback, cancellation, and large JSON response behavior.

## C. Test strategy to turn into a reviewed implementation plan

The decisions above name required tests. Consolidate them into a focused suite, **not one redundant test per decision paragraph**. Challenge assertions against realistic incorrect implementations before approving each TDD slice.

### Slice 3: Minimal Durable Board Upload — complete; approved implementation and test boundary

Use exactly 11 slices in `PLAN.md`: 1 engine, 2 JSON serialization, 3 durable upload, 4 relative preview and absolute-generation retrieval via checkpoints 4a-4f in PLAN.md, 5 atomic Next, 6 concurrent Next and SQLite contention handling, 7 bounded dictionary-based final search, 8 final-state HTTP persistence, 9 concurrent final-state discovery, 10 HTTP/resource coverage, and 11 final verification, cleanup, and performance review. Slice 3 has only these responsibilities:

1. Accept a valid rectangular boolean grid through `POST /api/boards`.
2. Validate basic board structure and configured resource safeguards.
3. Convert the request into the existing `BoardState` representation.
4. Persist the board in SQLite at generation 0 using the existing System.Text.Json living-cell coordinate serialization.
5. Return `201 Created` with the generated board ID and common state response data; no Location header is required until Slice 4.
6. Reopen the database and reconstruct the stored board correctly.
7. Return an appropriate sanitized error and no successful upload response when persistence fails.

Create only `Boards` with `Id`, `Width`, `Height`, `CurrentState`, and `CurrentGeneration`. Introduce only the endpoint, DTOs, service operations, storage operations, and simple reproducible initialization needed for this slice. Use Microsoft.Data.Sqlite directly with parameterized SQL and the approved durability settings. Do not introduce a migration framework or scaffold future endpoints/methods. `FinalStateResults` waits until Slice 8. Do not implement retry frameworks, concurrency coordination, caching, or speculative performance optimizations. Preserve existing request-size and structural-validation safeguards; `MaxCells` and additional dimension caps are deferred pending measured need and explicit review.

**Focused Slice 3 acceptance tests:**

1. Valid asymmetric upload returns `201`, a unique board ID, correct dimensions/cells, and generation 0.
2. Invalid requests are rejected, including null, empty, jagged, and malformed grids; cover configured basic safeguards.
3. After closing and reopening real file-backed SQLite, retrieve through the storage layer and reconstruct identical dimensions, living cells, and generation. No public read endpoint is needed before Slice 4.
4. A failed database write returns an appropriate error, never a successful upload response.

Use the existing incremental TDD workflow: implement one focused behavior at a time, execute RED tests and report the expected failure, continue directly to GREEN, run targeted tests and relevant regressions, and stop for explicit developer review before refactoring or the next change. Do not add process-kill simulations, complex fault-injection infrastructure, concurrency test harnesses, or historical-generation tests in Slice 3.

### Cross-slice test strategy

The suite below spans the full implementation; it does not expand completed Slice 3. Proposed Slice 4a adds initial-state persistence, and Slice 5 additionally proves Next leaves InitialState unchanged. Add tests with their corresponding slices in `PLAN.md`; retain advanced resource/transport coverage for Slice 10 and final verification for Slice 11. Verify the upload Location header in Slice 4 when preview becomes available. Stateful advancement, atomic state/generation updates, later durable lookup-first final results, cancellation, and computation-budget safeguards remain required.

1. **Pure engine unit tests:** Moore-neighborhood rules (birth at 3, survival at 2/3, death otherwise); all-dead, block, blinker, and other known patterns; sparse coordinates, 1x1 / 1xN / Nx1 / asymmetric grids, boundaries, immutable input, and exact expected configurations derived independently.
2. **Cycle and internal-budget tests:** Stable period 1, oscillators of period >1, transient then cycle, exact collision-safe equality, cancellation, success and exhaustion at the **actual generation-engine call count** boundary. Independently verify that each returned representative belongs to the eventual stable state/cycle, matches its claimed generation, and reports the exact period and engine-call count. Reject transient representatives, inaccurate labels/periods, and uncounted extra calculations; do not require a canonical representative or a competing algorithm.
3. **SQLite integration tests:** Upload/reload, durable current progress across restarts, atomic Next state/generation commit and rollback, no automatic re-advancement after response loss or an ambiguous commit, first Final State **lookup before calculate**, one successful saved result, zero engine calls on cache hit, reuse after Next and restart, and no result from failed calculation or cancellation observed before commit. Inject a database failure after the conditional board update but before commit and assert that both state and generation roll back. Cover cancellation during database work, rollback/cleanup failure handling, unrelated constraints, and sanitized unexpected errors. All database guarantees require real file-backed SQLite connections, not mocks alone.
4. **HTTP integration tests:** Five routes under the proposed contract, real serializer and validation, exact status and `ProblemDetails`, relative stepsAhead=0/positive previews, absolute generationNumber=0/positive reconstruction from InitialState, final-state response snapshot semantics, cancellation, unchanged InitialState after Next, no public `maxIterations` behavior, and response size. Add the focused resource/validation cases below; TestServer alone is insufficient evidence for Kestrel request-size enforcement.
5. **Concurrency tests:** Barrier-synchronize Next requests reading the same generation; assert one loser reloads authoritative state and recalculates, and that the two successful responses represent consecutive correct committed generations without lost updates. Test bounded optimistic-conflict exhaustion. Hold a real writer lock to exercise provider busy waiting, release it deterministically for success, and hold it through configured exhaustion for the proposed error path. Reproduce stale-snapshot busy and same-connection locked cases to test their different classifications. Two first-time Final State requests save one winner and return its record; unrelated constraint failures remain errors. Concurrent Final State calculation and Next advancement never corrupt current progress or invalidate a discovered eventual conclusion. Use deterministic barriers/events and narrowly scoped SQLite/test-harness faults; do not use sleeps to arrange races or build a general fault-injection framework. Finite watchdogs may detect hangs, but normal tests must not assert performance latency targets. Review contention/timeout mechanics before Slice 6.
6. **Final performance sanity check:** Use representative boards, including reasonably large boards and the 1,024 x 1,024 support/test baseline, with simple measurements and existing .NET tooling. Check generation speed, dictionary-based cycle detection, memory, JSON serialization, and persistence only to identify meaningful performance problems. No exhaustive workload sweep, comparative benchmark, dedicated framework, or alternative implementation is required. Propose targeted improvements for developer review only if actual measurements justify them; performance observations do not establish functional correctness.

**Proposed Slice 4 evidence:** Durable identical initial/current upload values; both GETs preserve all persistent values and accurate labels; zero engine calls for zero; absolute positive cases with current progress greater/equal/less than target; original cells after current cells change and reopen. Use shared provisional budget/validation policy. Specialized concurrent-Next orchestration remains in Slice 6; elaborate expensive-work/disconnect cancellation remains in Slice 10.

**Focused HTTP validation/resource tests (proposed acceptance details):**

- Run a small group in the existing integration-test project against the production app composition hosted by real Kestrel on an ephemeral loopback port, using normal `HttpClient`, the same middleware, and temporary file-backed SQLite. Keep WebApplicationFactory/TestServer for ordinary HTTP tests; no additional project, server framework, proxy, or external infrastructure is needed.
- For configured body limit `L`, construct valid UTF-8 JSON bodies of exactly `L-1`, `L`, and `L+1` bytes using harmless whitespace padding and structurally valid boards. Below/at the limit, accept valid uploads; above it, return `413`. Cover known `Content-Length` and HTTP/1.1 chunked/unknown-length uploads. Test application early rejection and Kestrel's native enforcement independently so an application guard cannot mask a missing server limit. Configure any per-request size feature before reads, not in an endpoint filter that runs after JSON binding.
- Send very wide single-row boards and large sparse/dense rectangles, including the 1,024 x 1,024 support baseline, with an adequate test-configured request-size limit; use small overrides for routine byte-boundary cases. Dimension/cell-count boundary cases apply only if those limits are subsequently approved; keep body size within its limit when testing such validation (`400`) and isolate it from payload rejection (`413`). Assert orientation/round-trip correctness, no arithmetic overflow in size validation, and no persisted board on rejection. Check larger practical workloads where useful during the lightweight performance review; the baseline is not a production ceiling.
- For oversized uploads, verify rejection before full grid materialization/domain mapping or SQLite writes. Use narrowly scoped test observations of body-read/binding entry where needed; a `413` assertion alone does not prove resource protection. Known-length rejection should precede JSON parsing; streamed requests may parse a bounded prefix but must not buffer/materialize the entire oversized grid. Measure allocation/peak-memory effects separately during the lightweight performance review instead of asserting arbitrary memory/timing thresholds in ordinary tests.
- Preview `stepsAhead` validation: missing/empty, malformed noninteger, negative, above configured maximum, numeric parse overflow, and duplicate values (including equal duplicates). Proposed clarification: require one unambiguous value and return existing `400` ProblemDetails for these invalid requests. Seed a real persisted generation near its representable maximum, then use an otherwise valid `stepsAhead` whose addition would overflow: check before engine work, return proposed `400`, and leave state unchanged; `stepsAhead=0` at the maximum remains valid. Do not wrap generation labels or return `500`/final-search `422` for this validation failure.
- Cancel real expensive generation work through request cancellation, covering relative preview, absolute-generation retrieval, and an uncached final-state search. Coordinate entry/cancellation with the existing test controls, verify the server observes cancellation and stops further calculations, and verify no current-state mutation or final-result write before commit. Do not treat a canceled client await as proof of server cancellation or require a particular response after disconnect. Include cancellation checks within a costly engine call as well as between calls; measure latency separately.

**Kestrel/413 evidence status:** The .NET 8 Kestrel source checks an oversized declared length when body reading starts, and Minimal API binding preserves the resulting HTTP status. ProblemDetails still depends on middleware, negotiation, and response state. Actual runtime behavior for this app is unverified until the planned tests run; preserve the approved `413` convention and review any observed envelope limitation rather than silently weaken it. [.NET 8 Kestrel body-size check](https://github.com/dotnet/aspnetcore/blob/v8.0.0/src/Servers/Kestrel/Core/src/Internal/Http/Http1ContentLengthMessageBody.cs), [server error-handling boundary](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-8.0#server-exception-handling).

Use an independent correctness oracle where possible. Assert exact cell configurations, persisted state and generation, saved final-result metadata, and engine **call counts**, not merely non-null responses, success status, or mocks. Before each implementation cycle, conduct a targeted adversarial review; follow the approved RED → GREEN workflow and stop after GREEN for developer approval.

## D. Unresolved or conditional details: ask in the plan, do not silently decide

1. **Dimension and payload limits:** Preserve existing safeguards. Additional `MaxCells` and dimension caps are deferred until measurements justify a separately approved change. 1,024 x 1,024 is the baseline to support/test, **not** a permanent maximum. Verify existing Kestrel request-size behavior in Slice 10; do not treat proposed limits as implemented or required to close Slice 3.
2. **Internal Final State cap:** The unit is **decided**: actual generation-engine calls, without any public override. The numeric value (proposed 10,000) remains provisional pending developer review informed by the lightweight performance sanity check.
3. **Cycle detection algorithm (resolved):** Straightforward dictionary-based detection with actual state equality is approved. Brent is deferred unless measurements demonstrate a significant performance or memory problem and the developer approves considering it. Preserve exact periods, generation labels, actual engine-call counts, cancellation, and the any-verified-state convention.
4. **Persistence serialization (resolved):** Minimal System.Text.Json coordinate serialization is approved; width/height remain separate. Custom binary encoding and alternative-codec comparisons are deferred unless measurements demonstrate a meaningful JSON serialization or storage bottleneck. No codec-selection gate is required for the JSON implementation.
5. **SQLite retry implementation and tuning:** Review the proposed mechanics/classification in Decision 14 before Slice 6 and validate the configurable retry count, provider timeout, and effective total wait bound in the retained SQLite contention/timeout correctness review. The proposed `409` mapping for identified SQLite contention exhaustion requires explicit developer approval. Retry eligible concurrency failures only; unexpected failures and uncertain commits are not blind-retry cases. No numeric values are locked here.
6. **HTTP Location header (resolved):** No Location requirement in Slice 3. Add `Location: /api/boards/{id}/preview?stepsAhead=0` in Slice 4 when the read endpoint exists; verify it resolves to live current state, not immutable generation-0 history.
7. **Optional cache:** Do not implement `IMemoryCache` initially; `FinalStateResults` is approved *durable memoization*, not a general cache.
8. **Performance fallback:** Do not silently change the approved rectangular JSON API or introduce extra services if dense boards strain resources. Present measured tradeoffs for developer approval.

**Scope and unresolved details:** The proposed Slice 4 revision adds absolute-generation retrieval; budget configuration and unresolved validation details remain for review. No caller `maxIterations`; no final-result invalidation on Next; no client request-token, deduplication, saved Next-response, or expiration contract. Each Next request is a separate advancement request. Atomic durable state/generation updates, authoritative reload/recalculation after optimistic conflict, rollback, bounded SQLite contention handling, sanitized errors, cancellation/resource cleanup, and correct bounded final-state detection remain required. These are approved decisions, not open questions for Codex to re-decide.

**Approved final-state representative selection:** Return the verified stable configuration or any verified configuration in the detected repeating cycle, with the existing response fields and accurate generation, period, and actual engine-call count. No canonical/first-repeat requirement applies. The first persisted conclusion and its metadata remain unchanged on reuse.

## E. Suggested Codex deliverable: plan only

Respond with:

- A brief **requirements-to-decisions traceability matrix for the proposed five-route contract**, distinguishing relative preview from absolute-generation retrieval and marking the Slice 4 revision pending documentation review.
- An implementation plan containing exactly 11 small reviewable slices with minimum project boundaries and concrete acceptance criteria, **without implementation code**.
- A test-first sequence that targets the resolved adversarial risks: lost updates during concurrent Next, authoritative reload/recalculation, rollback and bounded contention, counted cycle-detection work, lookup-first final-state reuse, concurrent first-time final calculations and Next interleavings.
- A practical two-table SQLite schema/transaction plan: atomic state/generation advancement with optimistic concurrency and separate on-demand final-result insertion with unique board ID and **no Next-driven invalidation**. Preserve uncertain-commit safety without adding a client request-token or deduplication mechanism.
- A lightweight final performance sanity check of the working application using representative boards and existing .NET tooling; keep dictionary-based detection and JSON persistence unless a measured bottleneck justifies a separately approved targeted improvement.
- A short list of genuinely unresolved values and response conventions. **Stop after presenting the plan and wait for developer approval.**

**Guardrail:** No extra external services, no large generic frameworks, no background worker implementation, no speculative in-memory caching, no per-generation/snapshot storage, no caller-defined final-state budget, and no writing code before the plan is approved.
