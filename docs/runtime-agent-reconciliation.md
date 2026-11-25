# Runtime-agent reconciliation

The agent still attempts a heartbeat and checks desired routes on each cycle
(15 seconds of delay after processing). The control plane can gate routing
during a deployment; a conflict invalidates the local cache so the next
successful fetch triggers full reconciliation. The worker's deployment lock
and Caddy helper are unchanged.

- Status and logs share one successful Docker discovery per cycle, including
  an empty result. Failed discovery is retried by the next collector operation.
  Containers that appear after discovery are picked up on the next cycle.
- Log reads still overlap by design: Docker and the Caddy journal use the same
  20-second lookback and existing tail and batch limits. The cursor file records
  acknowledgements, not read positions. Unchanged acknowledgements don't
  replace the file; a failed write is retried at the next acknowledgement.
- Status is still collected each cycle. Changed snapshots are sent immediately
  and unchanged ones are refreshed on the first successful cycle at least a
  minute after the last send. Heartbeats are not suppressed.
- New or changed routes trigger full reconciliation, as do the first successful
  fetch after restart or deployment gating, failed installation retries, and
  the first eligible cycle at least a minute after successful installation.
  Unchanged ready certificates are probed on full reconciliation; pending
  ones are retried every eligible cycle without reinstalling. The agent reports
  full reconciliation and readiness changes, and retries unacknowledged
  results with the latest desired version. The minute is a schedule threshold,
  not a wall-clock bound if the control plane or local commands are slow or down.

## Initial synthetic comparison

Baseline: `e8cbb6232bad3d1881b519053b7336d4bdd2aa18` (`main`). Both builds
used .NET SDK 10.0.404, Release, Linux arm64. Three runs per variant, eight
warmup cycles and 240 measured cycles per run, with four Compose containers
and two ready routes. A virtual clock advances 15 seconds per cycle (one
logical hour); network, TLS and API persistence are simulated in-process.
The idle case launches real shell fixtures in place of Docker, systemd and
the helper. The overlap case uses in-process fixtures for those commands.

| Per logical hour | Main | Candidate |
| --- | ---: | ---: |
| Docker `ps` / `inspect` launches or calls | 480 / 480 | 240 / 240 |
| Docker log / journal calls | 960 / 240 | 960 / 240 |
| Heartbeat / desired routing requests | 240 / 240 | 240 / 240 |
| Routing helper calls / TLS probes | 240 / 480 | 60 / 120 |
| Status inserts / routing row updates (modeled) | 240 / 480 | 60 / 120 |
| Status + routing result POST bodies, idle (approx.) | 312 kB | 77 kB |
| Routing request file replacements / bytes written | 240 / 102,000 | 60 / 25,500 |
| Cursor file replacements / bytes written, overlap | 240 / 60,480 | 1 / 252 |

The overlap workload replayed 320 Docker lines and 24 journal lines per
cycle. Both builds read 5,158,800 input bytes and parsed, submitted and received
acknowledgements for 82,560 records (344 distinct modeled inserts). Log POST
bodies totaled 20,345,280 bytes in both builds.

| Measurement, median (min–max) | Main | Candidate |
| --- | ---: | ---: |
| Idle wall time, ms | 1,140 (869–1,374) | 1,202 (623–1,291) |
| Idle managed-process CPU, ms | 843 (747–903) | 579 (531–719) |
| Idle managed allocations, MB | 136.9 (136.8–136.9) | 101.2 (101.0–101.3) |
| Overlap wall time, ms | 554 (553–641) | 471 (460–597) |
| Overlap managed-process CPU, ms | 587 (554–700) | 488 (484–524) |
| Overlap managed allocations, MB | 253.0 (252.9–253.2) | 248.7 (248.3–249.0) |

Idle CPU decreased in the synthetic runs (median 3.51 to 2.41 ms per
cycle), but idle wall-time ranges overlap. These are whole-harness costs,
including the mock API, not Docker daemon or database CPU or physical
disk writes. No change was made to parsing, replay, batch ordering or fairness:
the allocation totals don't identify parser stacks, and a cursor-based read
window would need a separate replay design. Real local Docker/Caddy drift
and TLS recovery timings and PostgreSQL write/latency measurements remain
to be measured. Parser profiles and synthetic burst/churn coverage were
subsequently added in the comparison below.
Synthetic clock tests cover immediate desired-route convergence, readiness
retry on the next cycle, and the one-minute full reconciliation and snapshot
refresh thresholds.

### Reproduce locally

From the repository root with .NET 10 installed:

```bash
set -e
root="$PWD/.local/artifacts/runtime-agent-perf"
git check-ignore "$root/probe"
mkdir -p "$root/main"
git archive e8cbb6232bad3d1881b519053b7336d4bdd2aa18 modules/runtime-agent modules/redaction |
  tar -x -C "$root/main"
dotnet build tools/runtime-agent-benchmark -c Release --artifacts-path "$root/build-main" \
  -p:AgentSourceRoot="$root/main" -p:Baseline=true
dotnet build tools/runtime-agent-benchmark -c Release --artifacts-path "$root/build-candidate"
for run in 1 2 3; do
  for workload in idle overlap; do
    runner=children
    if [ "$workload" = overlap ]; then runner=mock; fi
    for variant in main candidate; do
      scratch=$(mktemp -d "$root/$variant-$workload-$run.XXXXXX")
      dotnet "$root/build-$variant/bin/RuntimeAgentBenchmark/release/RuntimeAgentBenchmark.dll" \
        "$scratch" "$workload" "$runner" > "$scratch.json"
    done
  done
done
```

## Log allocation and burst/churn comparison (#122)

Baseline agent source: `786958673fcda62de3ef904fdd12d7ee8bfb8443`.
Candidate source and shared benchmark:
`5939ae43637c64e1c7b5b0536aa67227b0aecf80`. Both used SDK 10.0.404,
runtime 10.0.15, Release, Linux arm64. Three interleaved runs per variant
and workload, eight warmup cycles followed by 240 measured cycles, four
containers, two routes and 15-second virtual cycles. All commands and HTTP
responses were isolated in-process synthetic fixtures; no Docker daemon,
journal, VPS, provider or hosted API was accessed.

EventPipe allocation stacks were captured **before** editing the parser.
The baseline showed allocations in Docker field splitting, collector
whole-output splitting and LINQ grouping, not just API serialization.
The candidate parses timestamp fields as spans, scans physical/logical
lines without whole-output split arrays, truncates before copying long
logical lines, and keeps an ordered first-distinct batch with a hash set
bounded by the existing 500-record cap. It still reads and parses every
source after the cap. SHA-256 cursor inputs, same-timestamp records,
stdout/stderr identity, whitespace/fallback handling, redaction, the
20-second windows, Docker tail, progress metrics and cursor persistence
are unchanged. No cursor-based read position or fairness policy was added.

The burst fixture supplies 100 lines per container (50 per stream) and
120 journal lines each cycle, half from the previous cycle and half new.
All lines in each half share a timestamp but have distinct messages.
Churn uses the same input, rotates discovery order each cycle and replaces
all container IDs and service generations every four cycles. Overlap keeps
the original 344-record repeated input.

| Measured input / output per run, identical in both variants | Overlap | Burst | Churn |
| --- | ---: | ---: | ---: |
| Input bytes | 5,158,800 | 8,399,660 | 8,399,660 |
| Parsed fixture lines before cap | 82,560 | 124,800 | 124,800 |
| Submitted / acknowledged records | 82,560 / 82,560 | 120,000 / 120,000 | 120,000 / 120,000 |
| Distinct modeled inserts | 344 | 62,640 | 74,440 |
| Maximum submitted batch | 344 | 500 | 500 |
| Cursor replacements / payload bytes | 1 / 252 | 240 / 60,480 | 240 / 2,191,680 |
| Log POST bytes | 20,345,280 | 29,910,900 | 33,606,900 |

Every fixture input line produces one logical parsed record; the parsed
counts are fixture line counts before deduplication/capping, not a new
production telemetry counter. Acknowledged counts include successful
deduplicated replays. Cursor and routing writes count file replacements
and serialized bytes, not physical disk I/O. Churn retains old service
acknowledgements, explaining its growing cursor file; no cleanup policy
was introduced. Routing file writes stayed at 60 / 25,500 bytes per run.

| Managed allocation MB, median (min-max), decimal MB | Baseline | Candidate |
| --- | ---: | ---: |
| Idle mock | 5.335 (5.335-5.335) | 5.368 (5.368-5.368) |
| Overlap | 249.87 (249.50-249.97) | 231.77 (231.77-231.78) |
| Burst | 390.53 (390.52-390.65) | 367.82 (367.43-369.11) |
| Churn | 414.25 (413.89-414.95) | 391.31 (391.04-391.47) |

| CPU / wall ms, median (min-max) | Baseline CPU | Candidate CPU | Baseline wall | Candidate wall |
| --- | ---: | ---: | ---: | ---: |
| Idle mock | 18.93 (18.54-21.79) | 19.99 (19.04-21.82) | 18.07 (16.27-19.02) | 19.07 (16.43-19.35) |
| Overlap | 484.98 (482.80-503.11) | 492.63 (466.12-503.79) | 457.59 (452.41-465.61) | 459.26 (448.08-482.22) |
| Burst | 812.02 (761.16-822.40) | 819.83 (765.72-891.94) | 764.82 (726.54-775.33) | 767.71 (722.69-817.69) |
| Churn | 821.20 (799.48-825.03) | 773.88 (768.68-799.57) | 775.20 (753.61-783.79) | 727.96 (727.54-748.51) |

The measured allocation reduction is 7.24% / 5.81% / 5.54% for
overlap / burst / churn. Idle adds about 33 kB per logical hour rather
than preallocating a maximum-size batch on empty hosts. These are
whole-harness costs including fixture generation and API serialization.
CPU/wall ranges overlap for most cases; this is an allocation improvement,
not a claimed general speedup or real-host result.

One EventPipe capture per variant/workload includes startup and warmup
as well as measured cycles. The allocation report sums GC allocation-tick
weights, not exact object sizes. All six captures had zero missing-stack
bytes. Inclusive frames overlap; the report's allocation buckets assign
each stack once, but JIT inlining and sampling can shift attribution.

| Sampled allocation MB, baseline -> candidate | Overlap | Burst | Churn |
| --- | ---: | ---: | ---: |
| Total | 260.60 -> 241.63 | 406.40 -> 383.45 | 430.46 -> 407.03 |
| Docker fields, excluding logical records/redaction | 24.38 -> 9.29 | 31.31 -> 15.90 | 36.32 -> 15.68 |
| Whole-output collector lines | 13.96 -> 0.11 | 22.80 -> 0 | 23.22 -> 0.21 |
| Grouping | 15.77 -> 0 | 22.27 -> 0 | 21.22 -> 0 |
| Redaction | 54.58 -> 62.40 | 89.86 -> 89.48 | 90.68 -> 92.86 |

Redaction remains a substantial cost and was not changed. Hash-set and
record allocation remains; eliminated stack weights are not all net savings.
Raw JSON, traces and ETLX intermediates are git-ignored under
`.local/artifacts/perf-122/`; this maintained summary and the tools are
the reproducible evidence, not committed generated reports.

### Ordering, progress and recovery evidence

All three runs per variant had identical counts, per-source submissions
and inserts, command/API request counts, cursor write bytes and SHA-256
digests of the complete serialized log request sequence:

| Workload | Log request sequence SHA-256 |
| --- | --- |
| Overlap | `EB0A60E1644DCB1C212F3126DE6834E68E3140EEFCDD9BF4923CD3931571ED60` |
| Burst | `93648CC21D0D3B3D6D10F22DDD30CBB5A7CE63EF204E09CE3E4AD5D6786582A0` |
| Churn | `3535DB04B500C47B3896881DD0F944C561555C222D08CE37712DDB3E98C8AEE4` |

Burst/churn submit all 400 container records and the first 100 of 120
journal records each cycle. Twenty new journal records are deferred
to the next overlapping read (15 virtual seconds), yielding 14,440
distinct journal inserts in 240 cycles; the final 20 new records would
need a subsequent cycle. This preserves existing first-source ordering,
not guaranteed fairness under arbitrary overload. If Docker records
alone fill the cap, later sources can still starve; changing that policy
would require separate replay/ordering design and qualification.

The `verify` mode passes against **both** baseline and candidate. Across
eight cycles per workload it checks exact replayed records/cursors,
stream identity, same-timestamp distinctness, redaction, source ordering,
batch limits, per-cycle insert progress and deferred journal recovery.
It simulates a crash after API acceptance but before saving the cursor:
a new store loads the last persisted state, the same window replays
without new modeled inserts, and the replay acknowledgement is saved.
This is in-process crash/restart modeling, not power-loss qualification.
The affected runtime-agent and runtime API tests pass (42 tests),
including failed cursor-write retry and new whitespace, CRLF, truncation
and first-distinct cap regressions.

**Still open:** the real runtime-host/VPS log run in #122/#247, physical
disk and external process costs, and real crash/fairness qualification.
This source/fixture comparison does not close #122 or qualify the hosted
candidate.

### Reproduce the log comparison

Run from the repository root. The same harness is used for both agent
sources; do **not** set `Baseline=true` here (that switch is for the
older pre-reconciliation comparison).

```bash
set -e
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
root="$PWD/.local/artifacts/runtime-log-122"
git check-ignore "$root/probe"
mkdir -p "$root/source-baseline"
git archive 7869586 modules/runtime-agent modules/redaction |
  tar -x -C "$root/source-baseline"
dotnet build tools/runtime-agent-benchmark -c Release \
  --artifacts-path "$root/build-baseline" \
  -p:AgentSourceRoot="$root/source-baseline"
dotnet build tools/runtime-agent-benchmark -c Release \
  --artifacts-path "$root/build-candidate"
for variant in baseline candidate; do
  scratch=$(mktemp -d "$root/verify-$variant.XXXXXX")
  dotnet "$root/build-$variant/bin/RuntimeAgentBenchmark/release/RuntimeAgentBenchmark.dll" \
    "$scratch" verify mock
done
for run in 1 2 3; do
  for workload in idle overlap burst churn; do
    for variant in baseline candidate; do
      scratch=$(mktemp -d "$root/$variant-$workload-$run.XXXXXX")
      dotnet "$root/build-$variant/bin/RuntimeAgentBenchmark/release/RuntimeAgentBenchmark.dll" \
        "$scratch" "$workload" mock > "$root/$variant-$workload-$run.json"
    done
  done
done
dotnet tool install dotnet-trace --tool-path "$root/tools" --version 9.0.661903
dotnet build tools/runtime-agent-allocation-report -c Release \
  --artifacts-path "$root/report-build"
for variant in baseline candidate; do
  for workload in overlap burst churn; do
    scratch=$(mktemp -d "$root/profile-$variant-$workload.XXXXXX")
    "$root/tools/dotnet-trace" collect \
      --providers Microsoft-Windows-DotNETRuntime:0x1:5 \
      --output "$root/$variant-$workload.nettrace" --show-child-io -- \
      dotnet "$root/build-$variant/bin/RuntimeAgentBenchmark/release/RuntimeAgentBenchmark.dll" \
      "$scratch" "$workload" mock > "$root/profile-$variant-$workload.log"
    dotnet "$root/report-build/bin/RuntimeAgentAllocationReport/release/RuntimeAgentAllocationReport.dll" \
      "$root/$variant-$workload.nettrace" > "$root/$variant-$workload-allocations.json"
  done
done
```

## Cursor-write optimization, protocol fixture, and graceful EventPipe completion (#119, #122, #324)

- **Cursor-write and collection refinements (#122):** `CursorStore` serializes
  sorted canonical JSON in memory before creating a temporary file, compares
  against the last persisted payload to skip byte-identical writes, cleans up
  partial `.tmp` files on write failure while keeping `_dirty` set for retry,
  tracks `WriteCount` / `SkippedWriteCount` / `BytesWritten`, and exposes
  opt-in `PruneInactiveSources` for churn cleanup. `RuntimeCollector` hashes
  cursor lines using stack/pooled buffers (`SHA256.TryHashData`) without heap
  array allocations and skips parsing/hashing lines after the 500-record batch
  cap is reached while still executing later source commands.
- **Reproducible acceptance-protocol fixture (#119):** `tools/runtime-agent-benchmark`
  adds the `protocol` workload (`off`, `on`, and `on-optimized` modes) covering
  30 virtual minutes idle, duplicate replay, 500-record burst, route change,
  failed/recovered HTTPS probe, 409 deployment gate with container rollover,
  and restart with a pending acknowledgement, reporting synthetic collector
  footprint separately from product work.
- **Graceful EventPipe completion and aggregate-only inspection (#324):**
  `Selfrun.RuntimeAgent` handles `SIGTERM`/`SIGINT` by completing the active
  cycle so started `Activity` spans emit stop markers before exit.
  `tools/runtime-agent-benchmark` supports `--start-file` / `--ready-file` / `--stop-file`
  coordination, `EventPipeSessionCoordinator` (`CaptureWhileAliveAsync` and
  `CaptureLaunchedProcessWhileAliveAsync`) stops and drains the EventPipe
  session to its final NetTrace EOF marker (`EndObject` + `NullReference`)
  while the target is still alive, and `RuntimeAgentAllocationReport`
  (`--capture-benchmark` and `--verify-activity`) inspects `.nettrace` files
  strictly under `.local/artifacts/` with aggregate-only JSON output certifying
  clean stream completion, zero lost events, and matched Activity start/stop
  closure.

