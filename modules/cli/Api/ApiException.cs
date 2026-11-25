// SPDX-License-Identifier: MPL-2.0

namespace Selfrun.Cli.Api;

public sealed class ApiException : Exception
{
    public ApiException(
        int statusCode,
        string code,
        string message,
        IReadOnlyList<Selfrun.Common.Contracts.ErrorDetail>? details = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Details = details ?? [];
    }

    public int StatusCode { get; }
    public string Code { get; }
    public IReadOnlyList<Selfrun.Common.Contracts.ErrorDetail> Details { get; }
}
