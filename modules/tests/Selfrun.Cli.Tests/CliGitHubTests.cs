// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Http.Json;
using Selfrun.Cli;
using Selfrun.Cli.Api;
using Selfrun.Cli.Auth;
using Selfrun.Cli.Commands;

namespace Selfrun.Tests;

public sealed class CliGitHubTests
{
    [Theory]
    [InlineData("setup", "https://console.example/settings/github?organizationId=org")]
    [InlineData("status", "app_created")]
    [InlineData("repositories", "fixture/private")]
    public async Task BrowserSetupAndReadCommandsUseTheSelectedTenantWithoutAppSecrets(string command, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("selfrun-github-");
        try
        {
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment); tokens.SaveToken("synthetic-token");
            var handler = new Handler();
            var console = new Console();
            var application = new CliApplication(console, environment, tokens, new SelfrunApiClient(environment, tokens, new HttpClient(handler)));
            Assert.Equal(CliExitCodes.Success, await application.RunAsync(["github", command, "--organization", "org", "--no-browser"], default));
            Assert.Contains(expected, string.Join('\n', console.Messages));
            Assert.DoesNotContain("synthetic-token", string.Join('\n', console.Messages));
            Assert.All(handler.Requests, request => Assert.StartsWith("/api/", request));
            if (command is "status" or "setup") Assert.Contains(handler.Requests, request => request.Contains("organizationId=org"));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task MultiOrganizationSetupRequiresAnExplicitSelectionBeforeOpeningABrowser()
    {
        var directory = Directory.CreateTempSubdirectory("selfrun-github-");
        try
        {
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment); tokens.SaveToken("synthetic-token");
            var handler = new Handler(); var console = new Console();
            var application = new CliApplication(console, environment, tokens, new SelfrunApiClient(environment, tokens, new HttpClient(handler)));
            Assert.Equal(CliExitCodes.ValidationError, await application.RunAsync(["github", "setup", "--no-browser"], default));
            Assert.Single(handler.Requests);
        }
        finally { directory.Delete(true); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery; Requests.Add(path);
            object body = path switch
            {
                "/api/me" => CliApiFixtures.Me("org", "other"),
                "/api/github-apps/setup-url?organizationId=org" => new { url = "https://console.example/settings/github?organizationId=org" },
                "/api/github-apps?organizationId=org" => new { organizationId = "org", status = "app_created", connectionId = "connection", appSlug = "fixture" },
                "/api/github-apps/connection/repositories" => new[] { new { id = 1, fullName = "fixture/private", @private = true } },
                _ => throw new InvalidOperationException(path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
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
