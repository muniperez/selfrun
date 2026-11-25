# Apps data fetch performance

The canonical `GET /api/apps?organizationId=...` handler returns application
metadata, environment summaries, active domains, runtime observations, and
placement/SSH lock state. Deployment history is not part of the response.

## Authenticated HTTP benchmark

The benchmark runs the API on loopback Kestrel, authenticates reads with seeded
API keys, and concurrently posts runtime status snapshots using seeded runtime
tokens. It verifies the application/environment/deployment/domain/runtime summary
and checks that an API key for one organization receives `403` for another.
Before and after runs use the same generated workload and request counts:

| Dataset | Apps / environments / deployments | Reads per run | Concurrent readers | Runtime status writes per run |
| --- | --- | ---: | ---: | ---: |
| small | 6 / 18 / 180 | 30 | 5 | 30 |
| history | 6 / 18 / 18,000 | 30 | 5 | 30 |
| scaling | 50 / 150 / 150,000 | 30 | 5 | 30 |

Each cell below shows the median p95 from three independent runs, with the
minimum–maximum run-to-run p95 in parentheses. “Before” materialized deployment
lock metadata for every deployment; “after” filters lock candidates in SQL and
returns distinct app IDs and lock flags. The result checks passed in both
conditions; response timestamps updated by ingestion are excluded from the
stable summary comparison.

| Dataset | Read p95 without ingestion, before → after | Read p95 with ingestion, before → after | SQL commands / summed command ms per read, no ingestion | SQL commands / summed command ms per read, with ingestion | Response bytes, before → after | Isolated sequential allocation/read, before → after |
| --- | --- | --- | --- | --- | --- | --- |
| small | 251 ms (246–258) → 232 ms (226–237) | 306 ms (298–352) → 300 ms (273–328) | 11 / 33 ms → 11 / 31 ms | 11 / 43 ms → 11 / 38 ms | 40,142 → 40,141 | 695,492 → 646,562 bytes |
| history | 316 ms (293–355) → 203 ms (201–247) | 373 ms (372–379) → 286 ms (242–321) | 11 / 34 ms → 11 / 35 ms | 11 / 43 ms → 11 / 46 ms | 41,294 → 41,294 | 8,074,880 → 643,967 bytes |
| scaling | 957 ms (946–1,056) → 390 ms (381–395) | 1,131 ms (1,117–1,383) → 494 ms (435–514) | 11 / 70 ms → 11 / 145 ms | 11 / 88 ms → 11 / 160 ms | 343,844 → 343,845 | 64,665,890 → 2,549,986 bytes |

The runtime writer's median p95 across three runs was 181 / 227 / 353 ms before
and 212 / 208 / 240 ms after for small / history / scaling, respectively. It
issued two SQL commands per status write; mean summed command time per write was
5.4 / 4.2 / 8.3 ms before and 5.4 / 3.8 / 6.0 ms after. The ingest response was
43–45 bytes.

The matched local runs used Release builds on Ubuntu 24.04.5 with four reported
CPUs, .NET SDK 11.0.100 targeting .NET 10 (runtime 10.0.15), and PostgreSQL
17.12. The end-to-end p95 includes bearer authentication, database work, API
serialization, loopback HTTP, and response transfer. SQL duration is the sum of
EF command wall times for an operation; it includes local command latency and
was variable, especially at the largest size, so the benchmark does not claim a
consistent database-time reduction. Allocation is process-wide allocated bytes
during an isolated sequential read, not a per-thread profiler measurement.
These results are local measurements, not production guarantees.

PostgreSQL plans showed why the narrower lock projection was retained. In the
18,000-deployment history case, the old query returned 18,000 rows at a 95-byte
projected width (4.66 ms execution); the new query returned no lock candidates
at a 15-byte width (5.17 ms). In the 150,000-deployment case, the old sequential
scan returned 150,000 rows (42.31 ms); the new parallel scan returned no rows
(31.89 ms). The history is still inspected by PostgreSQL, but is no longer
materialized in the API process. No index or additional round trip was added.
Stable summary fields matched; response sizes differed by at most one byte due
to changing observation timestamps.

Older handler-only measurements (which excluded authentication and HTTP) were
6.8 / 6.2 / 11.2 ms warm p95 and 22.9 / 9.8 / 21.7 ms five-client p95 for the
same small / history / scaling dataset sizes. They are historical context only
and are not directly comparable to the authenticated HTTP measurements above.

## Reproducing the benchmark

Run from the repository root against a **fresh, disposable local PostgreSQL
database**. The executable only accepts a local database named `apps_perf*`,
rejects a nonempty public schema, applies product migrations, and inserts
synthetic data. It does not drop or clear databases.

```sh
docker run --name selfrun-apps-perf --rm -d \
  -e POSTGRES_PASSWORD=synthetic-perf -e POSTGRES_DB=apps_perf \
  -p 127.0.0.1:55439:5432 postgres:17-alpine
# Wait until this readiness check succeeds.
docker exec selfrun-apps-perf pg_isready -U postgres
SELFRUN_PERF_DATABASE='Host=127.0.0.1;Port=55439;Database=apps_perf;Username=postgres;******' \
  dotnet run --project tools/app-summary-benchmark/AppSummaryBenchmark.csproj -c Release
docker stop selfrun-apps-perf
```

Replace the redacted suffix with the Npgsql connection-string password key and
the same value configured in `POSTGRES_PASSWORD`.

The tool writes captured SQL and PostgreSQL plans under the git-ignored
`.local/artifacts/apps-data-fetch/` directory. Set `SELFRUN_PERF_OUTPUT` to
override the SQL/plan directory; redirect console output to a file there when
keeping run logs. Do not copy generated outputs into `docs/` or stage them;
update this maintained summary only after reviewing matched runs.

Regression coverage checks tenant authorization, latest non-asset selection,
timezone/sub-millisecond ordering, domain filtering, bounded query counts,
cancellation, and absence of tracking/writes or serialization-time database
access. PostgreSQL qualification uses actual migrations and generated query
plans; SQLite tests cover additional asset-only/null/tie and snapshot-history
cases.
