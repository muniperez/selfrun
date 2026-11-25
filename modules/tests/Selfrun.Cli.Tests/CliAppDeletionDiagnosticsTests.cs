// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Http.Json;
using Selfrun.Cli;
using Selfrun.Cli.Api;
using Selfrun.Cli.Auth;
using Selfrun.Cli.Commands;

namespace Selfrun.Tests;

public sealed class CliAppDeletionDiagnosticsTests
{
    private const string Recovery = "legacy_provider_import_required: Environment 'production' requires provider import or reconciliation before retrying deletion.";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task List_displays_stored_application_and_environment_deletion_errors(bool applicationFailed)
    {
        using var fixture = new Fixture(applicationFailed);

        Assert.Equal(CliExitCodes.Success, await fixture.Cli.RunAsync(["apps", "list"], default));

        Assert.Contains("demo\tapplication_demo\t1 environments", fixture.Console.Output);
        Assert.Contains(Recovery, fixture.Console.Output);
        Assert.Contains(applicationFailed ? "Application deletion: failed" : "Environment 'production' deletion: failed", fixture.Console.Output);
        Assert.Equal(new[] { "/api/me", "/api/apps" }, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task Inspect_keeps_deletion_blocked_and_displays_stored_recovery_guidance(
        bool applicationFailed, bool explicitEnvironment, bool detailFailed)
    {
        using var fixture = new Fixture(applicationFailed, detailFailed: detailFailed);
        string[] args = explicitEnvironment
            ? ["apps", "inspect", "demo", "--environment", "production"]
            : ["apps", "inspect", "demo"];

        Assert.Equal(CliExitCodes.ValidationError, await fixture.Cli.RunAsync(args, default));

        Assert.Contains(Recovery, fixture.Console.Output);
        Assert.DoesNotContain(fixture.Handler.Requests, path => path.EndsWith("/services"));
        Assert.Equal(detailFailed, fixture.Handler.Requests.Contains("/api/environments/environment_demo"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Inspect_retains_generic_failure_without_stored_error(string? error)
    {
        using var fixture = new Fixture(false, error);

        Assert.Equal(CliExitCodes.ValidationError, await fixture.Cli.RunAsync(["apps", "inspect", "demo"], default));
        Assert.Contains("blocked until cleanup succeeds.", fixture.Console.Output);
        Assert.DoesNotContain(Recovery, fixture.Console.Output);
    }

    [Fact]
    public async Task List_preserves_active_application_output()
    {
        using var fixture = new Fixture(false, active: true);

        Assert.Equal(CliExitCodes.Success, await fixture.Cli.RunAsync(["apps", "list"], default));
        Assert.Equal("demo\tapplication_demo\t1 environments\n", fixture.Console.Output);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly HttpClient _http;
        public Handler Handler { get; }
        public RecordingConsole Console { get; } = new();
        public CliApplication Cli { get; }

        public Fixture(bool applicationFailed, string? error = Recovery, bool detailFailed = false, bool active = false)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (!Directory.Exists(Path.Combine(root.FullName, ".git")) && !File.Exists(Path.Combine(root.FullName, ".git")))
                root = root.Parent ?? throw new InvalidOperationException("Repository root not found.");
            _directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".local", "artifacts", "cli-deletion-tests", Guid.NewGuid().ToString("N")));
            var environment = CliEnvironment.ForTests("https://api.example", _directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_synthetic_test");
            Handler = new Handler(applicationFailed, error, detailFailed, active);
            _http = new HttpClient(Handler);
            Cli = new CliApplication(Console, environment, tokens, new SelfrunApiClient(environment, tokens, _http));
        }

        public void Dispose()
        {
            _http.Dispose();
            _directory.Delete(true);
        }
    }

    private sealed class Handler(bool applicationFailed, string? error, bool detailFailed, bool active) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            var application = new
            {
                id = "application_demo", organizationId = "org", name = "Demo", slug = "demo",
                deletionStatus = applicationFailed ? "failed" : null,
                deletionError = applicationFailed ? error : null,
                environments = new[]
                {
                    new
                    {
                        id = "environment_demo", applicationId = "application_demo", name = "production", kind = "persistent",
                        deletionStatus = !applicationFailed && !detailFailed && !active ? "failed" : null,
                        deletionError = error
                    }
                }
            };
            object response = path switch
            {
                "/api/me" => new { id = "user", email = "test@example.com", organizations = new[] { new { id = "org", name = "Test", slug = "test", role = "owner" } } },
                "/api/apps" => new[] { application },
                "/api/environments/environment_demo" => new
                {
                    id = "environment_demo", applicationId = "application_demo", organizationId = "org",
                    name = "production", slug = "demo", database = "none", plan = "Small",
                    deletionStatus = "failed", deletionError = error, domains = Array.Empty<object>(), branches = Array.Empty<object>()
                },
                _ => throw new InvalidOperationException(request.Method + " " + path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) });
        }
    }

    private sealed class RecordingConsole : IConsole
    {
        public string Output { get; private set; } = "";
        public bool SupportsAnsi => false;
        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message + "\n";
        public void WriteError(string message) => WriteLine(message);
        public string? ReadLine() => null;
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
