FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY modules/contracts/Minicloud.Contracts.csproj modules/contracts/
COPY modules/common/Minicloud.Common.csproj modules/common/
COPY modules/workspace-gateway/Minicloud.WorkspaceGateway.csproj modules/workspace-gateway/
RUN dotnet restore modules/workspace-gateway/Minicloud.WorkspaceGateway.csproj \
    --disable-parallel -m:1 -p:UseSharedCompilation=false --disable-build-servers
COPY modules/contracts modules/contracts
COPY modules/common modules/common
COPY modules/workspace-gateway modules/workspace-gateway
RUN dotnet publish modules/workspace-gateway/Minicloud.WorkspaceGateway.csproj \
    --configuration Release --no-restore --output /app/publish \
    -m:1 -p:UseSharedCompilation=false -p:UseAppHost=false --disable-build-servers

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
COPY --from=build /app/publish .
COPY LICENSE /usr/share/minicloud/LICENSE
COPY LICENSES /usr/share/minicloud/LICENSES
USER $APP_UID
ENTRYPOINT ["dotnet", "Minicloud.WorkspaceGateway.dll"]
