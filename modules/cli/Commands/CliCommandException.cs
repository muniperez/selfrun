// SPDX-License-Identifier: MPL-2.0

namespace Selfrun.Cli.Commands;

internal sealed class CliCommandException : Exception
{
    public CliCommandException(int exitCode, string message)
        : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
