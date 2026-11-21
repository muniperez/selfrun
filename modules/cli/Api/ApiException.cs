// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Cli.Api;

public sealed class ApiException : Exception
{
    public ApiException(
        int statusCode,
        string code,
        string message,
        IReadOnlyList<Minicloud.Common.Contracts.ErrorDetail>? details = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Details = details ?? [];
    }

    public int StatusCode { get; }
    public string Code { get; }
    public IReadOnlyList<Minicloud.Common.Contracts.ErrorDetail> Details { get; }
}
