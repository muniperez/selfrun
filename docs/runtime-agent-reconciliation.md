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

## Synthetic comparison

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
and TLS recovery timings, sustained bursts or churn, parser allocation
profiles, and PostgreSQL write/latency measurements remain to be measured.
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
