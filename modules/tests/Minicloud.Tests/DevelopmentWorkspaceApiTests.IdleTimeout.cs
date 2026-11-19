using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed partial class DevelopmentWorkspaceApiTests
{
    [Fact]
    public async Task IdleTimeoutReadWriteAndResetPreserveLegacyCreateAndOperatorFallback()
    {
        using var owner = Client(token);
        var workspace = await Create(owner);
        Assert.Null(workspace.IdleTimeoutMinutes);
        Assert.Equal(360, workspace.EffectiveIdleTimeoutMinutes);
        var request = Request();
        string readToken;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var legacy = await db.DevelopmentWorkspaces.SingleAsync();
            legacy.RequestDigest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                request.OrganizationId, request.Name, request.ImageId, request.Profile,
                request.PlanId, request.VolumeSizeGb, request.IdempotencyKey
            })));
            readToken = AddKey(db, scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>(), "org", [ApiKeyScopes.AppsRead]);
            await db.SaveChangesAsync();
        }

        factory.Policy.IdleGraceMinutes = 30;
        using var reader = Client(readToken);
        var url = $"{Root}/{workspace.Id}?organizationId=org";
        var settings = $"{Root}/{workspace.Id}/idle-timeout?organizationId=org";
        Assert.Equal(30, (await reader.GetFromJsonAsync<DevelopmentWorkspaceResponse>(url))!.EffectiveIdleTimeoutMinutes);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(settings, new UpdateDevelopmentWorkspaceIdleTimeoutRequest(90))).StatusCode);

        var saved = await owner.PutAsJsonAsync(settings, new UpdateDevelopmentWorkspaceIdleTimeoutRequest(90));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(90, (await saved.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!.EffectiveIdleTimeoutMinutes);
        Assert.Equal(90, (await reader.GetFromJsonAsync<DevelopmentWorkspaceResponse>(url))!.IdleTimeoutMinutes);
        var listed = Assert.Single((await reader.GetFromJsonAsync<DevelopmentWorkspaceResponse[]>($"{Root}?organizationId=org"))!);
        Assert.Equal(90, listed.EffectiveIdleTimeoutMinutes);
        var replay = await Create(owner);
        Assert.Equal(workspace.Id, replay.Id);
        Assert.Equal(90, replay.IdleTimeoutMinutes);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(settings, new { })).StatusCode);

        var customRequest = Request() with { Name = "Custom", IdempotencyKey = "custom", IdleTimeoutMinutes = 10_080 };
        var created = await owner.PostAsJsonAsync(Root, customRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(10_080, (await created.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!.EffectiveIdleTimeoutMinutes);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(Root, customRequest with { IdleTimeoutMinutes = 1 })).StatusCode);

        var reset = await owner.PutAsJsonAsync(settings, new UpdateDevelopmentWorkspaceIdleTimeoutRequest(null));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var inherited = (await reset.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!;
        Assert.Null(inherited.IdleTimeoutMinutes);
        Assert.Equal(30, inherited.EffectiveIdleTimeoutMinutes);
        await AssertNoCompute();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10081")]
    [InlineData("2147483648")]
    [InlineData("1.5")]
    [InlineData("\"90\"")]
    public async Task IdleTimeoutRejectsInvalidJsonValuesOnCreateAndUpdate(string jsonValue)
    {
        using var owner = Client(token);
        var workspace = await Create(owner);
        var settings = $"{Root}/{workspace.Id}/idle-timeout?organizationId=org";
        var invalidUpdate = new JsonObject { ["idleTimeoutMinutes"] = JsonNode.Parse(jsonValue) };
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(settings, invalidUpdate)).StatusCode);
        var invalidCreate = JsonSerializer.SerializeToNode(Request() with { Name = "Invalid", IdempotencyKey = "invalid" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        invalidCreate["idleTimeoutMinutes"] = JsonNode.Parse(jsonValue);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(Root, invalidCreate)).StatusCode);
        Assert.Null((await owner.GetFromJsonAsync<DevelopmentWorkspaceResponse>($"{Root}/{workspace.Id}?organizationId=org"))!.IdleTimeoutMinutes);
        Assert.Single((await owner.GetFromJsonAsync<DevelopmentWorkspaceResponse[]>($"{Root}?organizationId=org"))!);
    }
}
