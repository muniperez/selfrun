using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;

namespace Minicloud.Tests;

public sealed class CliWorkspaceTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateDirectory(Path.Combine(
        Directory.GetCurrentDirectory(), ".local", "artifacts", "issue110", "client", Guid.NewGuid().ToString("N")));
    private readonly Handler _handler = new();
    private readonly Console _console = new();

    private Task<int> Run(params string[] args)
    {
        var environment = CliEnvironment.ForTests("https://api.example", _directory.FullName);
        var tokens = new TokenStore(environment);
        tokens.SaveToken("synthetic-token");
        return new CliApplication(_console, environment, tokens,
            new MinicloudApiClient(environment, tokens, new HttpClient(_handler)))
            .RunAsync(["workspaces", .. args], default);
    }

    [Fact]
    public async Task MultiOrganizationSelectionIsRequiredBeforeAnyWorkspaceRequest()
    {
        Assert.Equal(CliExitCodes.ValidationError, await Run("list"));
        Assert.Single(_handler.Requests);
        Assert.Contains("accessible organization", string.Join('\n', _console.Messages));
        Assert.Equal(CliExitCodes.ValidationError, await Run("list", "--organization", "outsider"));
        Assert.All(_handler.Requests, request => Assert.Equal("/api/me", request.Path));
    }

    [Fact]
    public async Task CreateAndReadRemainExplicitAndNeverStartCompute()
    {
        Assert.Equal(CliExitCodes.Success, await Run("create", "--organization", "org", "--name", "Coding",
            "--image", "image", "--profile", "Standard", "--plan", "plan", "--volume-gb", "20", "--idempotency-key", "create-once"));
        var request = _handler.Requests.Last();
        Assert.Equal("/api/development-workspaces", request.Path);
        Assert.Equal("org", request.Body.GetProperty("organizationId").GetString());
        Assert.Equal("create-once", request.Body.GetProperty("idempotencyKey").GetString());
        Assert.Equal(CliExitCodes.Success, await Run("status", "workspace", "--organization", "org"));
        Assert.Equal(CliExitCodes.Success, await Run("vms", "workspace", "--organization", "org"));
        Assert.DoesNotContain(_handler.Requests, x => x.Path.Contains("/start"));
        var output = string.Join('\n', _console.Messages);
        Assert.Contains("retained", output);
        Assert.Contains("Git-only", output);
        Assert.Contains("generation=3", output);
        Assert.DoesNotContain("synthetic-token", output);
    }

    [Fact]
    public async Task LaunchRequiresBothExplicitPoliciesAndSendsSelectedRepositoryAndResolvedSha()
    {
        string[] options = ["launch", "workspace", "--organization", "org", "--image", "image", "--profile", "Heavy",
            "--plan", "plan", "--repository", "https://git.example/repo.git", "--base-ref", "refs/heads/main",
            "--base-commit", new string('a', 40), "--idempotency-key", "launch-once"];
        Assert.Equal(CliExitCodes.ValidationError, await Run(options));
        Assert.Equal(CliExitCodes.ValidationError, await Run([.. options, "--push-authorized"]));
        Assert.DoesNotContain(_handler.Requests, x => x.Method == "POST");
        Assert.Equal(CliExitCodes.Success, await Run([.. options, "--push-authorized", "--acknowledge-ignored-data-loss"]));
        var request = _handler.Requests.Last();
        Assert.EndsWith("/workspace/vms/ephemeral?organizationId=org", request.Path);
        Assert.True(request.Body.GetProperty("pushAuthorized").GetBoolean());
        Assert.True(request.Body.GetProperty("acknowledgeIgnoredDataLoss").GetBoolean());
        Assert.Equal(new string('a', 40), request.Body.GetProperty("baseCommit").GetString());
        Assert.Equal("launch-once", request.Body.GetProperty("idempotencyKey").GetString());
    }

    [Theory]
    [InlineData("vm-start", "start")]
    [InlineData("vm-stop", "stop")]
    [InlineData("vm-cancel-idle-stop", "cancel-idle-stop")]
    public async Task VmControlsBindToSelectedVmAndGeneration(string command, string operation)
    {
        Assert.Equal(CliExitCodes.ValidationError, await Run(command, "workspace", "--organization", "org", "--vm", "ephemeral"));
        Assert.Equal(CliExitCodes.Success, await Run(command, "workspace", "--organization", "org", "--vm", "ephemeral", "--generation", "3"));
        var request = _handler.Requests.Last();
        Assert.Equal($"/api/development-workspaces/workspace/vms/ephemeral/{operation}?organizationId=org", request.Path);
        Assert.Equal(3, request.Body.GetProperty("generation").GetInt64());
    }

    [Fact]
    public async Task InitialPrimaryGenerationZeroCanBeExplicitlyStartedOrResized()
    {
        Assert.Equal(CliExitCodes.Success, await Run("vm-start", "workspace", "--organization", "org", "--vm", "primary", "--generation", "0"));
        Assert.Equal(0, _handler.Requests.Last().Body.GetProperty("generation").GetInt64());
        Assert.Equal(CliExitCodes.Success, await Run("resize-next-start", "workspace", "--organization", "org", "--generation", "0",
            "--image", "image", "--profile", "Heavy", "--plan", "heavy-plan"));
        Assert.Equal(0, _handler.Requests.Last().Body.GetProperty("generation").GetInt64());
    }

    [Fact]
    public async Task DiscardRequiresGenerationAndSeparateIrreversibleDataAcknowledgements()
    {
        string[] options = ["vm-discard", "workspace", "--organization", "org", "--vm", "ephemeral", "--generation", "3"];
        Assert.Equal(CliExitCodes.ValidationError, await Run(options));
        Assert.Equal(CliExitCodes.ValidationError, await Run([.. options, "--discard-local-data"]));
        Assert.DoesNotContain(_handler.Requests, x => x.Method == "POST");
        Assert.Equal(CliExitCodes.Success, await Run([.. options, "--discard-local-data", "--acknowledge-ignored-data-loss"]));
        var body = _handler.Requests.Last().Body;
        Assert.Equal(3, body.GetProperty("generation").GetInt64());
        Assert.True(body.GetProperty("discardLocalData").GetBoolean());
        Assert.True(body.GetProperty("acknowledgeIgnoredDataLoss").GetBoolean());
        Assert.Contains("IRREVERSIBLE", string.Join('\n', _console.Messages));
    }

    [Theory]
    [InlineData("resize-next-start", "resize")]
    [InlineData("vm-resize-next-start", "vms/ephemeral/resize")]
    public async Task ResizeIsNextStartOnlyAndGenerationBound(string command, string route)
    {
        Assert.Equal(CliExitCodes.Success, await Run(command, "workspace", "--organization", "org", "--vm", "ephemeral",
            "--generation", "3", "--image", "image", "--profile", "Heavy", "--plan", "heavy-plan"));
        var request = _handler.Requests.Last();
        Assert.Equal($"/api/development-workspaces/workspace/{route}?organizationId=org", request.Path);
        Assert.Equal("heavy-plan", request.Body.GetProperty("planId").GetString());
        Assert.Equal(3, request.Body.GetProperty("generation").GetInt64());
    }

    [Theory]
    [InlineData("task-renew", "PUT", "lease")]
    [InlineData("task-complete", "POST", "complete")]
    public async Task TaskLeaseUsesVmGenerationAndExplicitCompletion(string command, string method, string operation)
    {
        Assert.Equal(CliExitCodes.Success, await Run(command, "workspace", "--organization", "org", "--vm", "ephemeral",
            "--generation", "3", "--task", "build", "--cancelled"));
        var request = _handler.Requests.Last();
        Assert.Equal(method, request.Method);
        Assert.Equal($"/api/development-workspaces/workspace/vms/ephemeral/tasks/build/{operation}?organizationId=org", request.Path);
        Assert.Equal(3, request.Body.GetProperty("generation").GetInt64());
        if (command == "task-complete") Assert.True(request.Body.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task StopAllEnumeratesAffectedVmsAndDisplaysActionableGitSaveGuards()
    {
        Assert.Equal(CliExitCodes.Success, await Run("stop-all", "workspace", "--organization", "org"));
        Assert.Equal("GET", _handler.Requests[^2].Method);
        Assert.Equal("POST", _handler.Requests[^1].Method);
        var output = string.Join('\n', _console.Messages);
        Assert.Contains("VM primary", output);
        Assert.Contains("VM ephemeral", output);
        Assert.Contains("Awaiting Git save", output);
        Assert.Contains("remote_unavailable", output);
        Assert.Contains("retry stop", output);
    }

    public void Dispose() => _directory.Delete(true);

    private sealed record Captured(string Method, string Path, JsonElement Body);
    private sealed class Handler : HttpMessageHandler
    {
        public List<Captured> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            var json = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new(request.Method.Method, path, JsonDocument.Parse(json).RootElement.Clone()));
            object body = path == "/api/me" ? CliApiFixtures.Me("org", "other")
                : path.Contains("/tasks/") ? new { vmId = "ephemeral", taskId = "build", generation = 3, status = "active", expiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }
                : path.EndsWith("/vms?organizationId=org") ? Vms
                : new { id = "workspace", name = "Coding", organizationId = "org", volumeSizeGb = 20, currency = "EUR", vms = Vms };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
        private static readonly object[] Vms = [
            new { id = "primary", kind = "primary", generation = 3, desiredState = "running", phase = "ready", profile = "Standard", sku = "sku", region = "region" },
            new { id = "ephemeral", kind = "ephemeral", generation = 3, desiredState = "stopped", phase = "awaiting_git_save",
                profile = "Heavy", sku = "sku", region = "region", errorCode = "remote_unavailable", errorMessage = "Restore remote access", gitStatus = "blocked" },
        ];
    }
    private sealed class Console : IConsole
    {
        public List<string> Messages = [];
        public bool SupportsAnsi => false;
        public void Write(string message) => Messages.Add(message);
        public void WriteLine(string message = "") => Messages.Add(message);
        public void WriteError(string message) => Messages.Add(message);
        public string? ReadLine() => null;
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
