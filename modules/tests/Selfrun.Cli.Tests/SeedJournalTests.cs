// SPDX-License-Identifier: MPL-2.0
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Selfrun.Cli;
using Selfrun.Cli.Api;
using Selfrun.Cli.Auth;
using Selfrun.Cli.Commands;
using Selfrun.Cli.Config;
using Selfrun.Cli.Seed;

namespace Selfrun.Tests;

public sealed class SeedJournalTests : IDisposable
{
    private readonly string home = Path.GetFullPath(Path.Combine(".local", "artifacts", "seed-journal-tests-" + Guid.NewGuid().ToString("N")));
    public void Dispose() { if (Directory.Exists(home)) Directory.Delete(home, true); }

    [Fact]
    public void Persistent_lock_excludes_a_second_invocation()
    {
        using var first = new SeedJournal(home, new string('a', 32), true);
        Assert.Throws<CliCommandException>(() => new SeedJournal(home, new string('a', 32)));
    }

    [Fact]
    public void Interrupted_write_is_not_interpreted_as_a_fresh_operation()
    {
        using var journal = new SeedJournal(home, new string('a', 32), true);
        SeedFiles.WriteNew(Path.Combine(journal.DirectoryPath, "operation.pending"), "{}"u8.ToArray());
        Assert.Throws<CliCommandException>(() => journal.Load());
    }

    [Fact]
    public void Publishing_guided_state_never_replaces_a_committed_dispatch_marker()
    {
        SeedFiles.Directory(home);
        var dispatch = Path.Combine(home, "create-dispatched.json");
        var first = dispatch + ".pending-original";
        var later = dispatch + ".pending-later";
        SeedFiles.WriteNew(first, "original dispatch"u8.ToArray());
        SeedJournal.PublishDurably(first, dispatch, overwrite: false);
        SeedFiles.WriteNew(later, "later dispatch"u8.ToArray());

        Assert.Throws<CliCommandException>(() => SeedJournal.PublishDurably(later, dispatch, overwrite: false));
        SeedFiles.Check(dispatch);
        Assert.Equal("original dispatch", File.ReadAllText(dispatch));
        Assert.Equal("later dispatch", File.ReadAllText(later));
    }

    [Fact]
    public void Credential_input_rejects_links_and_public_permissions()
    {
        if (OperatingSystem.IsWindows()) return;
        SeedFiles.Directory(home);
        var path = Path.Combine(home, "credential");
        SeedFiles.WriteNew(path, "synthetic-secret"u8.ToArray());
        var link = Path.Combine(home, "credential-link");
        File.CreateSymbolicLink(link, path);
        Assert.Throws<CliCommandException>(() => SeedFiles.Check(link));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        Assert.Throws<CliCommandException>(() => SeedFiles.Check(path));
    }

    [Fact]
    public void Protected_export_sink_never_replaces_existing_recovery_bytes()
    {
        SeedFiles.Directory(home);
        var path = Path.Combine(home, "openbao-recovery.json");
        SeedFiles.WriteNew(path, "synthetic-original-recovery"u8.ToArray());
        Assert.Throws<IOException>(() => SeedFiles.WriteNew(path, "synthetic-replacement"u8.ToArray()));
        SeedFiles.Check(path);
        Assert.Equal("synthetic-original-recovery", File.ReadAllText(path));
    }

    [Fact]
    public void Seed_instance_addition_preserves_selection_and_rejects_conflicting_profiles()
    {
        var store = new InstanceStore(home);
        var state = new InstanceStore.State { Active = "original", Instances = [new("original", "https://old.example.com", "apps.old.example.com")] };
        store.Save(state);
        var seed = new InstanceProfile("seed", "https://cloud.example.com", "apps.example.com");
        store.AddWithoutSelecting(seed);
        Assert.Equal("original", store.Load().Active);
        Assert.Contains(seed, store.Load().Instances);
        Assert.Throws<CliCommandException>(() => store.AddWithoutSelecting(seed with { Url = "https://different.example.com" }));
        Assert.Equal(2, store.Load().Instances.Count);
    }

    [Fact]
    public void Signed_inventory_contract_rejects_tampering_and_duplicates_before_execution()
    {
        SeedFiles.Directory(home);
        var path = Path.Combine(home, "server-manifest.json");
        SeedFiles.WriteNew(path, "{}"u8.ToArray());
        var digest = SeedGuard.Hash("{}");
        SeedFiles.WriteNew(Path.Combine(home, "checksums.txt"), System.Text.Encoding.UTF8.GetBytes($"{digest}  server-manifest.json\n"));
        SeedRelease.VerifyInventory(home, new HashSet<string> { "server-manifest.json" });
        File.WriteAllText(path, "{\"tampered\":true}");
        Assert.Throws<CliCommandException>(() => SeedRelease.VerifyInventory(home, new HashSet<string> { "server-manifest.json" }));
        File.WriteAllText(path, "{}");
        File.AppendAllText(Path.Combine(home, "checksums.txt"), $"{digest}  server-manifest.json\n");
        Assert.Throws<CliCommandException>(() => SeedRelease.VerifyInventory(home, new HashSet<string> { "server-manifest.json" }));
    }

    [Theory]
    [InlineData("selfrun-release")]
    [InlineData("selfrun")]
    public void Signed_archive_preserves_current_and_historical_image_bindings(string family)
    {
        SeedFiles.Directory(home);
        var images = new[] { "api", "dashboard", "worker" }.ToDictionary(
            name => name, name => $"ghcr.io/muniventures/{family}-{name}@sha256:{new string('b', 64)}");
        var core = JsonSerializer.Serialize(new { schemaVersion = 1, images });
        var files = new Dictionary<string, string>
        {
            ["server-release.json"] = core, ["server_install.py"] = "# synthetic fixture"
        };
        var bundleName = "selfrun-server-1.2.3-rc.1.tar.gz";
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true))
        using (var tar = new TarWriter(gzip))
            foreach (var (name, text) in files)
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(text));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
            }
        SeedFiles.WriteNew(Path.Combine(home, bundleName), buffer.ToArray());
        var hashes = files.ToDictionary(pair => pair.Key, pair => SeedGuard.Hash(pair.Value));
        var verifier = typeof(SeedRelease).GetMethod("VerifyArchive", BindingFlags.NonPublic | BindingFlags.Static)!;
        JsonDocument Manifest() => JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, images, bundle = new { name = bundleName }, files = hashes
        }));
        using (var manifest = Manifest()) verifier.Invoke(null, [home, manifest.RootElement]);

        var original = images["api"];
        images["api"] = images["worker"];
        using (var manifest = Manifest())
        {
            var error = Assert.Throws<TargetInvocationException>(() => verifier.Invoke(null, [home, manifest.RootElement]));
            Assert.IsType<CliCommandException>(error.InnerException);
            Assert.Contains("Runtime manifest differs", error.InnerException!.Message);
        }
        images["api"] = original;
        hashes["server_install.py"] = new string('c', 64);
        using (var manifest = Manifest())
        {
            var error = Assert.Throws<TargetInvocationException>(() => verifier.Invoke(null, [home, manifest.RootElement]));
            Assert.IsType<CliCommandException>(error.InnerException);
            Assert.Contains("member digest mismatch", error.InnerException!.Message);
        }
    }

    [Fact]
    public void Release_compatibility_refuses_an_unlisted_source_without_a_version_range()
    {
        using var compatibility = JsonDocument.Parse("""
            {"protocolVersion":1,"minimumVersion":null,"maximumExclusiveVersion":null,"sourceRevisions":["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"]}
            """);
        Assert.Throws<CliCommandException>(() => SeedRelease.RequireCompatible(compatibility.RootElement));
    }

    [Theory]
    [InlineData("seed", "--api-key", "synthetic-secret")]
    [InlineData("seed", "--domain", "127.0.0.1")]
    [InlineData("seed", "resume", "not-an-operation")]
    public async Task Seed_validation_bypasses_instance_discovery_and_does_not_echo_secret(params string[] args)
    {
        var environment = CliEnvironment.ForTests("", home);
        var console = new ConsoleFixture();
        var token = new TokenStore(environment);
        var application = new CliApplication(console, environment, token,
            new SelfrunApiClient(environment, token, new HttpClient(new NoRequests())));
        Assert.Equal(CliExitCodes.ValidationError, await application.RunAsync(args, default));
        Assert.DoesNotContain("synthetic-secret", console.Output);
        Assert.Equal("", environment.ApiBaseUrl);
        Assert.False(File.Exists(Path.Combine(home, "instances.json")));
    }

    private sealed class NoRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Instance API must not be used by offline seed.");
    }

    private sealed class ConsoleFixture : IConsole
    {
        public string Output = "";
        public bool SupportsAnsi => false;
        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message;
        public void WriteError(string message) => Output += message;
        public string? ReadLine() => throw new InvalidOperationException("No input expected.");
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException("No hidden input expected.");
    }
}
