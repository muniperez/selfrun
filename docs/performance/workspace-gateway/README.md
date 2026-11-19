# Workspace gateway memory and relay cost

The workspace gateway admits HTTP and WebSocket requests per session, relays
them to a workspace's loopback upstream and re-authorizes each WebSocket with
the control plane every `ReauthorizationSeconds` (default 5). With
`OperatorDiagnostics:Enabled=true` it emits these low-cardinality metrics on
the `Minicloud.Operator` meter. No host, session, path or target is tagged.

| Metric | Tags |
| --- | --- |
| `minicloud.gateway.connections.active` | `operation` (`http`, `websocket`) |
| `minicloud.gateway.admission.wait` (s) | `outcome` (`admitted`, `rejected`, `timeout`, `cancelled`) |
| `minicloud.gateway.relay.bytes` | `operation`, `direction` (`upstream`, `downstream`) |
| `minicloud.gateway.websocket.closes` | `outcome` (`client_closed`, `upstream_closed`, `message_limit`, `activity_rejected`, `revoked`, `lifetime`, `aborted`) |

## Local benchmark

`tools/workspace-gateway-benchmark` starts the real gateway `Program` in a child
process with the recommended container shape emulated: one visible CPU
(`DOTNET_PROCESSOR_COUNT=1`) and a 192 MiB GC heap limit. The harness acts as
the trusted loopback proxy that supplies `X-Forwarded-Proto`. It also hosts a
fake control plane, which counts resolves and activity calls, and a fake T3
upstream with a WebSocket echo, a 32 KiB asset with a 10 ms delay and a
500 ms `/held` route. Default limits apply: 128 connections, 16 per session,
and 5 s re-authorization.

| Scenario | Workload |
| --- | --- |
| `footprint` | Idle gateway, then 32, 64 and 128 open WebSockets (8 per session). Takes a forced-GC snapshot at each step. Then holds 128 idle sockets for 30 s and closes them all. |
| `relay` | 32 WebSockets × 300 echo round trips each, at 1 KiB and at 16 KiB messages. |
| `admission` | 48 clients × 20 bootstrap asset requests on one session, which queue behind the 16-per-session cap. Then 32 concurrent non-bootstrap requests on one session, which must split 16×200 and 16×429. |

Each run writes JSON to stdout. The child reports RSS and anonymous RSS from
`/proc`, GC heap and committed bytes, precise allocated bytes, CPU time,
threads and the metrics above, read through an in-process `MeterListener`. Set
`GATEWAY_BENCH_METRICS=0` to turn that listener off for memory-only
comparisons.

### Results

Each cell shows the median, with the min–max across runs in parentheses. All
runs used Release builds, .NET 10.0.15 on Ubuntu 26.04 arm64 with 6 host CPUs,
and the gateway limited to 1 CPU. Footprint has 10 interleaved runs per build
with the listener off. Relay and admission have 3 interleaved runs per build.
"main" is `origin/main`. "This change" adds the metrics only.

| Workload | Metric | main | This change |
| --- | --- | ---: | ---: |
| Idle | RSS / anonymous RSS | 119.6 / 43.4 MiB | 120.2 / 43.9 MiB |
| | GC heap after full GC | 1,162 KiB | 1,165 KiB |
| 128 open WebSockets | RSS | 134.4 MiB (133.4–142.8) | 135.5 MiB (133.2–142.4) |
| | Anonymous RSS per socket | 100.5 KiB (100.0–150.4) | 100.4 KiB (99.8–149.8) |
| | GC heap / committed per socket | 78.9 / 99.1 KiB | 78.8 / 99.2 KiB |
| 128 idle sockets, 30 s | Gateway CPU | 31.8 ms/s (27.6–37.1) | 33.4 ms/s (25.6–38.7) |
| | Allocation | 129 KiB/s (123–135) | 126 KiB/s (120–132) |
| | Control-plane resolves | 25.6/s | 25.6/s |
| | Log lines | 0 | 0 |
| Close 128 sockets | Time to close all | 11.4 ms (5.2–20.1) | 11.3 ms (5.2–28.9) |
| | Client close status | 128 × PolicyViolation | 128 × PolicyViolation |
| | RSS above idle after close | 30.2 MiB (23.1–47.6) | 29.1 MiB (25.4–37.2) |
| Relay, 1 KiB × 32 | Round trips/s | 21,813 (19,083–22,054) | 20,007 (16,356–23,900) |
| | p50 / p99 round trip | 1.15 / 4.1 ms | 1.15 / 5.9 ms |
| | Gateway CPU per relayed MiB | 9.4 ms (8.2–9.7) | 9.7 ms (9.0–10.1) |
| | Gateway allocation per round trip | 0.25 KiB | 0.25 KiB |
| Relay, 16 KiB × 32 | Relayed MiB/s | 362 (357–394) | 389 (332–390) |
| | Gateway CPU per relayed MiB | 1.1 ms | 1.1 ms |
| | Gateway allocation per round trip | 0.30 KiB | 0.31 KiB |
| | Failures / unexpected disconnects | 0 / 0 | 0 / 0 |
| Bootstrap queue, 48 × 20 | Requests/s | 1,160 (1,079–1,212) | 1,151 (1,017–1,171) |
| | Client p50 / p99 | 25.1 / 157 ms | 26.9 / 151 ms |
| | Admission wait p50 / p99 | n/a | 12.3 / 139 ms |
| | Upstream max concurrent | 16 | 16 |
| | Gateway CPU / allocation per request | 0.18 ms / 27.5 KiB | 0.16 ms / 27.3 KiB |
| Non-bootstrap, 32 on one session | Statuses | 16×200, 16×429 | 16×200, 16×429 |

No behavior change is justified by this baseline. At the 128-connection cap,
sockets add about 13 MiB of anonymous memory. That is well inside the
recommended 256 MiB container. Per-message relay allocation is already about
0.25 KiB. Admission caps, queueing, 429 responses, close status and drain
behaved identically in both builds.

Anonymous RSS per socket is bimodal in both builds. Most runs show about
100 KiB. A few show about 130–150 KiB when the thread pool injects extra
threads under the 1-CPU limit. The GC heap does not change in those runs.

Environment caveat: the host was shared with unrelated jobs, so throughput and
latency are noisy. CPU time, allocation and memory are the primary metrics.
All traffic is plain loopback HTTP, so these numbers are not production
guarantees.

### Findings not changed here

- **Per-socket memory.** Each WebSocket holds two 16 KiB pump buffers and, on
  the T3 bearer path, a 16 KiB activity-observer buffer. That is about
  48 KiB of the roughly 79 KiB managed heap per socket. Pooling would not
  shrink this for open sockets, because receives hold their buffer while idle.
  Zero-byte reads could, but the saving is about 4–6 MiB at the cap.
- **Re-authorization.** Each open WebSocket resolves its target every 5 s. That
  is 25.6 control-plane calls per second at 128 sockets, and it accounts for
  most of the idle CPU and allocation. The interval is a revocation bound, so
  this change leaves it alone.
- **Retained RSS.** After all sockets close, the GC heap returns to under 1 MiB,
  but RSS stays about 25–35 MiB above idle. This is runtime and native
  high-water memory, not a leak.
- **Close status.** The gateway closes both sides with `PolicyViolation`, even
  when the client initiated the close. This is existing behavior and is kept.

### Reproduce locally

From the repository root with .NET 10 installed, compare a baseline revision
(`BASE`) against the working tree:

```bash
set -e
root="$PWD/.local/artifacts/gateway-perf"
git check-ignore "$root/probe"
mkdir -p "$root/base"
git archive "$BASE" modules | tar -x -C "$root/base"
dotnet build tools/workspace-gateway-benchmark -c Release --artifacts-path "$root/build-base" \
  -p:GatewaySourceRoot="$root/base"
dotnet build tools/workspace-gateway-benchmark -c Release --artifacts-path "$root/build-candidate"
for run in 1 2 3; do
  for scenario in footprint relay admission; do
    for variant in base candidate; do
      dotnet "$root/build-$variant/bin/WorkspaceGatewayBenchmark/release/WorkspaceGatewayBenchmark.dll" \
        "$scenario" > "$root/$variant-$scenario-$run.json"
    done
  done
done
```

A baseline without these metrics reports no admission-wait or close-outcome
fields.

### Still to measure on a VPS

The following still need measurement on a VPS:

- Caddy TLS termination cost.
- Real control-plane resolve latency over the network.
- Memory and CPU under an actual cgroup limit.
- Upstream workspace latency.
- Long-lived browser sessions with real T3 traffic.
