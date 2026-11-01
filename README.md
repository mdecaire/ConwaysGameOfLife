# Conway's Game of Life API

C#/.NET 8 Minimal API with local SQLite persistence. Requires the .NET 8 SDK; package restore requires NuGet access. Slices 1-4 are implemented. Slice 4 is ready for developer review; advancement and final-state discovery remain future work.

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

| Method and route | Behavior |
|---|---|
| `POST /api/boards` | Upload a nonempty rectangular boolean grid under `cells`; persist initial/current state at generation zero; return `201` with a unique ID |
| `GET /api/boards/{id}/preview?stepsAhead=N` | Calculate N generations from a consistent current snapshot; return its absolute generation without writes |
| `GET /api/boards/{id}/generations/{generationNumber}` | Reconstruct an absolute generation from the original upload without writes |

Example upload body:

```json
{"cells":[[false,true,false,false],[true,false,false,true]]}
```

Responses contain `boardId`, `generation`, `width`, `height`, and `cells` indexed by row then column. The finite board never wraps. Upload's `Location: /api/boards/{id}/preview?stepsAhead=0` identifies the live current board. `/generations/0` always identifies the original uploaded configuration. Both zero reads perform no engine calls.

Generation quantities use nonnegative 64-bit integers. `stepsAhead` is required. Missing/empty query values, malformed/fractional/overflowing quantities, negative values, invalid IDs, and overflowing current-generation-plus-offset return `400` ProblemDetails. Unknown boards return `404` ProblemDetails. Both GETs propagate request cancellation and preserve all stored values.

There is no approved numeric retrieval computation cap. Tests exercise 10,001 calls on both routes. Large counts can occupy CPU and a request thread until cancellation. A configurable server request timeout is proposed for developer review; no timeout value or new generation limit has been introduced. The final-state search budget belongs to its own slice. Real Kestrel payload-boundary and elaborate disconnect tests remain in Slice 10.

Run regression tests:

```powershell
dotnet test GameOfLife.sln -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
```

On Windows accounts without Event Log write permission, disable that provider for the test process so intentional failed-write tests can exercise exception handling:

```powershell
$env:Logging__EventLog__LogLevel__Default = 'None'
```

Tests use temporary file-backed SQLite with pooling disabled and clean up their own files. Coverage includes durable upload/reopen, independent changing glider phases, current versus initial origin, zero-call reads, exact engine call counts, validation/overflow, snapshot consistency, cancellation, unchanged persistence, and live Location resolution after restart.
