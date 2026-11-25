FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime-agent
WORKDIR /src
ARG SELFRUN_AGENT_RID=linux-x64
COPY modules/redaction modules/redaction
COPY modules/runtime-agent modules/runtime-agent
RUN case "$SELFRUN_AGENT_RID" in \
      linux-x64) other_rid=linux-arm64 ;; \
      linux-arm64) other_rid=linux-x64 ;; \
      *) echo "Unsupported runtime agent RID: $SELFRUN_AGENT_RID" >&2; exit 1 ;; \
    esac \
    && dotnet publish modules/runtime-agent/Selfrun.RuntimeAgent.csproj -c Release -r "$SELFRUN_AGENT_RID" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "/agent/$SELFRUN_AGENT_RID" \
    && dotnet publish modules/runtime-agent/Selfrun.RuntimeAgent.csproj -c Release -r "$other_rid" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "/agent/$other_rid"

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["modules/contracts/Selfrun.Contracts.csproj", "modules/contracts/"]
COPY ["modules/redaction/Selfrun.Redaction.csproj", "modules/redaction/"]
COPY ["modules/common/Selfrun.Common.csproj", "modules/common/"]
COPY ["modules/worker/Selfrun.Worker.csproj", "modules/worker/"]
RUN dotnet restore "modules/worker/Selfrun.Worker.csproj"
COPY . .
WORKDIR "/src/modules/worker"
RUN dotnet publish "Selfrun.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY LICENSE /usr/share/selfrun/LICENSE
COPY LICENSES /usr/share/selfrun/LICENSES
COPY --from=runtime-agent /agent/linux-x64/Selfrun.RuntimeAgent /app/runtime-agent/linux-x64/selfrun-agent
COPY --from=runtime-agent /agent/linux-arm64/Selfrun.RuntimeAgent /app/runtime-agent/linux-arm64/selfrun-agent
COPY templates/runtime/selfrun-agent.service templates/runtime/apply-selfrun-routing.py templates/runtime/install-selfrun-agent.sh /app/runtime-agent/
ENTRYPOINT ["dotnet", "Selfrun.Worker.dll"]
