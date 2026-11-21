// SPDX-License-Identifier: MPL-2.0
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minicloud.Cli.Seed;

internal static class SeedLocalSource
{
    internal const string Trust = "operator-pinned-local-source";
    private const string SourceResource = "Minicloud.Cli.Seed.source.tar";
    internal static string? EmbeddedTag => typeof(SeedLocalSource).Assembly.GetManifestResourceInfo(SourceResource) is not null
        && SeedRelease.CliSourceRevision is { Length: 40 } revision ? "server-v0.0.1-local." + revision : null;

    internal static string PrepareEmbedded(string archive, string tag)
    {
        var revision = SeedRelease.CliSourceRevision;
        SeedGuard.Require(tag == EmbeddedTag, "This executable does not carry the selected committed source. Resume with the original executable.");
        if (!File.Exists(archive))
        {
            var pending = archive + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var source = typeof(SeedLocalSource).Assembly.GetManifestResourceStream(SourceResource)!)
                using (var reader = new TarReader(source))
                using (var output = SeedFiles.Create(pending))
                {
                    using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
                    {
                        // git archive records the selected commit in its global PAX header.
                        var header = reader.GetNextEntry() as PaxGlobalExtendedAttributesTarEntry;
                        SeedGuard.Require(header?.GlobalExtendedAttributes.GetValueOrDefault("comment") == revision,
                            "Embedded source revision differs from this executable.");
                        while (reader.GetNextEntry() is { } entry)
                            writer.WriteEntry(entry);
                        var descriptor = new
                        {
                            schemaVersion = 1, kind = Trust, sourceRevision = revision, serverVersion = tag[8..],
                            cliCompatibility = new { protocolVersion = 1, minimumVersion = (string?)null,
                                maximumExclusiveVersion = (string?)null, sourceRevisions = new[] { revision } }
                        };
                        using var data = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(descriptor));
                        writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "minicloud-local-source.json")
                        {
                            DataStream = data, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                            ModificationTime = DateTimeOffset.UnixEpoch
                        });
                    }
                    output.Flush(true);
                }
                SeedJournal.PublishDurably(pending, archive, overwrite: false);
            }
            finally { File.Delete(pending); }
        }
        SeedFiles.Check(archive);
        using var input = File.OpenRead(archive);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    internal static SeedVerifiedRelease Verify(string archive, string expectedDigest, string tag, string directory)
    {
        SeedFiles.Check(archive);
        SeedGuard.Require(Regex.IsMatch(expectedDigest, "^[0-9a-f]{64}$")
            && new FileInfo(archive).Length is > 0 and <= 256 * 1024 * 1024,
            "Local source requires an explicit SHA256 and a bounded protected archive.");
        SeedFiles.Directory(directory);
        var copy = Path.Combine(directory, "source.tar.gz");
        using (var input = File.OpenRead(archive))
        using (var output = SeedFiles.Create(copy)) input.CopyTo(output);
        using (var input = File.OpenRead(copy))
            SeedGuard.Require(Convert.ToHexStringLower(SHA256.HashData(input)) == expectedDigest,
                "Local source archive differs from the operator-approved SHA256.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        byte[]? descriptor = null, runner = null;
        long expanded = 0;
        using var file = File.OpenRead(copy);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            var name = entry.Name.TrimEnd('/');
            expanded += entry.Length;
            SeedGuard.Require(SeedRelease.SafeMember(name) && seen.Add(name)
                && !name.Split('/').Any(x => x is ".git" or ".local")
                && entry.EntryType is TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile
                && entry.Length is >= 0 and <= 64 * 1024 * 1024 && expanded <= 512L * 1024 * 1024
                && seen.Count <= 20000, "Unsafe or oversized local source archive.");
            if (name is not ("minicloud-local-source.json" or "scripts/self-hosting/local_source.py")) continue;
            SeedGuard.Require(entry.EntryType != TarEntryType.Directory && entry.Length <= 1048576,
                "Invalid local source descriptor/runner.");
            using var bytes = new MemoryStream();
            entry.DataStream!.CopyTo(bytes);
            if (name == "minicloud-local-source.json") descriptor = bytes.ToArray();
            else runner = bytes.ToArray();
        }
        SeedGuard.Require(descriptor is not null && runner is not null
            && new[] { "modules/api/Dockerfile", "modules/dashboard/Dockerfile", "modules/worker/Dockerfile",
                "packaging/server/bundle/server_install.py", "packaging/server/release.json" }.All(seen.Contains),
            "Incomplete local source archive.");
        using var metadata = SeedPortable.ReadDocument(System.Text.Encoding.UTF8.GetString(descriptor));
        var root = metadata.RootElement;
        SeedGuard.Require(root.EnumerateObject().Select(x => x.Name).ToHashSet().SetEquals(
            ["schemaVersion", "kind", "sourceRevision", "serverVersion", "cliCompatibility"])
            && root.GetProperty("schemaVersion").GetInt32() == 1
            && root.GetProperty("kind").GetString() == Trust
            && "server-v" + root.GetProperty("serverVersion").GetString() == tag
            && Regex.IsMatch(root.GetProperty("sourceRevision").GetString() ?? "", "^[0-9a-f]{40}$"),
            "Local source identity/version differs from the selected deployment.");
        SeedRelease.RequireCompatible(root.GetProperty("cliCompatibility"));
        SeedFiles.WriteNew(Path.Combine(directory, "local-source.py"), runner);
        return new(expectedDigest, root.GetProperty("sourceRevision").GetString()!);
    }
}
