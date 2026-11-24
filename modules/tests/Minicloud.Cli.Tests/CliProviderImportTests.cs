// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;

namespace Minicloud.Tests;

public sealed class CliProviderImportTests
{
    [Fact]
    public async Task Add_reads_provider_environment_credential_without_printing_it()
    {
        var previous = Environment.GetEnvironmentVariable("HCLOUD_TOKEN");
        Environment.SetEnvironmentVariable("HCLOUD_TOKEN", "synthetic-provider-import-token");
        var directory = Directory.CreateTempSubdirectory("provider-add-");
        try
        {
            var handler = new Handler(); var console = new Console();
            var application = Application(directory.FullName, handler, console);
            Assert.Equal(CliExitCodes.Success, await application.RunAsync(["providers", "add", "--organization", "org", "--name", "Secondary", "--provider", "hetzner"], default));
            Assert.Equal("/api/providers", handler.Path);
            Assert.Equal("synthetic-provider-import-token", handler.Body!.RootElement.GetProperty("credential").GetString());
            Assert.DoesNotContain("synthetic-provider-import-token", string.Join('\n', console.Messages));
            Assert.Contains(console.Messages, x => x.Contains("Secondary", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable("HCLOUD_TOKEN", previous); directory.Delete(true); }
    }

    [Fact]
    public async Task Add_prefers_selfrun_credential_env_var_and_falls_back_to_minicloud_alias()
    {
        var prevSelfrun = Environment.GetEnvironmentVariable("SELFRUN_GCP_CREDENTIAL_JSON");
        var prevLegacy = Environment.GetEnvironmentVariable("MINICLOUD_GCP_CREDENTIAL_JSON");
        var prevSa = Environment.GetEnvironmentVariable("GCP_SERVICE_ACCOUNT_JSON");
        var prevGac = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS_JSON");
        var prevToken = Environment.GetEnvironmentVariable("GCP_ACCESS_TOKEN");
        var directory = Directory.CreateTempSubdirectory("provider-add-alias-");
        try
        {
            Environment.SetEnvironmentVariable("GCP_SERVICE_ACCOUNT_JSON", null);
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS_JSON", null);
            Environment.SetEnvironmentVariable("GCP_ACCESS_TOKEN", null);

            const string selfrunJson = """{"projectId":"proj-1","accessToken":"synthetic-selfrun-gcp-token"}""";
            const string legacyJson = """{"projectId":"proj-1","accessToken":"synthetic-legacy-gcp-token"}""";

            Environment.SetEnvironmentVariable("SELFRUN_GCP_CREDENTIAL_JSON", selfrunJson);
            Environment.SetEnvironmentVariable("MINICLOUD_GCP_CREDENTIAL_JSON", legacyJson);
            var handler = new Handler(); var console = new Console();
            var application = Application(directory.FullName, handler, console);
            Assert.Equal(CliExitCodes.Success, await application.RunAsync(["providers", "add", "--organization", "org", "--name", "Gcp Primary", "--provider", "gcp", "--project-id", "proj-1"], default));
            Assert.Equal(selfrunJson, handler.Body!.RootElement.GetProperty("credential").GetString());
            Assert.DoesNotContain("synthetic-selfrun-gcp-token", string.Join('\n', console.Messages));

            Environment.SetEnvironmentVariable("SELFRUN_GCP_CREDENTIAL_JSON", null);
            Assert.Equal(CliExitCodes.Success, await application.RunAsync(["providers", "add", "--organization", "org", "--name", "Gcp Legacy", "--provider", "gcp", "--project-id", "proj-1"], default));
            Assert.Equal(legacyJson, handler.Body!.RootElement.GetProperty("credential").GetString());
            Assert.DoesNotContain("synthetic-legacy-gcp-token", string.Join('\n', console.Messages));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SELFRUN_GCP_CREDENTIAL_JSON", prevSelfrun);
            Environment.SetEnvironmentVariable("MINICLOUD_GCP_CREDENTIAL_JSON", prevLegacy);
            Environment.SetEnvironmentVariable("GCP_SERVICE_ACCOUNT_JSON", prevSa);
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS_JSON", prevGac);
            Environment.SetEnvironmentVariable("GCP_ACCESS_TOKEN", prevToken);
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Import_requires_explicit_resource_arrays_and_sends_only_the_selected_account()
    {
        var directory = Directory.CreateTempSubdirectory("provider-import-");
        try
        {
            var path = Path.Combine(directory.FullName, "manifest.json");
            var handler = new Handler(); var console = new Console();
            var application = Application(directory.FullName, handler, console);
            await File.WriteAllTextAsync(path, """{"organizationId":"org","providerAccountId":"provider_a","appBindings":[{"appId":"app_a","planName":"Small","allowFailedDeletion":true}],"retainedVolumeIds":[]}""");
            Assert.Equal(CliExitCodes.Success, await application.RunAsync(["providers", "import-legacy", "--manifest", path], default));
            Assert.Equal("/api/providers/provider_a/import-legacy", handler.Path);
            Assert.Equal("app_a", handler.Body!.RootElement.GetProperty("appBindings")[0].GetProperty("appId").GetString());
            Assert.True(handler.Body.RootElement.GetProperty("appBindings")[0].GetProperty("allowFailedDeletion").GetBoolean());
            handler.Path = null;
            await File.WriteAllTextAsync(path, """{"organizationId":"org","providerAccountId":"provider_a","appBindings":[]}""");
            Assert.Equal(CliExitCodes.ValidationError, await application.RunAsync(["providers", "import-legacy", "--manifest", path], default));
            Assert.Null(handler.Path);
        }
        finally { directory.Delete(true); }
    }

    private static CliApplication Application(string home, HttpMessageHandler handler, IConsole console)
    {
        var environment = CliEnvironment.ForTests("https://api.example", home);
        var tokens = new TokenStore(environment); tokens.SaveToken("mc_test");
        return new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, new HttpClient(handler)));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? Path;
        public JsonDocument? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            object response = Path.EndsWith("/import-legacy")
                ? new { createdPlans = 1, boundApps = 1, boundServers = 1, boundRuntimes = 1, boundVolumes = 0 }
                : new { id = "provider_a", organizationId = "org", name = "Secondary", providerType = "hetzner", enabled = true, validationStatus = "unchecked", revision = 1, planCount = 0 };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
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
