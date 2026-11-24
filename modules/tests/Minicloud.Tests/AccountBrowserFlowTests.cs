using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class AccountBrowserFlowTests : IAsyncLifetime
{
    private const string FlowId = "11111111-2222-4333-8444-555555555555";
    private const string IdentityId = "22222222-2222-4333-8444-555555555555";
    private const string SessionId = "33333333-2222-4333-8444-555555555555";
    private const string Dashboard = "https://dashboard.example.test";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private FlowFactory _factory = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new FlowFactory(_connection);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.example.test"), HandleCookies = false, AllowAutoRedirect = false
        });
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Id = "user-1", KratosIdentityId = IdentityId, Email = "operator@example.test" });
        db.Organizations.Add(new Organization { Id = "org-1", Name = "Example", Slug = "example" });
        db.OrganizationMembers.Add(new OrganizationMember { Id = "member-1", UserId = "user-1", OrganizationId = "org-1", Role = OrganizationRole.Owner });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task StartAndReadRewriteOnlyTheFixedFlowActionAndAllowlistedCookies()
    {
        var response = await _client.GetAsync("/api/account/flows/login/browser?returnTo=%2Fcli%2Flogin%3Fsession%3Drequest-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var body = (await response.Content.ReadFromJsonAsync<AccountFlowResponse>())!;
        Assert.Null(body.Session);
        Assert.Equal($"/api/account/flows/login?flow={FlowId}", body.Flow!.Ui.Action);
        Assert.DoesNotContain("private upstream diagnostic", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(body.Flow.Ui.Nodes, node => node.Type == "script");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("csrf_token_test=", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.DoesNotContain("domain=", cookie);
        var upstream = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("/self-service/login/browser", upstream.Uri.AbsolutePath);
        Assert.Contains(Uri.EscapeDataString(Dashboard + "/cli/login?session=request-1"), upstream.Uri.Query);
        Assert.Equal(Dashboard, upstream.Origin);

        var read = await _client.GetAsync($"/api/account/flows/login?id={FlowId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal($"/self-service/login/flows?id={FlowId}", _factory.Upstream.Requests.Last().Uri.PathAndQuery);
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/%2f%2fevil.example")]
    [InlineData("/%0aevil")]
    public async Task UnsafeReturnTargetsNeverReachKratos(string path)
    {
        var response = await _client.GetAsync("/api/account/flows/login/browser?returnTo=" + Uri.EscapeDataString(path));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Theory]
    [InlineData("/api/account/flows/registration/browser")]
    [InlineData("/api/account/flows/admin/browser")]
    [InlineData("/api/account/flows/recovery/browser")]
    [InlineData("/api/account/flows/login?id=../../admin")]
    public async Task UnapprovedKindsAndIdsAreRejected(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task SubmitRequiresExactOriginAndUpstreamCsrfBeforeCallingKratos()
    {
        var response = await _client.PostAsJsonAsync($"/api/account/flows/login?flow={FlowId}", LoginFields());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var request = Submit("login", new { identifier = "operator@example.test", password = "synthetic-password" });
        response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task LoginVerifiesWhoamiAndIssuesOnlyALocalOpaqueSessionForTheBoundIdentity()
    {
        var (cookie, csrf) = await Login();
        Assert.NotEmpty(csrf);
        Assert.DoesNotContain(SessionId, cookie);
        Assert.DoesNotContain(IdentityId, cookie);
        Assert.Equal(2, _factory.Upstream.Requests.Count);
        Assert.Equal("/sessions/whoami", _factory.Upstream.Requests[1].Uri.AbsolutePath);
        Assert.Contains("ory_kratos_session=synthetic-ory-session", _factory.Upstream.Requests[1].Cookie);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
        request.Headers.Add("Cookie", cookie);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(csrf, (await response.Content.ReadFromJsonAsync<AccountSessionResponse>())!.CsrfToken);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var stored = await db.BrowserSessions.SingleAsync();
        Assert.DoesNotContain(cookie.Split('=')[1], stored.TokenHash);
        Assert.Equal("user-1", stored.UserId);
        Assert.Single(await db.Users.ToListAsync());
        Assert.Single(await db.OrganizationMembers.ToListAsync());
    }

    [Fact]
    public async Task LoginCannotAdoptExistingOperatorByEmail()
    {
        _factory.Upstream.Identity = "44444444-2222-4333-8444-555555555555";
        using var request = Submit("login", LoginFields());
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.Empty(await db.BrowserSessions.ToListAsync());
        Assert.Equal(IdentityId, (await db.Users.SingleAsync()).KratosIdentityId);
    }

    [Fact]
    public async Task SettingsRequiresBrowserSessionCsrfAndRotatesAllLocalSessions()
    {
        var (cookie, csrf) = await Login();
        using (var invalid = Submit("settings", PasswordChangeFields(), cookie))
        {
            var response = await _client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var request = Submit("settings", PasswordChangeFields(), cookie + "; csrf_token_test=settings-csrf-cookie");
        request.Headers.Add("X-Minicloud-CSRF", csrf);
        var changed = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEmpty((await changed.Content.ReadFromJsonAsync<AccountFlowResponse>())!.Session!.CsrfToken);
        Assert.NotEqual(cookie, LocalCookie(changed));
        var upstream = Assert.Single(_factory.Upstream.Requests, x => x.Uri.AbsolutePath == "/self-service/settings");
        Assert.Equal($"http://kratos:4433/self-service/settings?flow={FlowId}", upstream.Uri.AbsoluteUri);
        Assert.Equal(Dashboard, upstream.Origin);
        Assert.Equal("application/json", upstream.Accept);
        Assert.Contains("ory_kratos_session=synthetic-ory-session", upstream.Cookie);
        Assert.Contains("csrf_token_test=settings-csrf-cookie", upstream.Cookie);
        Assert.DoesNotContain(cookie, upstream.Cookie);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var sessions = await db.BrowserSessions.AsNoTracking().ToListAsync();
        Assert.Single(sessions);
        Assert.Single(sessions, session => session.RevokedAt is null);
        using var stale = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
        stale.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task SettingsPasswordCanCompleteAfterKratosBreachLookupRetries()
    {
        var (cookie, csrf) = await Login();
        _factory.Upstream.SettingsPasswordDelay = TimeSpan.FromSeconds(11);
        using var request = Submit("settings", PasswordChangeFields(), cookie);
        request.Headers.Add("X-Minicloud-CSRF", csrf);

        var changed = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(cookie, LocalCookie(changed));
    }

    [Theory]
    [InlineData(400, """{"id":"11111111-2222-4333-8444-555555555555","type":"browser","state":"show_form","ui":{"nodes":[{"group":"password","attributes":{"name":"password","type":"password","value":"synthetic-new-password"},"messages":[{"id":4000032,"type":"error","text":"private upstream diagnostic"}]}]}}""", "auth_password_rejected")]
    [InlineData(400, """{"error":{"code":400,"reason":"private upstream diagnostic"}}""", "auth_flow_invalid")]
    [InlineData(403, """{"error":{"id":"session_refresh_required","code":403,"reason":"private upstream diagnostic"},"redirect_browser_to":"http://kratos:4433/self-service/login/browser?refresh=true"}""", "session_refresh_required")]
    [InlineData(403, """{"error":{"id":"security_csrf_violation","code":403}}""", "csrf_invalid")]
    [InlineData(410, """{"error":{"id":"self_service_flow_expired","code":410},"redirect_browser_to":"http://kratos:4433/self-service/settings/browser","use_flow_id":"22222222-2222-4333-8444-555555555555"}""", "auth_flow_expired")]
    [InlineData(422, """{"error":{"id":"browser_location_change_required","code":422},"redirect_browser_to":"http://kratos:4433/self-service/login/browser"}""", "auth_flow_invalid")]
    [InlineData(503, """{"error":{"code":503,"reason":"private upstream diagnostic"}}""", "auth_unavailable")]
    public async Task SettingsPasswordErrorsAreActionableAndDoNotRotateTheSession(int upstreamStatus, string body, string code)
    {
        var (cookie, csrf) = await Login();
        _factory.Upstream.Requests.Clear();
        _factory.Upstream.SettingsPasswordError = (upstreamStatus, body);
        using var request = Submit("settings", PasswordChangeFields(), cookie);
        request.Headers.Add("X-Minicloud-CSRF", csrf);

        var response = await _client.SendAsync(request);

        Assert.Equal(upstreamStatus == 422 ? 400 : upstreamStatus, (int)response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal(code, JsonDocument.Parse(content).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("private upstream diagnostic", content);
        Assert.DoesNotContain("synthetic-new-password", content);
        Assert.DoesNotContain("http://kratos:", content);
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("/self-service/settings", Assert.Single(_factory.Upstream.Requests).Uri.AbsolutePath);
        using var session = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
        session.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(session)).StatusCode);
    }

    [Fact]
    public async Task SettingsProfileExposesAndMergesFirstAndLastNameIntoOneTraitsObject()
    {
        var (cookie, csrf) = await Login();
        using (var read = new HttpRequestMessage(HttpMethod.Get, "/api/account/flows/settings/browser"))
        {
            read.Headers.Add("Cookie", cookie + "; ory_kratos_session=synthetic-ory-session");
            var flow = (await (await _client.SendAsync(read)).Content.ReadFromJsonAsync<AccountFlowResponse>())!.Flow!;
            Assert.Equal("Synthetic", Assert.Single(flow.Ui.Nodes, node => node.Name == "traits.name.first").Value);
            Assert.Equal("Operator", Assert.Single(flow.Ui.Nodes, node => node.Name == "traits.name.last").Value);
        }
        using var request = Submit("settings", new Dictionary<string, string>
        {
            ["csrf_token"] = "flow-csrf", ["method"] = "profile", ["traits.email"] = "operator@example.test",
            ["traits.name.first"] = "Ada", ["traits.name.last"] = "Lovelace"
        }, cookie);
        request.Headers.Add("X-Minicloud-CSRF", csrf);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
        var traits = JsonDocument.Parse(_factory.Upstream.Requests.First(x => x.Body.Length > 0 && x.Uri.AbsolutePath == "/self-service/settings").Body)
            .RootElement.GetProperty("traits");
        Assert.Equal("operator@example.test", traits.GetProperty("email").GetString());
        Assert.Equal("""{"first":"Ada","last":"Lovelace"}""", traits.GetProperty("name").GetRawText());
    }

    [Fact]
    public async Task SettingsProfileForwardsLastNameAlone()
    {
        var (cookie, csrf) = await Login();
        _factory.Upstream.Requests.Clear();
        using var request = Submit("settings", new Dictionary<string, string>
        {
            ["csrf_token"] = "flow-csrf", ["method"] = "profile", ["traits.name.last"] = "Lovelace"
        }, cookie);
        request.Headers.Add("X-Minicloud-CSRF", csrf);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
        var traits = JsonDocument.Parse(_factory.Upstream.Requests[0].Body).RootElement.GetProperty("traits");
        Assert.Equal("""{"name":{"last":"Lovelace"}}""", traits.GetRawText());
    }

    [Theory]
    [InlineData("traits.name.middle", null)]
    [InlineData("traits.nickname", null)]
    [InlineData("traits.name", "traits.name.last")]
    [InlineData("traits.name.last", "traits.name")]
    public async Task SettingsProfileRejectsUnknownOrConflictingNameTraits(string field, string? second)
    {
        var (cookie, csrf) = await Login();
        _factory.Upstream.Requests.Clear();
        var fields = new Dictionary<string, string> { ["csrf_token"] = "flow-csrf", ["method"] = "profile", [field] = "Lovelace" };
        if (second is not null) fields[second] = "Ada";
        using var request = Submit("settings", fields, cookie);
        request.Headers.Add("X-Minicloud-CSRF", csrf);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(request)).StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task LogoutUsesTheReconstructedBrowserSessionAndRevokesIt()
    {
        var (cookie, csrf) = await Login();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/logout");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", Dashboard);
        request.Headers.Add("X-Minicloud-CSRF", csrf);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-minicloud-session=;"));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.NotNull((await db.BrowserSessions.AsNoTracking().SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task EnabledRecoveryRequiresAVerifiedBoundSessionAndRevokesPreviousLocalAccess()
    {
        var (cookie, _) = await Login();
        _factory.Services.GetRequiredService<IOptions<KratosOptions>>().Value.EmailRecoveryEnabled = true;
        var start = await _client.GetAsync("/api/account/flows/recovery/browser");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var request = Submit("recovery", new { csrf_token = "flow-csrf", code = "123456", method = "code" });
        var recovered = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.NotEqual(cookie, LocalCookie(recovered));
        using var stale = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
        stale.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task DisabledRecoveryRejectsAllRecoveryRoutesWithoutCallingKratos()
    {
        var start = await _client.GetAsync("/api/account/flows/recovery/browser");
        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
        var read = await _client.GetAsync($"/api/account/flows/recovery?id={FlowId}");
        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
        using var submit = Submit("recovery", new { csrf_token = "flow-csrf", email = "operator@example.test", method = "code" });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(submit)).StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task RecoveryCodeFlowHandlesKratos422SettingsTransitionRevokesPriorSessionsAndPreservesKeysAndMemberships()
    {
        const string firstUpstreamSession = "33333333-1111-4333-8444-555555555551";
        const string secondUpstreamSession = "33333333-2222-4333-8444-555555555552";
        const string recoveredUpstreamSession = "33333333-9999-4333-8444-555555555559";

        _factory.Upstream.UpstreamSessionId = firstUpstreamSession;
        var (firstBrowserCookie, _) = await Login();
        _factory.Upstream.UpstreamSessionId = secondUpstreamSession;
        var (secondBrowserCookie, _) = await Login();

        var apiKeyToken = new ApiKeyTokenService().Generate();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.GrafanaProxySessions.Add(new GrafanaProxySession
            {
                Id = "grafana-prior", OrganizationId = "org-1", AppId = "app-1",
                MinicloudUserId = "user-1", SessionHash = "grafana-prior-hash"
            });
            db.GrafanaLaunchCodes.Add(new GrafanaLaunchCode
            {
                Id = "launch-prior", OrganizationId = "org-1", AppId = "app-1",
                MinicloudUserId = "user-1", CodeHash = "launch-prior-hash", Destination = "/"
            });
            db.ApiKeys.Add(new ApiKey
            {
                Id = "machine-key-1", OrganizationId = "org-1", Name = "CI machine key",
                KeyPrefix = apiKeyToken.Prefix, KeyHash = new ApiKeyTokenService().Hash(apiKeyToken.Token),
                ScopesJson = "[\"read\"]"
            });
            await db.SaveChangesAsync();
        }

        _factory.Services.GetRequiredService<IOptions<KratosOptions>>().Value.EmailRecoveryEnabled = true;
        _factory.Upstream.UpstreamSessionId = recoveredUpstreamSession;
        _factory.Upstream.RecoverySubmit = body =>
        {
            if (body.Contains("\"email\"", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = FlowId, type = "browser", state = "sent_email",
                        expires_at = DateTimeOffset.UtcNow.AddMinutes(10),
                        ui = new
                        {
                            action = $"http://kratos:4433/self-service/recovery?flow={FlowId}", method = "POST",
                            nodes = new object[]
                            {
                                new { group = "default", attributes = new { name = "csrf_token", type = "hidden", value = "sent-csrf", required = true } },
                                new { group = "code", attributes = new { name = "code", type = "text", value = "", required = true } },
                                new { group = "code", attributes = new { name = "method", type = "hidden", value = "code", required = false } },
                                new { group = "code", attributes = new { name = "method", type = "submit", value = "code", required = false } },
                                new { group = "code", attributes = new { name = "email", type = "submit", value = "operator@example.test", required = false } }
                            },
                            messages = new[] { new { id = 1060003, type = "info", text = "An email containing a recovery code has been sent." } }
                        }
                    })
                };
            }
            if (body.Contains("\"code\":\"000000\"", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = FlowId, type = "browser", state = "sent_email",
                        expires_at = DateTimeOffset.UtcNow.AddMinutes(10),
                        ui = new
                        {
                            action = $"http://kratos:4433/self-service/recovery?flow={FlowId}", method = "POST",
                            nodes = new object[]
                            {
                                new { group = "default", attributes = new { name = "csrf_token", type = "hidden", value = "sent-csrf", required = true } },
                                new { group = "code", attributes = new { name = "code", type = "text", value = "", required = true } },
                                new { group = "code", attributes = new { name = "method", type = "submit", value = "code", required = false } }
                            },
                            messages = new[] { new { id = 4060006, type = "error", text = "The recovery code is invalid or has already been used." } }
                        }
                    })
                };
            }
            var transition = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = JsonContent.Create(new
                {
                    error = new { id = "browser_location_change_required", code = 422, status = "Unprocessable Entity" },
                    redirect_browser_to = $"{Dashboard}/settings/account?flow=aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"
                })
            };
            transition.Headers.Add("Set-Cookie", "ory_kratos_session=recovered-ory-session; Path=/; HttpOnly");
            return transition;
        };

        // Step 1: start flow and submit email -> sent_email flow (no session issued yet).
        var start = await _client.GetAsync("/api/account/flows/recovery/browser");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var sendEmail = Submit("recovery", new { csrf_token = "flow-csrf", email = "operator@example.test", method = "code" });
        var sentResponse = await _client.SendAsync(sendEmail);
        Assert.Equal(HttpStatusCode.OK, sentResponse.StatusCode);
        var sentBody = (await sentResponse.Content.ReadFromJsonAsync<AccountFlowResponse>())!;
        Assert.Null(sentBody.Session);
        Assert.Contains(sentBody.Flow!.Ui.Nodes, n => n.Name == "code" && n.Type == "text");
        Assert.Contains(sentBody.Flow.Ui.Nodes, n => n.Name == "email" && n.Type == "submit" && n.Value == "operator@example.test");

        // Step 2: invalid/reused code returns sanitized error flow and never issues a session or revokes existing ones.
        using var wrongCode = Submit("recovery", new { csrf_token = "sent-csrf", code = "000000", method = "code" });
        var wrongResponse = await _client.SendAsync(wrongCode);
        Assert.Equal(HttpStatusCode.OK, wrongResponse.StatusCode);
        var wrongBody = (await wrongResponse.Content.ReadFromJsonAsync<AccountFlowResponse>())!;
        Assert.Null(wrongBody.Session);
        Assert.Contains(wrongBody.Flow!.Ui.Messages, m => m.Id == 4060006 && m.Type == "error");
        Assert.False(wrongResponse.Headers.TryGetValues("Set-Cookie", out var wrongCookies) &&
            wrongCookies.Any(c => c.StartsWith("__Host-minicloud-session=")));

        // Step 3: valid code triggers Kratos v1.3.1 422 browser_location_change_required -> API verifies /sessions/whoami and admits.
        _factory.Kratos.Writes.Clear();
        using var redeem = Submit("recovery", new { csrf_token = "sent-csrf", code = "123456", method = "code" });
        var recoveredResponse = await _client.SendAsync(redeem);
        Assert.Equal(HttpStatusCode.OK, recoveredResponse.StatusCode);
        var recoveredJson = await recoveredResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("redirect_browser_to", recoveredJson);
        Assert.DoesNotContain("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", recoveredJson);
        var recoveredBody = JsonSerializer.Deserialize<AccountFlowResponse>(recoveredJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(recoveredBody.Flow);
        Assert.NotNull(recoveredBody.Session);
        var recoveredCookie = LocalCookie(recoveredResponse);
        Assert.NotEqual(firstBrowserCookie, recoveredCookie);
        Assert.NotEqual(secondBrowserCookie, recoveredCookie);
        Assert.Contains("ory_kratos_session=recovered-ory-session", _factory.Upstream.Requests.Last().Cookie);

        // Prior browser sessions, upstream Kratos sessions, and Grafana sessions are all revoked.
        foreach (var staleCookie in new[] { firstBrowserCookie, secondBrowserCookie })
        {
            using var check = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
            check.Headers.Add("Cookie", staleCookie);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(check)).StatusCode);
        }
        Assert.Equal(2, _factory.Kratos.Writes.Count(w => w == "revoke"));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            Assert.All(await db.GrafanaProxySessions.AsNoTracking().ToListAsync(), s => Assert.NotNull(s.RevokedAt));
            Assert.All(await db.GrafanaLaunchCodes.AsNoTracking().ToListAsync(), c => Assert.NotNull(c.ConsumedAt));
        }

        // Step 4: recovered session enters settings and rotates password.
        using var settingsStart = new HttpRequestMessage(HttpMethod.Get, "/api/account/flows/settings/browser");
        settingsStart.Headers.Add("Cookie", recoveredCookie + "; ory_kratos_session=recovered-ory-session");
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(settingsStart)).StatusCode);
        using var changePassword = Submit("settings", PasswordChangeFields(), recoveredCookie);
        changePassword.Headers.Add("X-Minicloud-CSRF", recoveredBody.Session!.CsrfToken);
        var passwordResponse = await _client.SendAsync(changePassword);
        Assert.Equal(HttpStatusCode.OK, passwordResponse.StatusCode);

        // Step 5: machine API key and organization membership remain unchanged and usable.
        using var machineMe = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        machineMe.Headers.Add("Authorization", "Bearer " + apiKeyToken.Token);
        var machineResponse = await _client.SendAsync(machineMe);
        Assert.Equal(HttpStatusCode.OK, machineResponse.StatusCode);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var key = await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == "machine-key-1");
            Assert.Null(key.RevokedAt);
            var member = await db.OrganizationMembers.AsNoTracking().SingleAsync(m => m.Id == "member-1");
            Assert.Equal("user-1", member.UserId);
            Assert.Equal("org-1", member.OrganizationId);
            Assert.Equal(OrganizationRole.Owner, member.Role);
        }
    }

    [Theory]
    [InlineData("https://evil.example/settings/account?flow=1", true, HttpStatusCode.ServiceUnavailable)]
    [InlineData("http://dashboard.example.test/settings/account?flow=1", true, HttpStatusCode.ServiceUnavailable)]
    [InlineData("https://dashboard.example.test/other?flow=1", true, HttpStatusCode.ServiceUnavailable)]
    [InlineData("https://user@dashboard.example.test/settings/account?flow=1", true, HttpStatusCode.ServiceUnavailable)]
    [InlineData("https://dashboard.example.test/settings/account?flow=1#frag", true, HttpStatusCode.ServiceUnavailable)]
    [InlineData("https://dashboard.example.test/settings/account?flow=1", false, HttpStatusCode.ServiceUnavailable)]
    public async Task Recovery422WithUntrustedRedirectOrMissingSessionCookieFailsClosed(
        string redirectTo, bool includeSessionCookie, HttpStatusCode expected)
    {
        var (existingCookie, _) = await Login();
        _factory.Services.GetRequiredService<IOptions<KratosOptions>>().Value.EmailRecoveryEnabled = true;
        _factory.Upstream.RecoverySubmit = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = JsonContent.Create(new
                {
                    error = new { id = "browser_location_change_required", code = 422 },
                    redirect_browser_to = redirectTo
                })
            };
            if (includeSessionCookie)
                response.Headers.Add("Set-Cookie", "ory_kratos_session=recovered-ory-session; Path=/; HttpOnly");
            return response;
        };

        using var redeem = Submit("recovery", new { csrf_token = "flow-csrf", code = "123456", method = "code" });
        var result = await _client.SendAsync(redeem);
        Assert.Equal(expected, result.StatusCode);
        Assert.DoesNotContain(redirectTo, await result.Content.ReadAsStringAsync());
        Assert.False(result.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(c => c.StartsWith("__Host-minicloud-session=")));

        using var existingCheck = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
        existingCheck.Headers.Add("Cookie", existingCookie);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(existingCheck)).StatusCode);
    }

    [Fact]
    public async Task RecoveryForUnboundIdentityFailsClosedWithoutRevokingAdmittedUserSessions()
    {
        var (existingCookie, _) = await Login();
        _factory.Services.GetRequiredService<IOptions<KratosOptions>>().Value.EmailRecoveryEnabled = true;
        _factory.Upstream.Identity = "99999999-9999-4999-8999-999999999999";
        using var redeem = Submit("recovery", new { csrf_token = "flow-csrf", code = "123456", method = "code" });
        var result = await _client.SendAsync(redeem);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.False(result.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(c => c.StartsWith("__Host-minicloud-session=")));

        using var existingCheck = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
        existingCheck.Headers.Add("Cookie", existingCookie);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(existingCheck)).StatusCode);
    }

    [Fact]
    public async Task ExplicitInvalidBearerCannotFallBackToTheBrowserCookieForSettings()
    {
        var (cookie, _) = await Login();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/account/flows/settings/browser");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Authorization", "Bearer invalid-synthetic");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
        Assert.Equal(2, _factory.Upstream.Requests.Count);
    }

    [Fact]
    public async Task OversizedAndUnexpectedFieldsFailWithoutUpstreamMutation()
    {
        using var oversized = Submit("login", new { csrf_token = "csrf", password = new string('x', 65536) });
        var response = await _client.SendAsync(oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var unexpected = Submit("login", new { csrf_token = "csrf", url = "http://admin.example" });
        response = await _client.SendAsync(unexpected);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Theory]
    [InlineData(400, 400, "auth_flow_invalid")]
    [InlineData(401, 401, "unauthenticated")]
    [InlineData(403, 403, "csrf_invalid")]
    [InlineData(410, 410, "auth_flow_expired")]
    [InlineData(503, 503, "auth_unavailable")]
    [InlineData(302, 503, "auth_unavailable")]
    public async Task UpstreamErrorsAndRedirectsAreSanitizedAndNeverFollowed(int upstreamStatus, int expected, string code)
    {
        _factory.Upstream.Status = upstreamStatus;
        using var request = Submit("login", LoginFields());
        var response = await _client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(code, body);
        Assert.DoesNotContain("private upstream diagnostic", body);
        Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task DisabledGoogleKeepsTheFrozenFlowShapeAndRejectsProviderRoutesWithoutUpstreamCalls()
    {
        _factory.Upstream.Google = true;
        var response = await _client.GetAsync("/api/account/flows/login/browser");
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("providers", json);
        Assert.DoesNotContain("oidc", json);
        Assert.DoesNotContain("google", json);
        _factory.Upstream.Requests.Clear();

        using var start = ProviderStart("login");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(start)).StatusCode);
        using var generic = Submit("login", new { csrf_token = "flow-csrf", method = "oidc", provider = "google" });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(generic)).StatusCode);
        var callback = await Callback("synthetic-state", Pending("synthetic-state"));
        Assert.Equal(Dashboard + "/sign-in/continue?result=failed&kind=login&returnTo=%2Fapps", callback.Headers.Location!.AbsoluteUri);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task EnabledGoogleIsAnExplicitProviderActionThatReturnsOnlyTheValidatedProviderUrl()
    {
        EnableGoogle();
        var flow = (await (await _client.GetAsync("/api/account/flows/login/browser"))
            .Content.ReadFromJsonAsync<AccountFlowResponse>())!.Flow!;
        var provider = Assert.Single(flow.Ui.Providers!);
        Assert.Equal(new AccountFlowProvider("google", $"/api/account/flows/login/oidc?flow={FlowId}", "POST"), provider);
        Assert.DoesNotContain(flow.Ui.Nodes, node => node.Group == "oidc");

        using var withoutOrigin = new HttpRequestMessage(HttpMethod.Post, $"/api/account/flows/login/oidc?flow={FlowId}")
        { Content = JsonContent.Create(new { csrf_token = "flow-csrf", provider = "google" }) };
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(withoutOrigin)).StatusCode);
        using var extra = ProviderStart("login", new { csrf_token = "flow-csrf", provider = "google", url = "https://evil.example" });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(extra)).StatusCode);
        using var other = ProviderStart("login", new { csrf_token = "flow-csrf", provider = "github" });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(other)).StatusCode);
        Assert.Single(_factory.Upstream.Requests);

        using var request = ProviderStart("login", returnTo: "/cli/login?session=request-1");
        request.Headers.Add("Cookie", "__Host-minicloud-session=local-secret; csrf_token_test=flow-cookie; unrelated=private");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var body = (await response.Content.ReadFromJsonAsync<AccountProviderRedirectResponse>())!;
        Assert.Equal(new AccountProviderRedirectResponse("google", UpstreamHandler.GoogleUrl), body);
        var upstream = _factory.Upstream.Requests.Last();
        Assert.Equal($"/self-service/login?flow={FlowId}", upstream.Uri.PathAndQuery);
        Assert.Equal("csrf_token_test=flow-cookie", upstream.Cookie);
        Assert.Equal(Dashboard, upstream.Origin);
        Assert.Equal(new Dictionary<string, string> { ["csrf_token"] = "flow-csrf", ["method"] = "oidc", ["provider"] = "google" },
            JsonSerializer.Deserialize<Dictionary<string, string>>(upstream.Body));
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(cookies, cookie => cookie.StartsWith("ory_kratos_continuity=synthetic-continuity;"));
        var pending = Assert.Single(cookies, cookie => cookie.StartsWith("__Host-minicloud-oidc="));
        Assert.Contains("secure", pending);
        Assert.Contains("httponly", pending);
        Assert.DoesNotContain("domain=", pending);
        Assert.Equal(Pending("synthetic-state", "/cli/login?session=request-1") + ".0", pending.Split(';')[0].Split('=', 2)[1]);
        Assert.DoesNotContain(cookies, cookie => cookie.StartsWith("__Host-minicloud-session="));
    }

    [Theory]
    [InlineData("http://accounts.google.com/o/oauth2/v2/auth?client_id=c&response_type=code&state=s&redirect_uri={callback}")]
    [InlineData("https://evil.example/o/oauth2/v2/auth?client_id=c&response_type=code&state=s&redirect_uri={callback}")]
    [InlineData("https://accounts.google.com.evil.example/o/oauth2/v2/auth?client_id=c&response_type=code&state=s&redirect_uri={callback}")]
    [InlineData("https://user@accounts.google.com/o/oauth2/v2/auth?client_id=c&response_type=code&state=s&redirect_uri={callback}")]
    [InlineData("https://accounts.google.com:8443/o/oauth2/v2/auth?client_id=c&response_type=code&state=s&redirect_uri={callback}")]
    [InlineData("https://accounts.google.com/logout?client_id=c&response_type=code&state=s&redirect_uri={callback}")]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth?client_id=c&response_type=code&redirect_uri={callback}")]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth?client_id=c&response_type=code&state=s&redirect_uri=https%3A%2F%2Fevil.example%2Fcallback")]
    [InlineData("/self-service/login/browser")]
    public async Task ProviderRedirectsOutsideTheAllowlistFailClosed(string redirect)
    {
        EnableGoogle();
        _factory.Upstream.ProviderRedirect = redirect.Replace("{callback}", Uri.EscapeDataString(UpstreamHandler.Callback));
        using var request = ProviderStart("login");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("auth_unavailable", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(cookie => cookie.StartsWith("__Host-minicloud-oidc=")));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(303)]
    public async Task ProviderStartNeverFollowsOrExposesOtherUpstreamResponses(int status)
    {
        EnableGoogle();
        _factory.Upstream.ProviderStatus = status;
        using var request = ProviderStart("login");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.SendAsync(request)).StatusCode);
        Assert.Single(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task CallbackForwardsOnlyBoundedProviderParametersAndReturnsToCliConsent()
    {
        EnableGoogle();
        var response = await Callback("synthetic-state", Pending("synthetic-state", "/cli/login?session=request-1"),
            "&code=synthetic-code&scope=email&iss=https%3A%2F%2Faccounts.google.com",
            "csrf_token_test=flow-cookie; ory_kratos_continuity=synthetic-continuity; unrelated=private");
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(Dashboard + "/sign-in/continue?result=success&kind=login&returnTo=%2Fcli%2Flogin%3Fsession%3Drequest-1",
            response.Headers.Location!.AbsoluteUri);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var callback = _factory.Upstream.Requests[0];
        Assert.Equal("/self-service/methods/oidc/callback/google?state=synthetic-state&code=synthetic-code", callback.Uri.PathAndQuery);
        Assert.Equal("csrf_token_test=flow-cookie; ory_kratos_continuity=synthetic-continuity", callback.Cookie);
        Assert.Equal("application/json", callback.Accept);
        Assert.Equal("/sessions/whoami", _factory.Upstream.Requests[1].Uri.AbsolutePath);
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(cookies, cookie => cookie.StartsWith("__Host-minicloud-oidc=;"));
        Assert.Contains(cookies, cookie => cookie.StartsWith("__Host-minicloud-session=") && !cookie.StartsWith("__Host-minicloud-session=;"));
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Equal("user-1", (await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().BrowserSessions.SingleAsync()).UserId);
    }

    [Theory]
    [InlineData("other-state", "/apps")]
    [InlineData("synthetic-state", "//evil.example")]
    [InlineData("synthetic-state", null)]
    public async Task CallbackWithoutTheMatchingPendingStateOrSafeReturnNeverReachesKratos(string state, string? returnTo)
    {
        EnableGoogle();
        var pending = returnTo is null ? null : state == "synthetic-state"
            ? $"login.{Guid.Parse(FlowId):N}.{StateHash("synthetic-state")}.{Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(returnTo))}"
            : Pending("synthetic-state", returnTo);
        var response = await Callback(state, pending, "&code=synthetic-code");
        Assert.Equal(Dashboard + "/sign-in/continue?result=failed&kind=login&returnTo=%2Fapps", response.Headers.Location!.AbsoluteUri);
        Assert.Empty(_factory.Upstream.Requests);
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            cookie => cookie.StartsWith("__Host-minicloud-session="));
    }

    [Theory]
    [InlineData(400, "&flow=" + FlowId)]
    [InlineData(303, "")]
    [InlineData(422, "")]
    public async Task CallbackFailuresContinueOnlyTheSamePendingFlowAndAreNeverFollowed(int status, string flow)
    {
        EnableGoogle();
        _factory.Upstream.CallbackStatus = status;
        var response = await Callback("synthetic-state", Pending("synthetic-state"), "&error=access_denied");
        Assert.Equal(Dashboard + "/sign-in/continue?result=failed&kind=login&returnTo=%2Fapps" + flow, response.Headers.Location!.AbsoluteUri);
        var upstream = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("/self-service/methods/oidc/callback/google?state=synthetic-state&error=access_denied", upstream.Uri.PathAndQuery);
    }

    [Fact]
    public async Task CallbackForUnadmittedIdentityOrOutageIssuesNoLocalSession()
    {
        EnableGoogle();
        _factory.Upstream.Identity = "44444444-2222-4333-8444-555555555555";
        var response = await Callback("synthetic-state", Pending("synthetic-state"), "&code=synthetic-code");
        Assert.Equal(Dashboard + "/sign-in/continue?result=forbidden&kind=login&returnTo=%2Fapps", response.Headers.Location!.AbsoluteUri);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("__Host-minicloud-session="));
        _factory.Upstream.CallbackStatus = 503;
        response = await Callback("synthetic-state", Pending("synthetic-state"), "&code=synthetic-code");
        Assert.Equal(Dashboard + "/sign-in/continue?result=unavailable&kind=login&returnTo=%2Fapps", response.Headers.Location!.AbsoluteUri);
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().BrowserSessions.ToListAsync());
    }

    [Fact]
    public async Task SettingsLinkRequiresTheBrowserSessionCsrfAndSubmitsTheLinkField()
    {
        var (cookie, csrf) = await Login();
        EnableGoogle();
        using (var missingCsrf = ProviderStart("settings", cookie: cookie))
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(missingCsrf)).StatusCode);
        Assert.Equal(2, _factory.Upstream.Requests.Count);
        using var request = ProviderStart("settings", cookie: cookie);
        request.Headers.Add("X-Minicloud-CSRF", csrf);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Dictionary<string, string> { ["csrf_token"] = "flow-csrf", ["method"] = "oidc", ["link"] = "google" },
            JsonSerializer.Deserialize<Dictionary<string, string>>(_factory.Upstream.Requests.Last().Body));
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__Host-minicloud-oidc=settings."));
    }

    [Fact]
    public async Task KeepSignedInIssuesARememberedPersistentCookieAndStoresThePreference()
    {
        var standard = await _client.SendAsync(Submit("login", LoginFields()));
        var standardCookie = standard.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-minicloud-session="));
        Assert.DoesNotContain("expires=", standardCookie);
        Assert.False((await standard.Content.ReadFromJsonAsync<AccountFlowResponse>())!.Session!.KeepSignedIn);

        using var request = Submit("login", LoginFields(), keepSignedIn: true);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-minicloud-session="));
        Assert.Contains("expires=", cookie);
        var session = (await response.Content.ReadFromJsonAsync<AccountFlowResponse>())!.Session!;
        Assert.True(session.KeepSignedIn);
        Assert.True(session.KeepSignedInPreference);
        Assert.True(session.ExpiresAt > DateTimeOffset.UtcNow.AddDays(29));
        Assert.True(session.IdleExpiresAt > DateTimeOffset.UtcNow.AddDays(6));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.True((await db.Users.SingleAsync()).KeepSignedIn);
    }

    [Fact]
    public async Task RenewalRequiresCsrfIsWindowedAndExtendsTheUpstreamSession()
    {
        var (cookie, csrf) = await Login();
        using (var missingCsrf = Renew(cookie, null))
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(missingCsrf)).StatusCode);

        using (var early = Renew(cookie, csrf))
        {
            var response = await _client.SendAsync(early);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<AccountSessionResponse>())!;
            Assert.True(body.RenewAfter > DateTimeOffset.UtcNow);
            Assert.Equal(0, _factory.Kratos.Extensions);
        }

        // Move the session into its renewal window, with the upstream session ending before the renewed deadline.
        await AgeSession(TimeSpan.FromMinutes(90));
        _factory.Kratos.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60);
        using var due = Renew(cookie, csrf);
        var renewed = await _client.SendAsync(due);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var session = (await renewed.Content.ReadFromJsonAsync<AccountSessionResponse>())!;
        Assert.Equal(1, _factory.Kratos.Extensions);
        Assert.True(session.IdleExpiresAt > DateTimeOffset.UtcNow.AddMinutes(115));
        Assert.Equal(csrf, session.CsrfToken);
        Assert.False(renewed.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task RenewalStillExtendsRememberedSessionsAfterTheOperatorShortensThePolicy()
    {
        using var login = Submit("login", LoginFields(), keepSignedIn: true);
        var loggedIn = await _client.SendAsync(login);
        var cookie = LocalCookie(loggedIn);
        var csrf = (await loggedIn.Content.ReadFromJsonAsync<AccountFlowResponse>())!.Session!.CsrfToken;
        _factory.Services.GetRequiredService<IOptions<PortalSessionOptions>>().Value.KeepSignedInEnabled = false;

        // The stored idle deadline is still days away, but the standard policy now ends it 30 minutes from now.
        await AgeSession(TimeSpan.FromMinutes(90));
        using var due = Renew(cookie, csrf);
        var renewed = await _client.SendAsync(due);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var session = (await renewed.Content.ReadFromJsonAsync<AccountSessionResponse>())!;
        Assert.False(session.KeepSignedIn);
        Assert.True(session.IdleExpiresAt > DateTimeOffset.UtcNow.AddMinutes(115));
        Assert.True(session.RenewAfter > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RenewalDuringAnOutageIsRecoverableAndAfterLogoutCannotRestoreTheSession()
    {
        var (cookie, csrf) = await Login();
        await AgeSession(TimeSpan.FromMinutes(90));
        _factory.Kratos.Unavailable = true;
        using (var outage = Renew(cookie, csrf))
        {
            var response = await _client.SendAsync(outage);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("auth_unavailable", await response.Content.ReadAsStringAsync());
        }
        _factory.Kratos.Unavailable = false;
        using (var recovered = Renew(cookie, csrf))
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(recovered)).StatusCode);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/account/logout");
        logout.Headers.Add("Cookie", cookie);
        logout.Headers.Add("Origin", Dashboard);
        logout.Headers.Add("X-Minicloud-CSRF", csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(logout)).StatusCode);
        using var afterLogout = Renew(cookie, csrf);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(afterLogout)).StatusCode);
    }

    [Fact]
    public async Task UpstreamRevocationEndsRenewal()
    {
        var (cookie, csrf) = await Login();
        _factory.Kratos.Active = false;
        using var request = Renew(cookie, csrf);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.NotNull((await db.BrowserSessions.AsNoTracking().SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task PreferenceChangesTheCurrentSessionCookiePersistence()
    {
        var (cookie, csrf) = await Login();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/account/session/preference")
        { Content = JsonContent.Create(new { keepSignedIn = true }) };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", Dashboard);
        request.Headers.Add("X-Minicloud-CSRF", csrf);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reissued = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-minicloud-session="));
        Assert.Equal(cookie, reissued.Split(';')[0]);
        Assert.Contains("expires=", reissued);
        var session = (await response.Content.ReadFromJsonAsync<AccountSessionResponse>())!;
        Assert.True(session.KeepSignedIn);
        Assert.True(session.ExpiresAt > DateTimeOffset.UtcNow.AddDays(29));
    }

    [Fact]
    public async Task LogoutDuringAnInFlightRenewalWinsAndRevokesTheLateUpstreamExtension()
    {
        var (cookie, csrf) = await Login();
        await AgeSession(TimeSpan.FromMinutes(90));
        var before = await StoredSession();
        _factory.Kratos.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60);
        _factory.Kratos.DuringExtend = async () =>
        {
            using var logout = Logout(cookie, csrf);
            Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(logout)).StatusCode);
        };

        using var renew = Renew(cookie, csrf);
        var response = await _client.SendAsync(renew);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(["revoke", "extend", "revoke"], _factory.Kratos.Writes);
        var stored = await StoredSession();
        Assert.NotNull(stored.RevokedAt);
        Assert.Equal(before.IdleExpiresAt, stored.IdleExpiresAt);
        Assert.Equal(before.RenewedAt, stored.RenewedAt);
        using var again = Renew(cookie, csrf);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task RevocationDuringAnInFlightRenewalIsKeptEvenWhenTheExtensionResponseIsLost()
    {
        var (cookie, csrf) = await Login();
        await AgeSession(TimeSpan.FromMinutes(90));
        _factory.Kratos.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60);
        _factory.Kratos.LoseExtendResponse = true;
        _factory.Kratos.DuringExtend = async () =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BrowserSessionService>()
                .RevokeUserSessionsAsync("user-1", CancellationToken.None);
        };

        using var renew = Renew(cookie, csrf);
        var response = await _client.SendAsync(renew);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("auth_unavailable", await response.Content.ReadAsStringAsync());
        Assert.Equal(["extend", "revoke"], _factory.Kratos.Writes);
        Assert.NotNull((await StoredSession()).RevokedAt);
    }

    [Fact]
    public async Task TokenRotationDuringRenewalLeavesTheNewGenerationAndItsUpstreamSessionAlone()
    {
        var (cookie, csrf) = await Login();
        await AgeSession(TimeSpan.FromMinutes(90));
        _factory.Kratos.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60);
        (string Cookie, string Csrf) rotated = default;
        _factory.Kratos.DuringExtend = async () => rotated = await Login();

        using var renew = Renew(cookie, csrf);
        var response = await _client.SendAsync(renew);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(["extend"], _factory.Kratos.Writes);
        var stored = await StoredSession();
        Assert.Null(stored.RevokedAt);
        Assert.Equal(BrowserSessionService.HashSecret(rotated.Cookie.Split('=', 2)[1]), stored.TokenHash);
        Assert.Equal(stored.CreatedAt, stored.RenewedAt);
        using var current = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
        current.Headers.Add("Cookie", rotated.Cookie);
        var session = await _client.SendAsync(current);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(rotated.Csrf, (await session.Content.ReadFromJsonAsync<AccountSessionResponse>())!.CsrfToken);
    }

    [Fact]
    public async Task RotationThenLogoutDuringRenewalStillRevokesTheLateUpstreamExtension()
    {
        var (cookie, csrf) = await Login();
        await AgeSession(TimeSpan.FromMinutes(90));
        _factory.Kratos.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60);
        _factory.Kratos.DuringExtend = async () =>
        {
            var (rotated, rotatedCsrf) = await Login();
            using var logout = Logout(rotated, rotatedCsrf);
            Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(logout)).StatusCode);
        };

        using var renew = Renew(cookie, csrf);
        var response = await _client.SendAsync(renew);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(["revoke", "extend", "revoke"], _factory.Kratos.Writes);
        Assert.NotNull((await StoredSession()).RevokedAt);
    }

    [Fact]
    public async Task ExpiryDuringAnInFlightRenewalEndsTheSessionAndItsUpstreamSession()
    {
        var (cookie, csrf) = await Login();
        await AgeSession(TimeSpan.FromMinutes(90));
        _factory.Kratos.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60);
        _factory.Kratos.DuringExtend = () => AgeSession(TimeSpan.FromMinutes(45));

        using var renew = Renew(cookie, csrf);
        var response = await _client.SendAsync(renew);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(["extend", "revoke"], _factory.Kratos.Writes);
        var stored = await StoredSession();
        Assert.NotNull(stored.RevokedAt);
        Assert.True(stored.IdleExpiresAt < DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OverlappingRenewalsCoalesceAndKeepTheCookieAndCsrfToken()
    {
        var (cookie, csrf) = await Login();
        await AgeSession(TimeSpan.FromMinutes(90));
        _factory.Kratos.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60);
        HttpResponseMessage? first = null;
        _factory.Kratos.DuringExtend = async () =>
        {
            using var competing = Renew(cookie, csrf);
            first = await _client.SendAsync(competing);
        };

        using var renew = Renew(cookie, csrf);
        var second = await _client.SendAsync(renew);

        Assert.Equal(HttpStatusCode.OK, first!.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var won = (await first.Content.ReadFromJsonAsync<AccountSessionResponse>())!;
        var coalesced = (await second.Content.ReadFromJsonAsync<AccountSessionResponse>())!;
        Assert.Equal(won.IdleExpiresAt, coalesced.IdleExpiresAt);
        Assert.Equal(won.RenewAfter, coalesced.RenewAfter);
        Assert.Equal(csrf, won.CsrfToken);
        Assert.Equal(csrf, coalesced.CsrfToken);
        Assert.False(first.Headers.Contains("Set-Cookie"));
        Assert.False(second.Headers.Contains("Set-Cookie"));
        var stored = await StoredSession();
        Assert.Equal(won.IdleExpiresAt, stored.IdleExpiresAt);
        Assert.True(stored.IdleExpiresAt > DateTimeOffset.UtcNow.AddMinutes(115));
        Assert.Equal(BrowserSessionService.HashSecret(cookie.Split('=', 2)[1]), stored.TokenHash);
        Assert.DoesNotContain("revoke", _factory.Kratos.Writes);

        using var current = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
        current.Headers.Add("Cookie", cookie);
        var session = await _client.SendAsync(current);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(csrf, (await session.Content.ReadFromJsonAsync<AccountSessionResponse>())!.CsrfToken);
    }

    [Fact]
    public async Task PreferenceChangeDuringAnInFlightRenewalIsAppliedBeforeTheRenewalCommits()
    {
        var (cookie, csrf) = await Login();
        await AgeSession(TimeSpan.FromMinutes(90));
        _factory.Kratos.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60);
        _factory.Kratos.DuringExtend = async () =>
        {
            using var preference = Preference(cookie, csrf, keepSignedIn: true);
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(preference)).StatusCode);
        };

        using var renew = Renew(cookie, csrf);
        var response = await _client.SendAsync(renew);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<AccountSessionResponse>())!;
        Assert.True(session.KeepSignedIn);
        Assert.True(session.KeepSignedInPreference);
        Assert.Equal(csrf, session.CsrfToken);
        Assert.True(session.IdleExpiresAt > DateTimeOffset.UtcNow.AddDays(6));
        var stored = await StoredSession();
        Assert.True(stored.Persistent);
        Assert.Equal(session.IdleExpiresAt, stored.IdleExpiresAt);
        Assert.True(stored.User.KeepSignedIn);
    }

    private HttpRequestMessage Logout(string cookie, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/logout");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", Dashboard);
        request.Headers.Add("X-Minicloud-CSRF", csrf);
        return request;
    }

    private HttpRequestMessage Preference(string cookie, string csrf, bool keepSignedIn)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/account/session/preference")
        { Content = JsonContent.Create(new { keepSignedIn }) };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", Dashboard);
        request.Headers.Add("X-Minicloud-CSRF", csrf);
        return request;
    }

    private async Task<BrowserSession> StoredSession()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        return await db.BrowserSessions.AsNoTracking().Include(x => x.User).SingleAsync();
    }

    private HttpRequestMessage Renew(string cookie, string? csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/session/renew");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", Dashboard);
        if (csrf is not null) request.Headers.Add("X-Minicloud-CSRF", csrf);
        return request;
    }

    private async Task AgeSession(TimeSpan age)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var session = await db.BrowserSessions.SingleAsync(x => x.RevokedAt == null);
        session.CreatedAt -= age;
        session.RenewedAt -= age;
        session.LastSeenAt -= age;
        session.ExpiresAt -= age;
        session.IdleExpiresAt -= age;
        await db.SaveChangesAsync();
    }

    private void EnableGoogle()
    {
        _factory.Upstream.Google = true;
        _factory.Services.GetRequiredService<IOptions<KratosOptions>>().Value.GoogleEnabled = true;
    }

    private void EnablePasskey()
    {
        _factory.Upstream.Passkey = true;
        _factory.Services.GetRequiredService<IOptions<KratosOptions>>().Value.PasskeyEnabled = true;
    }

    private static string SyntheticPasskeyAssertionJson() => JsonSerializer.Serialize(new
    {
        id = "Y3JlZC0x",
        rawId = "Y3JlZC0x",
        type = "public-key",
        response = new
        {
            authenticatorData = "YXV0aC1kYXRh",
            clientDataJSON = "Y2xpZW50LWRhdGE",
            signature = "c2lnbmF0dXJl",
            userHandle = "dXNlci1oYW5kbGU"
        }
    });

    private static string SyntheticPasskeyRegistrationJson() => JsonSerializer.Serialize(new
    {
        id = "Y3JlZC0y",
        rawId = "Y3JlZC0y",
        type = "public-key",
        authenticatorAttachment = "platform",
        response = new
        {
            attestationObject = "YXR0ZXN0YXRpb24",
            clientDataJSON = "Y2xpZW50LWRhdGE",
            transports = new[] { "internal", "hybrid" }
        }
    });

    [Fact]
    public async Task DisabledPasskeyOmitsPasskeyOptionsAndRejectsSubmissionsWithoutUpstreamCalls()
    {
        _factory.Upstream.Passkey = true;
        var caps = (await (await _client.GetAsync("/api/account/capabilities")).Content.ReadFromJsonAsync<AccountCapabilitiesResponse>())!;
        Assert.False(caps.Passkey);

        var response = await _client.GetAsync("/api/account/flows/login/browser");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passkey", json);
        Assert.DoesNotContain("webauthn", json);
        Assert.DoesNotContain("kratos-webauthn-script", json);

        var (cookie, csrf) = await Login();
        _factory.Upstream.Requests.Clear();

        using var loginSubmit = Submit("login", new
        {
            csrf_token = "flow-csrf", method = "passkey", passkey_login = SyntheticPasskeyAssertionJson()
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(loginSubmit)).StatusCode);

        using var registerSubmit = Submit("settings", new
        {
            csrf_token = "flow-csrf", method = "passkey", passkey_settings_register = SyntheticPasskeyRegistrationJson()
        }, cookie);
        registerSubmit.Headers.Add("X-Minicloud-CSRF", csrf);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(registerSubmit)).StatusCode);

        using var removeSubmit = Submit("settings", new
        {
            csrf_token = "flow-csrf", method = "passkey", passkey_remove = "637265642d31"
        }, cookie);
        removeSubmit.Headers.Add("X-Minicloud-CSRF", csrf);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(removeSubmit)).StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task EnabledPasskeyProjectsSanitizedLoginOptionsAndCompletesPasswordlessSignIn()
    {
        EnablePasskey();
        var caps = (await (await _client.GetAsync("/api/account/capabilities")).Content.ReadFromJsonAsync<AccountCapabilitiesResponse>())!;
        Assert.True(caps.Passkey);
        Assert.True(caps.Password);

        var start = await _client.GetAsync("/api/account/flows/login/browser");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var raw = await start.Content.ReadAsStringAsync();
        Assert.DoesNotContain("kratos-webauthn-script", raw);
        Assert.DoesNotContain("injected_upstream_field", raw);
        var flow = JsonSerializer.Deserialize<AccountFlowResponse>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Flow!;
        Assert.DoesNotContain(flow.Ui.Nodes, node => node.Group is "passkey" or "webauthn");
        var loginOptions = flow.Ui.Passkey!.LoginOptions!.Value.GetProperty("publicKey");
        Assert.Equal("c3ludGhldGljLWNoYWxsZW5nZQ", loginOptions.GetProperty("challenge").GetString());
        Assert.Equal("dashboard.example.test", loginOptions.GetProperty("rpId").GetString());

        using var request = Submit("login", new
        {
            csrf_token = "synthetic-flow-csrf", method = "passkey", passkey_login = SyntheticPasskeyAssertionJson()
        }, keepSignedIn: true);
        var loginResponse = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginBody = (await loginResponse.Content.ReadFromJsonAsync<AccountFlowResponse>())!;
        Assert.Null(loginBody.Flow);
        Assert.True(loginBody.Session!.KeepSignedIn);
        Assert.NotEmpty(LocalCookie(loginResponse));

        var upstreamSubmit = _factory.Upstream.Requests.First(x => x.Uri.AbsolutePath == "/self-service/login" && x.Body.Length > 0);
        using var forwarded = JsonDocument.Parse(upstreamSubmit.Body);
        Assert.Equal("passkey", forwarded.RootElement.GetProperty("method").GetString());
        Assert.Equal("synthetic-flow-csrf", forwarded.RootElement.GetProperty("csrf_token").GetString());
        using var forwardedAssertion = JsonDocument.Parse(forwarded.RootElement.GetProperty("passkey_login").GetString()!);
        Assert.Equal("Y3JlZC0x", forwardedAssertion.RootElement.GetProperty("id").GetString());

        // Unbound identities are still rejected for passkey login.
        _factory.Upstream.Identity = "44444444-2222-4333-8444-555555555555";
        using var unbound = Submit("login", new
        {
            csrf_token = "synthetic-flow-csrf", method = "passkey", passkey_login = SyntheticPasskeyAssertionJson()
        });
        var rejected = await _client.SendAsync(unbound);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.False(rejected.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task PasskeySettingsExposesMultipleCredentialsAndEnrollsOrRemovesWithSessionRotation()
    {
        EnablePasskey();
        var (cookie, csrf) = await Login();
        _factory.Upstream.PasskeyCredentials =
        [
            ("637265642d31", "Touch ID", 1775001600, false),
            ("637265642d32", "unnamed", 1775088000, true)
        ];

        using (var read = new HttpRequestMessage(HttpMethod.Get, "/api/account/flows/settings/browser"))
        {
            read.Headers.Add("Cookie", cookie + "; ory_kratos_session=synthetic-ory-session");
            var res = await _client.SendAsync(read);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var raw = await res.Content.ReadAsStringAsync();
            Assert.DoesNotContain("kratos-webauthn-script", raw);
            var flow = JsonSerializer.Deserialize<AccountFlowResponse>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Flow!;
            var passkey = flow.Ui.Passkey!;
            Assert.NotNull(passkey.RegistrationOptions);
            Assert.Equal("Minicloud", passkey.RegistrationOptions!.Value.GetProperty("publicKey").GetProperty("rp").GetProperty("name").GetString());
            Assert.Equal(2, passkey.Credentials.Count);
            Assert.Equal("Touch ID", passkey.Credentials[0].DisplayName);
            Assert.True(passkey.Credentials[0].CanRemove);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1775001600), passkey.Credentials[0].AddedAt);
            Assert.Equal("Passkey", passkey.Credentials[1].DisplayName);
            Assert.False(passkey.Credentials[1].CanRemove);
        }

        // Missing CSRF or malformed/mixed passkey payloads fail without calling Kratos.
        _factory.Upstream.Requests.Clear();
        using (var noCsrf = Submit("settings", new
        {
            csrf_token = "flow-csrf", method = "passkey", passkey_settings_register = SyntheticPasskeyRegistrationJson()
        }, cookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(noCsrf)).StatusCode);
        }
        using (var mixed = Submit("settings", new
        {
            csrf_token = "flow-csrf", method = "passkey",
            passkey_settings_register = SyntheticPasskeyRegistrationJson(),
            passkey_remove = "637265642d31"
        }, cookie))
        {
            mixed.Headers.Add("X-Minicloud-CSRF", csrf);
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(mixed)).StatusCode);
        }
        using (var extraField = Submit("settings", new
        {
            csrf_token = "flow-csrf", method = "passkey",
            passkey_remove = "637265642d31",
            password = "should-not-be-allowed"
        }, cookie))
        {
            extraField.Headers.Add("X-Minicloud-CSRF", csrf);
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(extraField)).StatusCode);
        }
        Assert.Empty(_factory.Upstream.Requests);

        // Enroll a passkey -> rotates session.
        using var enroll = Submit("settings", new
        {
            csrf_token = "flow-csrf", method = "passkey", passkey_settings_register = SyntheticPasskeyRegistrationJson()
        }, cookie);
        enroll.Headers.Add("X-Minicloud-CSRF", csrf);
        var enrolled = await _client.SendAsync(enroll);
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        var enrolledBody = (await enrolled.Content.ReadFromJsonAsync<AccountFlowResponse>())!;
        var rotatedCookie = LocalCookie(enrolled);
        var rotatedCsrf = enrolledBody.Session!.CsrfToken;
        Assert.NotEqual(cookie, rotatedCookie);

        // Remove a passkey using the rotated session -> rotates again.
        using var remove = Submit("settings", new
        {
            csrf_token = "flow-csrf", method = "passkey", passkey_remove = "637265642d31"
        }, rotatedCookie);
        remove.Headers.Add("X-Minicloud-CSRF", rotatedCsrf);
        var removed = await _client.SendAsync(remove);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.NotEqual(rotatedCookie, LocalCookie(removed));
    }

    [Fact]
    public async Task PasskeySettingsSessionRefreshRequiredPreservesSessionAndAllowsReauthRetry()
    {
        EnablePasskey();
        var (cookie, csrf) = await Login();
        _factory.Upstream.SettingsPasskeyError = (403,
            """{"error":{"id":"session_refresh_required","code":403,"reason":"private upstream diagnostic"},"redirect_browser_to":"http://kratos:4433/self-service/login/browser?refresh=true"}""");

        using var enroll = Submit("settings", new
        {
            csrf_token = "flow-csrf", method = "passkey", passkey_settings_register = SyntheticPasskeyRegistrationJson()
        }, cookie);
        enroll.Headers.Add("X-Minicloud-CSRF", csrf);
        var staleResponse = await _client.SendAsync(enroll);
        Assert.Equal(HttpStatusCode.Forbidden, staleResponse.StatusCode);
        var staleBody = await staleResponse.Content.ReadAsStringAsync();
        Assert.Contains("session_refresh_required", staleBody);
        Assert.DoesNotContain("private upstream diagnostic", staleBody);
        Assert.False(staleResponse.Headers.Contains("Set-Cookie"));

        // Existing session remains active until re-authentication completes.
        using (var check = new HttpRequestMessage(HttpMethod.Get, "/api/account/session"))
        {
            check.Headers.Add("Cookie", cookie);
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(check)).StatusCode);
        }

        // In-place re-authentication via refresh login flow with passkey, then retry enrollment.
        _factory.Upstream.SettingsPasskeyError = null;
        using var reauthStart = new HttpRequestMessage(HttpMethod.Get, "/api/account/flows/reauthentication/browser?returnTo=%2Fsettings%2Faccount");
        reauthStart.Headers.Add("Cookie", cookie + "; ory_kratos_session=synthetic-ory-session");
        var started = await _client.SendAsync(reauthStart);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var binding = started.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-minicloud-reauthentication=")).Split(';')[0];
        Assert.Contains("refresh=true", _factory.Upstream.Requests.Last().Uri.Query);

        using (var noCsrf = Submit("reauthentication", LoginFields(), cookie + "; " + binding))
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(noCsrf)).StatusCode);

        using var reauthSubmit = Submit("reauthentication", new
        {
            csrf_token = "synthetic-flow-csrf", method = "passkey", passkey_login = SyntheticPasskeyAssertionJson()
        }, cookie + "; " + binding);
        reauthSubmit.Headers.Add("X-Minicloud-CSRF", csrf);
        var reauthed = await _client.SendAsync(reauthSubmit);
        Assert.Equal(HttpStatusCode.OK, reauthed.StatusCode);
        var refreshedCookie = LocalCookie(reauthed);
        var refreshedCsrf = (await reauthed.Content.ReadFromJsonAsync<AccountFlowResponse>())!.Session!.CsrfToken;

        using var retry = Submit("settings", new
        {
            csrf_token = "flow-csrf", method = "passkey", passkey_settings_register = SyntheticPasskeyRegistrationJson()
        }, refreshedCookie);
        retry.Headers.Add("X-Minicloud-CSRF", refreshedCsrf);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(retry)).StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReauthenticationIsBoundToTheInitiatingSessionAndUser(bool otherSession, bool otherUser)
    {
        var (cookie, csrf) = await Login();
        using var start = new HttpRequestMessage(HttpMethod.Get, "/api/account/flows/reauthentication/browser");
        start.Headers.Add("Cookie", cookie);
        var started = await _client.SendAsync(start);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var binding = started.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-minicloud-reauthentication=")).Split(';')[0];
        if (otherUser)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.Users.Add(new User { Id = "user-2", KratosIdentityId = "44444444-2222-4333-8444-555555555555", Email = "other@example.test" });
            await db.SaveChangesAsync();
            _factory.Upstream.Identity = "44444444-2222-4333-8444-555555555555";
        }
        if (otherSession)
        {
            _factory.Upstream.UpstreamSessionId = "55555555-2222-4333-8444-555555555555";
            (cookie, csrf) = await Login();
        }
        _factory.Upstream.Requests.Clear();
        using var submit = Submit("reauthentication", LoginFields(), cookie + (otherSession ? "; " + binding : ""));
        submit.Headers.Add("X-Minicloud-CSRF", csrf);
        var rejected = await _client.SendAsync(submit);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.False(rejected.Headers.Contains("Set-Cookie"));
        Assert.Empty(_factory.Upstream.Requests);
        await using var check = _factory.Services.CreateAsyncScope();
        Assert.All(await check.ServiceProvider.GetRequiredService<MinicloudDbContext>().BrowserSessions.ToListAsync(),
            session => Assert.Null(session.RevokedAt));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public async Task ReauthenticationChecksReturnedIdentityBeforeReplacingStateAndPreservesRememberMe(
        bool differentIdentity, bool changedUpstreamSession, bool remembered)
    {
        using var login = Submit("login", LoginFields(), keepSignedIn: remembered);
        var loggedIn = await _client.SendAsync(login);
        var cookie = LocalCookie(loggedIn);
        var csrf = (await loggedIn.Content.ReadFromJsonAsync<AccountFlowResponse>())!.Session!.CsrfToken;
        using var start = new HttpRequestMessage(HttpMethod.Get, "/api/account/flows/reauthentication/browser");
        start.Headers.Add("Cookie", cookie);
        var started = await _client.SendAsync(start);
        var binding = started.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-minicloud-reauthentication=")).Split(';')[0];
        if (differentIdentity)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.Users.Add(new User { Id = "user-2", KratosIdentityId = "44444444-2222-4333-8444-555555555555", Email = "other@example.test" });
            await db.SaveChangesAsync();
            _factory.Upstream.Identity = "44444444-2222-4333-8444-555555555555";
        }
        if (changedUpstreamSession)
            _factory.Upstream.UpstreamSessionId = "55555555-2222-4333-8444-555555555555";
        using var submit = Submit("reauthentication", LoginFields(), cookie + "; " + binding, keepSignedIn: !remembered);
        submit.Headers.Add("X-Minicloud-CSRF", csrf);
        var response = await _client.SendAsync(submit);
        Assert.Equal(differentIdentity ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
        if (differentIdentity)
        {
            Assert.False(response.Headers.Contains("Set-Cookie"));
            await using var scope = _factory.Services.CreateAsyncScope();
            Assert.Null((await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().BrowserSessions.SingleAsync()).RevokedAt);
        }
        else
        {
            var session = (await response.Content.ReadFromJsonAsync<AccountFlowResponse>())!.Session!;
            Assert.Equal(remembered, session.KeepSignedIn);
            Assert.Equal(remembered, session.KeepSignedInPreference);
            Assert.NotEqual(csrf, session.CsrfToken);
            var replacementCookie = LocalCookie(response);
            Assert.NotEqual(cookie, replacementCookie);
            using var check = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
            check.Headers.Add("Cookie", replacementCookie);
            var active = await _client.SendAsync(check);
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
            Assert.Equal(session.CsrfToken, (await active.Content.ReadFromJsonAsync<AccountSessionResponse>())!.CsrfToken);
            await using var scope = _factory.Services.CreateAsyncScope();
            var rows = await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>()
                .BrowserSessions.AsNoTracking().ToListAsync();
            Assert.Equal(changedUpstreamSession ? 2 : 1, rows.Count);
            var live = Assert.Single(rows, row => row.RevokedAt is null);
            Assert.Equal(remembered, live.Persistent);
            Assert.Equal(_factory.Upstream.UpstreamSessionId, live.KratosSessionId);
            if (changedUpstreamSession)
                Assert.NotNull(rows.Single(row => row.KratosSessionId == SessionId).RevokedAt);
        }
    }

    [Fact]
    public async Task ReauthenticationCannotRestoreASessionRevokedWhileIdentityIsBeingConfirmed()
    {
        var (cookie, csrf) = await Login();
        using var start = new HttpRequestMessage(HttpMethod.Get, "/api/account/flows/reauthentication/browser");
        start.Headers.Add("Cookie", cookie);
        var started = await _client.SendAsync(start);
        var binding = started.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-minicloud-reauthentication=")).Split(';')[0];
        _factory.Upstream.DuringLogin = async () =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BrowserSessionService>().RevokeAsync(cookie.Split('=')[1], default);
        };
        using var submit = Submit("reauthentication", LoginFields(), cookie + "; " + binding);
        submit.Headers.Add("X-Minicloud-CSRF", csrf);
        var rejected = await _client.SendAsync(submit);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.False(rejected.Headers.Contains("Set-Cookie"));
        await using var check = _factory.Services.CreateAsyncScope();
        Assert.NotNull((await check.ServiceProvider.GetRequiredService<MinicloudDbContext>().BrowserSessions.SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task RemovedPasskeyLoginFailureIsSanitizedAndPasswordFallbackRemainsIntact()
    {
        EnablePasskey();
        _factory.Upstream.LoginPasskeyError = (400, """{"error":{"reason":"private upstream diagnostic: unknown credential"}}""");
        using var badPasskey = Submit("login", new
        {
            csrf_token = "synthetic-flow-csrf", method = "passkey", passkey_login = SyntheticPasskeyAssertionJson()
        });
        var failed = await _client.SendAsync(badPasskey);
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        var body = await failed.Content.ReadAsStringAsync();
        Assert.Contains("auth_flow_invalid", body);
        Assert.DoesNotContain("private upstream diagnostic", body);

        // Password sign-in still succeeds cleanly.
        var (cookie, csrf) = await Login();
        Assert.NotEmpty(cookie);
        Assert.NotEmpty(csrf);
    }

    private static string StateHash(string state) => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    private static string Pending(string state, string returnTo = "/apps", string kind = "login") =>
        $"{kind}.{Guid.Parse(FlowId):N}.{StateHash(state)}.{Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(returnTo))}";

    private async Task<HttpResponseMessage> Callback(string state, string? pending, string query = "&code=synthetic-code", string? cookies = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/account/oidc/self-service/methods/oidc/callback/google?state={Uri.EscapeDataString(state)}{query}");
        var cookie = string.Join("; ", new[] { pending is null ? null : $"__Host-minicloud-oidc={pending}", cookies }.Where(x => x is not null));
        if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        return response;
    }

    private static HttpRequestMessage ProviderStart(string kind, object? fields = null, string? returnTo = null, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/account/flows/{kind}/oidc?flow={FlowId}" +
            (returnTo is null ? "" : "&returnTo=" + Uri.EscapeDataString(returnTo)))
        { Content = JsonContent.Create(fields ?? new { csrf_token = "flow-csrf", provider = "google" }) };
        request.Headers.Add("Origin", Dashboard);
        if (cookie is not null) request.Headers.Add("Cookie", cookie + "; ory_kratos_session=synthetic-ory-session");
        return request;
    }

    private async Task<(string Cookie, string Csrf)> Login()
    {
        using var request = Submit("login", LoginFields());
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<AccountFlowResponse>())!;
        Assert.Null(body.Flow);
        return (LocalCookie(response), body.Session!.CsrfToken);
    }

    private static string LocalCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith("__Host-minicloud-session=")).Split(';')[0];

    private static object LoginFields() =>
        new { csrf_token = "synthetic-flow-csrf", identifier = "operator@example.test", password = "synthetic-password", method = "password" };

    private static object PasswordChangeFields() =>
        new { csrf_token = "synthetic-flow-csrf", password = "synthetic-new-password", method = "password" };

    private static HttpRequestMessage Submit(string kind, object fields, string? cookie = null, bool? keepSignedIn = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/account/flows/{kind}?flow={FlowId}" +
            (keepSignedIn is null ? "" : $"&keepSignedIn={keepSignedIn.Value.ToString().ToLowerInvariant()}"))
        { Content = JsonContent.Create(fields) };
        request.Headers.Add("Origin", Dashboard);
        if (cookie is not null) request.Headers.Add("Cookie", cookie + "; ory_kratos_session=synthetic-ory-session");
        return request;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class FlowFactory(SqliteConnection connection) : WebApplicationFactory<Program>
    {
        public UpstreamHandler Upstream { get; } = new();
        public SessionValidator Kratos { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:RunMigrationsOnStartup"] = "false",
                ["ConnectionStrings:Minicloud"] = "Data Source=:memory:",
                ["Cors:AllowedOrigins:0"] = Dashboard,
                ["Kratos:DashboardOrigin"] = Dashboard, ["Kratos:PublicBaseUrl"] = "http://kratos:4433"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.UseSharedSqliteConnection(connection);
                services.RemoveAll<IKratosSessionValidator>();
                services.AddSingleton<IKratosSessionValidator>(Kratos);
                services.AddHttpClient<KratosBrowserFlowClient>().ConfigurePrimaryHttpMessageHandler(() => Upstream);
            });
        }
    }

    private sealed class SessionValidator : IKratosSessionValidator
    {
        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(1);
        public bool Unavailable { get; set; }
        public bool Active { get; set; } = true;
        public int Extensions { get; private set; }
        /// <summary>Upstream writes in order, so a test can tell whether a revocation landed after an extension.</summary>
        public List<string> Writes { get; } = [];
        /// <summary>Runs once while the next extension is in flight, before it lands upstream.</summary>
        public Func<Task>? DuringExtend { get; set; }
        /// <summary>The next extension lands upstream but its response is lost (timeout or outage).</summary>
        public bool LoseExtendResponse { get; set; }
        public Task<bool> IsSessionActiveAsync(string sessionId, string identityId, CancellationToken cancellationToken) => Task.FromResult(Active);
        public Task<KratosSessionState?> GetActiveSessionAsync(string sessionId, string identityId, CancellationToken cancellationToken) =>
            Unavailable ? throw new KratosUnavailableException() : Task.FromResult(Active ? new KratosSessionState(ExpiresAt) : null);
        public async Task<KratosSessionState?> ExtendSessionAsync(string sessionId, string identityId, CancellationToken cancellationToken)
        {
            if (DuringExtend is { } during)
            {
                DuringExtend = null;
                await during();
            }
            Extensions++;
            Writes.Add("extend");
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
            if (LoseExtendResponse)
            {
                LoseExtendResponse = false;
                throw new KratosUnavailableException();
            }
            return await GetActiveSessionAsync(sessionId, identityId, cancellationToken);
        }
        public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            Writes.Add("revoke");
            return Task.CompletedTask;
        }
    }

    private sealed class UpstreamHandler : HttpMessageHandler
    {
        public const string Callback = "https://api.example.test/api/account/oidc/self-service/methods/oidc/callback/google";
        public static readonly string GoogleUrl = "https://accounts.google.com/o/oauth2/v2/auth?client_id=synthetic-client&redirect_uri=" +
            Uri.EscapeDataString(Callback) + "&response_type=code&scope=openid%20email&state=synthetic-state";
        public List<(Uri Uri, string Cookie, string Origin, string Body, string Accept)> Requests { get; } = [];
        public string Identity { get; set; } = IdentityId;
        public string UpstreamSessionId { get; set; } = SessionId;
        public int Status { get; set; } = 200;
        public TimeSpan SettingsPasswordDelay { get; set; }
        public (int Status, string Body)? SettingsPasswordError { get; set; }
        public Func<string, HttpResponseMessage>? RecoverySubmit { get; set; }
        public (int Status, string Body)? SettingsPasskeyError { get; set; }
        public (int Status, string Body)? LoginPasskeyError { get; set; }
        public Func<Task>? DuringLogin { get; set; }
        public bool Google { get; set; }
        public bool Passkey { get; set; }
        public List<(string Id, string DisplayName, long AddedAtUnix, bool Disabled)> PasskeyCredentials { get; set; } = [];
        public string ProviderRedirect { get; set; } = GoogleUrl;
        public int ProviderStatus { get; set; } = 422;
        public int CallbackStatus { get; set; } = 200;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!, request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : "",
                request.Headers.TryGetValues("Origin", out var origin) ? origin.Single() : "", body, request.Headers.Accept.ToString()));
            if (request.RequestUri.AbsolutePath == "/self-service/login" && request.Method == HttpMethod.Post &&
                DuringLogin is { } during)
            {
                DuringLogin = null;
                await during();
            }
            if (request.RequestUri!.AbsolutePath == "/self-service/recovery" && request.Method == HttpMethod.Post &&
                RecoverySubmit is { } recoverySubmit)
                return recoverySubmit(body);
            if (request.RequestUri!.AbsolutePath == "/self-service/settings" && request.Method == HttpMethod.Post &&
                body.Contains("\"method\":\"password\"", StringComparison.Ordinal))
            {
                if (SettingsPasswordDelay > TimeSpan.Zero)
                    await Task.Delay(SettingsPasswordDelay, cancellationToken);
                if (SettingsPasswordError is { } error)
                {
                    var rejected = new HttpResponseMessage((HttpStatusCode)error.Status)
                    {
                        Content = new StringContent(error.Body, Encoding.UTF8, "application/json")
                    };
                    if (error.Status == 403)
                        rejected.Headers.Add("Set-Cookie", "ory_kratos_continuity=synthetic-continuity; Path=/; HttpOnly");
                    return rejected;
                }
                var accepted = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { id = FlowId, type = "browser", state = "success", identity = new { id = Identity } })
                };
                accepted.Headers.Add("Set-Cookie", "ory_kratos_session=updated-ory-session; Path=/; HttpOnly");
                return accepted;
            }
            if (request.RequestUri.AbsolutePath == "/self-service/settings" && request.Method == HttpMethod.Post &&
                body.Contains("\"method\":\"passkey\"", StringComparison.Ordinal) && SettingsPasskeyError is { } settingsPasskeyError)
            {
                var rejected = new HttpResponseMessage((HttpStatusCode)settingsPasskeyError.Status)
                {
                    Content = new StringContent(settingsPasskeyError.Body, Encoding.UTF8, "application/json")
                };
                if (settingsPasskeyError.Status == 403)
                    rejected.Headers.Add("Set-Cookie", "ory_kratos_continuity=synthetic-continuity; Path=/; HttpOnly");
                return rejected;
            }
            if (request.RequestUri.AbsolutePath == "/self-service/login" && request.Method == HttpMethod.Post &&
                body.Contains("\"method\":\"passkey\"", StringComparison.Ordinal) && LoginPasskeyError is { } loginPasskeyError)
            {
                return new HttpResponseMessage((HttpStatusCode)loginPasskeyError.Status)
                {
                    Content = new StringContent(loginPasskeyError.Body, Encoding.UTF8, "application/json")
                };
            }
            return Respond(request, body);
        }

        private HttpResponseMessage Respond(HttpRequestMessage request, string body)
        {
            if (Status != 200)
                return new HttpResponseMessage((HttpStatusCode)Status)
                { Content = new StringContent("private upstream diagnostic") };
            if (request.RequestUri!.AbsolutePath == "/sessions/whoami")
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new { id = UpstreamSessionId, active = true, identity = new { id = Identity } }) };
            if (request.RequestUri.AbsolutePath == "/self-service/methods/oidc/callback/google")
            {
                var callback = new HttpResponseMessage((HttpStatusCode)CallbackStatus)
                {
                    Content = CallbackStatus switch
                    {
                        200 => JsonContent.Create(new { session = new { id = SessionId } }),
                        400 => JsonContent.Create(new { id = FlowId, type = "browser" }),
                        _ => JsonContent.Create(new { redirect_browser_to = "https://evil.example/" })
                    }
                };
                if (CallbackStatus == 303) callback.Headers.Location = new Uri("https://evil.example/");
                callback.Headers.Add("Set-Cookie", "ory_kratos_session=synthetic-ory-session; Path=/; HttpOnly");
                return callback;
            }
            if (request.Method == HttpMethod.Post && body.Contains("\"oidc\""))
            {
                var redirect = new HttpResponseMessage((HttpStatusCode)ProviderStatus)
                {
                    Content = JsonContent.Create(new
                    {
                        error = new { id = "browser_location_change_required", code = 422 },
                        redirect_browser_to = ProviderRedirect
                    })
                };
                if (ProviderStatus == 303) redirect.Headers.Location = new Uri(ProviderRedirect, UriKind.RelativeOrAbsolute);
                redirect.Headers.Add("Set-Cookie", "ory_kratos_continuity=synthetic-continuity; Path=/; HttpOnly");
                return redirect;
            }
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = request.RequestUri.AbsolutePath.EndsWith("settings") || request.RequestUri.AbsolutePath.EndsWith("recovery")
                        ? JsonContent.Create(new { state = request.RequestUri.AbsolutePath.EndsWith("recovery") ? "passed_challenge" : "success" })
                        : JsonContent.Create(new { session = new { id = SessionId } })
                };
                response.Headers.Add("Set-Cookie", "ory_kratos_session=synthetic-ory-session; Path=/; HttpOnly");
                return response;
            }
            var kind = request.RequestUri.AbsolutePath.Split('/')[2];
            var nodes = new List<object>
            {
                new { group = "default", attributes = new { name = "csrf_token", type = "hidden", value = "synthetic-csrf", required = true } },
                new { group = "default", attributes = new { name = "evil", type = "script", value = "private upstream diagnostic", required = false } }
            };
            if (kind == "settings")
            {
                // Profile nodes captured from Kratos v1.3.1 with the packaged identity schema.
                nodes.Add(new { group = "profile", attributes = new { name = "traits.email", type = "email", value = "operator@example.test", required = true } });
                nodes.Add(new { group = "profile", attributes = new { name = "traits.name.first", type = "text", value = "Synthetic", required = false } });
                nodes.Add(new { group = "profile", attributes = new { name = "traits.name.last", type = "text", value = "Operator", required = false } });
                nodes.Add(new { group = "profile", attributes = new { name = "method", type = "submit", value = "profile", required = false } });
            }
            if (Google)
            {
                nodes.Add(new { group = "oidc", attributes = new { name = kind == "settings" ? "link" : "provider", type = "submit", value = "google", required = false } });
            }
            if (Passkey && kind == "login")
            {
                var challengeJson = JsonSerializer.Serialize(new
                {
                    publicKey = new
                    {
                        challenge = "c3ludGhldGljLWNoYWxsZW5nZQ",
                        rpId = "dashboard.example.test",
                        timeout = 60000,
                        userVerification = "preferred",
                        injected_upstream_field = "must-not-forward"
                    }
                });
                nodes.Add(new { group = "webauthn", attributes = new { name = "webauthn_script", type = "script", value = "kratos-webauthn-script", required = false } });
                nodes.Add(new { group = "passkey", attributes = new { name = "passkey_challenge", type = "hidden", value = challengeJson, required = false } });
                nodes.Add(new { group = "passkey", attributes = new { name = "passkey_login", type = "hidden", value = "", required = false } });
                nodes.Add(new { group = "passkey", attributes = new { name = "passkey_login_trigger", type = "button", value = "", required = false } });
            }
            if (Passkey && kind == "settings")
            {
                var createDataJson = JsonSerializer.Serialize(new
                {
                    publicKey = new
                    {
                        challenge = "c3ludGhldGljLXJlZy1jaGFsbGVuZ2U",
                        rp = new { name = "Minicloud", id = "dashboard.example.test" },
                        user = new { id = "dXNlci1pZA", name = "operator@example.test", displayName = "operator@example.test" },
                        pubKeyCredParams = new[] { new { type = "public-key", alg = -7 }, new { type = "public-key", alg = -257 } },
                        timeout = 60000,
                        authenticatorSelection = new { authenticatorAttachment = "platform", residentKey = "required", requireResidentKey = true, userVerification = "preferred" }
                    }
                });
                nodes.Add(new { group = "webauthn", attributes = new { name = "webauthn_script", type = "script", value = "kratos-webauthn-script", required = false } });
                nodes.Add(new { group = "passkey", attributes = new { name = "passkey_create_data", type = "hidden", value = createDataJson, required = false } });
                nodes.Add(new { group = "passkey", attributes = new { name = "passkey_settings_register", type = "hidden", value = "", required = false } });
                foreach (var cred in PasskeyCredentials)
                {
                    nodes.Add(new
                    {
                        group = "passkey",
                        attributes = new { name = "passkey_remove", type = "submit", value = cred.Id, required = false, disabled = cred.Disabled },
                        meta = new
                        {
                            label = new
                            {
                                id = 1050018,
                                text = $"Remove passkey \"{cred.DisplayName}\"",
                                type = "info",
                                context = new { display_name = cred.DisplayName, added_at_unix = cred.AddedAtUnix }
                            }
                        }
                    });
                }
            }
            var flowResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = FlowId, type = "browser", expires_at = DateTimeOffset.UtcNow.AddMinutes(10),
                    ui = new
                    {
                        action = $"http://kratos:4433/self-service/{kind}?flow={FlowId}", method = "POST",
                        nodes,
                        messages = new[] { new { id = 1, type = "error", text = "private upstream diagnostic" } }
                    }
                }), Encoding.UTF8, "application/json")
            };
            flowResponse.Headers.Add("Set-Cookie", "csrf_token_test=synthetic-cookie; Domain=kratos; Path=/self-service; SameSite=None");
            flowResponse.Headers.Add("Set-Cookie", "not_allowed=private; Path=/");
            return flowResponse;
        }
    }
}
