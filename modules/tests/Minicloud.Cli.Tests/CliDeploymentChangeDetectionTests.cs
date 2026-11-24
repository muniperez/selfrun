// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.IO.Compression;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;
using Minicloud.Common.Contracts;

namespace Minicloud.Tests;

public sealed class CliDeploymentChangeDetectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Git_ignored_build_context_fails_before_upload_and_included_retry_succeeds(bool ignoreDockerfile)
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-ignored-deploy-");
        try
        {
            RunGit(directory.FullName, "init", "--quiet");
            var source = Directory.CreateDirectory(Path.Combine(directory.FullName, ".local", "app"));
            File.WriteAllText(Path.Combine(source.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 3000\nCOPY index.js /index.js\nCMD [\"node\", \"/index.js\"]");
            File.WriteAllText(Path.Combine(source.FullName, "index.js"), "console.log('test');");
            var dependencies = Directory.CreateDirectory(Path.Combine(source.FullName, "node_modules"));
            File.WriteAllText(Path.Combine(dependencies.FullName, "ignored.js"), "not uploaded");
            var ignorePath = Path.Combine(directory.FullName, ".gitignore");
            File.WriteAllText(ignorePath, ignoreDockerfile ? "Dockerfile\nnode_modules/\n" : ".local/\nnode_modules/\n");
            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            File.WriteAllText(configPath, $$"""
                app: demo
                appId: app_main
                services:
                  web:
                    buildMode: source
                    sourcePath: {{source.FullName}}
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """);
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new ChangeDetectionHandler(new Dictionary<string, string>());
            using var http = new HttpClient(handler);
            var app = new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, http));

            Assert.Equal(CliExitCodes.ValidationError, await app.RunAsync(["deploy", "--config", configPath], default));
            Assert.Contains("ignored by git", console.Output);
            Assert.Contains(".gitignore:1", console.Output);
            Assert.Contains("own repository", console.Output);
            Assert.Contains("Nothing has been uploaded", console.Output);
            Assert.Contains(ignoreDockerfile ? Path.Combine(source.FullName, "Dockerfile") : source.FullName, console.Output);
            Assert.Equal(0, handler.ArtifactsCreatedCount);
            Assert.Equal(0, handler.DeploymentsCreatedCount);

            File.WriteAllText(ignorePath, "node_modules/\n");
            Assert.True(await app.RunAsync(["deploy", "--config", configPath], default) == CliExitCodes.Success, console.Output);
            Assert.Equal(1, handler.ArtifactsCreatedCount);
            Assert.Equal(1, handler.DeploymentsCreatedCount);
            Assert.Contains("Dockerfile", handler.UploadedEntries);
            Assert.Contains("index.js", handler.UploadedEntries);
            Assert.DoesNotContain(handler.UploadedEntries, path => path.Contains("node_modules"));
        }
        finally { directory.Delete(true); }
    }

    private static void RunGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seed_default_deploy_fails_before_upload_and_source_retry_succeeds(bool saveSourceMode)
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-seed-deploy-");
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(directory.FullName, "app"));
            File.WriteAllText(Path.Combine(source.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 3000\nCMD [\"node\", \"index.js\"]");
            File.WriteAllText(Path.Combine(source.FullName, "index.js"), "console.log('test');");
            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            var yaml = $$"""
                app: demo
                appId: app_main
                services:
                  web:
                    sourcePath: {{source.FullName}}
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """;
            File.WriteAllText(configPath, yaml);
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new ChangeDetectionHandler(new Dictionary<string, string>(), registryUnavailable: true);
            using var http = new HttpClient(handler);
            var app = new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, http));

            Assert.Equal(CliExitCodes.ValidationError, await app.RunAsync(["deploy", "--config", configPath], default));
            Assert.Contains("local_build_unavailable", console.Output);
            Assert.Contains("--build-mode source", console.Output);
            Assert.Contains("buildMode: source", console.Output);
            Assert.Contains("Nothing has been uploaded", console.Output);
            Assert.Equal(0, handler.ArtifactsCreatedCount);
            Assert.Equal(0, handler.DeploymentsCreatedCount);

            if (saveSourceMode) { File.WriteAllText(configPath, yaml + "\n    buildMode: source\n"); }
            var args = saveSourceMode
                ? new[] { "deploy", "--config", configPath }
                : new[] { "deploy", "--config", configPath, "--build-mode", "source" };
            Assert.True(await app.RunAsync(args, default) == CliExitCodes.Success, console.Output);
            Assert.Equal(1, handler.ImageBuildTargetsRequestedCount);
            Assert.Equal(1, handler.ArtifactsCreatedCount);
            Assert.Equal(1, handler.DeploymentsCreatedCount);
            Assert.NotNull(handler.LastCreatedRequest!.Services.Single().ArtifactId);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("local")]
    public async Task Deploy_skips_deployment_when_no_services_have_changed(string buildMode)
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-change-detect-");
        try
        {
            var webDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "web"));
            File.WriteAllText(Path.Combine(webDir.FullName, "index.js"), "console.log('web');");
            File.WriteAllText(Path.Combine(webDir.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 3000\nCMD [\"node\", \"index.js\"]");

            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, $$"""
                app: demo
                appId: app_main
                database: sqlite
                services:
                  web:
                    buildMode: {{buildMode}}
                    sourcePath: {{webDir.FullName}}
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """);

            var configResult = MinicloudConfigLoader.Load(configPath);
            var expectedHash = ServiceChecksumCalculator.Compute("web", configResult.Config!.Services["web"] with { BuildMode = "source" });

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");

            var handler = new ChangeDetectionHandler(new Dictionary<string, string>
            {
                ["web"] = expectedHash
            });
            var client = new MinicloudApiClient(environment, tokens, new HttpClient(handler));
            var app = new CliApplication(console, environment, tokens, client);

            var exitCode = await app.RunAsync(["deploy", "--no-publish", "--config", configPath], CancellationToken.None);

            Assert.True(exitCode == CliExitCodes.Success, console.Output);
            Assert.Contains("No services have changed since the last deployment.", console.Output);
            Assert.Contains("Timing: phase=change_detection_hash", console.Output);
            Assert.Contains("duration_ms=", console.Output);
            Assert.Contains("items=1 outcome=succeeded", console.Output);
            Assert.Equal(0, handler.DeploymentsCreatedCount);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deploy_deploys_only_changed_services(bool privateServiceChanged)
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-change-detect-");
        try
        {
            var webDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "web"));
            File.WriteAllText(Path.Combine(webDir.FullName, "index.js"), "console.log('web');");
            File.WriteAllText(Path.Combine(webDir.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 3000\nCMD [\"node\", \"index.js\"]");

            var apiDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "api"));
            File.WriteAllText(Path.Combine(apiDir.FullName, "server.js"), "console.log('api');");
            File.WriteAllText(Path.Combine(apiDir.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 8080\nCMD [\"node\", \"server.js\"]");

            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, $$"""
                app: demo
                appId: app_main
                database: sqlite
                services:
                  web:
                    buildMode: source
                    sourcePath: {{webDir.FullName}}
                    image: ghcr.io/example/web:123
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                  api:
                    buildMode: source
                    sourcePath: {{apiDir.FullName}}
                    image: ghcr.io/example/api:123
                    port: 8080
                    public: false
                    path: /
                    healthPath: /health
                """);

            var configResult = MinicloudConfigLoader.Load(configPath);
            var apiHash = ServiceChecksumCalculator.Compute("api", configResult.Config!.Services["api"]);
            var webHash = ServiceChecksumCalculator.Compute("web", configResult.Config.Services["web"]);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");

            var handler = new ChangeDetectionHandler(new Dictionary<string, string>
            {
                ["api"] = privateServiceChanged ? "old_api_hash_123" : apiHash,
                ["web"] = privateServiceChanged ? webHash : "old_web_hash_123"
            });
            var client = new MinicloudApiClient(environment, tokens, new HttpClient(handler));
            var app = new CliApplication(console, environment, tokens, client);

            var exitCode = await app.RunAsync(["deploy", "--config", configPath], CancellationToken.None);

            Assert.True(exitCode == CliExitCodes.Success, console.Output);
            var changedService = privateServiceChanged ? "api" : "web";
            var unchangedService = privateServiceChanged ? "web" : "api";
            Assert.Contains($"Deploying changed services: {changedService} (skipping unchanged: {unchangedService})", console.Output);
            Assert.Equal(1, handler.DeploymentsCreatedCount);
            Assert.Single(handler.LastCreatedRequest!.Services);
            Assert.Equal(changedService, handler.LastCreatedRequest.Services[0].Name);
            var timingTracePath = Environment.GetEnvironmentVariable("MINICLOUD_CLI_TIMING_TRACE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(timingTracePath))
            {
                await File.WriteAllTextAsync(timingTracePath, console.Output);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Deploy_all_deploys_all_services_regardless_of_hashes()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-change-detect-");
        try
        {
            var webDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "web"));
            File.WriteAllText(Path.Combine(webDir.FullName, "index.js"), "console.log('web');");
            File.WriteAllText(Path.Combine(webDir.FullName, "Dockerfile"), "FROM node:20\nCMD [\"node\", \"index.js\"]");

            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, $$"""
                app: demo
                appId: app_main
                database: sqlite
                services:
                  web:
                    buildMode: source
                    sourcePath: {{webDir.FullName}}
                    image: ghcr.io/example/web:123
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """);

            var configResult = MinicloudConfigLoader.Load(configPath);
            var webHash = ServiceChecksumCalculator.Compute("web", configResult.Config!.Services["web"]);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");

            var handler = new ChangeDetectionHandler(new Dictionary<string, string>
            {
                ["web"] = webHash
            });
            var client = new MinicloudApiClient(environment, tokens, new HttpClient(handler));
            var app = new CliApplication(console, environment, tokens, client);

            var exitCode = await app.RunAsync(["deploy", "all", "--no-publish", "--config", configPath], CancellationToken.None);

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Equal(1, handler.DeploymentsCreatedCount);
            Assert.Single(handler.LastCreatedRequest!.Services);
            Assert.Equal("web", handler.LastCreatedRequest.Services[0].Name);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("local", false)]
    [InlineData("local", true)]
    [InlineData("source", false)]
    public async Task Deploy_rejects_private_only_new_app_before_build_or_upload(string buildMode, bool explicitSelection)
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-private-preflight-");
        try
        {
            var serviceDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "worker"));
            await File.WriteAllTextAsync(Path.Combine(serviceDir.FullName, "Dockerfile"), "FROM node:20\nCMD [\"node\", \"worker.js\"]");
            await File.WriteAllTextAsync(Path.Combine(serviceDir.FullName, "worker.js"), "console.log('worker');");
            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, $$"""
                app: demo
                appId: app_main
                database: sqlite
                services:
                  worker:
                    buildMode: {{buildMode}}
                    sourcePath: {{serviceDir.FullName}}
                    port: 3000
                    public: false
                    path: /
                    healthPath: /health
                """);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new ChangeDetectionHandler(new Dictionary<string, string>());
            var app = new CliApplication(console, environment, tokens,
                new MinicloudApiClient(environment, tokens, new HttpClient(handler)));

            string[] args = explicitSelection
                ? ["deploy", "worker", "--config", configPath]
                : ["deploy", "--config", configPath];
            var exitCode = await app.RunAsync(args, CancellationToken.None);

            Assert.Equal(CliExitCodes.ValidationError, exitCode);
            Assert.Contains("services.worker.public: At least one public service is required", console.Output);
            Assert.Equal(0, handler.ImageBuildTargetsRequestedCount);
            Assert.Equal(0, handler.ArtifactsCreatedCount);
            Assert.Equal(0, handler.DeploymentsCreatedCount);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("web", false, CliExitCodes.Success)]
    [InlineData("web", true, CliExitCodes.Success)]
    [InlineData("worker", true, CliExitCodes.ValidationError)]
    [InlineData(null, true, CliExitCodes.ValidationError)]
    public async Task Scoped_private_deploy_requires_another_active_public_service(
        string? activePublicService,
        bool includePublicServiceInConfig,
        int expectedExitCode)
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-private-scoped-");
        try
        {
            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            var yaml = """
                app: demo
                appId: app_main
                database: sqlite
                services:
                  worker:
                    image: example/worker:latest
                    port: 3000
                    public: false
                    path: /
                    healthPath: /health
                """;
            if (includePublicServiceInConfig)
            {
                yaml += "\n" + """
                      web:
                        image: example/web:latest
                        port: 3000
                        public: true
                        path: /
                        healthPath: /health
                    """;
            }
            await File.WriteAllTextAsync(configPath, yaml);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var activeHashes = new Dictionary<string, string>();
            if (activePublicService is not null)
            {
                activeHashes[activePublicService] = "active-public-hash";
            }
            var handler = new ChangeDetectionHandler(activeHashes);
            var app = new CliApplication(console, environment, tokens,
                new MinicloudApiClient(environment, tokens, new HttpClient(handler)));

            var exitCode = await app.RunAsync(["deploy", "worker", "--no-publish", "--config", configPath], CancellationToken.None);

            Assert.Equal(expectedExitCode, exitCode);
            if (expectedExitCode == CliExitCodes.Success)
            {
                Assert.Single(handler.LastCreatedRequest!.Services);
                Assert.Equal("worker", handler.LastCreatedRequest.Services[0].Name);
            }
            else
            {
                Assert.Equal(0, handler.DeploymentsCreatedCount);
                Assert.Contains("services.worker.public: At least one public service is required", console.Output);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Deployment_validation_prints_field_details_and_returns_validation_error()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-validation-details-");
        try
        {
            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, """
                app: demo
                appId: app_main
                database: sqlite
                services:
                  web:
                    image: example/web:latest
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new ChangeDetectionHandler(new Dictionary<string, string>(), failDeploymentValidation: true);
            var app = new CliApplication(console, environment, tokens,
                new MinicloudApiClient(environment, tokens, new HttpClient(handler)));

            var exitCode = await app.RunAsync(["deploy", "web", "--no-publish", "--config", configPath], CancellationToken.None);

            Assert.Equal(CliExitCodes.ValidationError, exitCode);
            Assert.Contains("API error (validation_failed): Deployment request is invalid.", console.Output);
            Assert.Contains("services[0].port: Port must be between 1 and 65535.", console.Output);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Deploy_does_not_submit_when_artifact_upload_fails()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-upload-fail-");
        try
        {
            var webDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "web"));
            await File.WriteAllTextAsync(Path.Combine(webDir.FullName, "index.js"), "console.log('web');");
            await File.WriteAllTextAsync(Path.Combine(webDir.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 3000\nCMD [\"node\", \"index.js\"]");
            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, $$"""
                app: demo
                appId: app_main
                database: sqlite
                services:
                  web:
                    buildMode: source
                    sourcePath: {{webDir.FullName}}
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new ChangeDetectionHandler(new Dictionary<string, string>(), failArtifactUpload: true);
            var app = new CliApplication(console, environment, tokens,
                new MinicloudApiClient(environment, tokens, new HttpClient(handler)));

            var exitCode = await app.RunAsync(["deploy", "web", "--config", configPath], CancellationToken.None);

            Assert.True(exitCode == CliExitCodes.NetworkOrApiUnavailable, console.Output);
            Assert.Equal(0, handler.DeploymentsCreatedCount);
            Assert.Contains("API error", console.Output);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Deploy_does_not_create_any_artifact_when_later_bundle_fails()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-bundle-fail-");
        try
        {
            var firstDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "first"));
            await File.WriteAllTextAsync(Path.Combine(firstDir.FullName, "index.js"), "console.log('first');");
            await File.WriteAllTextAsync(Path.Combine(firstDir.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 3000");
            var secondDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "second"));
            await File.WriteAllTextAsync(Path.Combine(secondDir.FullName, "index.js"), "console.log('second');");
            await File.WriteAllTextAsync(Path.Combine(secondDir.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 4000");
            await File.WriteAllTextAsync(Path.Combine(secondDir.FullName, DeploymentArtifactBundler.ManifestEntryName), "reserved");
            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, $$"""
                app: demo
                appId: app_main
                database: sqlite
                services:
                  first:
                    buildMode: source
                    sourcePath: {{firstDir.FullName}}
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                  second:
                    buildMode: source
                    sourcePath: {{secondDir.FullName}}
                    port: 4000
                    public: false
                    path: /
                    healthPath: /health
                """);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new ChangeDetectionHandler(new Dictionary<string, string>());
            var app = new CliApplication(console, environment, tokens,
                new MinicloudApiClient(environment, tokens, new HttpClient(handler)));

            var exitCode = await app.RunAsync(["deploy", "all", "--config", configPath], CancellationToken.None);

            Assert.Equal(CliExitCodes.ValidationError, exitCode);
            Assert.Equal(0, handler.ArtifactsCreatedCount);
            Assert.Equal(0, handler.DeploymentsCreatedCount);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class ChangeDetectionHandler(
        IReadOnlyDictionary<string, string> deployedHashes,
        bool failArtifactUpload = false,
        bool failDeploymentValidation = false,
        bool registryUnavailable = false) : HttpMessageHandler
    {
        public int DeploymentsCreatedCount { get; private set; }
        public int ArtifactsCreatedCount { get; private set; }
        public int ImageBuildTargetsRequestedCount { get; private set; }
        public CreateDeploymentRequest? LastCreatedRequest { get; private set; }
        public List<string> UploadedEntries { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/me")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "usr_1",
                        email = "dev@example.com",
                        organizations = new[]
                        {
                            new { id = "org_1", name = "Test Org", slug = "test-org", role = "owner" }
                        }
                    })
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/apps/app_main")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(CliApiFixtures.Application("app_main", "org_1", "app_main")) };

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/environments/app_main")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "app_main",
                        applicationId = "app_main",
                        organizationId = "org_1",
                        name = "Demo",
                        slug = "demo",
                        plan = "p0",
                        database = "sqlite",
                        branches = Array.Empty<object>()
                    })
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/environments/app_main/image-build-target")
            {
                ImageBuildTargetsRequestedCount++;
                if (registryUnavailable)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = JsonContent.Create(new { error = new { code = "local_build_unavailable", message = "Local image publication is not enabled." } })
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { registry = "registry.example", repository = "example", platform = "linux/amd64" })
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/domains/services")
            {
                var services = deployedHashes.Select(kvp => new
                {
                    name = kvp.Key,
                    image = "ghcr.io/example/" + kvp.Key + ":latest",
                    port = 3000,
                    @public = kvp.Key != "api",
                    path = "/",
                    healthPath = "/health",
                    domains = Array.Empty<object>(),
                    sha256 = kvp.Value
                }).ToArray();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(services)
                };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/deployments")
            {
                DeploymentsCreatedCount++;
                LastCreatedRequest = await request.Content!.ReadFromJsonAsync<CreateDeploymentRequest>(cancellationToken: cancellationToken);
                if (failDeploymentValidation)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = JsonContent.Create(new
                        {
                            error = new
                            {
                                code = "validation_failed",
                                message = "Deployment request is invalid.",
                                details = new[]
                                {
                                    new { field = "services[0].port", message = "Port must be between 1 and 65535." }
                                }
                            }
                        })
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "dep_test_123",
                        number = "20261110.4",
                        appId = "app_main",
                        status = "succeeded"
                    })
                };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/artifacts")
            {
                ArtifactsCreatedCount++;
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "art_upload_test",
                        appId = "app_main",
                        serviceName = "web",
                        status = "uploading",
                        uploadUrl = "/api/artifacts/art_upload_test/file"
                    })
                };
            }

            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/api/artifacts/art_upload_test/file")
            {
                using var bytes = new MemoryStream(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
                UploadedEntries.AddRange(archive.Entries.Select(entry => entry.FullName));
                return failArtifactUpload
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = JsonContent.Create(new
                        {
                            error = new { code = "artifact_upload_failed", message = "simulated upload failure" }
                        })
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new
                        {
                            id = "art_upload_test",
                            appId = "app_main",
                            serviceName = "web",
                            status = "ready"
                        })
                    };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/deployments/20261110.4/refresh")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "dep_test_123",
                        number = "20261110.4",
                        appId = "app_main",
                        organizationId = "org_1",
                        database = "sqlite",
                        status = "succeeded",
                        services = Array.Empty<object>()
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class TestConsole : IConsole
    {
        public bool SupportsAnsi => false;
        public string Output { get; private set; } = "";
        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message + Environment.NewLine;
        public void WriteError(string message) => Output += message + Environment.NewLine;
        public string? ReadLine() => throw new InvalidOperationException();
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
