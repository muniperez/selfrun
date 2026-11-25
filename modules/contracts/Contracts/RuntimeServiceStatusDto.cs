// SPDX-License-Identifier: MPL-2.0

namespace Selfrun.Common.Contracts;

public sealed record RuntimeServiceStatusDto(
    string Name,
    string? Container,
    string State,
    string? Health,
    string? Image,
    int? RestartCount);
