using Microsoft.Extensions.Hosting;
using Selfrun.Common.Services;

namespace Selfrun.Worker.Maintenance;

public sealed class MaintenanceParticipantRegistrationService(MaintenanceParticipant participant) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => participant.RunRegistrationLoopAsync(stoppingToken);
}
