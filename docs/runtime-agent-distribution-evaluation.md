# Runtime-agent distribution evaluation

Evaluated 2026-11-18. The baseline was the self-contained, single-file
`linux-x64` and `linux-arm64` agent published by the worker image. The candidate
moved the unchanged `LogRedactor` implementation into
`modules/redaction/Minicloud.Redaction.csproj`; the runtime agent now references
that dependency-free project rather than the EF-bearing common project. Both
variants used the same .NET 10.0.404 SDK, RIDs and publish flags:

```sh
dotnet publish modules/runtime-agent/Minicloud.RuntimeAgent.csproj \
  -c Release -r <linux-x64|linux-arm64> --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None -p:DebugSymbols=false
```

No trimming or Native AOT was enabled. The runtime-agent project has no package
references after isolation.

## Distribution

Sizes below are the exact executables extracted from the baseline and candidate
worker images. Per-host transfer is one architecture-specific executable.
Worker image size is `docker image inspect .Size` (uncompressed image layers);
each image was built once on the same x64 Docker host.

| Target | Baseline agent / transfer | Lean agent / transfer | Bytes saved | Reduction |
| --- | ---: | ---: | ---: | ---: |
| Linux x64 | 87,515,698 | 73,741,378 | 13,774,320 | 15.7% |
| Linux arm64 | 94,953,110 | 81,082,084 | 13,871,026 | 14.6% |

| Worker image | Baseline | Lean | Bytes saved | Reduction |
| --- | ---: | ---: | ---: | ---: |
| x64 image containing both agents | 331,729,100 | 304,102,181 | 27,626,919 | 8.3% |

The worker image retains both architecture payloads, so the image saving is
approximately the sum of the two agent savings.

## Build spread

Three publishes per variant and RID ran through
`mcr.microsoft.com/dotnet/sdk:10.0` (10.0.404), with a shared NuGet cache.
Artifact sizes were identical across all three runs. After the first cache
population, the two warm build times were:

| RID | Baseline warm runs | Lean warm runs |
| --- | ---: | ---: |
| Linux x64 | 2.64–2.72 s | 1.99–2.02 s |
| Linux arm64 | 2.73–2.75 s | 2.01–2.05 s |

The first baseline x64 run took 34.88 s while populating the cache; subsequent
first-run timings are not directly comparable because that cache was warm.

## Runtime measurement

The exact x64 executables from the worker images each ran five times on the
same x64 Linux host. Each run used a synthetic local control API, no Docker
containers, no logs and an empty routing table. The agent completed its first
heartbeat, status submission and routing exchange. RSS was sampled ten times
at 100 ms intervals, starting one second after that first iteration completed.
Heartbeat is process-start to receipt of the first heartbeat request.

| Measurement | Baseline (median; run range) | Lean (median; run range) |
| --- | ---: | ---: |
| First heartbeat | 135.47 ms (132.87–144.22) | 137.89 ms (131.03–152.24) |
| RSS | 52,764 KiB (52,500–52,888) | 52,704 KiB (52,664–52,856) |

Heartbeat and RSS are effectively unchanged at this workload. Native arm64
execution was unavailable on the x64-only host, so these runtime measurements
do not establish arm64 startup or resident-memory behavior. The arm64 publish
and transfer sizes above were measured from the worker images, not inferred
from an arm64 run.

## Compatibility and decision

- Redaction behavior was not rewritten: `LogRedactor` moved unchanged, retaining
  its namespace and callers. The runtime-agent redaction regression and
  deployment packaging tests passed.
- A local in-place install cycle using the exact x64 image payloads installed
  baseline, upgraded to lean, then restored baseline at the same executable
  path and configuration. All three started and reported heartbeat version
  `0.2.0`. This exercises payload compatibility and rollback, not the complete
  remote SSH deployment orchestration.
- The lean build saves about 13.8 MB on each runtime-agent transfer and 27.6 MB
  from the worker image, without a measurable x64 runtime-memory or heartbeat
  change. Keep the existing self-contained distribution and adopt only the
  dependency isolation. Do not enable trimming or AOT based on this evaluation;
  native arm64 runtime qualification remains outstanding.
