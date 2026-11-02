# Conway's Game of Life API

C#/.NET 8 Minimal API with local SQLite persistence. Requires the .NET 8 SDK; package restore requires NuGet access. Slices 1-10 are reviewed; Slice 11 performance verification and test execution are complete for developer review. The independent final code review remains separate.

Run from the repository root:

```powershell
dotnet restore GameOfLife.sln
dotnet run --project GameOfLife.Api -- --urls http://localhost:5000
```

SQLite defaults to `boards.db` relative to the process working directory. Startup creates the schema and enables WAL; connections use foreign keys and full synchronization. A generated database is local data and should not be committed.

Slice 4 adds `InitialState`. Schema initialization does not upgrade an older `Boards` table. For a disposable older development database, select a fresh file:

```powershell
$env:ConnectionStrings__Boards = 'Data Source=boards-slice4.db'
dotnet run --project GameOfLife.Api -- --urls http://localhost:5000
```

Retaining non-disposable older data requires an explicit migration policy. Never infer original cells from an advanced current state.

**Request size limit:** The API enforces a configurable maximum HTTP request body size through Kestrel. Requests exceeding this limit are rejected with HTTP 413 (Payload Too Large). For chunked transfers, framing overhead counts toward the limit, so the maximum accepted payload size may vary by transfer encoding.

| Method and route | Behavior |
|---|---|
| `POST /api/boards` | Upload a nonempty rectangular boolean grid under `cells`; persist initial/current state at generation zero; return `201` with a unique ID |
| `POST /api/boards/{id}/next` | Advance current state once, atomically commit state and generation, and return `200`; reload and recalculate on verified generation conflicts |
| `GET /api/boards/{id}/states-ahead?stepsAhead=N` | Get N States Ahead: return the state N generations ahead of the current committed generation without advancing the board |
| `GET /api/boards/{id}/generations/{generationNumber}` | Get absolute generation N: calculate N generations from the initial uploaded board at generation 0, regardless of current progress; no writes |
| `GET /api/boards/{id}/final-state` | Return a saved conclusion, or discover and persist one from current state without advancing the board |

Example upload body:

```json
{"cells":[[false,true,false,false],[true,false,false,true]]}
```

Responses contain `boardId`, `generation`, `width`, `height`, and `cells` indexed by row then column. The finite board never wraps. Upload's `Location: /api/boards/{id}/states-ahead?stepsAhead=0` identifies the live current board. `/generations/0` always identifies the original uploaded configuration. Both zero reads perform no engine calls.

Next calculates outside a short SQLite transaction, then updates current state and generation only when the board ID and expected generation match. A verified generation conflict disposes the attempt, reloads committed state, and recalculates. `Next:MaxAttempts` (environment variable `Next__MaxAttempts`) defaults provisionally to three total attempts and must be positive. Exhausted verified conflicts return `409` ProblemDetails; an absent board returns `404`. Next at the maximum generation returns `400` before calculation. Each successful request returns its own committed state, even if another request has since advanced the board. The original upload remains unchanged.

Connections use private cache. Set a positive whole-second `Default Timeout` in `ConnectionStrings:Boards` to tune provider waiting (provider default: 30 seconds); zero/unlimited waiting is rejected. Ordinary writer contention uses only the provider's finite command waiting, including implicit BEGIN/COMMIT commands. Busy/locked exceptions are database errors and return sanitized `500`, without an application retry or automatic translation to `409`. Only a zero-row conditional update permits an optimistic retry. The finite attempt count bounds the number of command-wait windows; this is not a hard end-to-end request deadline. See PLAN.md for the wait accounting and deferred exploratory cases.

Uncommitted updates roll back. Exceptions, including commit failures or lost acknowledgements, never cause automatic re-advancement. Cancellation is checked before new attempts/calculations and before commit; synchronous provider waiting returns control before cancellation can be observed. After successful persistence, Next and Final State map and return their results without further application cancellation checks; cancellation or response loss cannot undo a confirmed commit. Each Next request advances separately, including a repeated client request.

Generation quantities use nonnegative 64-bit integers. `stepsAhead` is required. Missing/empty query values, malformed/fractional/overflowing quantities, negative values, invalid IDs, and overflowing current-generation-plus-offset return `400` ProblemDetails. Unknown boards return `404` ProblemDetails. Both GETs propagate request cancellation and preserve all stored values.

There is no approved numeric retrieval computation cap. Tests exercise 10,001 calls on both routes. Large counts can occupy CPU and a request thread until cancellation. A configurable server request timeout is proposed for developer review; no timeout value or new generation limit has been introduced. The domain final-state search defaults provisionally to 10,000 actual engine calls, with an internal-only override. It returns Stable, Cycle, or Inconclusive with exact generation/period/call metadata, verifying living-cell equality after fingerprint lookup. This limit applies only to final-state discovery. Real Kestrel tests cover request-size boundaries and server-observed disconnect cancellation for both retrieval routes and uncached Final State. Disconnected clients are not guaranteed an HTTP error response.

Request Final State with `GET http://localhost:5000/api/boards/{id}/final-state` (replace `{id}` with the uploaded ID). A successful `200` response contains the common board fields plus `startingGeneration`, `conclusionKind`, `cycleLength`, and `iterationsUsed`. `Stable` means the verified configuration repeats every generation (`cycleLength: 1`); `Cycle` means the returned configuration belongs to a verified repeating orbit with a longer period. `generation` labels that representative on the original discovery trajectory. For example, a block discovered from generation 5 returns generation 6, startingGeneration 5, conclusionKind `Stable`, cycleLength 1, and iterationsUsed 1; a blinker from generation 5 returns its repeated phase at generation 7 with kind `Cycle`, period 2, and two iterations.

Final State checks SQLite before any generation calculations. Startup adds `FinalStateResults` alongside an existing compatible `Boards` table. Successful conclusions use the existing coordinate codec and are committed atomically, with one unique result per board. Later requests return the original configuration and metadata with zero new engine calls, including after application restart and Next. Saved `iterationsUsed` remains the original discovery count, and saved `generation` may be less than the board's later current generation. Upload, Next, preview, and absolute retrieval do not discover or populate final results.

An uncached search uses the provisional internal limit of 10,000 actual engine calls. If no conclusion can be proved within that limit, Final State returns `422` ProblemDetails and saves nothing. A supplied `maxIterations` query parameter (including empty values) returns `400`; unknown boards return `404`. Unexpected persistence failures use sanitized `500` handling. Failed discoveries and cancellation observed before commit save no result and leave current progress unchanged. Cancellation after a confirmed commit preserves the saved conclusion. Tests cover simultaneous discovery and interleavings with Next.

Before running tests on Windows accounts without Event Log write permission, disable that provider for the test process so intentional failed-write tests can exercise exception handling:

```powershell
$env:Logging__EventLog__LogLevel__Default = 'None'
```

Run regression tests:

```powershell
dotnet test GameOfLife.sln -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
```

Tests use temporary file-backed SQLite with pooling disabled and clean up their own files. Coverage includes durable upload/reopen, independent changing glider phases, current versus initial origin, zero-call reads, exact engine call counts, validation/overflow, snapshot consistency, cancellation, unchanged persistence, and live Location resolution after restart.

Release measurements on a Windows 10 / i7-4790K machine with .NET 8.0.33 and server GC gave median one-generation times of 11 ms for a sparse 1,024 × 1,024 board, 758 ms at approximately 25% density, and 1,029 ms when full. A sparse 2,048 × 2,048 board took 13 ms; 1,024 × 1,024 is not a size ceiling. Populated calculations allocated approximately 207–259 MiB per call cumulatively. Full-board JSON decoding and SQLite reconstruction were also material costs; all settings remain unchanged pending review.

Final Debug and Release runs each passed 53 unit and 103 integration tests with no skips. An earlier Release run hit one nonreproduced Windows file-watcher exception during Kestrel fixture teardown; its six-case group and the complete Release rerun passed. See [Slice 11 performance verification](PERFORMANCE.md) for all samples, memory definitions, persistence/HTTP observations, exact commands, the NuGet audit network limitation, and operational review items. These local serial observations establish no concurrent-load or arbitrary-generation-count guarantee.
