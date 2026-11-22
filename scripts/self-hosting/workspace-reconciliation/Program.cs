using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Services;
using Npgsql;
using WorkspaceReconciliation;

if (args.Length != 2 || args[0] is not ("--check" or "--apply-offline"))
{
    Console.Error.WriteLine("Usage: WorkspaceReconciliation (--check | --apply-offline) PRIVATE_SCOPE.json");
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; deadline.Cancel(); };
try
{
    var scope = JsonSerializer.Deserialize<RecoveryScope>(await File.ReadAllTextAsync(args[1], deadline.Token), RecoveryScope.Json)
        ?? throw new RecoveryRefused("A recovery scope is required.");
    scope.Validate();
    var connection = new NpgsqlConnectionStringBuilder(
        (await File.ReadAllTextAsync(Required("RECONCILIATION_DB_FILE"), deadline.Token)).Trim())
    {
        Pooling = false,
        IncludeErrorDetail = false,
        Multiplexing = false,
        Options = "-c search_path=public"
    };
    if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database)
        || string.IsNullOrWhiteSpace(connection.Username))
        throw new RecoveryRefused("An explicit PostgreSQL host, database and user are required.");

    var baoOptions = new OpenBaoSecretStoreOptions
    {
        Address = Required("OpenBao__Address"), TokenFile = Required("OpenBao__TokenFile"),
        CaCertificateFile = Environment.GetEnvironmentVariable("OpenBao__CaCertificateFile") ?? "",
        MountPath = Required("OpenBao__MountPath"), EnvironmentPrefix = Required("OpenBao__EnvironmentPrefix")
    };
    if (!Uri.TryCreate(baoOptions.Address, UriKind.Absolute, out var baoUri) || baoUri.Scheme != "https"
        || baoUri.UserInfo.Length != 0 || baoUri.Query.Length != 0 || baoUri.Fragment.Length != 0 || baoUri.AbsolutePath != "/")
        throw new RecoveryRefused("OpenBao requires an explicit HTTPS origin.");

    using var baoHandler = OpenBaoHttpHandler.Create(baoOptions);
    if (baoHandler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
    using var baoHttp = new HttpClient(baoHandler);
    var bao = new OpenBaoSecretStore(baoHttp, Options.Create(baoOptions));
    using var providerHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    await using var db = new MinicloudDbContext(new DbContextOptionsBuilder<MinicloudDbContext>()
        .UseNpgsql(connection.ConnectionString).Options);
    var recovery = new Recovery(db, bao, baoOptions.EnvironmentPrefix, new HetznerAbsence(providerHttp));
    Console.WriteLine(await recovery.RunAsync(scope, apply: args[0] == "--apply-offline", deadline.Token));
    return 0;
}
catch (RecoveryRefused error)
{
    Console.Error.WriteLine($"Recovery refused: {error.Message}");
}
catch (PostgresException error)
{
    Console.Error.WriteLine($"Recovery did not report success: PostgreSQL SQLSTATE {error.SqlState}. Recheck before retrying.");
}
catch (Exception error) when (error is JsonException or IOException or ArgumentException or UnauthorizedAccessException
    or HttpRequestException or SecretStoreUnavailableException or DbUpdateException or NpgsqlException or OperationCanceledException
    or KeyNotFoundException or FormatException or InvalidOperationException)
{
    Console.Error.WriteLine($"Recovery did not report success ({error.GetType().Name}). Recheck before retrying.");
}
return 1;

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new RecoveryRefused($"Set {name}.");
