# Conway’s Game of Life API implementation plan

> Current implementation status (Slice 8 handoff): Slices 1-7 are complete and reviewed under the latest request. Slice 8 final-state HTTP discovery and durable SQLite result reuse are implemented for developer review. Stop before Slice 9.

## 1. Baseline and architecture

The complete `GAME_OF_LIFE_DESIGN_UPDATED.md` has been read, and AGENTS.md applies. Slices 1-3 are complete. Slice 4 was approved for implementation by the latest request and milestones 4a-4f are now implemented, awaiting developer review. Retain 11 numbered slices and the existing architecture. No performance measurements have been performed.

Use exactly three minimal `net8.0` projects. No dedicated benchmarking project or framework is required:

| Project | Responsibility |
|---|---|
| `GameOfLife.Api` | All production code, organized into Endpoints, Application, Domain, and Infrastructure folders |
| `GameOfLife.UnitTests` | Isolated rules, calculations, validation, JSON serialization, and cycle-detection tests |
| `GameOfLife.IntegrationTests` | HTTP contracts and real file-backed SQLite persistence, concurrency, and recovery |


Use ASP.NET Core Minimal APIs, System.Text.Json, Microsoft.Data.Sqlite, xUnit, and `WebApplicationFactory`. Keep production dependencies minimal. Add only a small real-Kestrel group within the existing integration-test project for transport/body-limit verification; TestServer does not establish Kestrel enforcement. Reuse production composition with an ephemeral loopback port, normal `HttpClient`, and temporary file-backed SQLite; no new test framework/project or external infrastructure.

Key classes:

- **Endpoints:** `BoardEndpoints`, request/response DTOs, and upload validation.
- **Application:** `BoardService` coordinates upload, advancement, preview, and final-state discovery.
- **Domain:** `BoardState`, `GenerationEngine`, and cycle detection. These contain no HTTP or database dependencies.
- **Infrastructure:** `SqliteBoardStore`, `BoardStateCodec`, and schema initialization.

Use constructor injection. Introduce a small generation-engine interface for measured call counts and controlled concurrency tests; avoid generic repositories and additional architectural frameworks.

**Approved submission clarification:** Ship `GameOfLife.Api`, `GameOfLife.UnitTests`, and `GameOfLife.IntegrationTests`; keep all production layers within the API project. Use dictionary-based cycle detection and System.Text.Json coordinate persistence. Do not implement competing algorithms or codecs solely to evaluate performance. Use simple measurements and existing .NET tooling for the final performance review; propose targeted improvements only if an actual bottleneck is found. The 4-5-hour estimate guides scope; it is not permission to omit correctness or approval checkpoints.

## 2. Endpoint contracts and persistence

| Endpoint | Application/domain behavior | Persistence | Principal acceptance tests |
|---|---|---|---|
| `POST /api/boards` | Validate and map rectangular boolean cells to `BoardState` | Slice 4a adds identical initial/current states at generation 0 in one upload insert | `201`, unique ID, asymmetric orientation/dimensions, malformed/null/empty/jagged inputs, configured safeguards, real SQLite reopen, no success on failed write; Location deferred to Slice 4 |
| `POST /api/boards/{id}/next` | Calculate one generation from committed current state | Atomically update current state and generation | Sequential advancement, restart durability, rollback, concurrent requests without lost updates, authoritative reload/recalculation, `409` on exhausted optimistic retries; SQLite contention mapping awaits approval |
| `GET /api/boards/{id}/preview?stepsAhead=N` | Calculate from one committed current snapshot | No writes | Zero calls for zero; accurate absolute label; unchanged stored values; snapshot consistency; invalid offsets |
| `GET /api/boards/{id}/generations/{generationNumber}` | Proposed: reconstruct from immutable initial state | No writes or derived-result storage | Original cells at zero after progress/reopen; zero calls for zero; correct positive targets regardless of current progress; invalid targets |
| `GET /api/boards/{id}/final-state` | Look up saved conclusion first; calculate only on miss | Insert one successful conclusion per board | Zero engine calls on saved-result lookup, exact work cap, `422` without a result on exhaustion, concurrent insertion and restart reuse |

The proposed Slice 4 revision replaces historical-retrieval deferral with reconstruction from immutable initial state. Relative `stepsAhead` is an offset; absolute `generationNumber` is an index from upload. Common response `generation` always labels the absolute generation of the returned cells. With current generation 5, preview offsets 0 and 3 return 5 and 8; absolute targets 0, 3, and 8 return those generations from upload. Both GETs are read-only and return 404 for unknown boards. Each Next request is a separate advancement request, including a client retransmission; there is no request-token, deduplication, or original-response replay contract.

Response conventions (Final State fields retained as approved; other HTTP proposals unchanged):

- Common state fields: `boardId`, `generation`, `width`, `height`, `cells`.
- Final-state responses add `startingGeneration`, `conclusionKind` (`Stable` or `Cycle`), `cycleLength`, and `iterationsUsed`.
- Final-state `generation` retains the original discovery’s absolute result generation, including when it predates current progress.
- Slice 3 upload returns `201 Created` with the generated `boardId`, dimensions, generation 0, and cells using the common state response; no `Location` header is required. Add `Location: /api/boards/{id}/preview?stepsAhead=0` in Slice 4 when that endpoint is available. This URI represents the live current board, not immutable generation-0 history.
- Reject a supplied final-state `maxIterations` parameter with `400`; provide no public budget override.
- Use ProblemDetails for the documented error statuses. Disconnection/cancellation need not produce an HTTP response.
- Verify `413` and its ProblemDetails envelope on real Kestrel in Production configuration. Use existing request-size features before body binding and existing ProblemDetails/status-code/exception middleware; preserve body-read `413` rather than remapping it to `400`/`500`. A ProblemDetails body is not automatic for every server rejection, unsupported `Accept`, or disconnected/started response. Record actual results and obtain review of any envelope limitation; no universal guarantee is established by documentation alone.

**Approved final-state representative selection:** The primary outcome is stabilization or a repeating cycle. Return the verified stable configuration or any verified configuration belonging to the detected cycle. Do not require a canonical/first-repeated state or reconstruction merely to select one. Retain the existing response fields, including `generation`, `startingGeneration`, `conclusionKind`, `cycleLength`, and `iterationsUsed`: the generation must match the returned configuration relative to the starting snapshot, the period must be verified, and all actual generation-engine calls (including additional cycle-detection calculations) must be counted. The dictionary-based implementation may return any verified cycle member with its correct generation label. This removes representative-selection complexity while preserving the meaningful outcome and accurate supporting metadata.

SQLite design:

- **Boards:** Slice 3 created `Id`, `Width`, `Height`, `CurrentState`, `CurrentGeneration`. Proposed Slice 4a adds one immutable `InitialState` column using the existing JSON coordinate codec. Upload writes identical initial/current states at generation 0 in one insert. Next changes only current state/generation. Relative preview reads current state/generation together in one query; absolute retrieval initially always computes from InitialState even when current progress is ahead of the target. No intermediate generations, snapshots, cache, or separate initial-state table.

Assume disposable development databases. CREATE TABLE IF NOT EXISTS does not upgrade an existing table; rebuild a disposable database when applying Slice 4a. If non-disposable data must be retained, stop for an explicit migration policy before schema changes. Never infer original state from an advanced current state. No migration framework is proposed. InitialState serves absolute reconstruction; current state/generation already support restart recovery. This documentation step deletes/rebuilds no database.
- **FinalStateResults (Slice 8):** unique board FK, starting/result generations, representative state, period, and actual discovery engine-call count.

Use Microsoft.Data.Sqlite directly with parameterized SQL, simple reproducible schema initialization from checked-in code, separate connections per operation, WAL, foreign keys, and `synchronous=FULL`. Create only `Boards` in Slice 3; add `FinalStateResults` in Slice 8. Do not introduce Entity Framework Core, Dapper, other ORMs, or database migration frameworks without explicit approval. SQLite supports atomic multi-statement transactions; FULL synchronization supplies the intended WAL durability setting. [Microsoft transaction guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions), [SQLite synchronization guidance](https://www.sqlite.org/pragma.html#pragma_synchronous).

**Approved requirements:** Advancement calculates outside the write transaction. Current state and generation increment commit atomically using an expected-generation check. Roll back the whole attempt on pre-commit failure; dispose of failed resources before retrying. A generation conflict requires reloading and recalculating from authoritative state within a configured bound. Preserve first-final-result reuse and the existing HTTP conventions. An uncertain Next commit must not trigger automatic re-advancement; a later generation alone cannot identify which request committed.

Final-state insertion is separate from advancement. A competing successful insert returns the winner’s stored configuration and all metadata unchanged, even if the losing calculation found a different valid cycle representative. Lookup-first reuse performs zero new engine calls but retains the original `iterationsUsed`; Next and restart never invalidate or relabel that result.

**Approved failure categories; proposed implementation mechanics below:**

| Condition | Handling and outcome |
|---|---|
| Conditional generation update loses | End the attempt, reload authoritative state and recalculate. Bounded optimistic retry exhaustion uses approved `409`; no stale snapshot reuse. |
| SQLite busy/locked | Classify primary/extended error codes and operation context, independently of generation conflicts. Account for provider busy waits. Do not label resource-lifetime defects or every locking error as transient. |
| Constraint failure | Treat unexpected unique, FK, check, and not-null failures as database errors. Only the documented final-result uniqueness conflict reads the saved winner. |
| Failure before commit | Roll back the atomic state/generation unit; discard failed transactions and connections. No partial success response. |
| Unexpected database failure | No blind retries; sanitized `500` ProblemDetails when possible. Never expose SQL, database paths, provider messages, or stack traces. Preserve diagnostic details in server logs with a correlation identifier. |
| Cancellation or response loss | Stop new work/retries and release resources. A confirmed commit remains committed. A lost connection is not evidence of commit success or failure. |

**Proposed mechanics for review (no new numeric defaults):** Decision 14 in `GAME_OF_LIFE_DESIGN_UPDATED.md` remains the detailed SQLite policy; do not maintain a second divergent retry design here.

The retry and concurrency mechanics below belong to later slices, not Slice 3. Slice 3 needs only upload persistence, cleanup, and an appropriate sanitized error without a success response when a write fails; do not scaffold later operation methods or recovery infrastructure.

- `BoardService` owns operation-level retry decisions; `SqliteBoardStore` owns SQL, short non-deferred write transactions after calculation, classification, and cleanup. Use separate private-cache connections per attempt. Before retry/recalculation, dispose failed resources and reload authoritative state. Never reuse a failed snapshot.
- Use one configurable bounded operation loop and finite provider `DefaultTimeout`/`CommandTimeout`, including transaction start and verification, within the effective total wait bound. Let the provider handle ordinary busy waiting; do not restart full wait windows in a second loop. Distinguish restart-required `SQLITE_BUSY_SNAPSHOT` from ordinary busy and same-connection `SQLITE_LOCKED` resource defects using primary/extended codes and operation context. No new retry framework, numeric defaults, or silent transient-error allowlist.
- For a commit exception, end any still-active transaction and discard the connection. Never automatically repeat an ambiguously committed Next; return sanitized `500` when its outcome cannot be established. A response failure after a confirmed commit does not permit rerunning database work. Final-result uniqueness conflicts read the saved winner; unrelated constraints remain errors.
- Observe cancellation before/after operations and before commit/retry. Microsoft.Data.Sqlite operations execute synchronously; passing a token alone does not prove interruption. Keep waits finite, clean up independently of a canceled token, stop new work when cancellation is observed, and preserve committed state. No background verification/retry worker.

**HTTP clarification proposed for developer approval:** Preserve approved `409` for optimistic retry exhaustion and `500` for unexpected/unverifiable database outcomes. Propose extending the existing `409` convention to positively identified temporary SQLite contention after bounded wait/restart exhaustion. Do not treat that extension as approved, introduce `503` silently, or use final-search `422` for database errors. Cancellation introduces no new status.

## 3. Incremental TDD sequence

**Minimal setup:** Create only the buildable API and two test projects, package references, and the declarations needed for the first engine tests. Do not scaffold future services, all endpoint contracts, speculative alternatives, or generic fixtures in advance. Keep necessary setup and declaration-only scaffolding within the approved numbered slice, without implementing behavior before its tests or scaffolding future slices. Ask only if a material design contradiction or blocker requires a new decision; routine preparation within the slice has no separate approval gate.

For each approved numbered slice, challenge the proposed assertions and work through all its behaviors using small RED -> GREEN cycles:

1. **RED:** Write and execute the smallest meaningful failing test(s) for the next behavior; verify and report the expected failure. Continue directly to GREEN.
2. **GREEN:** Implement the minimum behavior, run targeted tests and relevant regression tests, and verify the behavior. Continue directly to the next behavior within the same approved numbered slice; do not pause for approval after GREEN.
3. **REFACTOR:** Refactor as appropriate within the approved slice, without changing approved behavior or expanding scope, and rerun relevant tests. No separate approval is required within that slice.
4. **COMPLETE:** Complete every required acceptance test for the numbered slice and run relevant regression tests.
5. **REVIEW:** STOP only when the entire numbered slice is complete. Report changed files, actual test commands/results, deviations, and unresolved issues. Wait for explicit developer approval before starting the next numbered slice.
6. **DECISION REQUIRED:** If a material design contradiction or blocker requires a new decision, stop and ask rather than guessing. Do not change approved scope, architecture, or provisional decisions to bypass it.

Each numbered row is one developer approval boundary. Internal milestones and individual behaviors are implemented incrementally, without separate approval gates. Do not automatically proceed to the next numbered slice. This workflow change does not authorize implementation in this documentation-only task or expand approved functionality or infrastructure.

| Slice | Behavior and implementation boundary | Required evidence / decision point |
|---|---|---|
| 1 | Pure finite-board generation engine | Independent rule/boundary fixtures, orientation, unchanged dimensions, immutable input, and cancellation checks; no HTTP or persistence dependency |
| 2 | Minimal JSON serialization for durable storage | Use System.Text.Json for living-cell coordinates; retain width/height separately and reconstruct the HashSet. Test empty/populated round trips and clear failure for invalid stored JSON. No custom converters or binary formats |
| 3 | Minimal Durable Board Upload | Valid asymmetric upload returns `201`, unique ID, correct dimensions/cells and generation 0; real SQLite reconstruction after close/reopen; invalid input and failed-write handling. Only `Boards` and upload DTOs/endpoint/service/storage/initialization needed here; no Location requirement |
| 4 | Relative preview and absolute-generation retrieval (4a-4f implemented; awaiting review) | Durable initial state; zero/positive relative and absolute reads; accurate labels; zero engine calls for zero; unchanged persistent values; live-current Location; validation, overflow, budget, cancellation |
| 5 | HTTP Next with atomic state/generation commit | Unchanged InitialState, correct one-step advance, durable restart progress, conditional generation update, rollback, sanitized errors, overflow prevention, and no success before commit. Return 409 immediately for a verified optimistic conflict; bounded reload-and-recalculate retries and detailed contention classification remain in Slice 6 |
| 6 | Concurrent Next and SQLite contention handling | After the SQLite contention/timeout correctness review: concurrent requests produce consecutive committed generations without lost updates; prove authoritative reload/recalculation, bounded conflict/contention handling, no automatic re-advancement after an uncertain commit, and failed-transaction cleanup |
| 7 | Bounded dictionary-based final search | Use a straightforward dictionary-based algorithm; verify actual board-state equality before identifying a cycle. Verify stable/cycle outcomes, accurate cycle lengths and generation labels, pre-call budget checks, all actual engine calls counted, and cancellation before exposing the endpoint. No competing algorithm is required |
| 8 | Final-state HTTP persistence | Introduce `FinalStateResults`. Reuse saved response before engine work; compute/persist only on miss; correct `422` on required over-budget work, no pre-commit failure result, unchanged current state and saved-result reuse after Next/restart |
| 9 | Concurrent final-state discovery | One durable winner; both requests return its unchanged configuration/metadata despite valid different representatives or a concurrent Next |
| 10 | HTTP/resource coverage | Complete the small real-Kestrel group, wide/large boards, payload/ProblemDetails boundaries, overflow and server-observed cancellation. Reuse earlier tests; do not duplicate the whole HTTP suite across hosts |
| 11 | Final verification, cleanup, and performance review | Run complete unit/integration suites; verify all five endpoints and persistence; test representative boards including reasonably large boards. Perform a lightweight performance sanity check of the working application, propose targeted improvements only for measured problems, remove unnecessary code/dependencies/artifacts, finalize README, and complete independent review |

**Sequencing defaults:** Establish the upload/reload vertical slice before complex concurrency work. Preserve existing safeguards, including Kestrel's request-size limit. Additional `MaxCells` and arbitrary board-dimension caps are deferred pending measured need and explicit review; the proposed 4,194,304-cell default is not approved. Small test overrides of existing limits are not production defaults. Development databases are disposable and are not shipped. Review operational limits using the working application and the final performance sanity check; the proposed 10,000 computation caps remain provisional. Use small injected retry/timeout settings for correctness tests without locking production tuning. Dictionary-based cycle detection and JSON persistence are approved. Consider Brent only if actual measurements reveal a significant performance or memory problem; reconsider custom binary formats, versioned binary protocols, bitset codecs, or other serialization implementations only if measurements reveal a meaningful storage or serialization bottleneck. Propose any targeted change for developer review before implementation.

### Slice 3: Minimal Durable Board Upload — complete; approved boundary

Accept a valid rectangular boolean grid through `POST /api/boards`, validate basic structure and configured resource safeguards, convert it into the existing `BoardState`, persist it in SQLite at generation 0, and return `201 Created` with the generated board ID and common state response. Reopen the database and reconstruct the stored board correctly. A persistence failure returns an appropriate error, never a successful upload response.

The completed Slice 3 created only `Boards`, with `Id`, `Width`, `Height`, `CurrentState`, and `CurrentGeneration`, and only the endpoint, DTOs, service operations, storage operations, and initialization needed for this slice. Reuse existing coordinate JSON serialization. Keep initialization simple and reproducible; no migration framework. Do not scaffold future endpoints or methods, retry frameworks, concurrency coordination, caching, or speculative performance optimizations. Preserve existing request-size and structural-validation safeguards; additional `MaxCells` and dimension caps are deferred pending evidence and review. `FinalStateResults` waits until Slice 8. Stateful Next still atomically persists current state and generation when introduced in Slice 5.

**Focused Slice 3 acceptance tests:** Keep changes focused and use small RED -> GREEN cycles; under the current workflow, developer review occurs after the entire approved numbered slice, including acceptance and relevant regression tests.

1. A valid asymmetric upload returns `201`, a unique board ID, correct dimensions/cells, and generation 0. No Location header is required.
2. Reject invalid requests, including null, empty, jagged, and malformed grids; cover configured basic safeguards.
3. Close and reopen real file-backed SQLite, then retrieve through the storage layer and reconstruct identical dimensions, living cells, and generation. The public read endpoint waits until Slice 4.
4. A failed database write returns an appropriate error and no successful upload response.

Do not add process-kill simulations, complex fault-injection infrastructure, concurrency test harnesses, or historical-generation tests in Slice 3. The broader later-slice test groups below do not expand this boundary; retain advanced resource/transport coverage for Slice 10.

### Slice 4: Implemented generation retrieval; awaiting developer review

Milestones 4a-4f are complete under one Slice 4 approval, using focused RED -> GREEN cycles. Retain the 11 numbered slices and stop before Slice 5.

| Milestone | Implemented behavior | Evidence |
|---|---|---|
| 4a | InitialState added to Boards; one upload insert writes identical initial/current coordinate JSON at generation 0 | Existing upload test now reads and compares both stored fields after app shutdown and SQLite reopen |
| 4b | Relative zero returns one committed current snapshot | Exact nonzero generation/cells after reopen, zero engine calls, unchanged persistent values |
| 4c | Relative positive computes from current state and labels current generation + offset | Independent changing glider phases, exact call counts, snapshot retained across an update during calculation, no writes |
| 4d | Absolute zero returns original upload | Original cells despite changed current state after reopen; zero calls and unchanged persistent values |
| 4e | Absolute positive always reconstructs from InitialState | Current progress below/equal/above target; exact glider phases, labels, calls, and unchanged persistence |
| 4f | Upload Location points to live current preview | URI resolves at upload and after seeded progress/app restart; generations/0 still returns original cells |

Validation uses standard Minimal API Guid/Int64 binding. Missing/empty preview offsets, malformed/fractional/unrepresentable counts, negative counts, and invalid IDs return 400 ProblemDetails. Unknown boards return 404. Addition overflow is rejected before engine work; zero at Int64.MaxValue succeeds. Duplicate-query semantics remain deferred.

The latest approved request removes the proposed retrieval computation budget: both routes are tested beyond 10,000 calls without a numeric cap. Cancellation is checked around reads, between calculations, and during response mapping; the existing engine checks inside calculations. Focused service tests with real SQLite verify cancellation stops after one engine call, accepts the Int64 maximum target before cancellation, and leaves storage unchanged. Elaborate transport/disconnect cancellation remains in Slice 10.

Operational concern for review: a large valid generation count can consume CPU and occupy a request thread for a long time, particularly with concurrent callers. The simplest proposed safeguard is a configurable server-controlled request timeout using existing ASP.NET Core request-timeout support and the propagated cancellation token. No timeout value or new guard is implemented pending review. The final-state iteration budget remains a separate future-slice decision.

InitialState uses the existing JSON codec and no new tables, cache, triggers, migration framework, or advancement infrastructure. Initialize creates the new schema for a fresh database; it does not upgrade an older Boards table. No repository database was present or deleted. Use a fresh disposable database path; non-disposable data requires an explicit migration decision.

Verification: Debug and Release solution runs each passed 23 unit and 47 integration cases. See README for runnable commands, current routes, database setup, and remaining boundaries.

Slice 4 executed TDD evidence (each RED followed directly by implementation and an integration regression run):

| Targeted filter (`FullyQualifiedName~` prefix) | Observed RED | Following GREEN integration total |
|---|---|---|
| ValidUploadsReturnUniqueIds | 1 failure: missing InitialState column | 17 passed |
| PreviewZero | 1 failure: missing route (404) | 18 passed |
| PreviewCalculates | 2 failures: starting generation returned | 20 passed |
| InvalidPreview OR PreviewRejects | 2 failures: negative/overflow accepted; 5 binding cases already passed | 27 passed |
| AbsoluteZero | 1 failure: missing route (404) | 28 passed |
| AbsolutePositive | 3 failures: generation zero returned | 31 passed |
| InvalidAbsolute OR MissingBoard OR InvalidBoardId | 1 failure: negative target accepted; 9 cases already passed | 41 passed |
| UploadLocation | 1 failure: missing header | 42 passed |

Targeted commands used `dotnet test GameOfLife.IntegrationTests --no-restore` with the filters above and `--verbosity minimal` (the first persistence run used quiet, then normal console logging to inspect the missing-column failure). Later runs also used `-m:1 -nr:false -p:UseSharedCompilation=false` to avoid shared-build stalls. Multi-filter expressions used `|` with a complete FullyQualifiedName~ prefix per filter. The first integration regression run had two environmental failures from the Windows Event Log provider denying writes; rerunning with `$env:Logging__EventLog__LogLevel__Default='None'` passed. No production logging configuration was changed.

Five final snapshot/no-old-cap/cancellation cases passed against the existing implementation; no RED is claimed for those already-supported paths. Final commands actually executed with the Event Log override:

```powershell
dotnet test GameOfLife.sln --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
dotnet test GameOfLife.sln -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
git diff --check
```

Both solution runs: 23 unit + 47 integration cases passed, no skips/failures. Diff whitespace check passed. No new packages or unnecessary infrastructure were identified. The retrieval-budget proposal was deliberately superseded by the latest request; no numeric cap was added. No other scope deviations or functional blockers remain. Review the timeout proposal above before implementing any operational guard.

### Slice 6: Concurrent Next and bounded contention handling — implemented for review

The latest Slice 6 request approves the focused implementation and supersedes older proposed mechanics/testing breadth for this slice. Slice 5 was inspected with a clean working tree; its Debug baseline passed 23 unit and 57 integration cases. Preserve its explicit non-deferred transaction and conditional UPDATE, with calculation outside the transaction.

- One operation-specific loop reloads and recalculates after a verified zero-row update. Every failed attempt disposes its command, transaction, and connection before the next read. `Next:MaxAttempts` defaults provisionally to three total attempts and accepts positive overrides. Exhaustion returns 409; disappearance returns 404. Each success returns its own committed cells/generation.
- Keep provider finite waiting instead of a second busy retry/backoff loop. Use private-cache connections and positive `Default Timeout` from the connection string (provider default 30 seconds); reject zero/unlimited waiting. Provider exceptions never enter the optimistic loop. Busy/locked failures retain sanitized 500; extending them to 409 is not part of this slice. No transient error-code allowlist or automatic uncertain-commit replay is introduced.
- Wait accounting: each attempt has a finite sequence of commands (connection configuration, snapshot SELECT, second connection configuration, BEGIN IMMEDIATE, conditional UPDATE, and COMMIT or rollback). A final existence read distinguishes disappearance after exhausted conflicts. All use the configured finite provider timeout, including transaction commands. Thus application-issued command wait windows are bounded by `(6 * MaxAttempts + 2) * DefaultTimeoutSeconds`, conservatively including the final existence read; provider polling granularity can overshoot each timeout. This accounts for command waiting, not a hard wall-clock deadline for connection opening, calculation, scheduling, I/O, or cleanup. No full provider wait window is restarted after a busy timeout. Numeric tuning remains provisional; no separate deadline/backoff framework is added.
- Cancellation is checked before each attempt/calculation, around synchronous database work, and before commit. Cancellation during provider waiting is observed when control returns; cleanup does not depend on a live request token. Existing rollback, immutable InitialState, and restart-durability tests remain in place.

Essential real file-backed SQLite tests: barrier-synchronized Next from generation 5 returns correct distinct generations 6 and 7, with three engine calls and durable generation 7; forced conflicts exhaust exactly three attempts without replacing the winner; a one-attempt override; rejection of unlimited provider waiting; held writer lock released after observing provider retries succeeds without recalculation; held lock through one-second timeout yields primary/extended SQLITE_BUSY (5/5), sanitized 500, unchanged state, closed connections, and later recovery; cancellation during that wait rolls back; real commit followed by simulated acknowledgement loss returns 500 without a second advancement. The acknowledgement-loss simulation is at the store/service boundary during connection disposal, after a genuine commit, and does not simulate every native COMMIT or storage-device failure. Retain the existing deferred-constraint COMMIT-failure and trigger-induced pre-commit rollback cases.

Focused RED evidence: concurrent Next returned 409 and conflict exhaustion calculated once instead of three times; after the loop both passed. A separate RED showed zero/unlimited provider timeout was accepted; validation made it pass. Override/cancellation, lock waiting, acknowledgement loss, and existing rollback guarantees already passed when exercised; no new RED is claimed for those supported paths. The first concurrency test run after implementation exposed a test-only double-deserialization of a disposed response stream; corrected to inspect the generation from buffered JSON before the shared assertion.

Targeted commands actually used `dotnet test GameOfLife.IntegrationTests --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal` with `--filter` expressions for `ConcurrentNextReloads|RepeatedGenerationConflicts`, `UnlimitedProviderWaiting|ConflictAttemptOverride`, `WriterLock|LostAcknowledgement`, `CancellationDuringProviderWait`, and the combined BoardNextTests/BoardConcurrencyTests groups (each term prefixed with `FullyQualifiedName~`). The Windows Event Log override documented in README was used. Full solution commands in Debug and Release (`dotnet test GameOfLife.sln [ -c Release ] --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal`) each passed 23 unit and 64 integration cases, with no failures/skips. `git diff --check` passed. An initial baseline invocation using default parallel/shared build settings stalled; the single-node commands above completed successfully.

Deferred exploratory coverage: stale read-transaction BUSY_SNAPSHOT reproduction, same-connection/shared-cache LOCKED matrices, exhaustive cleanup fault combinations, and process-kill/disk-failure simulations. The implementation takes no read transaction into calculation, starts non-deferred writes afterward, and uses private independent connections, so these cases do not justify additional retry machinery. No change to Decision 14's approved concurrency, rollback, cancellation, or uncertain-commit requirements is needed. Slice 6 is complete under the latest Slice 7 authorization; see the Slice 7 handoff below.

### Slice 7: Bounded final-state discovery - implemented for review

The latest Slice 7 request authorizes only the pure discovery component, small result contract, and focused unit tests. No HTTP, ProblemDetails, database, persistence, memoization, concurrency coordination, alternate simulator, or canonical representative selection is included.

- `FinalStateDiscovery` reuses `IGenerationEngine`. It records the initial state at iteration zero, looks up an order-independent fingerprint after each call, verifies dimensions and living-cell set equality, and retains all unequal observations in the same bucket. A matching fingerprint alone cannot establish a repeat or discard prior observations.
- `FinalStateResult` reports Stable (period 1), Cycle (period greater than 1), or Inconclusive; returned state, starting/result absolute generations, nullable cycle length/start, and actual engine calls. Successful searches return the newly calculated repeating state; inconclusive searches return the latest calculated state (the input at zero budget). Cycle start is the first observed absolute generation in the verified cycle. No input mutation occurs.
- The provisional default is 10,000 actual engine calls, with an internal-only budget overload for focused tests. Check before every call; exact-cap detection succeeds and zero budget performs no calls. Preview and absolute-generation retrieval remain uncapped. Cancellation is checked at entry, before/after engine calls, during fingerprint traversal and bucket scanning, and before returning; the same token reaches the existing engine. Negative inputs and unrepresentable next-generation labels throw before the affected engine call, following existing nonnegative Int64 conventions.
- Focused RED evidence: the zero-budget declaration stub threw; empty/block returned Inconclusive; blinker and transient stability stopped after one call; single-observation fingerprint storage missed both colliding clock phases; cancellation allowed an engine call or returned after cancellation; negative inputs and generation overflow were accepted. Each was followed by minimum implementation and successful unit regressions. Pulsar, the default-budget glider, transient-to-cycle metadata, and additional maximum-generation boundaries already passed the general algorithm; no new implementation RED is claimed for those paths.
- Thirty new unit cases cover independent empty/block, blinker, period-3 pulsar, transient single-cell and transient-to-blinker fixtures, budgets, absolute metadata, immutability, cancellation/token forwarding, and overflow. Two unequal clock phases naturally share the actual production fingerprint: one-call tests reject a false conclusion, while two-call tests prove both colliding observations survive, in either starting phase. No fingerprint injection framework or custom simulator is needed.
- Temporary mutations confirmed the assertions detect fingerprint-only matching (4 failing cases), a hard-coded oscillator period of 2 (pulsar fails), an extra permitted engine call (2 failing cases), and cycle-start labeling at the initial generation (transient-to-cycle fails). All mutations were restored before final verification.

Actual final verification used the existing Windows test-process override `$env:Logging__EventLog__LogLevel__Default = 'None'` and these commands:

```powershell
dotnet test GameOfLife.sln --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
dotnet test GameOfLife.sln -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
git diff --check
```

Debug and Release each passed 53 unit and 64 integration cases, with no failures/skips; the whitespace check passed. Targeted RED/GREEN runs used the same unit-project command with focused `FullyQualifiedName~` filters; the final full runs include all restored code. Changed files: `Domain/FinalStateDiscovery.cs`, `Domain/FinalStateResult.cs`, `FinalStateDiscoveryTests.cs`, `PLAN.md`, and README status/budget notes. No changes to the approved algorithm or scope were needed. Deliberate limits: the provisional budget is not performance tuning; dictionary storage retains observed states, and the simple fingerprint can collide, with exact comparisons preserving correctness. There are no functional blockers or new dependencies. Stop after Slice 7 for developer review; do not begin Slice 8.

### Slice 8: Final-state HTTP endpoint and durable persistence - implemented for review

The latest Slice 8 request approves the endpoint and persistence, reusing Slice 7 unchanged. `GET /api/boards/{id}/final-state` checks SQLite first, otherwise discovers from one committed snapshot with the existing 10,000-call default, commits only success, and returns the original saved metadata. It never advances current progress. Missing boards return 404, supplied `maxIterations` returns 400, exhaustion returns 422, and unexpected persistence errors retain sanitized 500 handling.

Schema initialization adds `FinalStateResults` with the approved six fields and unique board FK. Parameterized SQL uses the existing codec and a short transaction; `ON CONFLICT(BoardId) DO NOTHING` handles only the documented same-board conflict, followed by a winner lookup. Next never alters this table. No new dependencies, serializers, persistence hierarchy, memory cache, background work, locks, or retry framework were introduced.

Sixteen new integration cases cover exact stable/cycle HTTP and persisted metadata, unchanged current progress, byte-for-byte reuse and measured zero additional engine calls after second request/restart/three Next calls, real-glider exhaustion at exactly 10,000 calls, missing boards, unsupported query values, no automatic results from other routes, failed insert/commit rollback, unrelated uniqueness failures, cancellation at entry/after calculation/before commit, confirmed-commit preservation, and sequential same-board winner reuse. Cancellation cases use the application/store boundary and real SQLite; elaborate disconnect coverage remains Slice 10. Simultaneous discovery and Next interleavings remain Slice 9.

Actual RED: the initial stable HTTP/persistence acceptance test returned 404 because the route was absent. GREEN: endpoint/service/storage implementation made it pass. Workflow deviation: the general implementation covered multiple behaviors in that first cycle; remaining focused tests passed on introduction, rather than separate RED-first cycles for each behavior. No additional RED is claimed.

Actual verification (with `$env:Logging__EventLog__LogLevel__Default = 'None'`): focused `dotnet test GameOfLife.IntegrationTests --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal --filter FullyQualifiedName~BoardFinalStateTests` passed 16 cases. Full `dotnet test GameOfLife.sln --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal` and the same command with `-c Release` each passed 53 unit and 80 integration cases, with no failures/skips. `git diff --check` passed.

Changed files: `Application/ApplicationModels.cs`, `Application/BoardService.cs`, `Endpoints/BoardEndpoints.cs`, `Infrastructure/SqliteBoardStore.cs`, `BoardFinalStateTests.cs`, README, and this status/handoff. No unresolved functional blocker was found. The default budget remains provisional; performance tuning belongs to Slice 11. Stop for developer review; do not begin Slice 9. No commit or push.

## 4. Final performance review and SQLite correctness review

**Guiding principle:** Implement the simplest correct solution. Measure actual performance before introducing optimization complexity; do not optimize hypothetical bottlenecks. No mandatory comparative benchmarking, algorithm-selection gate, dedicated benchmark project, or benchmarking framework is required.

**Slice 11: Final verification, cleanup, and performance review**

1. Run the complete unit and integration test suites.
2. Verify all five endpoints under the proposed contract and persistence behavior, retaining the approved atomicity, concurrency, recovery, and computation-limit guarantees.
3. Test representative empty, sparse, populated, stable, and periodic boards, including reasonably large boards. Retain the 1,024 x 1,024 support/test baseline; it is not a production ceiling or a requirement for exhaustive workload sweeps.
4. Perform a lightweight performance sanity check of the working application using simple measurements and existing .NET tooling where sufficient.
5. Evaluate generation speed, dictionary-based cycle detection, memory consumption, JSON serialization, and SQLite persistence only to determine whether a meaningful performance problem exists. Record enough workload, runtime/machine, and measurement context to explain the observations and their limitations. No competing implementation is needed.
6. If performance is unacceptable, identify the actual bottleneck and propose targeted improvements for developer review. Do not automatically implement optimizations or add alternative algorithms/codecs.
7. Remove unnecessary code, dependencies, and artifacts while retaining required correctness coverage and the three-project solution.
8. Finalize README with setup/run/test instructions, approved behavior and limits, and a concise account of the sanity-check observations and limitations. Complete the final independent review. No comparative benchmark table or mandatory benchmark evidence is required.

**Before Slice 6: SQLite contention/timeout correctness review.** Preserve the focused real-SQLite review of provider busy waits, implicit transaction commands, finite timeout granularity, effective total wait, and cancellation. Review configurable retry/timeout/budget values and separately review the proposed SQLite-contention `409` mapping. This is validation of bounded waiting and failure handling, not a performance-selection gate; no dedicated benchmarking framework or exhaustive tuning matrix is required. Decision 14 remains the detailed policy; its unresolved mechanics and numeric settings require separate review.

Timing and memory observations belong in the lightweight performance review rather than arbitrary thresholds in correctness tests. Further profiling, optimization, or alternatives require concrete measured evidence and developer-approved scope. Deferred caching, background work, per-generation/snapshot storage, and external services remain out of scope; the proposed absolute-generation route reconstructs from InitialState.

## 5. Consolidated mandatory acceptance and final review

The groups below consolidate earlier overlapping test lists; they are requirements-to-tests coverage, not a demand for a new test for every sentence. Use parameterized cases and shared small fixtures where useful. Keep the strongest observation at the appropriate layer: pure rules in unit tests, database guarantees in real SQLite tests, and transport enforcement in the small Kestrel group. A broader scenario may cover several requirements when all relevant facts are asserted explicitly; never replace state/call-count assertions with success-status checks.

| Mandatory test group | Essential assertions |
|---|---|
| Engine and JSON serialization | Independently established life-rule, empty/block/blinker/longer-oscillator/transient expectations; finite nonwrapping corners/edges, 1×1/thin/asymmetric shapes, orientation, immutable input, and unchanged dimensions. JSON coordinate serialization round-trips empty/populated boards and clearly rejects invalid stored JSON; dimensions remain separate. Add cases only for concrete requirements or failure modes |
| Upload and durable state | Real JSON validation for null/empty/jagged/malformed boards and invalid IDs; approved statuses/DTO behavior, with Location added in Slice 4; correct current state and generation after reload/restart (Slice 3 may read through storage); no successful write response before commit; no implicit final-state calculation |
| Preview validation and isolation | One consistent committed snapshot during concurrent Next; stepsAhead=0/positive absolute labels, zero engine calls for zero, no writes; missing/empty/malformed/negative/out-of-range values, numeric parse overflow, and generation-addition overflow before engine work. Use standard ASP.NET Core binding; duplicate-query semantics/coverage are deferred for later review, with no Slice 4 test matrix. Proposed overflow/error details remain for review; stepsAhead=0 at the maximum generation succeeds |
| Absolute-generation retrieval | Reconstruct generationNumber from InitialState with current progress greater/equal/less than target; original cells at zero after current cells change and SQLite reopens; zero calls for zero; unchanged persisted values; unknown IDs and invalid/excessive targets under the shared provisional limits policy |
| Atomic advancement and durability | Current state and incremented generation commit together, leaving InitialState unchanged. Inject a failure after the conditional update but before commit; assert both values roll back. Verify committed progress after restart and that confirmed commit followed by response loss does not cause automatic re-advancement. Uncertain commit outcomes never trigger blind retries. Unexpected real database errors yield sanitized `500`, not disguised conflicts |
| Concurrent Next and bounded retries | Concurrent requests reading one generation produce correct consecutive committed generations; prove the loser reloads authoritative state and recalculates. Exhausted optimistic retries use `409`; stale snapshots cannot overwrite committed progress. There is no client retransmission deduplication guarantee |
| SQLite locking, cleanup and cancellation | Deterministically held/released writer locks exercise success and bounded exhaustion; distinguish ordinary busy, stale-snapshot busy and same-connection locked cases. Unexpected constraints remain errors. Dispose failed attempts before retry; test active-transaction/cleanup failure without hiding the original error or reusing damaged resources. Cancel during database work, prove eventual lock release/no further retry and rollback before commit; confirmed commits remain durable after later cancellation |
| Final search, budget and reuse | Verify stable state or any verified cycle member against independent fixtures (including empty/block stability in one engine call), with exact period and configuration at its claimed generation. Check the budget **before every engine call**, count all actual engine calls, prove exact-cap success, and return `422` without the forbidden extra call or saved result when more work is required. When fingerprints are used as dictionary keys, forced collisions of unequal states cannot establish a false cycle; verify actual state equality. No canonical representative or competing algorithm is required. First saved conclusion and all metadata (including original `iterationsUsed`) replay unchanged with zero new calls after Next/restart; concurrent first discoveries return the unique winner even across different valid phases |
| Real Kestrel and resource protection | In Production configuration, test valid UTF-8 bodies at `L-1`, `L`, `L+1` bytes, with known length and HTTP/1.1 chunked/unknown length; use padding to isolate byte limits. Test early application rejection and native Kestrel enforcement independently, with limits set before JSON binding. Check `413`, supported JSON ProblemDetails/content type/body status, and absent/supported/unsupported `Accept`; record any server-level envelope/connection limitation for review. Ensure known-length rejection precedes JSON parsing, streamed rejection permits only bounded prefix processing, no full oversized-grid materialization/domain mapping/persistence, and no size-validation arithmetic overflow. Include very wide single rows and 1,024×1,024 sparse/dense integration boards; keep routine byte-boundary fixtures small. Dimension/cell-count boundary tests apply only if such limits are subsequently approved; isolate their `400` from body-size `413` |
| Expensive-work cancellation and route contract | Cancel actual preview/uncached-final generation work after server entry; verify token observation, stopping within costly calls and between calls, unchanged committed board and no pre-commit final-result write. Client-await cancellation alone is insufficient; disconnect promises no status. Verify all five application routes, retained final fields, relative/absolute retrieval semantics, no public final budget override, and reviewed errors; include absolute-retrieval cancellation with unchanged persistent values |

Use separate file-backed SQLite connections and deterministic barriers/events. Reuse narrowly scoped SQLite-trigger/test-harness fault injection for rollback, contention, and commit-outcome tests; do not build a general fault framework. Real SQLite rollback tests, restart/reopen verification, and no automatic re-advancement after response loss or commit ambiguity remain mandatory. A simulated error after a real commit does not model every possible storage-device failure. Watchdogs may detect hangs; performance latency/memory measurements belong in the lightweight performance review.

**Approved test-scope deferrals:** Elaborate process-termination/process-kill scenarios around commit are optional follow-up work rather than submission requirements. Add them only if evidence warrants the effort and the developer approves the scope. Atomic transactions, protection against lost updates, durable progress, rollback, bounded contention handling, and cancellation/resource cleanup remain required.

**Submission cleanup acceptance:** Remove unnecessary code, dependencies, and artifacts without removing required correctness coverage, including collision/call-count tests where applicable. The final solution has three projects; retain production code, relevant tests, reproducible SQLite setup, design/plan documentation, README, and normal run/test instructions. Ship no generated user database. No benchmark project, competing implementation, or benchmark-specific cleanup is required.

**Final verification:** Build and execute the full unit/integration suite in Release after cleanup, verify all five endpoints and persistence behavior, and verify free/self-contained clone/build/run/test instructions. Report actual commands/results and the lightweight performance sanity-check observations and limitations. Complete an independent PR-review checkpoint against the design, final diff, retained tests, and actual results; fixes or optimizations are separate approved changes. Do not commit, push, approve, or merge automatically.

**Remaining review items and compatibility:** Numeric computation/retry limits, evidence for any additional upload limit, the SQLite-contention exhaustion status mapping, deferred duplicate-query semantics/coverage and preview-overflow `400` proposals, and the exact tested Kestrel/ProblemDetails envelope remain explicit review items. Dictionary-based cycle detection and JSON persistence are approved; no algorithm/codec-selection gate remains. The lightweight performance review replaces mandatory comparative benchmarking and broad benchmark workload requirements. The proposed Slice 4 revision changes query naming, initial-state persistence, historical-retrieval deferral, and route count only; all other approved architecture and deferrals remain unchanged. This documentation diff awaits review; milestones 4a-4f are internal to numbered Slice 4.

Slices 1-6 are complete. Slice 7 is implemented for developer review under the latest focused request; its handoff above records the scope and evidence. Stop here; Slice 8 requires explicit approval.
