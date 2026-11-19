# Worker claim polling and step cost

The deployment worker polls PostgreSQL every `Worker:PollingInterval` (default
2 s). Each idle loop runs five commands:

1. The authority check (`maintenance_authority`).
2. The abandoned-local-build scan.
3. The stale-ownership scan.
4. One claim transaction: `maintenance_authority FOR SHARE`, then the
   `FOR UPDATE OF d, a SKIP LOCKED` claim, then rollback.

With `OperatorDiagnostics:Enabled=true` the worker also emits these signals on
the `Minicloud.Operator` meter and activity source. No deployment, app or host
identifiers are tagged.

| Signal | Tags |
| --- | --- |
| `minicloud.worker.claims` | `outcome` (`claimed`, `empty`, `refused`) |
| `minicloud.worker.queue.wait` (s) | none. Claim time minus the durable queued timestamp, the same value as the `queue_wait` event. |
| `minicloud.worker.step.duration` (s) | `operation` (`resolve-provision`, `dns-setup`, `ssh-deploy`, `health-finalize`, `rollback`), `outcome` |
| `worker.deployment` span | Root span that starts at the durable claim time (`ownership_acquired_at`) |
| `worker.step` span | One child per step. DNS and SSH run concurrently as siblings with separate intervals, so do not add their durations to get wall time. |

## Local benchmark

`tools/worker-queue-benchmark` runs the real `PostgresDeploymentQueueConsumer`
against a throwaway database on any local PostgreSQL. It uses a fake pipeline
executor that waits `WORKER_BENCH_STEP_MS`, then completes the deployment the
way the real terminal transition does. Each run:

1. Creates and migrates a fresh database.
2. Seeds synthetic history: 200 apps and 2,000 terminal deployments.
3. Warms the worker for 10 s.
4. Measures, then drops the database.

```sh
dotnet build tools/worker-queue-benchmark -c Release --artifacts-path .local/artifacts/worker-bench/branch
WORKER_BENCH_POSTGRES="Host=127.0.0.1;Port=5432;Username=postgres;Database=postgres" \
WORKER_BENCH_PG_PID=<postmaster pid, optional> \
  dotnet .local/artifacts/worker-bench/branch/bin/WorkerQueueBenchmark/release/WorkerQueueBenchmark.dll \
  > .local/artifacts/worker-bench/results.jsonl
```

| Scenario | Workload |
| --- | --- |
| `idle` | Empty queue for `WORKER_BENCH_IDLE_SECONDS` (120), extrapolated per hour |
| `latency` | `WORKER_BENCH_DEPLOYMENTS` (40) deployments enqueued over 8 apps, with seeded exponential gaps (mean 1.5 s) and a 250 ms fake pipeline |

Each scenario prints one JSON line with these fields:

- `commands_by_kind`: client commands by kind, from an EF interceptor.
- `client_transactions`: transactions opened by the worker.
- `pg_xacts`, `pg_tuples_read`: deltas from `pg_stat_database`.
- `pg_cpu_ms`: Postgres CPU from `/proc`, when the postmaster PID is given.
- `worker_cpu_ms`, `worker_alloc_mb`: worker CPU and allocated bytes.
- `log_lines`: log lines at Information and above.
- `queue_wait_ms`: queue-wait percentiles from the durable `queue_wait` events.
- `claim_to_terminal_ms`: time from claim to terminal state.
- `claims_per_deployment`: claims per deployment, which counts retries.

`WORKER_BENCH_LOG=console` adds the default console logger, which writes to
stderr. `WORKER_BENCH_EF_COMMAND_LOG=<level>` sets the EF command log level.

### Results

Each cell shows the median, with the min–max across runs in parentheses.

- Runs: 3 interleaved Release runs per build.
- Environment: .NET 10.0.15 and PostgreSQL 17.5 on Ubuntu 26.04 arm64, 6 shared
  CPUs, default `PollingInterval` of 2 s and `MaxConcurrentDeployments` of 1.
- Builds: "main" is `origin/main`; "This change" adds the signals above.

| Workload | Metric | main | This change |
| --- | --- | ---: | ---: |
| Idle, per hour | Client commands | 9,000 | 9,000 |
| | Claim transactions (all empty) | 1,800 | 1,800 |
| | Postgres transactions (`pg_stat_database`) | 14,730 (14,729–14,730) | 14,730 (14,729–15,029) |
| | Postgres CPU | 12.3 s (7.5–13.5) | 12.9 s (12.6–13.2) |
| | Worker CPU | 49.8 s (35.5–52.0) | 50.4 s (49.6–51.5) |
| | Worker allocation | 811 MB (808–815) | 809 MB (806–809) |
| | Log lines | 9,030 | 9,030 |
| 40 deployments | Enqueue-to-claim p50 | 1,246 ms (925–1,290) | 1,031 ms (1,013–1,236) |
| | Enqueue-to-claim p95 | 1,979 ms (1,916–1,999) | 1,967 ms (1,925–1,997) |
| | Claim-to-terminal p50 (250 ms fake pipeline) | 259 ms (256–259) | 258 ms (256–259) |
| | Claims per deployment (retries) | 1.0 | 1.0 |
| | Client commands per deployment | 12.4 | 12.4 |
| | Worker CPU per deployment | 67.3 ms (64.7–67.5) | 66.5 ms (66.5–71.0) |
| | Postgres CPU per deployment | 13.0 ms (11.8–13.8) | 14.2 ms (12.8–14.8) |

Per-deployment values include the idle polls during the roughly 48 s window.
The latency run's 616 log lines are also mostly EF command logs.

All idle queries use indexes, take under 0.1 ms and touch a few buffers each
(checked with `auto_explain`). One idle run with 10× the history (20,000
deployments, 2,000 apps) gave the same 9,000 commands per hour, 188,418 tuples
read (vs 199,410), 12.9 s Postgres CPU and 48.1 s worker CPU. Idle cost does
not grow with history.

**Decision: no polling change.** Idle polling costs about 1.4% of one core in
the worker and about 0.35% in PostgreSQL. Empty claim transactions are 1,800
of the 9,000 idle commands per hour, so they do not dominate. A capped idle
backoff would raise enqueue-to-claim p95, now bounded by the 2 s interval, in
exchange for that small saving. Claims, leases, cancellation, fencing and
recovery are unchanged.

**Logging finding.** EF Core logs every command at Information, and the worker
sets no filter for it. Idle runs with the real console logger (3 runs each)
compared `Microsoft.EntityFrameworkCore.Database.Command` at Information with
the same category at Warning:

- Log lines: 9,030 vs 30 per hour.
- Message text: 15.1 vs 0 MiB per hour.
- Worker CPU: 54.2 (53.3–54.5) vs 50.2 (49.5–51.2) s per hour.
- Allocation: 997 (996–997) vs 770 (770–772) MB per hour.

Operators can set `Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Warning`.
Command errors are still logged at Error.

### Not measured locally

These need a VPS and live providers:

- Time to ready, CPU per deployment and retries through the real provider, DNS,
  SSH and health steps.
- Rollback cost.
- Lease renewal under network latency.
- Postgres on a separate host.

Collect them with the step spans and `minicloud.worker.step.duration` above.
