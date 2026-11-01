# Conway’s Game of Life API implementation plan

## 1. Baseline and architecture

The complete `GAME_OF_LIFE_DESIGN_UPDATED.md` has been read, and the supplied AGENTS.md instructions apply. The workspace has no application code. No source changes, functional tests, or benchmarks have been performed.

Start with three minimal `net8.0` projects. Add the fourth project only when focused benchmark comparisons are ready:

| Project | Responsibility |
|---|---|
| `GameOfLife.Api` | All production code, organized into Endpoints, Application, Domain, and Infrastructure folders |
| `GameOfLife.UnitTests` | Isolated rules, calculations, validation, codecs, and cycle-detection tests |
| `GameOfLife.IntegrationTests` | HTTP contracts and real file-backed SQLite persistence, concurrency, and recovery |
| `GameOfLife.Benchmarks` (temporary) | Focused development-time comparisons; remove this project and BenchmarkDotNet before final submission |

Use ASP.NET Core Minimal APIs, System.Text.Json, Microsoft.Data.Sqlite, xUnit, and `WebApplicationFactory`. Keep production dependencies minimal. Add only a small real-Kestrel group within the existing integration-test project for transport/body-limit verification; TestServer does not establish Kestrel enforcement. Reuse production composition with an ephemeral loopback port, normal `HttpClient`, and temporary file-backed SQLite; no new test framework/project or external infrastructure.

Key classes:

- **Endpoints:** `BoardEndpoints`, request/response DTOs, and upload validation.
- **Application:** `BoardService` coordinates upload, advancement, preview, and final-state discovery.
- **Domain:** `BoardState`, `GenerationEngine`, and cycle detection. These contain no HTTP or database dependencies.
- **Infrastructure:** `SqliteBoardStore`, `BoardStateCodec`, and schema initialization.

Use constructor injection. Introduce a small generation-engine interface for measured call counts and controlled concurrency tests; avoid generic repositories and additional architectural frameworks.

**Approved submission clarification:** The latest delivery instruction supersedes retaining four projects in the final submission. Ship `GameOfLife.Api`, `GameOfLife.UnitTests`, and `GameOfLife.IntegrationTests`; keep all production layers within the API project. The benchmark project, rejected algorithms, unused codecs, temporary comparison-only tests/references, and BenchmarkDotNet dependencies are development aids, not final deliverables. Retain the selected implementations and their correctness tests, and summarize actual measurements and rationale in README. The 4–5-hour estimate guides scope; it is not permission to omit correctness or approval checkpoints.

## 2. Endpoint contracts and persistence

| Endpoint | Application/domain behavior | Persistence | Principal acceptance tests |
|---|---|---|---|
| `POST /api/boards` | Validate and map rectangular boolean cells | Save initial/current state at generation 0 | `201`, orientation and dimensions, malformed/null/jagged inputs, configured limits, reload after restart |
| `POST /api/boards/{id}/next` | Calculate one generation from committed current state | Atomically update state/generation and optional idempotency response | Sequential advancement, rollback, concurrent distinct/duplicate keys, durable replay, `409` on exhausted optimistic retries; SQLite contention mapping awaits approval |
| `GET /api/boards/{id}/preview?n=N` | Calculate relative to one consistent current snapshot | No writes | N=0, correct absolute generation, unchanged stored state, concurrent advancement, invalid N |
| `GET /api/boards/{id}/final-state` | Look up saved conclusion first; calculate only on miss | Insert one successful conclusion per board | Zero engine calls on saved-result lookup, exact work cap, `422` without a result on exhaustion, concurrent insertion and restart reuse |

Historical retrieval remains deferred.

Response conventions (Final State fields retained as approved; other HTTP proposals unchanged):

- Common state fields: `boardId`, `generation`, `width`, `height`, `cells`.
- Final-state responses add `startingGeneration`, `conclusionKind` (`Stable` or `Cycle`), `cycleLength`, and `iterationsUsed`.
- Final-state `generation` retains the original discovery’s absolute result generation, including when it predates current progress.
- Upload includes `Location: /api/boards/{id}/preview?n=0`.
- Reject a supplied final-state `maxIterations` parameter with `400`; provide no public budget override.
- Use ProblemDetails for the documented error statuses. Disconnection/cancellation need not produce an HTTP response.
- Verify `413` and its ProblemDetails envelope on real Kestrel in Production configuration. Use existing request-size features before body binding and existing ProblemDetails/status-code/exception middleware; preserve body-read `413` rather than remapping it to `400`/`500`. A ProblemDetails body is not automatic for every server rejection, unsupported `Accept`, or disconnected/started response. Record actual results and obtain review of any envelope limitation; no universal guarantee is established by documentation alone.

**Approved final-state representative selection:** The primary outcome is stabilization or a repeating cycle. Return the verified stable configuration or any verified configuration belonging to the detected cycle. Do not require a canonical/first-repeated state or reconstruction merely to select one. Retain the existing response fields, including `generation`, `startingGeneration`, `conclusionKind`, `cycleLength`, and `iterationsUsed`: the generation must match the returned configuration relative to the starting snapshot, the period must be verified, and all actual generation-engine calls (including additional cycle-detection calculations) must be counted. Different algorithms may return different correct phases and generation labels; correctness does not require identical representatives. This removes representative-selection complexity while preserving the meaningful outcome and accurate supporting metadata.

SQLite design:

- **Boards:** ID, dimensions, initial/current encoded states, current generation.
- **FinalStateResults:** unique board FK, starting/result generations, representative state, period, and actual discovery engine-call count.
- **IdempotencyRecords:** unique board/key pair, original response, committed generation, and durable commit-relative retention/expiry metadata. Review the capture mechanism before slice 6; an earlier creation timestamp cannot silently replace the successful-commit anchor.

Use parameterized SQL, reproducible versioned schema setup, separate connections per operation, WAL, foreign keys, and `synchronous=FULL`. SQLite supports atomic multi-statement transactions; FULL synchronization supplies the intended WAL durability setting. [Microsoft transaction guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions), [SQLite synchronization guidance](https://www.sqlite.org/pragma.html#pragma_synchronous).

**Approved requirements:** Advancement calculates outside the write transaction. Recheck the retained key before every retry/recalculation and inside the transaction. Current state, generation increment, and optional idempotency record/original response commit atomically. Roll back the whole attempt on pre-commit failure; dispose of failed resources before retrying. A generation conflict requires reloading and recalculating from authoritative state. Preserve optional keys, original-response replay, first-final-result reuse, and the existing HTTP conventions.

Final-state insertion is separate from advancement. A competing successful insert returns the winner’s stored configuration and all metadata unchanged, even if the losing calculation found a different valid cycle representative. Lookup-first reuse performs zero new engine calls but retains the original `iterationsUsed`; Next and restart never invalidate or relabel that result.

**Approved failure categories; proposed implementation mechanics below:**

| Condition | Handling and outcome |
|---|---|
| Conditional generation update loses | End the attempt, recheck the retained key, reload and recalculate. Bounded optimistic retry exhaustion uses approved `409`; no stale snapshot reuse. |
| SQLite busy/locked | Classify primary/extended error codes and operation context, independently of generation conflicts. Account for provider busy waits. Do not label resource-lifetime defects or every locking error as transient. |
| Known idempotency unique conflict | Roll back/dispose the loser, read the matching committed record on a fresh connection, and replay the original response without advancing again. Unrelated unique, FK, check, and not-null failures are not duplicate requests. |
| Failure before commit | Restore the atomic state/generation/idempotency unit; discard failed transactions and connections. No partial success response. |
| Unexpected database failure | No blind retries; sanitized `500` ProblemDetails when possible. Never expose SQL, database paths, provider messages, or stack traces. Preserve diagnostic details in server logs with a correlation identifier. |
| Cancellation or response loss | Stop new work/retries and release resources. A confirmed commit remains committed. A lost connection is not evidence of commit success or failure. |

**Proposed mechanics for review (no new numeric defaults):** Decision 14 in `GAME_OF_LIFE_DESIGN_UPDATED.md` remains the detailed SQLite policy; do not maintain a second divergent retry design here.

- `BoardService` owns operation-level retry decisions; `SqliteBoardStore` owns SQL, short non-deferred write transactions after calculation, classification, cleanup, and recovery reads. Use separate private-cache connections per attempt. Before retry/recalculation, dispose failed resources, recheck the retained key, and reload authoritative state; recheck the key again transactionally. Never reuse a failed snapshot.
- Use one configurable bounded operation loop and finite provider `DefaultTimeout`/`CommandTimeout`, including transaction start and verification, within the effective total wait bound. Let the provider handle ordinary busy waiting; do not restart full wait windows in a second loop. Distinguish restart-required `SQLITE_BUSY_SNAPSHOT` from ordinary busy and same-connection `SQLITE_LOCKED` resource defects using primary/extended codes and operation context. No new retry framework, numeric defaults, or silent transient-error allowlist.
- For a commit exception, end any still-active transaction, discard the connection, and verify keyed outcomes by a fresh authoritative read. A retained record permits original-response replay; lookup failure is not absence. Verified absence after the attempt definitively ends permits retry only for an otherwise eligible transient failure, within the original budget and while not canceled. Unexpected errors remain nonretryable; unresolved or unkeyed ambiguity never triggers automatic re-advancement. Never overwrite a uniqueness winner or replay an unrelated constraint failure; final-result conflicts read the saved winner.
- Observe cancellation before/after operations and before commit/retry. Microsoft.Data.Sqlite operations execute synchronously; passing a token alone does not prove interruption. Keep waits finite, clean up independently of a canceled token, stop new work when cancellation is observed, and preserve already committed records for subsequent client replay. No background verification/retry worker.

**HTTP clarification proposed for developer approval:** Preserve approved `409` for optimistic retry exhaustion and `500` for unexpected/unverifiable database outcomes. Propose extending the existing `409` convention to positively identified temporary SQLite contention after bounded wait/restart exhaustion. Do not treat that extension as approved, introduce `503` silently, or use final-search `422` for database errors. Cancellation introduces no new status.

## 3. Incremental TDD sequence

**Minimal setup:** Create only the buildable API and two test projects, package references, and the declarations needed for the first engine tests. Do not scaffold future services, all endpoint contracts, benchmark candidates, or generic fixtures in advance. Review setup before the first RED; add any necessary declaration-only scaffolding for later slices as a separately reviewed preparation, without implementing behavior before its tests.

For every behavioral slice, challenge the proposed assertions, then:

1. **RED:** Write and execute the smallest meaningful failing test(s); report expected behavior and observed failure. **Stop for explicit developer approval before production code.**
2. **GREEN:** Implement the minimum behavior, run targeted tests and relevant regressions, report results. **Stop for explicit approval.**
3. **REFACTOR:** Only if explicitly approved and useful; rerun relevant tests. **Stop before the next RED.**

Each row is an implementation milestone. Where it contains multiple failure cases, implement one focused case at a time under these same stops; never implement the entire row or plan in one pass.

| Slice | Behavior and implementation boundary | Required evidence / decision point |
|---|---|---|
| 1 | Pure finite-board generation engine | Independent rule/boundary fixtures, orientation, unchanged dimensions, immutable input, and cancellation checks; no HTTP or persistence dependency |
| 2 | Minimal board encoding for durable storage | Begin with the design's provisionally favored binary coordinates; test round-trip and invalid data. Defer alternative codecs and final format selection to Gate A |
| 3 | First vertical slice: HTTP upload → SQLite commit → reload | Valid upload returns `201`, correct DTO/Location and generation 0; real SQLite reload after reopening/restarting; invalid input and failed-write handling. Introduce only the needed schema/store/service/endpoint code |
| 4 | Relative HTTP preview from persisted state | N=0 makes the uploaded board retrievable over HTTP after restart; positive N is nonmutating and snapshot-consistent. Validate queries/overflow and honor cancellation |
| 5 | HTTP Next with atomic state/generation commit | Correct one-step advance, conditional generation update, rollback, classified errors, and no success before commit; defer the full retry/replay workflow to slices 6–7 |
| 6 | Keyed Next replay and retention | Atomically include the original response/key; sequential replay, commit-relative expiry boundaries, rollback between update and key insert, response-loss/restart recovery, and uncertain-outcome verification |
| 7 | Concurrent Next and bounded contention recovery | After focused Gate B tuning: distinct/same-key races within retention, reload/recalculation, key rechecks, provider busy handling, known unique conflicts, and failed-transaction cleanup. Specialized expiration-race tests are optional, as noted below |
| 8 | One correct bounded final-search candidate | Use fingerprint-dictionary detection as the provisional comparison baseline, not the selected production algorithm. Verify stable/cycle outcomes, exact equality/period/metadata, pre-call budget checks, all calls counted, and cancellation before exposing the endpoint |
| 9 | Final-state HTTP lookup-first persistence | Reuse saved response before engine work; compute/persist only on miss; correct `422` on required over-budget work, no pre-commit failure result, unchanged current state and saved-result reuse after Next/restart |
| 10 | Concurrent first-time final discovery | One durable winner; both requests return its unchanged configuration/metadata despite valid different representatives or a concurrent Next |
| 11 | Close HTTP/resource coverage | Complete the small real-Kestrel group, wide/large boards, payload/ProblemDetails boundaries, overflow and server-observed cancellation. Reuse earlier tests; do not duplicate the whole HTTP suite across hosts |
| 12 | Focused algorithm/codec/resource decisions | Add the temporary benchmark project and only the alternative implementations needed for Gate A. Apply the same test-first/review workflow to candidate code, approve selections, and rerun the functional suite against the selected production implementations |
| 13 | Final submission cleanup and review | After selection approval, review the cleanup as a separate nonbehavioral change; remove rejected candidates, unused codecs and benchmark project/dependencies/references, then build/test the final three-project solution and complete independent review |

**Sequencing defaults:** Establish the upload/reload vertical slice before complex concurrency or alternate-algorithm work. Use explicit provisional, finite test/development limits for this functional work; they are not approved production defaults. Development databases are disposable and are not shipped. Finalize the selected codec/version and operational limits at Gate A before submission. Likewise, correctness tests can use small injected retry/timeout settings without locking production tuning. If a provisional choice blocks correct behavior or reasonable resource use, bring the concrete issue to review; do not add speculative optimizations.

## 4. Focused benchmarks and decision gates

**Mandatory decision evidence:** Correctness comes first. Benchmark only the unresolved selections and operational safeguards; do not block the early vertical slice on alternative implementations or exploratory profiling. Use one available machine in Release, fixed inputs/seeds, and a small repeatable workload set. Report actual hardware/runtime, build settings, workload/density, sample conditions, and measurements; multiple hardware profiles are not required.

- **Gate A — after tested functional primitives/endpoints, before final submission:** Compare fingerprint dictionary versus Brent and coordinate encoding versus packed bitset. Both candidates must pass the same relevant independent correctness fixtures before performance comparison. Count every engine call, including Brent's extra work; allow different valid representatives. Cover known stable/periodic/transient patterns and a small set of empty/sparse/mixed/dense workloads at 1,024×1,024 and a few larger practical sizes. Measure elapsed time, allocations, process peak memory, encoded size, JSON parsing/rendering/payload size, SQLite overhead, and representative request latency. Obtain approval of algorithm, versioned codec, dimensions/cell/body limits, and preview/final-search caps; the documented 10,000 caps remain provisional. No codec/algorithm winner is silently promoted.
- **Gate B — immediately before slice 7:** Use the tested atomic-write primitive for a small real-SQLite contention experiment. Include provider busy waits, implicit transaction commands, verification, timeout granularity, effective total wait, and cancellation. Approve configurable retry/timeout/budget values and separately review the proposed SQLite-contention `409` mapping. No exhaustive tuning matrix or second retry framework is needed.

**Optional unless evidence warrants it:** Extensive size/density/parameter sweeps, multiple hardware/OS profiles, more codecs (including JSON comparison), long stress campaigns, and other speculative optimizations. The focused measurements above remain mandatory; optional work needs a concrete observed problem and developer-approved scope. Do not introduce deferred caching, background work, history, or external services.

Use the temporary benchmark project only while decisions are being evaluated. After approval, remove it and rejected implementations from the submission rather than maintain multiple production strategies. In README, keep a concise table of actual measured conditions/results, selected algorithm/codec and operational settings, tradeoffs, and selection rationale. Preserve evidence and honest limitations without requiring benchmark tooling to build, run, or test the submitted app. Do not invent measurements or put timing/allocation thresholds in ordinary correctness tests.

## 5. Consolidated mandatory acceptance and final review

The groups below consolidate earlier overlapping test lists; they are requirements-to-tests coverage, not a demand for a new test for every sentence. Use parameterized cases and shared small fixtures where useful. Keep the strongest observation at the appropriate layer: pure rules in unit tests, database guarantees in real SQLite tests, and transport enforcement in the small Kestrel group. A broader scenario may cover several requirements when all relevant facts are asserted explicitly; never replace state/call-count assertions with success-status checks.

**Locked retention:** The default remains **24 hours measured from successful transaction commit**, configurable, with original-response replay before expiry and safe reuse at/after expiry. Persist commit-relative expiry metadata; configuration changes affect new records. Transactional cleanup during later keyed requests is sufficient; no background service. Changes to this decision require evidence and explicit developer approval.

| Mandatory test group | Essential assertions |
|---|---|
| Engine and codec | Independently established life-rule, empty/block/blinker/longer-oscillator/transient expectations; finite nonwrapping corners/edges, 1×1/thin/asymmetric shapes, orientation, immutable input, and unchanged dimensions. Selected codec round-trips empty/sparse/dense/large boards and rejects malformed/truncated data with documented version/order/endianness |
| Upload and durable state | Real JSON validation for null/empty/jagged/malformed boards and invalid IDs; approved statuses/DTO/Location behavior; correct initial/current state and generation after reload/restart; no successful write response before commit; no implicit final-state calculation |
| Preview validation and isolation | One consistent committed snapshot during concurrent Next; N=0/positive labels, no writes; missing/empty/malformed/negative/out-of-range/duplicate values (including equal duplicates), numeric parse overflow, and generation-addition overflow before engine work. Proposed `400` details remain for review; N=0 at the maximum generation succeeds |
| Atomic keyed advancement and recovery | State, incremented generation, and optional original-response/key commit together. Inject failure between update and key insertion and during insertion; assert full rollback. Replay after later advancement, confirmed commit plus lost HTTP response, and restart. Verify uncertain outcomes via fresh authoritative reads; unavailable verification or unkeyed ambiguity never causes blind re-advancement. Unexpected real database errors yield sanitized `500`, not disguised conflicts |
| Concurrent retries and retention | Distinct keys reading one generation produce correct consecutive results; prove the loser reloads/recalculates. Same retained key advances once and returns the original response, including known unique-conflict/retry paths; key rechecked before every retry/recalculation and transactionally. Keep basic just-before/at/after commit-relative expiry tests, delayed pre-commit work, and sequential key reuse at/after expiration. Specialized concurrent expired-key reuse and cleanup-race tests are deferred/optional |
| SQLite locking, cleanup and cancellation | Deterministically held/released writer locks exercise success and bounded exhaustion; distinguish ordinary busy, stale-snapshot busy and same-connection locked cases. Unrelated constraints do not replay as duplicates. Dispose failed attempts before retry; test active-transaction/cleanup failure without hiding the original error or reusing damaged resources. Cancel during database work, prove eventual lock release/no further retry and rollback before commit; committed records remain replayable after later cancellation |
| Final search, budget and reuse | Verify stable state or any verified cycle member against independent fixtures (including empty/block stability in one engine call), with exact period and configuration at its claimed generation. Check the budget **before every engine call**, count all calls (including Brent work), prove exact-cap success, and return `422` without the forbidden extra call or saved result when more work is required. If fingerprint detection is selected, forced collisions of unequal states cannot establish a false cycle. No canonical representative or identical algorithm work counts are required. First saved conclusion and all metadata (including original `iterationsUsed`) replay unchanged with zero new calls after Next/restart; concurrent first discoveries return the unique winner even across different valid phases |
| Real Kestrel and resource protection | In Production configuration, test valid UTF-8 bodies at `L-1`, `L`, `L+1` bytes, with known length and HTTP/1.1 chunked/unknown length; use padding to isolate byte limits. Test early application rejection and native Kestrel enforcement independently, with limits set before JSON binding. Check `413`, supported JSON ProblemDetails/content type/body status, and absent/supported/unsupported `Accept`; record any server-level envelope/connection limitation for review. Ensure known-length rejection precedes JSON parsing, streamed rejection permits only bounded prefix processing, no full oversized-grid materialization/domain mapping/persistence, and no size-validation arithmetic overflow. Include very wide single rows and 1,024×1,024 sparse/dense integration boards within/just beyond appropriate limits; keep routine byte-boundary fixtures small and isolate dimension `400` from body-size `413` |
| Expensive-work cancellation and route contract | Cancel actual preview/uncached-final generation work after server entry; verify token observation, stopping within costly calls and between calls, unchanged committed board and no pre-commit final-result write. Client-await cancellation alone is insufficient; disconnect promises no status. Verify the four required application routes, retained final response fields, no history route or public final budget override, and approved errors |

Use separate file-backed SQLite connections, barriers/events and an injected clock. Reuse narrowly scoped SQLite-trigger/test-harness fault injection; do not build a general fault framework. Real SQLite rollback tests, restart/reopen verification, and successful commit followed by HTTP response loss and keyed replay remain mandatory. A simulated error after a real commit tests application recovery, not every possible storage-device failure. Watchdogs may detect hangs; performance latency/memory measurements belong in benchmarks.

**Approved test-scope deferrals:** Specialized concurrent expired-key reuse and cleanup-race tests, and elaborate process-termination/process-kill scenarios around commit, are optional follow-up work rather than submission requirements. Add them only if evidence warrants the effort and the developer approves the scope. This supersedes broader test-depth expectations in the design/checklists; the 24-hour retention rule, basic expiration boundaries, atomic transactions, duplicate-key protection, and required recovery guarantees remain unchanged.

**Submission cleanup acceptance:** After approving Gate A selections, remove rejected algorithms, unused codecs, the entire `GameOfLife.Benchmarks` project, BenchmarkDotNet references/packages, comparison-only tests, and obsolete solution/build/documentation commands. Retain all selected-implementation correctness cases, including relevant collision/call-count tests; removing rejected candidates must not remove required behavioral coverage. The final solution has three projects; retain selected production code, relevant tests, reproducible SQLite setup, existing design/plan documentation, README benchmark evidence, and normal run/test instructions. Ship no generated user database.

**Final verification:** Build and execute the full retained unit/integration suite in Release after cleanup, verify the final project/reference graph has no benchmark dependency, and verify free/self-contained clone/build/run/test instructions. Report actual commands/results and benchmark limitations. Complete an independent PR-review checkpoint against the design, final diff, retained tests, and actual results; fixes are separate approved slices. Do not commit, push, approve, or merge automatically.

**Remaining review items and compatibility:** Numeric operational/retry limits, final algorithm/codec selection, commit-relative timestamp capture before slice 6, the SQLite-contention exhaustion status mapping, duplicate-query/preview-overflow `400` proposals, and the exact tested Kestrel/ProblemDetails envelope remain explicit review items. This sequence changes when provisional choices are finalized, not their approval requirement. The design register's broad benchmark catalogue is represented by the focused mandatory workload set; exhaustive sweeps and extra hardware remain optional. No other approved architecture or deferred-feature boundary changes.

No application code or tests are authorized by this documentation update. Stop for developer review.
