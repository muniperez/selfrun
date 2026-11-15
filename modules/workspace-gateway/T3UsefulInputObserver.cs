using System.Text.Json;

namespace Minicloud.WorkspaceGateway;

public static class T3UsefulInputObserver
{
    public const int MaximumMessageBytes = 1024 * 1024;

    public static bool IsUsefulInput(ReadOnlyMemory<byte> message)
    {
        if (message.Length is 0 or > MaximumMessageBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) return IsUsefulRequest(document.RootElement);
            foreach (var request in document.RootElement.EnumerateArray())
                if (IsUsefulRequest(request)) return true;
        }
        catch (JsonException) { }
        return false;
    }

    private static bool IsUsefulRequest(JsonElement request)
    {
        // Pinned v0.1.9 uses Effect RpcSerialization.layerJson, not JSON-RPC 2.0.
        if (request.ValueKind != JsonValueKind.Object || !StringEquals(request, "_tag", "Request") ||
            !request.TryGetProperty("id", out var id) ||
            !(id.ValueKind == JsonValueKind.String && id.GetString()!.Length is > 0 and <= 128 ||
              id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _)) ||
            !request.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array ||
            !request.TryGetProperty("tag", out var method) || method.ValueKind != JsonValueKind.String ||
            !request.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var header in headers.EnumerateArray())
            if (header.ValueKind != JsonValueKind.Array || header.GetArrayLength() != 2 ||
                header[0].ValueKind != JsonValueKind.String || header[1].ValueKind != JsonValueKind.String)
                return false;
        if (method.GetString() == "terminal.write")
            return NonEmptyIdentifier(payload, "threadId") && NonEmptyIdentifier(payload, "terminalId", 128) &&
                payload.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String &&
                data.GetString()!.Length is > 0 and <= 65536;
        if (method.GetString() != "orchestration.dispatchCommand" ||
            !NonEmptyIdentifier(payload, "commandId") || !NonEmptyIdentifier(payload, "threadId"))
            return false;
        if (StringEquals(payload, "type", "prepared-run.release"))
            return NonEmptyIdentifier(payload, "runId");
        return StringEquals(payload, "type", "message.dispatch") && StringEquals(payload, "createdBy", "user") &&
            (StringEquals(payload, "creationSource", "web") || StringEquals(payload, "creationSource", "mobile")) &&
            NonEmptyIdentifier(payload, "messageId") &&
            payload.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
            payload.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array &&
            (text.GetString()!.Length > 0 || attachments.GetArrayLength() > 0) &&
            payload.TryGetProperty("dispatchMode", out var mode) && mode.ValueKind == JsonValueKind.Object &&
            mode.TryGetProperty("type", out var modeType) && modeType.ValueKind == JsonValueKind.String &&
            (modeType.GetString() is "defer_start" or "queue_after_active" or "start_immediately" ||
             modeType.GetString() is "steer_active" or "restart_active" && NonEmptyIdentifier(mode, "targetRunId"));
    }

    private static bool StringEquals(JsonElement value, string property, string expected) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String && item.GetString() == expected;

    private static bool NonEmptyIdentifier(JsonElement value, string property, int maximum = 512) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String &&
        item.GetString() is { } text && text.Length > 0 && text.Length <= maximum && text == text.Trim();
}
