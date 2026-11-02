# Slice 11 performance verification

Measured November 1, 2026, against production code at commit `0bcdb81`. Slices 1–10 were already reviewed. This slice measures the existing implementation and executes the existing tests; the final independent repository/code/test-design review remains separate.

## Environment and method

- Windows 10 x64, build 19045; Intel Core i7-4790K CPU @ 4.00 GHz; eight logical processors visible to .NET. Runtime-reported available memory: 34,308,210,688 bytes (31.95 GiB), not a measurement of free memory. WMI/CIM hardware queries were denied; the CPU name came from the registry.
- SDK 10.0.403, MSBuild 18.9.15; actual target/runtime **.NET 8.0.33** and ASP.NET Core 8.0.33, x64. Microsoft.Data.Sqlite 8.0.22; native SQLite reported 3.41.2.
- Release builds, server GC matching the API's generated runtime configuration; normal runtime tiering settings. No debugger, new package, benchmark framework, competing implementation, or timing assertions. An initial workstation-GC exploration was superseded by the server-GC measurements below.
- A temporary console harness referenced `GameOfLife.Api` and called the real public engine, discovery, codec, and store. It was outside the solution and was removed after measurements, together with its outputs and temporary databases. The solution retains its three projects.
- Every table contains three sequential observations after one unmeasured workload-specific warm-up. The process retained the starting fixtures. Setup, board construction, schema initialization, console output, and explicit pre-sample GC were excluded. Test execution did not overlap measurements. These are local sanity checks, without controlled system load, disk-cache flushing, statistical confidence intervals, or service-level thresholds.
- Times use `Stopwatch` (10,000,000 ticks/second). All three samples appear in execution order, with their median. Warm-up does not eliminate tiering/scheduling effects: for example, moderate-board encode varied from 34 to 128 ms. Sub-millisecond rows include harness/JIT noise and should not be treated as precise latency predictions.
- **Allocated MiB** is the median delta from `GC.GetTotalAllocatedBytes(true)` across the measured action, including small measurement/result-object overhead. It is cumulative managed allocation, **not** live memory, peak memory, process memory, or native SQLite allocation. MiB = 1,048,576 bytes.
- **End heap growth** is `GC.GetTotalMemory(false)` after minus before an action, with no post-action collection. It includes uncollected garbage, depends on collections during the action, and does not isolate retained dictionary history or peaks. Explicit GC before each sample is outside the timer and does not reproduce normal request GC scheduling.
- Generation and discovery samples used a 15-second cancellation watchdog; none fired. Dense workloads calculated only one generation per sample. No full-budget populated-million-cell discovery or concurrency/load test was attempted.

## Generation engine

Random fixtures use a fresh `Random(1101)` per board, row-major traversal, and `NextDouble() < density`, with target densities 0.25, 0.001, 0.25, 1.0, and 0.001 respectively. Actual counts follow. Each sample calls `CalculateNextGeneration` **once from the same original state**; the three samples are not a three-generation trajectory.

| Starting dimensions | Living cells (actual density) | Calls/sample | Times ms (three samples) | Median ms | Allocated MiB | Output living cells |
|---|---:|---:|---|---:|---:|---:|
| 32 × 32 | 248 (24.22%) | 1 | 1.437, 1.810, 1.355 | 1.437 | 0.223 | 250 |
| 1,024 × 1,024 sparse | 1,085 (0.1035%) | 1 | 11.193, 13.714, 9.859 | 11.193 | 1.977 | 0 |
| 1,024 × 1,024 moderate | 261,848 (24.97%) | 1 | 769.237, 757.971, 754.272 | 757.971 | 207.021 | 298,123 |
| 1,024 × 1,024 full | 1,048,576 (100%) | 1 | 1,029.116, 1,055.144, 1,016.582 | 1,029.116 | 259.398 | 4 |
| 2,048 × 2,048 sparse | 4,173 (0.0995%) | 1 | 12.166, 13.468, 13.070 | 13.070 | 7.909 | 0 |

End heap growth was about 2.0 MiB for baseline sparse, 65.3–66.5 MiB for moderate, 39.6–40.7 MiB for full, and 7.9 MiB for larger sparse. The full board's smaller ending heap does not contradict its higher cumulative allocation: GC ran during calculation and the output contains only four cells.

For a small number of generations these results appear reasonable for the exercise on this machine. The larger sparse board completed comfortably; **1,024 × 1,024 is not a maximum**. Low-density random fixtures die immediately, so they establish sparse starting-state cost, not long-lived sparse simulation behavior. Populated boards demonstrate meaningful CPU and allocation costs. Candidate sets, neighbor enumeration/LINQ, and new output sets are likely contributors from the implementation; no allocation profiler was used to assign their individual shares. These measurements do not establish sustained throughput or safe concurrent-request capacity.

## Dictionary-based final-state discovery

Each sample starts at generation zero, uses the real default 10,000-call budget, and wraps the real engine with a call counter. The counter matched `IterationsUsed` in every observation. All successful generation labels equal the calls below because the starting generation is zero. This measures domain discovery only, excluding persistence, response mapping, and HTTP.

| Starting fixture | Outcome / period | Actual calls | Times ms (three samples) | Median ms | Allocated MiB | End heap growth MiB |
|---|---|---:|---|---:|---:|---:|
| Empty 1,024 × 1,024; 0 cells | Stable / 1 | 1 | 0.392, 0.020, 0.029 | 0.029 | 0.002 | 0.016 |
| Block 1,024 × 1,024; 4 cells | Stable / 1 | 1 | 0.051, 0.046, 0.062 | 0.051 | 0.005 | 0.016 |
| Blinker 32 × 32; 3 cells | Cycle / 2 | 2 | 0.064, 0.059, 0.054 | 0.059 | 0.008 | 0.016 |
| Padded pulsar 17 × 17; 48 cells | Cycle / 3 | 3 | 0.373, 0.394, 0.402 | 0.394 | 0.119 | 0.133 |
| R-pentomino 128 × 128; 5 cells | Cycle / 2; 109 result cells | 1,105 | 301.763, 287.254, 279.711 | 287.254 | 144.097 | 31.9–32.7 |
| Glider 3,005 × 3,005; 5 cells | Inconclusive | 10,000 | 130.127, 131.999, 130.305 | 130.305 | 53.110 | 17.0–17.6 |

The block uses `(10,10), (11,10), (10,11), (11,11)`; the blinker uses `(10,10), (11,10), (12,10)`. The pulsar is the existing padded fixture in `FinalStateDiscoveryTests`. The R-pentomino starts at `(65,64), (66,64), (64,65), (65,65), (65,66)`. The glider uses the existing default-budget test coordinates `(2,1), (3,2), (1,3), (2,3), (3,3)`, staying clear of the boundary through 10,000 calls. It has not repeated within this budget; this does not mean it can never converge on a finite board.

No significant discovery CPU or memory failure appeared on these fixtures. The transient's 144 MiB cumulative allocation is substantial, but is not 144 MiB of retained states. The existing dictionary stores all distinct observed states until completion; neither the call limit nor these sparse examples establishes a memory bound for heavily populated searches. Leave the provisional 10,000-call value unchanged. The observations do not justify a different cycle detector, fingerprint, or cache.

## Coordinate JSON and SQLite

All fixtures are the same 1,024 × 1,024 sparse/moderate/full boards above. `BoardStateCodec.Encode` returns a JSON string; decode includes reconstructing the living-cell `HashSet`. Every coordinate JSON character here is ASCII, so the UTF-8 byte counts equal string character counts; managed UTF-16 string storage is different.

| Board | Coordinate JSON UTF-8 bytes | Encode ms (samples → median) | Encode allocated MiB | Decode ms (samples → median) | Decode allocated MiB |
|---|---:|---|---:|---|---:|
| Sparse | 19,350 | 0.752, 0.691, 0.697 → **0.697** | 0.063 | 1.419, 1.340, 0.920 → **1.340** | 0.202 |
| Moderate | 4,669,553 | 127.953, 37.714, 33.899 → **37.714** | 14.901 | 101.644, 99.759, 85.801 → **99.759** | 44.374 |
| Full | 18,698,241 | 137.691, 139.696, 213.014 → **139.696** | 59.665 | 431.030, 414.475, 439.029 → **431.030** | 181.226 |

Production store measurements use separate logical connections per operation with the provider's default pooling enabled, private cache, WAL, foreign keys, and `synchronous=FULL` (observed value 2). Default provider timeout remains 30 seconds. Each workload used a new local file-backed database, initialized before timing. Reads are warm/cache-eligible; physical storage type and flush latency were not separately characterized.

`Insert` includes codec encoding and the real atomic insert of **both** identical initial/current states. `Find` includes provider string materialization, decoding, and state reconstruction. Warm-up round trips were checked against the original coordinates outside timing. No generation calculation is included.

| Board | Store upload/insert ms (samples → median) | Insert allocated MiB | Store read/reconstruct ms (samples → median) | Read allocated MiB |
|---|---|---:|---|---:|
| Sparse | 2.511, 2.538, 2.577 → **2.538** | 0.084 | 1.582, 1.334, 1.723 → **1.582** | 0.241 |
| Moderate | 127.250, 137.791, 173.983 → **137.791** | 19.357 | 140.208, 113.926, 120.901 → **120.901** | 53.282 |
| Full | 479.345, 504.344, 508.262 → **504.344** | 77.500 | 458.641, 531.095, 492.318 → **492.318** | 216.892 |

To separate codec work practically, the temporary harness also executed the same parameterized insert/read SQL and connection PRAGMAs with pre-encoded JSON or a returned string. This is a stage observation using the existing provider/schema, not an alternative persistence implementation. It still includes logical connection opening/configuration, SQL/provider work, string/UTF-8 conversion, and write commit. It is not isolated native SQLite execution time. The samples below cannot be subtracted from independently timed store samples to obtain exact codec cost.

| Board | Pre-encoded insert ms (samples → median) | Insert allocated MiB | Read text without decode ms (samples → median) | Read allocated MiB |
|---|---|---:|---|---:|
| Sparse | 2.313, 1.967, 1.891 → **1.967** | 0.021 | 0.301, 0.151, 0.161 → **0.161** | 0.040 |
| Moderate | 88.953, 89.430, 93.348 → **89.430** | 4.456 | 13.987, 13.983, 24.053 → **13.987** | 8.909 |
| Full | 379.478, 393.603, 331.453 → **379.478** | 17.835 | 50.819, 61.477, 61.775 → **61.477** | 35.667 |

After eight inserts per file (two warm-ups, three production inserts, three pre-encoded inserts) and a checkpoint, database file sizes were 331,776 / 74,809,344 / 299,487,232 bytes for sparse/moderate/full. These include schema/pages and **eight rows each**, with two state strings per row; they are not single-board sizes or disk-write byte counts.

SQLite completed these local serial operations reasonably for the exercise. Dense coordinate JSON is a meaningful cost: standalone full-board decoding took about 431 ms and allocated 181 MiB, versus about 61 ms for fetching the stored text. It is the dominant measured stage of dense reconstruction, although exact attribution requires profiling. The dense JSON also expands to about 17.8 MiB per stored state, duplicated initially by the approved schema. This does not demonstrate an unacceptable application failure or justify changing the codec without approval. Keep JSON and SQLite unchanged; cold-disk, sustained-write, contention, crash-durability, and multi-client throughput conclusions cannot be drawn from these timings.

## HTTP observations

The harness launched the **built production API entry point** in a separate Release process, Production environment, on ephemeral loopback HTTP with a fresh SQLite file. Startup, schema initialization, request-body construction, and relevant endpoint warm-ups were excluded. The normal Kestrel body-size setting was left unchanged. There was one caller; no TLS, compression, reverse proxy, or external network.

Upload timing includes sending prebuilt UTF-8 JSON, server binding/mapping/encoding/commit, the echoed response, full response buffering, and client extraction of the ID. Preview includes the SQLite read/reconstruction, response-grid mapping, optional engine call, server JSON, loopback transfer, and full client buffering; it excludes client JSON deserialization. The two stages therefore have slightly different client work. All requests succeeded. No per-request server allocation metric was collected.

| Board | Upload ms (samples → median) | Preview 0 ms (samples → median) | Preview 1 ms (samples → median) |
|---|---|---|---|
| Sparse | 223.554, 239.948, 262.752 → **239.948** | 46.234, 48.616, 46.628 → **46.628** | 65.588, 57.832, 54.523 → **57.832** |
| Moderate | 286.462, 234.783, 281.730 → **281.730** | 120.785, 131.500, 119.522 → **120.785** | 885.306, 887.544, 891.372 → **887.544** |

Sparse upload requests were 6,292,430 bytes; current responses were 6,292,521 bytes. Moderate requests were 6,031,667 bytes; current responses were 6,031,758 bytes. Preview-one responses were 6,293,606 bytes sparse (now empty) and 5,995,483 bytes moderate. The rectangular boolean API still transmits every dead cell: tiny coordinate persistence does not imply a tiny HTTP payload. Sparse zero-preview's approximately 47 ms versus approximately 1.6 ms store read illustrates the combined mapping/JSON/transport/client overhead; it does not isolate serialization alone. Moderate preview-one was approximately 888 ms versus approximately 758 ms engine-only in another process.

After all HTTP observations, the API process reported 258.3 MiB current and 322.2 MiB peak working set through `Process.WorkingSet64`/`PeakWorkingSet64`. This is whole-process resident memory across startup, warm-ups, and all requests; it includes runtime/native memory, excludes the client process, and is not a per-request allocation or isolated peak for any board.

## Operational assessment and review items

| Existing setting/behavior | Assessment from this work |
|---|---|
| Final-state default: 10,000 actual engine calls | The small moving fixture exhausted in approximately 130 ms; the transient concluded in approximately 287 ms. Retain provisionally. Calls do not cap work per call or dictionary bytes; populated full-budget discovery remains unmeasured. The override is internal code only, with no public query budget. |
| SQLite finite timeout | Retain positive connection-string `Default Timeout`, provider default 30 seconds. Uncontended timings do not tune lock waiting. Existing regression coverage exercises bounded contention; synchronous provider waiting can delay cancellation. The documented conservative command-wait accounting `(6 × MaxAttempts + 2) × timeout` gives 600 seconds at defaults, with polling overshoot; this is not a measured latency or a hard end-to-end deadline. |
| Next optimistic attempts: 3 total | Retain. Serial performance measurements cannot tune retry success under concurrency. The full suite retains existing authoritative-reload and conflict tests. SQLite busy/locked still uses sanitized 500, not an unapproved 409 mapping. |
| Kestrel body-size safeguard | Retain the existing framework/configuration setting; `Program.cs` introduces no numeric override. Approximately 6 MiB sample uploads succeeded, and the existing real-Kestrel suite validates configured limits/framing. A request-byte bound does not bound domain allocations, response size, or computation. No new dimension/cell-count ceiling is proposed from this sweep. |
| Preview/absolute retrieval: no numeric computation cap or application request timeout | A valid moderate-board generation actually consumed approximately 758 ms and 207 MiB cumulative allocation. Code performs every requested generation, so large counts can monopolize CPU/request threads; concurrent cost and longer trajectories were not measured. Cancellation support remains essential. This supports retaining the previously documented server-controlled request-timeout proposal for developer review, rather than silently implementing it or inventing a numeric generation cap. |

No automatic optimization or operational setting change was made. No measured operation failed or hit its watchdog. The exercise's representative bounded calculations appear workable; this is not certification for arbitrary board density, generation counts, or concurrent callers.

The clearest demonstrated resource concern is allocation churn for populated generation/reconstruction. If the developer wants a correction, first profile one such operation; a small engine candidate is replacing iterator/LINQ neighbor counting with direct counting inside the existing engine, preserving the approved representation and rules. Its benefit has not been measured, and it would add imperative loop logic, so it requires separate approval and correctness verification. A server-controlled timeout is the smaller operational containment already proposed: it could bound cumulative cancellable computation, but may reject legitimate long calculations and cannot immediately interrupt a synchronous SQLite wait. No timeout value is selected here. These are review proposals, not prerequisites inferred for a codec/algorithm rewrite.

## Final verification and handoff

No production behavior changed, so no new RED/GREEN cycle or tests were manufactured for this measurement slice. Existing tests verify all five routes, persistence/reopen, exact engine-call accounting, cancellation, transport boundaries, and concurrency behavior. The temporary harness adds observations, not correctness or timing assertions to those suites.

Executed from the repository root, with the documented Windows Event Log test-process override:

```powershell
$env:Logging__EventLog__LogLevel__Default = 'None'
dotnet build GameOfLife.sln --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
dotnet test GameOfLife.sln -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
dotnet build GameOfLife.sln -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
dotnet test GameOfLife.sln -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
```

| Execution | Unit passed / failed / skipped | Integration passed / failed / skipped |
|---|---|---|
| Complete Debug | 53 / 0 / 0 | 103 / 0 / 0 |
| First complete Release | 53 / 0 / 0 | 102 / 1 / 0 |
| Complete Release rerun | 53 / 0 / 0 | 103 / 0 / 0 |

Both final solution builds succeeded with zero errors and one NU1900 warning each. The initial pre-measurement Release build had zero warnings/errors. Building the temporary harness restored from existing packages, but NuGet's vulnerability-data fetch could not reach `https://api.nuget.org/v3/index.json`; the API's cached restore warning then appeared on subsequent no-restore builds/tests. No package changes were made, and no claim of a successful online vulnerability audit is made.

The first Release run failed `KestrelResourceTests.ChunkedLimitIncludesTheFramesActuallyWritten(chunkSize: 256, allowance: 0)` during fixture teardown. The stack was `FileSystemWatcher.StopRaisingEvents` → `PhysicalFileProvider.Dispose` → host/WebApplicationFactory disposal → `KestrelFixture.DisposeAsync` line 83, throwing `NullReferenceException`. The HTTP assertions were not the reported failure. The cause was not established; this is a nonreproduced teardown failure, not proof that the suite is reliably free of intermittent failures. No production or fixture changes were made. Before the full Release rerun, this focused command passed all six cases with zero failures/skips:

```powershell
dotnet test GameOfLife.IntegrationTests -c Release --no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal --filter FullyQualifiedName~ChunkedLimitIncludesTheFramesActuallyWritten
```

Measurement commands were the following temporary-only commands; the project was never added to the solution and is no longer retained:

```powershell
dotnet build .slice11-temp/Measure.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
dotnet build .slice11-temp/Measure.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
dotnet .slice11-temp/bin/Release/net8.0/Measure.dll compute
dotnet .slice11-temp/bin/Release/net8.0/Measure.dll storage
dotnet .slice11-temp/bin/Release/net8.0/Measure.dll http
git diff --check
```

The second harness build aligned server GC after the initial exploration; tables report only that configuration. Whitespace validation passed. Final repository changes are `PERFORMANCE.md`, the README summary/link, and PLAN status/handoff. No generated database, measurement executable/project, dependency, or test change remains. No commit or push was performed.

**Independent reviewer action:** Inspect the lifecycle in `GameOfLife.IntegrationTests/KestrelFixture.cs`, including ownership, shutdown/disposal order, synchronous versus asynchronous disposal paths, and file-provider lifetime across the Kestrel host, bootstrap host, and `WebApplicationFactory`. The passing reruns do not close this item: an intermittent teardown failure can affect a reviewer's machine, even though this observation does not establish a production bug. If a fixture lifecycle defect is confirmed, fix it simply within the existing fixture and tests; do not introduce new testing infrastructure. No lifecycle cause has been established or correction implemented here.

Slice 11 performance verification and test execution are complete for developer review. Remaining review items are the Kestrel fixture lifecycle investigation above, provisional operational values and the timeout proposal, and the separately requested independent final review. No independent review or additional implementation was performed in this slice.

## Follow-up optimization: direct neighbor counting

After the independent review, `GenerationEngine.cs` replaced the per-candidate LINQ live-neighbor count with direct iteration over the eight neighboring offsets, skipping the center and retaining the existing bounds check. Candidate collection, data structures, public contracts, Life rules, input immutability, and cancellation checks were unchanged.

This is a new paired before/after comparison, measured immediately around that change. The preceding Slice 11 results remain the historical baseline; the percentages below use the newly measured **Before** rows, not the original Slice 11 timings.

Environment: Windows 10 x64 build 19045, SDK 10.0.403, .NET 8.0.33, eight logical processors, Release builds with server GC and normal runtime tiering. The same temporary console harness referenced the real API engine before and after the change, outside the solution. Each workload used a fresh `Random(1101)`, row-major construction, and `NextDouble() < density`: moderate had 261,848 living cells (24.97%); full had 1,048,576 (100%). One unmeasured warm-up preceded three sequential single-generation samples from the same original state. Setup, pre-sample GC, checksum verification, and console output were excluded; tests did not overlap measurements. Each calculation had a 15-second cancellation watchdog; none fired. The harness was removed afterward.

| 1,024 x 1,024 board | Version | Times ms (three samples) | Median ms | Median time change | Allocated MiB (median) | Allocation change |
|---|---|---|---:|---:|---:|---:|
| Moderate | Before | 878.330, 833.409, 890.119 | 878.330 | — | 207.020 | — |
| Moderate | After | 755.657, 729.780, 736.820 | 736.820 | -16.1% | 81.319 | -60.7% |
| Full | Before | 1,088.144, 1,058.809, 1,260.033 | 1,088.144 | — | 259.398 | — |
| Full | After | 1,290.420, 1,085.934, 1,146.992 | 1,146.992 | +5.4% | 123.393 | -52.4% |

Times use `Stopwatch`; allocations are median deltas from `GC.GetTotalAllocatedBytes(true)`, with MiB = 1,048,576 bytes. These are **cumulative managed allocations, not peak or retained memory**. Moderate-board median execution time improved by 16.1%. Full-board median time increased by 5.4%, with overlapping sample ranges; these local observations do not demonstrate a reliable full-board timing improvement or establish sustained throughput.

Output SHA-256 checksums matched before and after for every sample (298,123 moderate output cells; four full-board output cells), and inputs remained unchanged. Existing targeted Release engine/discovery tests passed all 47 cases before and after. Following the change, complete Debug and Release suites each passed **53 unit + 103 integration tests**, with no failures or skips, using the documented Event Log override and serial/no-shared-compilation commands. No new regression tests were needed; `git diff --check` passed.

The small optimization was retained because it removes avoidable iterator/delegate allocations with straightforward loops, reducing cumulative allocations by 60.7% and 52.4% without expanding scope or weakening correctness coverage. Retention is justified by the allocation reduction, not a claimed full-board speedup.
