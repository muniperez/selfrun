FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY modules/contracts/Selfrun.Contracts.csproj modules/contracts/
COPY modules/redaction/Selfrun.Redaction.csproj modules/redaction/
COPY modules/common/Selfrun.Common.csproj modules/common/
COPY modules/telemetry/Selfrun.Telemetry.csproj modules/telemetry/
RUN dotnet restore modules/telemetry/Selfrun.Telemetry.csproj

COPY modules/contracts modules/contracts
COPY modules/redaction modules/redaction
COPY modules/common modules/common
COPY modules/telemetry modules/telemetry
RUN dotnet publish modules/telemetry/Selfrun.Telemetry.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl wget \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
COPY LICENSE /usr/share/selfrun/LICENSE
COPY LICENSES /usr/share/selfrun/LICENSES
ENTRYPOINT ["dotnet", "Selfrun.Telemetry.dll"]
