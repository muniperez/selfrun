using Selfrun.Api.Extensions;
using Selfrun.Api.Services;
using Selfrun.Common.Services;
using Selfrun.Common.Diagnostics;
using OpenTelemetry.Trace;

if (args.Length > 0 && args[0] == "bootstrap")
{
    Environment.ExitCode = await BootstrapCommand.RunAsync(args[1..]);
    return;
}

if (args.Length > 0 && args[0] == "maintenance")
{
    Environment.ExitCode = await MaintenanceCommand.RunAsync(args[1..]);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOperatorDiagnostics(builder.Configuration, "selfrun-api", tracing =>
    tracing.AddAspNetCoreInstrumentation(options => options.Filter = context =>
        OperatorDiagnostics.IsTracedServerPath(context.Request.Path.Value)));
if (args.Length > 0 && args[0] == "migrate")
{
    if (args.Length != 1)
    {
        throw new ArgumentException("The migrate command accepts no arguments.");
    }

    await DatabaseMigrationCommand.RunAsync(builder.Configuration, builder.Environment.EnvironmentName);
    return;
}

builder.Services.AddScoped<Selfrun.Api.Services.DatabaseBackupDownloadService>();
builder.Services.AddHttpClient(Selfrun.Api.Services.DatabaseBackupDownloadService.ClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<DatabaseConnectionService>();
builder.Services.AddScoped<DatabaseBindingService>();
builder.Services.AddScoped<DatabaseNetworkService>();

builder.Services.AddSelfrunApi(builder.Configuration, builder.Environment);
builder.Services.AddSelfrunBootstrap();
builder.Services.AddSetupTransport();

builder.Services.AddScoped<DatabaseManagementService>();
builder.Services.AddScoped<DatabaseBackupPurgeService>();

var app = builder.Build();

await app.ConfigureSelfrunApiAsync();
app.UseMiddleware<Selfrun.Api.Auth.MaintenanceAdmissionMiddleware>();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();
app.Run();

public partial class Program;
