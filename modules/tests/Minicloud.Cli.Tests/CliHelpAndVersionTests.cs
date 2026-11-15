// SPDX-License-Identifier: MPL-2.0

using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;

namespace Minicloud.Tests;

public sealed class CliHelpAndVersionTests
{
    private static (CliApplication app, TestConsole console) CreateApp()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-cli-help-test-");
        var console = new TestConsole();
        var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
        var tokens = new TokenStore(environment);
        var client = new MinicloudApiClient(environment, tokens);
        var app = new CliApplication(console, environment, tokens, client);
        return (app, console);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("version")]
    public async Task Version_flags_and_command_show_version_straight_away(string arg)
    {
        var (app, console) = CreateApp();

        var exitCode = await app.RunAsync([arg], CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        var lines = console.Output.Trim().Split('\n', StringSplitOptions.TrimEntries);
        Assert.Equal($"minicloud version {CliApplication.GetVersion()}", lines[0]);
        Assert.InRange(lines.Length, 1, 2);
        if (lines.Length == 2)
        {
            Assert.Matches("^source [0-9a-f]{40}$", lines[1]);
        }
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task Help_shows_version_and_commands(string arg)
    {
        var (app, console) = CreateApp();

        var exitCode = await app.RunAsync([arg], CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains($"Minicloud CLI version {CliApplication.GetVersion()}", console.Output);
        Assert.Contains("Usage:", console.Output);
        Assert.Contains("deploy", console.Output);
        Assert.Contains("status", console.Output);
        Assert.Contains("logs", console.Output);
        Assert.Contains("-v, --version", console.Output);
        Assert.Contains("-h, --help", console.Output);
    }

    [Fact]
    public async Task Empty_args_shows_general_help()
    {
        var (app, console) = CreateApp();

        var exitCode = await app.RunAsync([], CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains($"Minicloud CLI version {CliApplication.GetVersion()}", console.Output);
        Assert.Contains("Usage:", console.Output);
    }

    [Theory]
    [InlineData("help", "deploy")]
    [InlineData("deploy", "--help")]
    [InlineData("deploy", "-h")]
    public async Task Deploy_help_shows_deploy_details(params string[] args)
    {
        var (app, console) = CreateApp();

        var exitCode = await app.RunAsync(args, CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Deploy services to Minicloud", console.Output);
        Assert.Contains("minicloud deploy [service ...]", console.Output);
        Assert.Contains("minicloud deploy all", console.Output);
        Assert.Contains("minicloud deploy branch", console.Output);
        Assert.Contains("--no-publish", console.Output);
    }

    [Theory]
    [InlineData("help", "status")]
    [InlineData("status", "--help")]
    public async Task Status_help_shows_status_details(params string[] args)
    {
        var (app, console) = CreateApp();

        var exitCode = await app.RunAsync(args, CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Show deployment status and service URLs", console.Output);
        Assert.Contains("--watch", console.Output);
    }

    [Theory]
    [InlineData("help", "logs")]
    [InlineData("logs", "--help")]
    public async Task Logs_help_shows_logs_details(params string[] args)
    {
        var (app, console) = CreateApp();

        var exitCode = await app.RunAsync(args, CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("View and tail logs for an application or deployment", console.Output);
        Assert.Contains("--tail", console.Output);
        Assert.Contains("--since", console.Output);
    }

    [Theory]
    [InlineData("help", "agents")]
    [InlineData("agents", "--help")]
    public async Task Agents_help_documents_workspace_commands(params string[] args)
    {
        var (app, console) = CreateApp();

        var exitCode = await app.RunAsync(args, CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("minicloud agents create", console.Output);
        Assert.Contains("minicloud agents open", console.Output);
        Assert.Contains("minicloud agents auth", console.Output);
        Assert.Contains("--json", console.Output);
    }

    [Fact]
    public async Task Agent_json_mode_returns_structured_cli_errors()
    {
        var (app, console) = CreateApp();

        var exitCode = await app.RunAsync(["agents", "list", "--json"], CancellationToken.None);

        Assert.Equal(CliExitCodes.AuthError, exitCode);
        Assert.Contains("\"error\"", console.Output);
        Assert.Contains("\"code\":\"missing_token\"", console.Output);
        Assert.DoesNotContain("API error", console.Error);
    }

    [Fact]
    public void Pairing_url_uses_loopback_tunnel_and_preserves_pairing_material()
    {
        var pairingUrl = new Uri("http://127.0.0.1:3773/?pairingCode=one-time-code");

        var localUrl = CliApplication.RewritePairingUrlForTunnel(pairingUrl, "203.0.113.12", 49152);

        Assert.Equal("http", localUrl.Scheme);
        Assert.Equal("127.0.0.1", localUrl.Host);
        Assert.Equal(49152, localUrl.Port);
        Assert.Equal("/?pairingCode=one-time-code", localUrl.PathAndQuery);
    }

    [Fact]
    public void Pairing_url_rejects_an_unrelated_host()
    {
        var pairingUrl = new Uri("https://example.com/?pairingCode=one-time-code");

        Assert.Throws<CliCommandException>(() =>
            CliApplication.RewritePairingUrlForTunnel(pairingUrl, "203.0.113.12", 49152));
    }

    [Fact]
    public void Pairing_url_rejects_https_when_the_remote_service_uses_loopback_http()
    {
        var pairingUrl = new Uri("https://127.0.0.1:3773/?pairingCode=one-time-code");

        Assert.Throws<CliCommandException>(() =>
            CliApplication.RewritePairingUrlForTunnel(pairingUrl, "203.0.113.12", 49152));
    }

    [Fact]
    public async Task Help_unknown_command_returns_validation_error()
    {
        var (app, console) = CreateApp();

        var exitCode = await app.RunAsync(["help", "invalid-cmd"], CancellationToken.None);

        Assert.Equal(CliExitCodes.ValidationError, exitCode);
        Assert.Contains("Unknown command 'invalid-cmd'.", console.Error);
    }

    [Fact]
    public void GetVersion_returns_valid_version_string()
    {
        var version = CliApplication.GetVersion();

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual("0.0.0", version);
    }

    private sealed class TestConsole : IConsole
    {
        public bool SupportsAnsi => false;
        public string Output { get; private set; } = "";
        public string Error { get; private set; } = "";
        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message + Environment.NewLine;
        public void WriteError(string message) => Error += message + Environment.NewLine;
        public string? ReadLine() => throw new InvalidOperationException();
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
