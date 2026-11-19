# OTLP logs ingestion cost

`POST /otlp/v1/logs` on the telemetry gateway authenticates the app credential,
reads a bounded body, optionally decompresses it, decodes OTLP protobuf,
normalizes records, applies quota and forwards a Loki push. With
`OperatorDiagnostics:Enabled=true`, each stage emits a `Minicloud.Operator` span
(`otlp.auth`, `otlp.read`, `otlp.gzip`, `otlp.decode`, `otlp.normalize`,
`otlp.quota`, `otlp.encode`, `otlp.forward`) that carries only the `operation`
tag. Request bodies, tenant IDs and credentials are never attached.

## Local benchmark

`tools/otlp-ingestion-benchmark` runs the real telemetry services and
controllers on loopback Kestrel with a file-backed SQLite store (the
self-hosted default), a seeded synthetic credential and a loopback fake Loki.
The fake Loki checks the tenant header on every push and counts any forwarded
`Authorization` header. Scenarios:

| Scenario | Workload |
| --- | --- |
| `stages` | Sequential: 10 warmup + 100 measured requests each of 500 records gzip (63 KB encoded / 172 KB decoded), the same 500 records uncompressed, and 50 records gzip (7 KB / 17 KB). Records server CPU (`getrusage`) and allocated bytes per request and per stage span. |
| `load` | 500-record gzip, 8 concurrent clients, 16 warmup + 300 measured requests. |
| `faults` | Like `load`. The fake Loki returns 503 with `Retry-After` on every 10th push, 429 without `Retry-After` on some others, and partial-success rejections on others. |
| `overload` | Like `load`, with `MaxConcurrentIngestions=2` and 50 ms Loki latency. |

Load scenarios report throughput, client p50/p95, forward-stage latency,
process CPU and allocation per accepted MiB, the response status mix,
`Retry-After` and partial-success counts, tenant violations and credential
leaks.

### Results

Each cell shows the median of three runs, with the min–max across runs in
parentheses. The before/after runs were interleaved. "Before" is the stage-span
instrumentation only. "After" adds in-place nested protobuf decoding (no
per-level `ToByteArray` copies), pooled 64 KiB read/gzip scratch buffers, and
encoded-body buffers presized from the already validated `Content-Length`.
Limits, status codes, `Retry-After`, partial-success handling and tenant
attribution were identical in every run. All runs reported 0 tenant violations
and 0 credential leaks.

| Workload | Metric | Before | After |
| --- | --- | ---: | ---: |
| 500 records, gzip | Server allocation per request | 9,763 KiB (9,664–9,809) | 7,742 KiB (7,437–7,803) |
| | Allocation per accepted MiB | 58.1 MiB (57.5–58.4) | 46.1 MiB (44.3–46.5) |
| | Server CPU per accepted MiB | 234 ms (232–270) | 240 ms (225–246) |
| | Decode stage: allocation / CPU | 4,263 KiB / 2.12 ms | 2,351 KiB / 1.66 ms |
| | Read + gzip stages: allocation | 188 + 552 KiB | 124 + 487 KiB |
| | Auth stage: CPU | 21.7 ms | 21.8 ms |
| 500 records, uncompressed | Server allocation per request | 9,526 KiB (9,181–9,528) | 6,924 KiB (6,923–6,925) |
| | Server CPU per accepted MiB | 236 ms (223–245) | 213 ms (209–213) |
| 50 records, gzip | Server allocation per request | 1,285 KiB (1,254–1,289) | 932 KiB (928–936) |
| | Server CPU per accepted MiB | 1,356 ms (1,276–1,508) | 1,442 ms (1,362–1,492) |
| `load` | Requests/s | 42.3 (34.1–42.4) | 41.3 (36.3–42.3) |
| | Client p95 | 506 ms (492–510) | 501 ms (442–503) |
| | Process CPU per accepted MiB | 229 ms (206–230) | 225 ms (222–228) |
| | Process allocation per accepted MiB | 58.5 MiB (56.9–58.9) | 47.5 MiB (45.8–48.2) |
| `faults` | Status mix (identical) | 255×200, 30×503, 15×429 | 255×200, 30×503, 15×429 |
| | Process allocation per accepted MiB | 69.1 MiB (68.0–70.8) | 54.9 MiB (54.2–55.3) |
| `overload` | Accepted / 503 with `Retry-After` | 15.7% / 84.3% | 16.0% / 84.0% |
| | Process CPU per accepted MiB | 860 ms (848–930) | 840 ms (828–922) |

The robust change is allocation, which fell 21–27% per request. CPU differences
are within run-to-run spread, because credential verification dominates CPU.

Environment: Release builds, .NET 10.0.15, Server GC, Ubuntu 26.04 arm64 with
6 CPUs. The host was shared with unrelated CPU-heavy jobs (load average about
20), so wall-clock latency and throughput are noisy. CPU time and allocation are
the primary metrics. All traffic is loopback, so these numbers are not
production guarantees.

### Findings not changed here

- **Credential verification.** Every request runs PBKDF2-SHA256 at 210,000
  iterations. This takes about 21–24 ms of CPU, which is more than half the
  server CPU even for 500-record batches. The telemetry gateway has no
  verified-credential cache. Adding one would change revocation latency, so it
  needs a separate decision.
- **Auth before admission.** Authentication runs before the ingestion
  concurrency limiter, so requests rejected with 503 under overload still pay
  the PBKDF2 cost.
- **Quota service lifetime.** `TelemetryQuotaService` is registered as scoped,
  so its in-memory per-app token buckets and daily-counter gate are recreated
  for every request. The per-request burst cap and the persisted daily
  reservation still run. However, sustained-rate state does not carry over
  between requests, and concurrent daily reservations are not serialized
  in-process. This is a
  correctness issue, not a cost issue, so it needs its own change.
- **Logging volume.** Per request, the gateway logs one Loki forward message.
  Framework and EF command logs account for about 18 more events at the default
  `Information` level. Successful-ingest logs do not dominate, so no log
  aggregation was added.

### Reproduce locally

From the repository root with .NET 10 installed, compare a baseline revision
(`BASE`) against the working tree:

```bash
set -e
root="$PWD/.local/artifacts/otlp-perf"
git check-ignore "$root/probe"
mkdir -p "$root/base"
git archive "$BASE" modules/telemetry modules/common modules/redaction modules/contracts |
  tar -x -C "$root/base"
dotnet build tools/otlp-ingestion-benchmark -c Release --artifacts-path "$root/build-base" \
  -p:TelemetrySourceRoot="$root/base"
dotnet build tools/otlp-ingestion-benchmark -c Release --artifacts-path "$root/build-candidate"
for run in 1 2 3; do
  for scenario in stages load faults overload; do
    for variant in base candidate; do
      scratch="$root/$variant-$scenario-$run"
      mkdir "$scratch"
      dotnet "$root/build-$variant/bin/OtlpIngestionBenchmark/release/OtlpIngestionBenchmark.dll" \
        "$scratch" "$scenario" > "$scratch.json"
    done
  done
done
```

Stage attribution requires a baseline that already has the stage spans.

### Still to measure on a VPS

The following need measurement on a VPS: real Loki push latency and rejection
or retry rates, TLS and edge-proxy costs, and CPU under the production
container's CPU limit.
