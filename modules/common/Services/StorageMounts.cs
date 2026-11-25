using System.Text.Json;
using Selfrun.Common.Contracts;

namespace Selfrun.Common.Services;

public static class StorageMounts
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static IReadOnlyDictionary<string, StorageMountRequest> Read(string? json) =>
        json is null ? new Dictionary<string, StorageMountRequest>() : JsonSerializer.Deserialize<Dictionary<string, StorageMountRequest>>(json, Options)!;
}
