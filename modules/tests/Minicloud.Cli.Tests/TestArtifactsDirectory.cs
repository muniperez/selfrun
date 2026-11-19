// SPDX-License-Identifier: MPL-2.0
using System.Runtime.CompilerServices;

namespace Minicloud.Cli.Tests;

internal static class TestArtifactsDirectory
{
    // Tests share the relative .local/artifacts parent. Seed tests would create it as 0700 and require that
    // exact mode, while other tests running in parallel create it with the default mode, so whichever ran
    // first decided the outcome. Create it once, before any test, with a mode valid for both.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        foreach (var path in new[] { ".local", Path.Combine(".local", "artifacts") })
        {
            var full = Path.GetFullPath(path);
            if (System.IO.Directory.Exists(full)) continue;
            System.IO.Directory.CreateDirectory(full);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
