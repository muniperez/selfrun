using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WorkspaceReconciliation;

public sealed class HetznerAbsence(HttpClient http)
{
    public async Task VerifyAsync(RecoveryScope scope, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace))
            throw new RecoveryRefused("Recorded Hetzner credential is empty or invalid.");

        using (var witness = await GetAsync($"ssh_keys/{scope.ProjectWitnessSshKeyId}", token, ct))
        {
            if (witness.StatusCode != HttpStatusCode.OK)
                throw new RecoveryRefused($"Hetzner project witness is unavailable (HTTP {(int)witness.StatusCode}); absence is not proven.");
            using var body = await ReadAsync(witness, ct);
            var key = body.RootElement.GetProperty("ssh_key");
            if (key.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture) != scope.ProjectWitnessSshKeyId
                || key.GetProperty("fingerprint").GetString() != scope.ProjectWitnessFingerprint)
                throw new RecoveryRefused("Hetzner project witness did not match the reviewed ID and fingerprint.");
        }

        var resources = new (string Collection, string? Id)[]
        {
            ("servers", scope.ServerId), ("volumes", scope.VolumeId),
            ("primary_ips", scope.PrimaryIpId), ("firewalls", scope.FirewallId)
        };
        foreach (var (collection, id) in resources)
        {
            if (id is not null)
            {
                using var exact = await GetAsync($"{collection}/{id}", token, ct);
                if (exact.StatusCode != HttpStatusCode.NotFound)
                    throw new RecoveryRefused($"Exact Hetzner {collection} absence not confirmed (HTTP {(int)exact.StatusCode}).");
                using var notFound = await ReadAsync(exact, ct);
                if (notFound.RootElement.GetProperty("error").GetProperty("code").GetString() != "not_found")
                    throw new RecoveryRefused($"Hetzner {collection} did not return a resource-not-found response.");
            }

            // Use the workspace label alone: a resource from another generation or with bad
            // organization, kind, or operation labels must still block retirement.
            var selector = Uri.EscapeDataString($"workspace={scope.WorkspaceId}");
            using var inventory = await GetAsync($"{collection}?label_selector={selector}&per_page=50&page=1", token, ct);
            if (inventory.StatusCode != HttpStatusCode.OK)
                throw new RecoveryRefused($"Hetzner {collection} inventory unavailable (HTTP {(int)inventory.StatusCode}).");
            using var body = await ReadAsync(inventory, ct);
            if (body.RootElement.GetProperty(collection).GetArrayLength() != 0
                || body.RootElement.GetProperty("meta").GetProperty("pagination").GetProperty("next_page").ValueKind != JsonValueKind.Null)
                throw new RecoveryRefused($"Hetzner {collection} inventory is not empty for this workspace.");
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.hetzner.cloud/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
