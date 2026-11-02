# Conway's Game of Life API engineering plan and reference

## 1. Baseline and architecture

`README.md` is the primary reference for running and using the application. This document groups architecture, behavioral slices, acceptance criteria, and technical verification considerations. Requirements and rationale are in `GAME_OF_LIFE_DESIGN_UPDATED.md`; measurements and their limitations are in `PERFORMANCE.md`. Development workflow rules live in `AGENTS.md`.

Use exactly three minimal `net8.0` projects. No dedicated benchmarking project or framework is required:

| Project | Responsibility |
|---|---|
| `GameOfLife.Api` | All production code, organized into Endpoints, Application, Domain, and Infrastructure folders |
| `GameOfLife.UnitTests` | Isolated rules, calculations, validation, JSON serialization, and cycle-detection tests |
| `GameOfLife.IntegrationTests` | HTTP contracts and real file-backed SQLite persistence, concurrency, and recovery |

Use ASP.NET Core Minimal APIs, System.Text.Json, Microsoft.Data.Sqlite, xUnit, and `WebApplicationFactory`. Keep production dependencies minimal. A small real-Kestrel group within the existing integration-test project provides transport/body-limit verification; TestServer does not establish Kestrel enforcement. Reuse production composition with an ephemeral loopback port, normal `HttpClient`, and temporary file-backed SQLite; no new test framework/project or external infrastructure.

Key classes:

- **Endpoints:** `BoardEndpoints`, request/response DTOs, and upload validation.
- **Application:** `BoardService` coordinates upload, advancement, preview, and final-state discovery.
- **Domain:** `BoardState`, `GenerationEngine`, and cycle detection. These contain no HTTP or database dependencies.
- **Infrastructure:** `SqliteBoardStore`, `BoardStateCodec`, and schema initialization.

Constructor injection and the small generation-engine interface support measured call counts and controlled concurrency tests; avoid generic repositories and additional architectural frameworks.

**Scope:** Ship `GameOfLife.Api`, `GameOfLife.UnitTests`, and `GameOfLife.IntegrationTests`; keep all production layers within the API project. Use dictionary-based cycle detection and System.Text.Json coordinate persistence. Do not implement competing algorithms or codecs solely to evaluate performance. Use simple measurements and existing .NET tooling for the final performance review; propose targeted improvements only if an actual bottleneck is found. The 4-5-hour estimate guides scope while preserving correctness.

## 2. Endpoint contracts and persistence

| Endpoint | Application/domain behavior | Persistence | Principal acceptance tests |
|---|---|---|---|
| `POST /api/boards` | Validate and map rectangular boolean cells to `BoardState` | Identical initial/current states at generation 0 in one upload insert | `201`, unique ID, asymmetric orientation/dimensions, malformed/null/empty/jagged inputs, configured safeguards, real SQLite reopen, no success on failed write; live-current Location |
| `POST /api/boards/{id}/next` | Calculate one generation from committed current state | Atomically update current state and generation | Sequential advancement, restart durability, rollback, concurrent requests without lost updates, authoritative reload/recalculation, `409` on exhausted optimistic retries; sanitized `500` on SQLite busy/locked exceptions |
| `GET /api/boards/{id}/states-ahead?stepsAhead=N` | Calculate from one committed current snapshot | No writes | Zero calls for zero; accurate absolute label; unchanged stored values; snapshot consistency; invalid offsets |
| `GET /api/boards/{id}/generations/{generationNumber}` | Reconstruct from immutable initial state | No writes or derived-result storage | Original cells at zero after progress/reopen; zero calls for zero; correct positive targets regardless of current progress; invalid targets |
| `GET /api/boards/{id}/final-state` | Look up saved conclusion first; calculate only on miss | Insert one successful conclusion per board | Zero engine calls on saved-result lookup, exact work cap, `422` without a result on exhaustion, concurrent insertion and restart reuse |

Absolute retrieval reconstructs from immutable initial state rather than storing historical generations. Relative `stepsAhead` is an offset; absolute `generationNumber` is an index from upload. Common response `generation` always labels the absolute generation of the returned cells. With current generation 5, preview offsets 0 and 3 return 5 and 8; absolute targets 0, 3, and 8 return those generations from upload. Both GETs are read-only and return 404 for unknown boards. Each Next request is a separate advancement request, including a client retransmission; there is no request-token, deduplication, or original-response replay contract.

Response conventions:

- Common state fields: `boardId`, `generation`, `width`, `height`, `cells`.
- Final-state responses add `startingGeneration`, `conclusionKind` (`Stable` or `Cycle`), `cycleLength`, and `iterationsUsed`.
- Final-state `generation` retains the original discovery’s absolute result generation, including when it predates current progress.
- Upload returns `201 Created` with the generated `boardId`, dimensions, generation 0, cells using the common state response, and `Location: /api/boards/{id}/states-ahead?stepsAhead=0`. This URI represents the live current board, not immutable generation-0 history.
- Reject a supplied final-state `maxIterations` parameter with `400`; provide no public budget override.
- Use ProblemDetails for the documented error statuses. Disconnection/cancellation need not produce an HTTP response.
- Verify `413` and its ProblemDetails envelope on real Kestrel in Production configuration. Use existing request-size features before body binding and existing ProblemDetails/status-code/exception middleware; preserve body-read `413` rather than remapping it to `400`/`500`. A ProblemDetails body is not automatic for every server rejection, unsupported `Accept`, or disconnected/started response. Supported JSON paths return ProblemDetails; unsupported Accept and rejections outside middleware do not have a universal envelope guarantee.

**Approved final-state representative selection:** The primary outcome is stabilization or a repeating cycle. Return the verified stable configuration or any verified configuration belonging to the detected cycle. Do not require a canonical/first-repeated state or reconstruction merely to select one. Retain the existing response fields, including `generation`, `startingGeneration`, `conclusionKind`, `cycleLength`, and `iterationsUsed`: the generation must match the returned configuration relative to the starting snapshot, the period must be verified, and all actual generation-engine calls (including additional cycle-detection calculations) must be counted. The dictionary-based implementation may return any verified cycle member with its correct generation label. This removes representative-selection complexity while preserving the meaningful outcome and accurate supporting metadata.

SQLite design:

- **Boards:** `Id`, `Width`, `Height`, immutable `InitialState`, `CurrentState`, and `CurrentGeneration`, using the existing JSON coordinate codec. Upload writes identical initial/current states at generation 0 in one insert. Next changes only current state/generation. Relative preview reads current state/generation together in one query; absolute retrieval always computes from InitialState even when current progress is ahead of the target. No intermediate generations, snapshots, cache, or separate initial-state table.

- **FinalStateResults (Slice 8):** Unique board FK, starting/result generations, representative state, period, and actual discovery engine-call count.

Assume disposable development databases. CREATE TABLE IF NOT EXISTS does not upgrade an incompatible older table; use a fresh file for disposable data. Retaining non-disposable data requires an explicit migration policy. Never infer original state from an advanced current state. No migration framework is used. InitialState serves absolute reconstruction; current state/generation support restart recovery.

Use Microsoft.Data.Sqlite directly with parameterized SQL, simple reproducible schema initialization from checked-in code, separate connections per operation, WAL, foreign keys, and `synchronous=FULL`. Do not introduce Entity Framework Core, Dapper, other ORMs, or database migration frameworks without explicit approval. SQLite supports atomic multi-statement transactions; FULL synchronization supplies the intended WAL durability setting. [Microsoft transaction guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions), [SQLite synchronization guidance](https://www.sqlite.org/pragma.html#pragma_synchronous).

**Approved requirements:** Advancement calculates outside the write transaction. Current state and generation increment commit atomically using an expected-generation check. Roll back the whole attempt on pre-commit failure; dispose of failed resources before retrying. A generation conflict requires reloading and recalculating from authoritative state within a configured bound. Preserve first-final-result reuse and the existing HTTP conventions. An uncertain Next commit must not trigger automatic re-advancement; a later generation alone cannot identify which request committed.

Final-state insertion is separate from advancement. A competing successful insert returns the winner’s stored configuration and all metadata unchanged, even if the losing calculation found a different valid cycle representative. Lookup-first reuse performs zero new engine calls but retains the original `iterationsUsed`; Next and restart never invalidate or relabel that result.

**Failure categories:**

| Condition | Handling and outcome |
|---|---|
| Conditional generation update loses | End the attempt, reload authoritative state and recalculate. Bounded optimistic retry exhaustion uses approved `409`; no stale snapshot reuse. |
| SQLite busy/locked | Classify primary/extended error codes and operation context, independently of generation conflicts. Account for provider busy waits. Do not label resource-lifetime defects or every locking error as transient. |
| Constraint failure | Treat unexpected unique, FK, check, and not-null failures as database errors. Only the documented final-result uniqueness conflict reads the saved winner. |
| Failure before commit | Roll back the atomic state/generation unit; discard failed transactions and connections. No partial success response. |
| Unexpected database failure | No blind retries; sanitized `500` ProblemDetails when possible. Never expose SQL, database paths, provider messages, or stack traces. Preserve diagnostic details in server logs with a correlation identifier. |
| Cancellation or response loss | Stop new work/retries and release resources. A confirmed commit remains committed. A lost connection is not evidence of commit success or failure. |

**Storage mechanics:** Decision 14 in `GAME_OF_LIFE_DESIGN_UPDATED.md` records the failure policy; the SQLite waiting section below gives concrete timeout and command-wait accounting.

- `BoardService` owns operation-level retry decisions; `SqliteBoardStore` owns SQL, short non-deferred write transactions after calculation, classification, and cleanup. Use separate private-cache connections per attempt. Before retry/recalculation, dispose failed resources and reload authoritative state. Never reuse a failed snapshot.
- Use one bounded optimistic-conflict loop and finite provider `DefaultTimeout`/`CommandTimeout`, including implicit transaction commands. Let the provider handle ordinary busy waiting; do not restart full wait windows in a second loop. Distinguish restart-required `SQLITE_BUSY_SNAPSHOT` from ordinary busy and same-connection `SQLITE_LOCKED` resource defects using primary/extended codes and operation context. Provider exceptions do not enter the optimistic loop; no transient-error allowlist or second retry framework is used.
- For a commit exception, end any still-active transaction and discard the connection. Never automatically repeat an ambiguously committed Next; return sanitized `500` when its outcome cannot be established. A response failure after a confirmed commit does not permit rerunning database work. Final-result uniqueness conflicts read the saved winner; unrelated constraints remain errors.
- Observe cancellation before/after operations and before commit/retry. Microsoft.Data.Sqlite operations execute synchronously; passing a token alone does not prove interruption. Keep waits finite, clean up independently of a canceled token, stop new work when cancellation is observed, and preserve committed state. No background verification/retry worker.

**HTTP failure mapping:** Optimistic retry exhaustion returns `409`; SQLite busy/locked exceptions and unexpected or unverifiable database outcomes return sanitized `500`. Extending contention failures to `409` was considered but is outside the contract. Database failures do not use final-search `422`; cancellation introduces no new status.

## 3. Slice organization and acceptance evidence

The numbered slices group related behavior and testing concerns. Small RED -> GREEN cycles and behavior-preserving refactoring use independent expected results and relevant regressions.

| Slice | Behavior and implementation boundary | Acceptance evidence |
|---|---|---|
| 1 | Pure finite-board generation engine | Independent rule/boundary fixtures, orientation, unchanged dimensions, immutable input, and cancellation checks; no HTTP or persistence dependency |
| 2 | Minimal JSON serialization for durable storage | Use System.Text.Json for living-cell coordinates; retain width/height separately and reconstruct the HashSet. Test empty/populated round trips and clear failure for invalid stored JSON. No custom converters or binary formats |
| 3 | Minimal Durable Board Upload | Valid asymmetric upload returns `201`, unique ID, correct dimensions/cells and generation 0; real SQLite reconstruction after close/reopen; invalid input and failed-write handling. Upload writes identical initial/current states; live-current Location |
| 4 | Relative preview and absolute-generation retrieval (4a-4f) | Durable initial state; zero/positive relative and absolute reads; accurate labels; zero engine calls for zero; unchanged persistent values; live-current Location; validation, overflow, cancellation |
| 5 | HTTP Next with atomic state/generation commit | Unchanged InitialState, correct one-step advance, durable restart progress, conditional generation update, rollback, sanitized errors, overflow prevention, and no success before commit. Conditional update detects conflicts; bounded reload-and-recalculate handling is detailed in Slice 6 |
| 6 | Concurrent Next and SQLite contention handling | Concurrent requests produce consecutive committed generations without lost updates; prove authoritative reload/recalculation, bounded conflict/contention handling, no automatic re-advancement after an uncertain commit, and failed-transaction cleanup |
| 7 | Bounded dictionary-based final search | Use a straightforward dictionary-based algorithm; verify actual board-state equality before identifying a cycle. Verify stable/cycle outcomes, accurate cycle lengths and generation labels, pre-call budget checks, all actual engine calls counted, and cancellation in the domain component. No competing algorithm is required |
| 8 | Final-state HTTP persistence | Use `FinalStateResults` for successful conclusions. Reuse saved response before engine work; compute/persist only on miss; correct `422` on required over-budget work, no pre-commit failure result, unchanged current state and saved-result reuse after Next/restart |
| 9 | Concurrent final-state discovery | One durable winner; both requests return its unchanged configuration/metadata despite valid different representatives or a concurrent Next |
| 10 | HTTP/resource coverage | Real-Kestrel coverage includes wide/large boards, payload/ProblemDetails boundaries, overflow and server-observed cancellation. Reuse earlier tests; do not duplicate the whole HTTP suite across hosts |
| 11 | Performance verification and final test execution | Measure representative generation/discovery, memory, JSON, SQLite, and selected HTTP workloads in Release; execute complete Debug/Release suites and builds; document observations and operational limitations; remove temporary measurement artifacts and document run/use instructions in README. Independent code review assesses code and test quality separately from measurements |

**Scope boundaries:** Establish durable upload/reload before complex concurrency work. Preserve Kestrel request-size enforcement. No extra MaxCells or arbitrary dimension ceiling is configured; the earlier 4,194,304-cell suggestion had no measured basis. Small test overrides are not production defaults. The provisional 10,000-call budget applies only to final-state discovery, not retrieval. Dictionary detection and coordinate JSON remain selected; alternative algorithms/codecs require measured performance or memory need. Development databases are disposable and are not shipped.

### Slice 3: Minimal Durable Board Upload

Accept a valid rectangular boolean grid through `POST /api/boards`, validate basic structure and configured resource safeguards, convert it into the existing `BoardState`, persist it in SQLite at generation 0, and return `201 Created` with the generated board ID and common state response. Reopen the database and reconstruct the stored board correctly. A persistence failure returns an appropriate error, never a successful upload response.

Upload writes only Boards, including immutable InitialState, current state, dimensions, and generation zero, using the existing coordinate JSON codec. Keep schema initialization simple and reproducible; no migration framework, cache, or automatic final-state discovery is needed. Stateful Next and final-result insertion have separate acceptance boundaries.

**Focused Slice 3 acceptance tests:**

1. A valid asymmetric upload returns `201`, a unique board ID, correct dimensions/cells, generation 0, and the live-current Location.
2. Reject invalid requests, including null, empty, jagged, and malformed grids; cover configured basic safeguards.
3. Close and reopen real file-backed SQLite, then retrieve through the storage layer and reconstruct identical dimensions, living cells, and generation.
4. A failed database write returns an appropriate error and no successful upload response.

The upload boundary does not require process-kill simulations or a general fault-injection framework. Concurrency and transport concerns have separate groups below.

### Slice 4: Relative and absolute generation retrieval

Milestones 4a-4f group persistence, relative retrieval, absolute retrieval, and live-current Location evidence:

| Milestone | Behavior | Evidence |
|---|---|---|
| 4a | InitialState added to Boards; one upload insert writes identical initial/current coordinate JSON at generation 0 | Upload coverage reads and compares both stored fields after app shutdown and SQLite reopen |
| 4b | Relative zero returns one committed current snapshot | Exact nonzero generation/cells after reopen, zero engine calls, unchanged persistent values |
| 4c | Relative positive computes from current state and labels current generation + offset | Independent changing glider phases, exact call counts, snapshot retained across an update during calculation, no writes |
| 4d | Absolute zero returns original upload | Original cells despite changed current state after reopen; zero calls and unchanged persistent values |
| 4e | Absolute positive always reconstructs from InitialState | Current progress below/equal/above target; exact glider phases, labels, calls, and unchanged persistence |
| 4f | Upload Location points to live current preview | URI resolves at upload and after seeded progress/app restart; generations/0 still returns original cells |

Validation uses standard Minimal API Guid/Int64 binding. Missing/empty preview offsets, malformed/fractional/unrepresentable counts, negative counts, and invalid IDs return 400 ProblemDetails. Unknown boards return 404. Addition overflow is rejected before engine work; zero at Int64.MaxValue succeeds. Duplicate-query semantics are unspecified.

The earlier retrieval computation-cap proposal was discarded: both routes are tested beyond 10,000 calls without a numeric cap. Cancellation is checked around reads, between calculations, and during response mapping; the existing engine checks inside calculations. Focused service tests with real SQLite verify cancellation stops after one engine call, accepts the Int64 maximum target before cancellation, and leaves storage unchanged. Transport/disconnect cancellation is covered separately in Slice 10.

Operational limitation: large valid generation counts can consume CPU and occupy a request thread while the caller keeps the connection open. A server-controlled computation timeout is an optional production safeguard, not a configured limit. It could reject legitimate long calculations and cannot immediately interrupt synchronous SQLite waiting. The final-state call budget is separate.

InitialState uses the existing JSON codec without a separate snapshot table, cache, triggers, migration framework, or retrieval-specific advancement infrastructure. Initialize creates the new schema for a fresh database; it does not upgrade an older Boards table. Use a fresh disposable database path; non-disposable data requires an explicit migration decision.

### Slice 6: Concurrent Next and bounded contention handling

- One operation-specific loop reloads and recalculates after a verified zero-row update. Every failed attempt disposes its command, transaction, and connection before the next read. `Next:MaxAttempts` defaults provisionally to three total attempts and accepts positive overrides. Exhaustion returns 409; disappearance returns 404. Each success returns its own committed cells/generation.
- Keep provider finite waiting instead of a second busy retry/backoff loop. Use private-cache connections and positive `Default Timeout` from the connection string (provider default 30 seconds); reject zero/unlimited waiting. Provider exceptions never enter the optimistic loop. Busy/locked failures retain sanitized 500; they are not mapped to 409. No transient error-code allowlist or automatic uncertain-commit replay is introduced.
- Wait accounting: each attempt has a finite sequence of commands (connection configuration, snapshot SELECT, second connection configuration, BEGIN IMMEDIATE, conditional UPDATE, and COMMIT or rollback). A final existence read distinguishes disappearance after exhausted conflicts. All use the configured finite provider timeout, including transaction commands. Thus application-issued command wait windows are bounded by `(6 * MaxAttempts + 2) * DefaultTimeoutSeconds`, conservatively including the final existence read; provider polling granularity can overshoot each timeout. This accounts for command waiting, not a hard wall-clock deadline for connection opening, calculation, scheduling, I/O, or cleanup. No full provider wait window is restarted after a busy timeout. Numeric tuning remains provisional; no separate deadline/backoff framework is added.
- Cancellation is checked before each attempt/calculation, around synchronous database work, and before commit. Cancellation during provider waiting is observed when control returns; cleanup does not depend on a live request token. Existing rollback, immutable InitialState, and restart-durability tests remain in place.

Essential real file-backed SQLite tests: barrier-synchronized Next from generation 5 returns correct distinct generations 6 and 7, with three engine calls and durable generation 7; forced conflicts exhaust exactly three attempts without replacing the winner; a one-attempt override; rejection of unlimited provider waiting; held writer lock released after observing provider retries succeeds without recalculation; held lock through one-second timeout yields primary/extended SQLITE_BUSY (5/5), sanitized 500, unchanged state, closed connections, and later recovery; cancellation during that wait rolls back; real commit followed by simulated acknowledgement loss returns 500 without a second advancement. The acknowledgement-loss simulation is at the store/service boundary during connection disposal, after a genuine commit, and does not simulate every native COMMIT or storage-device failure. Retain the existing deferred-constraint COMMIT-failure and trigger-induced pre-commit rollback cases.

**Exploratory coverage limits:** Stale read-transaction BUSY_SNAPSHOT reproduction, same-connection/shared-cache LOCKED matrices, exhaustive cleanup faults, and process-kill/disk-failure simulations are not established by the focused suite. No read transaction enters calculation; writes start non-deferred on private independent connections. These scenarios do not justify speculative retry machinery.

### Slice 7: Bounded final-state discovery

- `FinalStateDiscovery` reuses `IGenerationEngine`. It records the initial state at iteration zero, looks up an order-independent fingerprint after each call, verifies dimensions and living-cell set equality, and retains all unequal observations in the same bucket. A matching fingerprint alone cannot establish a repeat or discard prior observations.
- `FinalStateResult` reports Stable (period 1), Cycle (period greater than 1), or Inconclusive; returned state, starting/result absolute generations, nullable cycle length/start, and actual engine calls. Successful searches return the newly calculated repeating state; inconclusive searches return the latest calculated state (the input at zero budget). Cycle start is the first observed absolute generation in the verified cycle. No input mutation occurs.
- The provisional default is 10,000 actual engine calls, with an internal-only budget overload for focused tests. Check before every call; exact-cap detection succeeds and zero budget performs no calls. Preview and absolute-generation retrieval remain uncapped. Cancellation is checked at entry, before/after engine calls, during fingerprint traversal and bucket scanning, and before returning; the same token reaches the existing engine. Negative inputs and unrepresentable next-generation labels throw before the affected engine call, following existing nonnegative Int64 conventions.
- Unit tests cover independent empty/block, blinker, period-3 pulsar, transient single-cell and transient-to-blinker fixtures, budgets, absolute metadata, immutability, cancellation/token forwarding, and overflow. Two unequal clock phases naturally share the actual production fingerprint: one-call tests reject a false conclusion, while two-call tests prove both colliding observations survive, in either starting phase. No fingerprint injection framework or custom simulator is needed.
- Temporary mutations confirmed the assertions detect fingerprint-only matching (4 failing cases), a hard-coded oscillator period of 2 (pulsar fails), an extra permitted engine call (2 failing cases), and cycle-start labeling at the initial generation (transient-to-cycle fails).

### Slice 8: Final-state HTTP endpoint and durable persistence

`GET /api/boards/{id}/final-state` checks SQLite first, otherwise discovers from one committed snapshot with the existing 10,000-call default, commits only success, and returns the original saved metadata. It never advances current progress. Missing boards return 404, supplied `maxIterations` returns 400, exhaustion returns 422, and unexpected persistence errors retain sanitized 500 handling.

Schema initialization adds `FinalStateResults` with the approved six fields and unique board FK. Parameterized SQL uses the existing codec and a short transaction; `ON CONFLICT(BoardId) DO NOTHING` handles only the documented same-board conflict, followed by a winner lookup. Next never alters this table. The endpoint reuses the codec and store without another serializer, persistence hierarchy, memory cache, background job, external lock, or retry framework.

Integration coverage includes exact stable/cycle HTTP and persisted metadata, unchanged current progress, byte-for-byte reuse and measured zero additional engine calls after second request/restart/three Next calls, real-glider exhaustion at exactly 10,000 calls, missing boards, unsupported query values, no automatic results from other routes, failed insert/commit rollback, unrelated uniqueness failures, cancellation at entry/after calculation/before commit, and sequential same-board winner reuse. Cancellation cases use real SQLite at the application/store boundary; transport disconnects and simultaneous discovery/Next interleavings have separate coverage.

### Slice 9: Concurrent final-state discovery

Concurrency cases in `BoardFinalStateTests.cs` use the existing file-backed SQLite fixture and counting-engine callback with controlled gates, finite waits, and gate release/request disposal in cleanup:

- Two first-time discoveries are held after their first engine call, proving both passed lookup and established snapshots before either can save. Releasing them verifies two HTTP 200 responses with byte-identical JSON and exactly one saved row matching the cells, generations, conclusion kind, period, and iterations. Both perform discovery (four total engine calls); host/database reopen preserves the winner with zero additional calls on reuse.
- The same test interleaves a committed Next between the two snapshots. Discoveries start at generations 5 and 6 on opposite blinker phases, so their independent valid candidates differ in cells and generation metadata. Either may win; both must return the stored winner unchanged. Five total calls account for both two-step discoveries and Next, whose committed progress remains unchanged.
- A separate overlap test pauses discovery from generation 5 while Next commits generation 6 and its expected cells. Discovery then saves its original generation-7 blinker conclusion without overwriting current progress. The separate saved-result replay theory verifies reuse after subsequent Next calls and restart with unchanged metadata and zero additional calculations.

**Confirmed-commit cancellation boundary:** Final State and successful Next map committed results with `CancellationToken.None`. A post-commit cancellation check previously allowed response mapping to throw after a durable write. Mapping preserves the confirmed result while cancellation remains effective before calculation and commit; no connection-disposal fault machinery is required. Controlled request interleavings do not establish exhaustive scheduler, stress, process-kill, or disk-failure coverage.

## 4. Performance and SQLite verification methodology

**Guiding principle:** Implement the simplest correct solution. Measure actual performance before introducing optimization complexity; do not optimize hypothetical bottlenecks. No mandatory comparative benchmarking, algorithm-selection gate, dedicated benchmark project, or benchmarking framework is required.

**Slice 11: Performance verification and final test execution**

1. Run the complete unit and integration test suites.
2. Verify all five endpoints under the documented contract and persistence behavior, retaining the approved atomicity, concurrency, recovery, and computation-limit guarantees.
3. Test representative empty, sparse, populated, stable, and periodic boards, including reasonably large boards. Retain the 1,024 x 1,024 support/test baseline; it is not a production ceiling or a requirement for exhaustive workload sweeps.
4. Perform a lightweight performance sanity check of the working application using simple measurements and existing .NET tooling where sufficient.
5. Evaluate generation speed, dictionary-based cycle detection, memory consumption, JSON serialization, and SQLite persistence only to determine whether a meaningful performance problem exists. Record enough workload, runtime/machine, and measurement context to explain the observations and their limitations. No competing implementation is needed.
6. If performance is unacceptable, identify the actual bottleneck and propose targeted improvements for developer review. Do not automatically implement optimizations or add alternative algorithms/codecs.
7. Remove temporary measurement artifacts while retaining required correctness coverage and the three-project solution. Production cleanup or optimization requires separately approved scope.
8. Finalize README with setup/run/test instructions, approved behavior and limits, and a concise account of the sanity-check observations and limitations. Assess code and test quality independently of measurements.

**SQLite correctness considerations:** Check provider busy waits, implicit transaction commands, finite timeout granularity, effective command-wait accounting, and cancellation with real file-backed SQLite. Retry count and finite provider waiting do not establish a hard end-to-end deadline. Busy/locked exceptions use sanitized `500`, not `409`.

Timing and memory observations belong in the lightweight performance review rather than arbitrary thresholds in correctness tests. Further profiling, optimization, or alternatives require concrete measured evidence and developer-approved scope. Deferred caching, background work, per-generation/snapshot storage, and external services remain out of scope; the absolute-generation route reconstructs from InitialState.

## 5. Consolidated acceptance criteria and test boundaries

The groups below consolidate earlier overlapping test lists; they are requirements-to-tests coverage, not a demand for a new test for every sentence. Use parameterized cases and shared small fixtures where useful. Keep the strongest observation at the appropriate layer: pure rules in unit tests, database guarantees in real SQLite tests, and transport enforcement in the small Kestrel group. A broader scenario may cover several requirements when all relevant facts are asserted explicitly; never replace state/call-count assertions with success-status checks.

| Mandatory test group | Essential assertions |
|---|---|
| Engine and JSON serialization | Independently established life-rule, empty/block/blinker/longer-oscillator/transient expectations; finite nonwrapping corners/edges, 1×1/thin/asymmetric shapes, orientation, immutable input, and unchanged dimensions. JSON coordinate serialization round-trips empty/populated boards and clearly rejects invalid stored JSON; dimensions remain separate. Add cases only for concrete requirements or failure modes |
| Upload and durable state | Real JSON validation for null/empty/jagged/malformed boards and invalid IDs; approved statuses/DTO behavior, including live-current Location; correct current state and generation after reload/restart (including storage-layer reconstruction); no successful write response before commit; no implicit final-state calculation |
| Preview validation and isolation | One consistent committed snapshot during concurrent Next; stepsAhead=0/positive absolute labels, zero engine calls for zero, no writes; missing/empty/malformed/negative/out-of-range values, numeric parse overflow, and generation-addition overflow before engine work. Use standard ASP.NET Core binding; duplicate-query semantics are unspecified; overflow returns `400` and stepsAhead=0 at the maximum generation succeeds |
| Absolute-generation retrieval | Reconstruct generationNumber from InitialState with current progress greater/equal/less than target; original cells at zero after current cells change and SQLite reopens; zero calls for zero; unchanged persisted values; unknown IDs and invalid or unrepresentable targets without a numeric computation cap |
| Atomic advancement and durability | Current state and incremented generation commit together, leaving InitialState unchanged. Inject a failure after the conditional update but before commit; assert both values roll back. Verify committed progress after restart and that confirmed commit followed by response loss does not cause automatic re-advancement. Uncertain commit outcomes never trigger blind retries. Unexpected real database errors yield sanitized `500`, not disguised conflicts |
| Concurrent Next and bounded retries | Concurrent requests reading one generation produce correct consecutive committed generations; prove the loser reloads authoritative state and recalculates. Exhausted optimistic retries use `409`; stale snapshots cannot overwrite committed progress. There is no client retransmission deduplication guarantee |
| SQLite locking, cleanup and cancellation | Deterministically held/released writer locks exercise success and bounded exhaustion; distinguish ordinary busy, stale-snapshot busy and same-connection locked cases when investigating provider failures. Unexpected constraints remain errors. Dispose failed attempts before retry; active-transaction/cleanup failures must not obscure the original error or lead to damaged-resource reuse; exhaustive fault combinations are exploratory. Cancel during database work, prove eventual lock release/no further retry and rollback before commit; confirmed commits remain durable after later cancellation |
| Final search, budget and reuse | Verify stable state or any verified cycle member against independent fixtures (including empty/block stability in one engine call), with exact period and configuration at its claimed generation. Check the budget **before every engine call**, count all actual engine calls, prove exact-cap success, and return `422` without the forbidden extra call or saved result when more work is required. When fingerprints are used as dictionary keys, forced collisions of unequal states cannot establish a false cycle; verify actual state equality. No canonical representative or competing algorithm is required. First saved conclusion and all metadata (including original `iterationsUsed`) replay unchanged with zero new calls after Next/restart; concurrent first discoveries return the unique winner even across different valid phases |
| Real Kestrel and resource protection | In Production configuration, test valid UTF-8 bodies at `L-1`, `L`, `L+1` bytes, with known length; chunked/unknown-length boundaries use transmitted bytes including framing; use padding to isolate byte limits. Test early application rejection and native Kestrel enforcement independently, with limits set before JSON binding. Check `413`, supported JSON ProblemDetails/content type/body status, and absent/supported/unsupported `Accept`; record any server-level envelope/connection limitation for review. Ensure known-length rejection precedes JSON parsing, streamed rejection permits only bounded prefix processing, no full oversized-grid materialization/domain mapping/persistence, and no size-validation arithmetic overflow. Include very wide single rows and 1,024×1,024 sparse/dense integration boards; keep routine byte-boundary fixtures small. Dimension/cell-count boundary tests apply only when such limits exist; isolate their `400` from body-size `413` |
| Expensive-work cancellation and route contract | Cancel actual preview/uncached-final generation work after server entry; verify token observation, stopping within costly calls and between calls, unchanged committed board and no pre-commit final-result write. Client-await cancellation alone is insufficient; disconnect promises no status. Verify all five application routes, retained final fields, relative/absolute retrieval semantics, no public final budget override, and reviewed errors; include absolute-retrieval cancellation with unchanged persistent values |

Use separate file-backed SQLite connections and deterministic barriers/events. Reuse narrowly scoped SQLite-trigger/test-harness fault injection for rollback, contention, and commit-outcome tests; do not build a general fault framework. Real SQLite rollback tests, restart/reopen verification, and no automatic re-advancement after response loss or commit ambiguity remain mandatory. A simulated error after a real commit does not model every possible storage-device failure. Watchdogs may detect hangs; performance latency/memory measurements belong in the lightweight performance review.

**Test-scope limits:** Elaborate process-termination/process-kill scenarios around commit are optional follow-up work rather than submission requirements. Add them only if evidence warrants the effort and the developer approves the scope. Atomic transactions, protection against lost updates, durable progress, rollback, bounded contention handling, and cancellation/resource cleanup remain required.

**Submission cleanup acceptance:** Remove unnecessary code, dependencies, and artifacts without removing required correctness coverage, including collision/call-count tests where applicable. The final solution has three projects; retain production code, relevant tests, reproducible SQLite setup, design/plan documentation, README, and normal run/test instructions. Ship no generated user database. No benchmark project, competing implementation, or benchmark-specific cleanup is required.

**Verification scope:** Builds, full Debug/Release suites, all five routes, persistence behavior, free/self-contained clone/build/run/test instructions, whitespace validation, and documented measurements/limitations. Independent code review assesses the design, retained tests, and actual results separately from performance observations. Distinguish executed checks from proposed or exploratory coverage.

## 6. Real Kestrel: transport, resources, and cancellation

### HTTP observations

The test-only Kestrel fixture replays production composition in Production on an ephemeral loopback listener with normal HttpClient and temporary file-backed SQLite, pooling disabled. The fixture owns Kestrel; WebApplicationFactory owns the returned bootstrap TestServer host. Temporary-file cleanup follows shutdown; a bounded cleanup-only retry handles transient Windows handles. A narrow stream observer measures bytes delivered to binding without enforcing limits or changing the production pipeline.

Transport and cancellation cases cover:

- **KnownLengthLimitUsesBytesBeforeBindingOrPersistence (3):** Valid UTF-8 asymmetric JSON with a multibyte property and whitespace at 255/256/257 bytes under L=256. Results are 201/201/413. Accepted uploads preserve dimensions/orientation and reach SQLite. Oversized known-length rejection delivers zero body bytes to binding and saves no board.
- **ChunkedLimitIncludesTheFramesActuallyWritten (6):** Unknown-length HTTP/1.1 requests use controlled writes of either 64 or 256 bytes. The test calculates framing from each actual chunk length (hex prefix, delimiters, terminal chunk), then places the native limit one byte below, equal to, or one byte above that transmitted size. Below rejects with 413/no board; equal/above accept and persist the expected cells. No universal overhead constant is assumed.
- **StreamedOversizedGridStopsBeforeCompleteBodyReachesBinding (1):** A valid padded 1,024-byte JSON body whose grid alone exceeds L=256 is streamed in 64-byte writes. Kestrel returns 413, the binder receives at most 256 bytes (less than the grid), and no board persists. This proves bounded delivery rather than claiming zero prefix allocation or measuring memory.
- **ErrorNegotiationPreservesStatusesAndSupportedJsonProblems (5):** Absent Accept, application/json, application/problem+json, */*, and text/html. Supported JSON requests return application/problem+json with matching status for binding 400, missing-board 404, and native 413. Unsupported text/html preserves 400/413 but uses the middleware fallback instead of ProblemDetails. Explicit endpoint 404 problems remain JSON, and valid uploads return application/json/201 even with text/html; no new 406 contract was introduced. Invalid/oversized requests save nothing.
- **LargeBoardsRoundTripEveryCellThroughHttpAndSqlite (3):** A 65,537-by-1 row and 1,024-by-1,024 sparse/dense boards, with asymmetric markers, under a test-only 16 MiB body limit. Verify dimensions, every returned cell in upload/current-zero/absolute-zero responses, and every stored coordinate through a new SQLite store/connection. The dense fixture is fully alive except three orientation markers; the sparse fixture has three living markers. These cases verify round trips rather than expensive generation work and impose no production dimension cap. The support baseline remains a baseline, not a maximum.
- **DisconnectReachesRunningCalculationAndLeavesPersistenceUnchanged (3):** Relative preview, absolute retrieval, and uncached Final State. At the existing engine seam, execute one real 128-by-128 dense calculation, signal completion, and keep that engine call in flight until request cancellation. Cancel HttpClient only after that signal, observe RequestAborted in the server engine, and wait for server-pipeline completion. Exactly one call occurs; current/initial cells and generation remain unchanged; no final result is saved. This proves server observation, not only canceled client awaiting. Watchdogs detect hangs without performance assertions. Existing unit tests deterministically prove cancellation within real candidate enumeration/evaluation; existing service tests prove stopping between calls and pre-commit persistence cancellation. No disconnected-client status is promised. Committed-result mapping follows the cancellation boundary described in Slice 9.

### Native chunked framing and error envelopes

The original payload-only chunked 255/256 assumptions were rejected by real Kestrel with 413. Controls showed that native accounting includes chunk framing, consistent with [the .NET 8 Kestrel implementation](https://github.com/dotnet/aspnetcore/blob/v8.0.22/src/Servers/Kestrel/Core/src/Internal/Http/Http1ChunkedEncodingMessageBody.cs). Native accounting is the selected contract. Tests calculate the frames for controlled writes and use transmitted-size boundaries; the unchanged known-length cases use payload-byte boundaries. This is an accepted transport distinction, not an application defect. README documents the transport distinction.

Actual 413 observations on installed ASP.NET Core 8.0.33: the supported JSON paths above return application/problem+json with body status 413. Unsupported Accept can produce a fallback body; server-level failures outside middleware and disconnected/started responses are not guaranteed an envelope. No custom envelope workaround is used.

These correctness tests establish no throughput, peak-memory, cancellation-latency, or stress guarantee. Retrieval remains uncapped; a server-controlled computation timeout is an optional production safeguard, not an implemented limit.

## 7. Historical performance findings and operational limitations

Historical Slice 11 measurements at `0bcdb81` used the Release generation engine on small populated, 1,024 × 1,024 sparse/moderate/full, and 2,048 × 2,048 sparse boards. Median one-call times were 1.437 / 11.193 / 757.971 / 1,029.116 / 13.070 ms respectively. Moderate/full calculations cumulatively allocated about 207/259 MiB per call. Discovery covered empty, block, blinker, pulsar, a 1,105-call transient, and an inconclusive glider stopping at exactly 10,000 calls (median 130.305 ms). Also measured production coordinate JSON, real file-backed SQLite with stage separation, and selected production-entry-point Kestrel round trips. These are bounded local serial observations, not stress or capacity guarantees.

Operational values are a provisional internal 10,000-call discovery budget, finite provider waiting, three total Next attempts, native Kestrel body-size enforcement, and uncapped retrieval. Dense JSON reconstruction and populated generation allocation are demonstrated costs; arbitrary-count/concurrent resource capacity is not established. A computation timeout is an optional production safeguard. Detailed original samples and the separate follow-up neighbor-counting comparison are in PERFORMANCE.md; the original timings are not the Before rows of that paired comparison.

### Historical verification limitations

An earlier Release run threw `NullReferenceException` during `FileSystemWatcher.StopRaisingEvents` / `PhysicalFileProvider.Dispose` / host disposal in `ChunkedLimitIncludesTheFramesActuallyWritten(256, 0)`, rather than its HTTP assertions. Focused and full reruns did not reproduce it; the cause remains unconfirmed. Removing redundant bootstrap shutdown leaves that host under factory ownership but is not proof of a fix.

NuGet vulnerability metadata was unreachable during the original temporary-harness restore; cached packages allowed execution, not a successful online audit. Historical test execution also exposed Windows Event Log permission failures and shared-build stalls. README documents the test-process Event Log override and serial/no-shared-compilation commands; production logging is unchanged.
