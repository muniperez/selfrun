// SPDX-License-Identifier: MPL-2.0

namespace Selfrun.Common.Contracts;

public sealed record RuntimeLogResponse(
    string Id,
    string ServerId,
    string Source,
    string? Service,
    string Stream,
    string Content,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt);
