using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Services;

namespace Minicloud.Worker.DevelopmentWorkspaces;

public static class DevelopmentWorkspaceWorkerRegistration
{
    public static IServiceCollection AddDevelopmentWorkspaceWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DevelopmentWorkspaceWorkerOptions>(configuration.GetSection(DevelopmentWorkspaceWorkerOptions.SectionName));
        services.AddSingleton<WorkspaceRuntimeTransport>();
        services.AddSingleton<IWorkspaceRuntimeTransport>(sp => sp.GetRequiredService<WorkspaceRuntimeTransport>());
        services.AddSingleton<WorkspaceTunnelManager>();
        services.AddSingleton<IWorkspaceTunnelManager>(sp => sp.GetRequiredService<WorkspaceTunnelManager>());
        services.AddHttpClient<GitHubAppClient>()
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddScoped<WorkspaceGitAccessService>();
        services.AddScoped<DevelopmentWorkspaceProcessor>();
        services.AddHostedService<DevelopmentWorkspaceWorker>();
        return services;
    }
}

public sealed class DevelopmentWorkspaceWorker(
    IServiceScopeFactory scopes, IOptions<DevelopmentWorkspaceOptions> feature,
    IOptions<DevelopmentWorkspaceWorkerOptions> options, ILogger<DevelopmentWorkspaceWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(LoopAsync(Loop.Process, stoppingToken), LoopAsync(Loop.Monitor, stoppingToken),
            LoopAsync(Loop.Filesystem, stoppingToken));

    private enum Loop { Process, Monitor, Filesystem }

    private async Task LoopAsync(Loop loop, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (feature.Value.Enabled)
                {
                    using var scope = scopes.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<DevelopmentWorkspaceProcessor>();
                    if (loop == Loop.Monitor) await processor.MonitorNextAsync(ct);
                    else if (loop == Loop.Filesystem) await processor.FilesystemNextAsync(ct);
                    else await processor.ProcessNextAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Development workspace reconciliation failed ({ErrorType}); no response body or credential was logged.", error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Value.PollSeconds, 1, 60)), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
