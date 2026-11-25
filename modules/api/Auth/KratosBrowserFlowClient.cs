using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Selfrun.Common.Contracts;

namespace Selfrun.Api.Auth;

public sealed class AccountFlowException(string code, int statusCode, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record KratosFlowResult(
    AccountBrowserFlow? Flow,
    string? IdentityId,
    string? SessionId,
    IReadOnlyDictionary<string, string> Cookies);

public sealed record KratosProviderRedirect(
    string RedirectUrl,
    string State,
    IReadOnlyDictionary<string, string> Cookies);

public sealed record KratosProviderCallbackResult(
    string? IdentityId,
    string? SessionId,
    Guid? ContinueFlowId,
    IReadOnlyDictionary<string, string> Cookies);

public sealed class KratosBrowserFlowClient(HttpClient client, IOptions<KratosOptions> options)
{
    public const int BodyLimit = 64 * 1024;
    public const string GoogleProvider = "google";
    public const string GoogleCallbackPath = "/api/account/oidc/self-service/methods/oidc/callback/google";
    private const string UpstreamGoogleCallbackPath = "/self-service/methods/oidc/callback/google";
    private const int ProviderValueLimit = 4096;
    private static readonly HashSet<string> GoogleAuthorizationHosts = new(StringComparer.OrdinalIgnoreCase) { "accounts.google.com" };
    private static readonly TimeSpan FlowTimeout = TimeSpan.FromSeconds(10);
    // Kratos v1.3.1 can spend about 20 seconds retrying an unreachable breach lookup before falling back.
    private static readonly TimeSpan PasswordChangeTimeout = TimeSpan.FromSeconds(30);
    private readonly KratosOptions _options = options.Value;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "csrf_token", "identifier", "password", "method", "email", "code",
        "traits.email", "traits.name", "traits.name.first", "traits.name.last"
    };

    public static bool IsFlowCookie(string name) =>
        name is "ory_kratos_session" or "ory_kratos_continuity" || Regex.IsMatch(name, @"^csrf_token_[A-Za-z0-9_-]{1,128}$");

    public string ValidateReturnTo(string? path)
    {
        path ??= "/apps";
        var decoded = Uri.UnescapeDataString(path);
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) ||
            decoded.StartsWith("//", StringComparison.Ordinal) ||
            decoded.Any(c => c == '\\' || char.IsControl(c) || char.IsWhiteSpace(c)) ||
            !Uri.TryCreate(_options.DashboardOrigin, UriKind.Absolute, out var dashboard) ||
            dashboard.Scheme != Uri.UriSchemeHttps || dashboard.GetLeftPart(UriPartial.Authority) != _options.DashboardOrigin ||
            !Uri.TryCreate(dashboard, path, out var target) ||
            target.GetLeftPart(UriPartial.Authority) != _options.DashboardOrigin)
        {
            throw InvalidFlow();
        }
        return target.AbsoluteUri;
    }

    public async Task<KratosFlowResult> SendAsync(
        string kind,
        string operation,
        Guid? id,
        string? returnTo,
        JsonElement? fields,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken cancellationToken)
    {
        if (kind is not ("login" or "settings" or "recovery") ||
            kind == "recovery" && !_options.EmailRecoveryEnabled ||
            operation is not ("start" or "read" or "submit") ||
            operation != "start" && (id is null || id == Guid.Empty))
        {
            throw InvalidFlow();
        }
        var baseUri = PublicOrigin();
        var path = operation switch
        {
            "start" => $"/self-service/{kind}/browser?return_to={Uri.EscapeDataString(ValidateReturnTo(returnTo))}" +
                (kind == "login" ? "&refresh=true" : ""),
            "read" => $"/self-service/{kind}/flows?id={id:D}",
            _ => $"/self-service/{kind}?flow={id:D}"
        };
        using var request = new HttpRequestMessage(operation == "submit" ? HttpMethod.Post : HttpMethod.Get, new Uri(baseUri, path));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("Origin", _options.DashboardOrigin);
        var cookieJar = FlowCookies(cookies);
        AddCookies(request, cookieJar);
        if (operation == "submit")
        {
            request.Content = new StringContent(SerializeFields(kind, fields), Encoding.UTF8, "application/json");
        }
        var changesPassword = kind == "settings" && operation == "submit" && fields is { } body &&
            body.TryGetProperty("method", out var method) && method.ValueEquals("password");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(changesPassword ? PasswordChangeTimeout : FlowTimeout);
        var upstreamToken = timeout.Token;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, upstreamToken);
            if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound) throw ExpiredFlow();
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new AccountFlowException("unauthenticated", 401, "Authentication was rejected.");
            if (kind == "settings" &&
                (response.StatusCode == HttpStatusCode.Forbidden || changesPassword && response.StatusCode == HttpStatusCode.BadRequest) &&
                await ReadSettingsErrorAsync(response, id, upstreamToken) is { } settingsError)
                throw settingsError;
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new AccountFlowException("csrf_invalid", 403, "The account form is invalid.");
            if ((int)response.StatusCode >= 500) throw new KratosUnavailableException();
            if (kind == "recovery" && operation == "submit" && response.StatusCode == HttpStatusCode.UnprocessableEntity &&
                await IsRecoverySettingsTransitionAsync(response, upstreamToken))
            {
                var recoveryCookies = ReadCookies(response);
                if (!recoveryCookies.TryGetValue("ory_kratos_session", out var sessionCookie) || sessionCookie.Length == 0)
                    throw new KratosUnavailableException();
                foreach (var cookie in recoveryCookies)
                {
                    if (cookie.Value.Length == 0) cookieJar.Remove(cookie.Key);
                    else cookieJar[cookie.Key] = cookie.Value;
                }
                var (identityId, sessionId) = await WhoamiAsync(baseUri, cookieJar, upstreamToken);
                return new KratosFlowResult(null, identityId, sessionId, recoveryCookies);
            }
            if (!response.IsSuccessStatusCode)
            {
                // Browser redirects are never followed or exposed as upstream URLs.
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                    throw InvalidFlow();
                throw new KratosUnavailableException();
            }
            var updatedCookies = ReadCookies(response);
            foreach (var cookie in updatedCookies)
            {
                if (cookie.Value.Length == 0) cookieJar.Remove(cookie.Key);
                else cookieJar[cookie.Key] = cookie.Value;
            }
            var payload = await ReadJsonAsync(response, upstreamToken);
            var verified = operation == "submit" &&
                (payload.TryGetProperty("session", out _) ||
                 payload.TryGetProperty("state", out var state) && state.GetString() is "success" or "passed_challenge");
            if (verified)
            {
                var (identityId, sessionId) = await WhoamiAsync(baseUri, cookieJar, upstreamToken);
                return new KratosFlowResult(null, identityId, sessionId, updatedCookies);
            }
            var flow = payload.Deserialize<UpstreamFlow>(JsonOptions) ?? throw new KratosUnavailableException();
            if (!Guid.TryParse(flow.Id, out var returnedId) || id is not null && returnedId != id ||
                flow.ExpiresAt <= DateTimeOffset.UtcNow)
                throw ExpiredFlow();
            if (flow.Type != "browser" || flow.Ui is null || flow.Ui.Nodes is null ||
                !Uri.TryCreate(flow.Ui.Action, UriKind.Absolute, out var action) ||
                action.GetLeftPart(UriPartial.Authority) != baseUri.GetLeftPart(UriPartial.Authority) ||
                action.AbsolutePath != $"/self-service/{kind}" || action.Query != $"?flow={flow.Id}" ||
                !string.Equals(flow.Ui.Method, "POST", StringComparison.OrdinalIgnoreCase))
                throw new KratosUnavailableException();
            var nodes = flow.Ui.Nodes
                .Where(node => node is not null && node.Attributes is not null && Fields.Contains(node.Attributes.Name) &&
                    node.Attributes.Type is "hidden" or "text" or "email" or "password" or "submit" &&
                    node.Group is not ("oidc" or "passkey" or "webauthn"))
                .Select(node => new AccountFlowNode(
                    node.Group, node.Attributes.Type, node.Attributes.Name,
                    node.Attributes.Type == "password" || node.Attributes.Name is "password" or "code" ? null : node.Attributes.Value.ToString(),
                    node.Attributes.Required, SanitizeMessages(node.Messages)))
                .ToArray();
            // Provider navigation is never a generic form submit; it uses the fixed provider action below.
            var providerField = kind == "login" ? "provider" : "link";
            var offersGoogle = _options.GoogleEnabled && kind is ("login" or "settings") &&
                flow.Ui.Nodes.Any(node => node is { Group: "oidc", Attributes: not null } &&
                    node.Attributes.Name == providerField && node.Attributes.Type == "submit" &&
                    node.Attributes.Value.ValueKind == JsonValueKind.String && node.Attributes.Value.GetString() == GoogleProvider);
            var providers = offersGoogle
                ? new[] { new AccountFlowProvider(GoogleProvider, $"/api/account/flows/{kind}/oidc?flow={flow.Id}", "POST") }
                : null;
            var passkey = _options.PasskeyEnabled && kind is ("login" or "settings")
                ? ExtractPasskey(kind, flow.Ui.Nodes)
                : null;
            return new KratosFlowResult(
                new AccountBrowserFlow(flow.Id, kind, flow.ExpiresAt,
                    new AccountFlowUi($"/api/account/flows/{kind}?flow={flow.Id}", "POST", nodes, SanitizeMessages(flow.Ui.Messages), providers, passkey)),
                null, null, updatedCookies);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or
                                           InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new KratosUnavailableException();
        }
    }

    /// <summary>
    /// Submits the Kratos OIDC method for an explicit user action and returns the provider authorization URL
    /// only when it targets the allowlisted HTTPS provider host and this API's fixed callback route.
    /// </summary>
    public async Task<KratosProviderRedirect> StartProviderAsync(
        string kind,
        Guid id,
        JsonElement? fields,
        IReadOnlyDictionary<string, string> cookies,
        string expectedCallbackUrl,
        CancellationToken cancellationToken)
    {
        if (!_options.GoogleEnabled || kind is not ("login" or "settings") || id == Guid.Empty)
            throw InvalidFlow();
        var csrf = ReadProviderFields(fields);
        var body = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["csrf_token"] = csrf,
            ["method"] = "oidc",
            [kind == "login" ? "provider" : "link"] = GoogleProvider
        };
        var baseUri = PublicOrigin();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, $"/self-service/{kind}?flow={id:D}"));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("Origin", _options.DashboardOrigin);
        AddCookies(request, FlowCookies(cookies));
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FlowTimeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var updatedCookies = ReadCookies(response);
            if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound) throw ExpiredFlow();
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new AccountFlowException("unauthenticated", 401, "Authentication was rejected.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new AccountFlowException("csrf_invalid", 403, "The account form is invalid.");
            if (response.StatusCode == HttpStatusCode.BadRequest) throw InvalidFlow();
            // Kratos signals browser navigation for JSON clients with 422; every other status (including 3xx) fails closed.
            if (response.StatusCode != HttpStatusCode.UnprocessableEntity) throw new KratosUnavailableException();
            var payload = await ReadJsonAsync(response, timeout.Token);
            if (!payload.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("id", out var errorId) || errorId.ValueKind != JsonValueKind.String ||
                errorId.GetString() != "browser_location_change_required" ||
                !payload.TryGetProperty("redirect_browser_to", out var target) || target.ValueKind != JsonValueKind.String)
                throw new KratosUnavailableException();
            var (redirect, state) = ValidateProviderRedirect(target.GetString(), expectedCallbackUrl);
            return new KratosProviderRedirect(redirect, state, updatedCookies);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or
                                           InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new KratosUnavailableException();
        }
    }

    /// <summary>
    /// Forwards only the bounded provider callback parameters to the fixed Kratos callback and never follows its redirects.
    /// </summary>
    public async Task<KratosProviderCallbackResult> CompleteProviderAsync(
        Guid flowId,
        string state,
        string? code,
        string? error,
        IReadOnlyDictionary<string, string> cookies,
        CancellationToken cancellationToken)
    {
        if (!_options.GoogleEnabled || flowId == Guid.Empty || !IsProviderValue(state) ||
            (code is null) == (error is null) || code is not null && !IsProviderValue(code) ||
            error is not null && !Regex.IsMatch(error, @"^[A-Za-z0-9_.-]{1,128}$"))
            throw InvalidFlow();
        var baseUri = PublicOrigin();
        var query = $"?state={Uri.EscapeDataString(state)}" +
            (code is null ? $"&error={Uri.EscapeDataString(error!)}" : $"&code={Uri.EscapeDataString(code)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, UpstreamGoogleCallbackPath + query));
        request.Headers.Accept.ParseAdd("application/json");
        var cookieJar = FlowCookies(cookies);
        AddCookies(request, cookieJar);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FlowTimeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var updatedCookies = ReadCookies(response);
            foreach (var cookie in updatedCookies)
            {
                if (cookie.Value.Length == 0) cookieJar.Remove(cookie.Key);
                else cookieJar[cookie.Key] = cookie.Value;
            }
            if ((int)response.StatusCode >= 500) throw new KratosUnavailableException();
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest))
                return new KratosProviderCallbackResult(null, null, null, updatedCookies);
            var payload = await ReadJsonAsync(response, timeout.Token);
            if (payload.ValueKind != JsonValueKind.Object)
                return new KratosProviderCallbackResult(null, null, null, updatedCookies);
            if (response.StatusCode == HttpStatusCode.OK &&
                (payload.TryGetProperty("session", out _) ||
                 payload.TryGetProperty("state", out var flowState) && flowState.ValueKind == JsonValueKind.String &&
                 flowState.GetString() == "success"))
            {
                var (identityId, sessionId) = await WhoamiAsync(baseUri, cookieJar, timeout.Token);
                return new KratosProviderCallbackResult(identityId, sessionId, null, updatedCookies);
            }
            // A returned form for the same pending flow can be re-read by the dashboard; nothing else is continued.
            var continueId = payload.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                Guid.TryParseExact(id.GetString(), "D", out var returnedId) && returnedId == flowId
                    ? flowId : (Guid?)null;
            return new KratosProviderCallbackResult(null, null, continueId, updatedCookies);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or
                                           InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new KratosUnavailableException();
        }
    }

    public static bool IsProviderValue(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= ProviderValueLimit && value.All(c => c is > ' ' and < (char)0x7f);

    private static string ReadProviderFields(JsonElement? fields)
    {
        if (fields is not { ValueKind: JsonValueKind.Object } body) throw InvalidFlow();
        string? csrf = null, provider = null;
        foreach (var field in body.EnumerateObject())
        {
            if (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString() is not { Length: <= BodyLimit } value)
                throw InvalidFlow();
            switch (field.Name)
            {
                case "csrf_token" when csrf is null: csrf = value; break;
                case "provider" when provider is null: provider = value; break;
                default: throw InvalidFlow();
            }
        }
        if (provider != GoogleProvider) throw InvalidFlow();
        if (string.IsNullOrWhiteSpace(csrf))
            throw new AccountFlowException("csrf_invalid", 403, "The account form is invalid.");
        return csrf;
    }

    private static (string Url, string State) ValidateProviderRedirect(string? value, string expectedCallbackUrl)
    {
        if (value is null || value.Length > ProviderValueLimit ||
            !Uri.TryCreate(value, UriKind.Absolute, out var target) ||
            target.Scheme != Uri.UriSchemeHttps || !target.IsDefaultPort ||
            !string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Fragment) ||
            !GoogleAuthorizationHosts.Contains(target.IdnHost) ||
            !target.AbsolutePath.StartsWith("/o/oauth2/", StringComparison.Ordinal))
            throw new KratosUnavailableException();
        var query = QueryHelpers.ParseQuery(target.Query);
        string? Single(string name) =>
            query.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
        var state = Single("state");
        if (!IsProviderValue(state) || string.IsNullOrEmpty(Single("client_id")) ||
            Single("response_type") != "code" ||
            !string.Equals(Single("redirect_uri"), expectedCallbackUrl, StringComparison.Ordinal))
            throw new KratosUnavailableException();
        return (target.AbsoluteUri, state!);
    }

    private async Task<(string IdentityId, string SessionId)> WhoamiAsync(
        Uri baseUri, IReadOnlyDictionary<string, string> cookieJar, CancellationToken cancellationToken)
    {
        using var whoami = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/sessions/whoami"));
        whoami.Headers.Accept.ParseAdd("application/json");
        AddCookies(whoami, cookieJar);
        using var sessionResponse = await client.SendAsync(whoami, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (sessionResponse.StatusCode == HttpStatusCode.Unauthorized)
            throw new AccountFlowException("unauthenticated", 401, "Authentication was rejected.");
        if (!sessionResponse.IsSuccessStatusCode) throw new KratosUnavailableException();
        var session = await ReadJsonAsync(sessionResponse, cancellationToken);
        if (!session.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.True)
            throw new AccountFlowException("unauthenticated", 401, "Authentication was rejected.");
        var sessionId = session.GetProperty("id").GetString();
        var identityId = session.GetProperty("identity").GetProperty("id").GetString();
        if (!Guid.TryParse(sessionId, out _) || !Guid.TryParse(identityId, out _))
            throw new KratosUnavailableException();
        return (identityId!, sessionId!);
    }

    private static Dictionary<string, string> FlowCookies(IReadOnlyDictionary<string, string> cookies) =>
        cookies.Where(cookie => IsFlowCookie(cookie.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    private Uri PublicOrigin()
    {
        if (!Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new KratosUnavailableException();
        return uri;
    }

    private string SerializeFields(string kind, JsonElement? fields)
    {
        if (fields is not { ValueKind: JsonValueKind.Object } body) throw InvalidFlow();
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var traits = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in body.EnumerateObject())
        {
            if (field.Value.ValueKind != JsonValueKind.String ||
                field.Value.GetString() is not { } value || value.Length > BodyLimit)
                throw InvalidFlow();
            if (field.Name is "passkey_login" or "passkey_settings_register" or "passkey_remove")
            {
                if (!_options.PasskeyEnabled ||
                    field.Name == "passkey_login" && kind != "login" ||
                    field.Name is ("passkey_settings_register" or "passkey_remove") && kind != "settings")
                    throw InvalidFlow();
                var normalized = field.Name switch
                {
                    "passkey_login" => NormalizePasskeyAssertion(value),
                    "passkey_settings_register" => NormalizePasskeyRegistration(value),
                    _ => NormalizePasskeyCredentialId(value)
                };
                if (!values.TryAdd(field.Name, normalized)) throw InvalidFlow();
                continue;
            }
            if (!Fields.Contains(field.Name))
                throw InvalidFlow();
            if (field.Name.StartsWith("traits.", StringComparison.Ordinal))
            {
                if (field.Name is "traits.name.first" or "traits.name.last")
                {
                    // Both name parts share one schema object; a flat "traits.name" string cannot be mixed in.
                    if (!traits.TryGetValue("name", out var existing)) traits["name"] = existing = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (existing is not Dictionary<string, string> name || !name.TryAdd(field.Name[12..], value))
                        throw InvalidFlow();
                }
                else if (!traits.TryAdd(field.Name[7..], value)) throw InvalidFlow();
            }
            else if (!values.TryAdd(field.Name, value)) throw InvalidFlow();
        }
        if (traits.Count > 0) values["traits"] = traits;
        if (!values.TryGetValue("csrf_token", out var csrf) || string.IsNullOrWhiteSpace(csrf.ToString()))
            throw new AccountFlowException("csrf_invalid", 403, "The account form is invalid.");
        var hasPasskeyLogin = values.ContainsKey("passkey_login");
        var hasPasskeyRegister = values.ContainsKey("passkey_settings_register");
        var hasPasskeyRemove = values.ContainsKey("passkey_remove");
        var hasAnyPasskeyField = hasPasskeyLogin || hasPasskeyRegister || hasPasskeyRemove;
        var isPasskeyMethod = values.TryGetValue("method", out var methodObj) &&
            string.Equals(methodObj as string, "passkey", StringComparison.Ordinal);
        if (isPasskeyMethod)
        {
            if (!_options.PasskeyEnabled || kind is not ("login" or "settings") || traits.Count > 0)
                throw InvalidFlow();
            if (kind == "login" && (!hasPasskeyLogin || values.Count != 3))
                throw InvalidFlow();
            if (kind == "settings" && (hasPasskeyRegister == hasPasskeyRemove || values.Count != 3))
                throw InvalidFlow();
        }
        else if (hasAnyPasskeyField)
        {
            throw InvalidFlow();
        }
        return JsonSerializer.Serialize(values);
    }

    private static AccountFlowPasskey? ExtractPasskey(string kind, UpstreamNode[] nodes)
    {
        if (kind == "login")
        {
            var challenge = nodes.FirstOrDefault(node => node is { Group: "passkey", Attributes: { Name: "passkey_challenge", Type: "hidden" } } &&
                node.Attributes.Value.ValueKind == JsonValueKind.String);
            if (challenge?.Attributes.Value.GetString() is not { } raw)
                return null;
            var loginOptions = SanitizeLoginOptions(raw);
            return loginOptions is null ? null : new AccountFlowPasskey(loginOptions, null, []);
        }

        if (kind == "settings")
        {
            JsonElement? registrationOptions = null;
            var createNode = nodes.FirstOrDefault(node => node is { Group: "passkey", Attributes: { Name: "passkey_create_data", Type: "hidden" } } &&
                node.Attributes.Value.ValueKind == JsonValueKind.String);
            if (createNode?.Attributes.Value.GetString() is { } rawCreate)
                registrationOptions = SanitizeRegistrationOptions(rawCreate);

            var credentials = new List<AccountFlowPasskeyCredential>();
            foreach (var node in nodes)
            {
                if (node is not { Group: "passkey", Attributes: { Name: "passkey_remove", Type: "submit" } } ||
                    node.Attributes.Value.ValueKind != JsonValueKind.String)
                    continue;
                var id = node.Attributes.Value.GetString();
                if (!IsPasskeyCredentialId(id))
                    continue;
                var (displayName, addedAt) = ReadCredentialMetadata(node.Meta?.Label?.Context);
                credentials.Add(new AccountFlowPasskeyCredential(id!, displayName, addedAt, !node.Attributes.Disabled));
            }
            if (registrationOptions is null && credentials.Count == 0)
                return null;
            return new AccountFlowPasskey(null, registrationOptions, credentials);
        }

        return null;
    }

    private static (string DisplayName, DateTimeOffset? AddedAt) ReadCredentialMetadata(JsonElement? context)
    {
        var displayName = "Passkey";
        DateTimeOffset? addedAt = null;
        if (context is not { ValueKind: JsonValueKind.Object } obj)
            return (displayName, addedAt);
        if (obj.TryGetProperty("display_name", out var rawName) && rawName.ValueKind == JsonValueKind.String)
        {
            var cleaned = new string((rawName.GetString() ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (cleaned.Length > 120) cleaned = cleaned[..120].Trim();
            if (!string.IsNullOrEmpty(cleaned) && !string.Equals(cleaned, "unnamed", StringComparison.OrdinalIgnoreCase))
                displayName = cleaned;
        }
        if (obj.TryGetProperty("added_at", out var rawAddedAt) && rawAddedAt.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(rawAddedAt.GetString(), out var parsedDate) && parsedDate.Year >= 2000)
        {
            addedAt = parsedDate;
        }
        else if (obj.TryGetProperty("added_at_unix", out var rawUnix) && rawUnix.ValueKind == JsonValueKind.Number &&
            rawUnix.TryGetInt64(out var seconds) && seconds > 0)
        {
            try { addedAt = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { addedAt = null; }
        }
        return (displayName, addedAt);
    }

    private static JsonElement? SanitizeLoginOptions(string raw)
    {
        if (raw.Length is 0 or > BodyLimit) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("publicKey", out var pk) || pk.ValueKind != JsonValueKind.Object)
                return null;
            if (!TryGetBase64Url(pk, "challenge", 1024, out var challenge)) return null;
            var publicKey = new Dictionary<string, object>(StringComparer.Ordinal) { ["challenge"] = challenge };
            if (TryGetBoundedString(pk, "rpId", 253, out var rpId))
                publicKey["rpId"] = rpId;
            if (TryGetPositiveLong(pk, "timeout", out var timeout))
                publicKey["timeout"] = timeout;
            if (TryGetAllowedString(pk, "userVerification", ["required", "preferred", "discouraged"], out var uv))
                publicKey["userVerification"] = uv;
            if (pk.TryGetProperty("allowCredentials", out var allowElement))
            {
                if (!TrySanitizeDescriptorList(allowElement, out var allowList)) return null;
                publicKey["allowCredentials"] = allowList;
            }
            return JsonSerializer.SerializeToElement(new Dictionary<string, object>(StringComparer.Ordinal) { ["publicKey"] = publicKey });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? SanitizeRegistrationOptions(string raw)
    {
        if (raw.Length is 0 or > BodyLimit) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("publicKey", out var pk) || pk.ValueKind != JsonValueKind.Object)
                return null;
            if (!TryGetBase64Url(pk, "challenge", 1024, out var challenge)) return null;
            if (!pk.TryGetProperty("rp", out var rpEl) || rpEl.ValueKind != JsonValueKind.Object ||
                !TryGetBoundedString(rpEl, "name", 128, out var rpName))
                return null;
            var rp = new Dictionary<string, object>(StringComparer.Ordinal) { ["name"] = rpName };
            if (TryGetBoundedString(rpEl, "id", 253, out var rpId))
                rp["id"] = rpId;

            if (!pk.TryGetProperty("user", out var userEl) || userEl.ValueKind != JsonValueKind.Object ||
                !TryGetBase64Url(userEl, "id", 1024, out var userId) ||
                !TryGetBoundedString(userEl, "name", 256, out var userName) ||
                !TryGetBoundedString(userEl, "displayName", 256, out var userDisplayName))
                return null;
            var user = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = userId,
                ["name"] = userName,
                ["displayName"] = userDisplayName
            };

            if (!pk.TryGetProperty("pubKeyCredParams", out var paramsEl) || paramsEl.ValueKind != JsonValueKind.Array)
                return null;
            var credParams = new List<Dictionary<string, object>>();
            foreach (var item in paramsEl.EnumerateArray())
            {
                if (credParams.Count >= 32 || item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String ||
                    typeEl.GetString() != "public-key" ||
                    !item.TryGetProperty("alg", out var algEl) || algEl.ValueKind != JsonValueKind.Number ||
                    !algEl.TryGetInt32(out var alg))
                    return null;
                credParams.Add(new Dictionary<string, object>(StringComparer.Ordinal) { ["type"] = "public-key", ["alg"] = alg });
            }
            if (credParams.Count == 0) return null;

            var publicKey = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["challenge"] = challenge,
                ["rp"] = rp,
                ["user"] = user,
                ["pubKeyCredParams"] = credParams
            };
            if (TryGetPositiveLong(pk, "timeout", out var timeout))
                publicKey["timeout"] = timeout;
            if (TryGetAllowedString(pk, "attestation", ["none", "indirect", "direct", "enterprise"], out var attestation))
                publicKey["attestation"] = attestation;
            if (pk.TryGetProperty("authenticatorSelection", out var selEl) && selEl.ValueKind == JsonValueKind.Object)
            {
                var selection = new Dictionary<string, object>(StringComparer.Ordinal);
                if (TryGetAllowedString(selEl, "authenticatorAttachment", ["platform", "cross-platform"], out var attachment))
                    selection["authenticatorAttachment"] = attachment;
                if (TryGetAllowedString(selEl, "residentKey", ["discouraged", "preferred", "required"], out var rk))
                    selection["residentKey"] = rk;
                if (selEl.TryGetProperty("requireResidentKey", out var rrk) && rrk.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    selection["requireResidentKey"] = rrk.GetBoolean();
                if (TryGetAllowedString(selEl, "userVerification", ["required", "preferred", "discouraged"], out var uv))
                    selection["userVerification"] = uv;
                if (selection.Count > 0)
                    publicKey["authenticatorSelection"] = selection;
            }
            if (pk.TryGetProperty("excludeCredentials", out var excludeElement))
            {
                if (!TrySanitizeDescriptorList(excludeElement, out var excludeList)) return null;
                publicKey["excludeCredentials"] = excludeList;
            }
            return JsonSerializer.SerializeToElement(new Dictionary<string, object>(StringComparer.Ordinal) { ["publicKey"] = publicKey });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TrySanitizeDescriptorList(JsonElement element, out List<Dictionary<string, object>> result)
    {
        result = [];
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in element.EnumerateArray())
        {
            if (result.Count >= 128 || item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String ||
                typeEl.GetString() != "public-key" ||
                !TryGetBase64Url(item, "id", 1024, out var credId))
                return false;
            var descriptor = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "public-key",
                ["id"] = credId
            };
            if (item.TryGetProperty("transports", out var transportsEl) && transportsEl.ValueKind == JsonValueKind.Array)
            {
                var transports = new List<string>();
                foreach (var t in transportsEl.EnumerateArray())
                {
                    if (transports.Count >= 8 || t.ValueKind != JsonValueKind.String) return false;
                    var tv = t.GetString();
                    if (tv is not ("usb" or "nfc" or "ble" or "hybrid" or "internal" or "smart-card")) continue;
                    transports.Add(tv);
                }
                if (transports.Count > 0) descriptor["transports"] = transports;
            }
            result.Add(descriptor);
        }
        return true;
    }

    private static string NormalizePasskeyAssertion(string raw)
    {
        if (raw.Length is 0 or > BodyLimit) throw InvalidFlow();
        try
        {
            using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetBase64Url(root, "id", 1024, out var id) ||
                !TryGetBase64Url(root, "rawId", 1024, out var rawId) ||
                !root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String ||
                typeEl.GetString() != "public-key" ||
                !root.TryGetProperty("response", out var resp) || resp.ValueKind != JsonValueKind.Object ||
                !TryGetBase64Url(resp, "authenticatorData", 16384, out var authenticatorData) ||
                !TryGetBase64Url(resp, "clientDataJSON", 16384, out var clientDataJson) ||
                !TryGetBase64Url(resp, "signature", 4096, out var signature))
                throw InvalidFlow();
            var responseObj = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["authenticatorData"] = authenticatorData,
                ["clientDataJSON"] = clientDataJson,
                ["signature"] = signature
            };
            if (resp.TryGetProperty("userHandle", out var userHandleEl))
            {
                if (userHandleEl.ValueKind == JsonValueKind.Null)
                    responseObj["userHandle"] = null;
                else if (userHandleEl.ValueKind == JsonValueKind.String && userHandleEl.GetString() is { } uh &&
                    (uh.Length == 0 || IsBase64Url(uh, 1024)))
                    responseObj["userHandle"] = uh;
                else
                    throw InvalidFlow();
            }
            return JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["rawId"] = rawId,
                ["type"] = "public-key",
                ["response"] = responseObj
            });
        }
        catch (JsonException)
        {
            throw InvalidFlow();
        }
    }

    private static string NormalizePasskeyRegistration(string raw)
    {
        if (raw.Length is 0 or > BodyLimit) throw InvalidFlow();
        try
        {
            using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetBase64Url(root, "id", 1024, out var id) ||
                !TryGetBase64Url(root, "rawId", 1024, out var rawId) ||
                !root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String ||
                typeEl.GetString() != "public-key" ||
                !root.TryGetProperty("response", out var resp) || resp.ValueKind != JsonValueKind.Object ||
                !TryGetBase64Url(resp, "attestationObject", 32768, out var attestationObject) ||
                !TryGetBase64Url(resp, "clientDataJSON", 16384, out var clientDataJson))
                throw InvalidFlow();
            var responseObj = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["attestationObject"] = attestationObject,
                ["clientDataJSON"] = clientDataJson
            };
            if (resp.TryGetProperty("transports", out var transportsEl) && transportsEl.ValueKind == JsonValueKind.Array)
            {
                var transports = new List<string>();
                foreach (var t in transportsEl.EnumerateArray())
                {
                    if (transports.Count >= 8 || t.ValueKind != JsonValueKind.String) throw InvalidFlow();
                    var tv = t.GetString();
                    if (tv is not ("usb" or "nfc" or "ble" or "hybrid" or "internal" or "smart-card")) continue;
                    transports.Add(tv);
                }
                if (transports.Count > 0) responseObj["transports"] = transports;
            }
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["rawId"] = rawId,
                ["type"] = "public-key",
                ["response"] = responseObj
            };
            if (TryGetAllowedString(root, "authenticatorAttachment", ["platform", "cross-platform"], out var attachment))
                payload["authenticatorAttachment"] = attachment;
            return JsonSerializer.Serialize(payload);
        }
        catch (JsonException)
        {
            throw InvalidFlow();
        }
    }

    private static string NormalizePasskeyCredentialId(string value) =>
        IsPasskeyCredentialId(value) ? value : throw InvalidFlow();

    private static bool IsPasskeyCredentialId(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 512 && Regex.IsMatch(value, @"^[A-Za-z0-9_-]{1,512}$");

    private static bool IsBase64Url(string? value, int maxLength) =>
        !string.IsNullOrEmpty(value) && value.Length <= maxLength &&
        value.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '=');

    private static bool TryGetBase64Url(JsonElement obj, string name, int maxLength, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return false;
        var candidate = el.GetString();
        if (!IsBase64Url(candidate, maxLength)) return false;
        value = candidate!;
        return true;
    }

    private static bool TryGetBoundedString(JsonElement obj, string name, int maxLength, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return false;
        var candidate = el.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > maxLength || candidate.Any(char.IsControl)) return false;
        value = candidate;
        return true;
    }

    private static bool TryGetAllowedString(JsonElement obj, string name, string[] allowed, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String) return false;
        var candidate = el.GetString();
        if (candidate is null || !allowed.Contains(candidate, StringComparer.Ordinal)) return false;
        value = candidate;
        return true;
    }

    private static bool TryGetPositiveLong(JsonElement obj, string name, out long value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number &&
            el.TryGetInt64(out value) && value > 0;
    }

    private static void AddCookies(HttpRequestMessage request, IReadOnlyDictionary<string, string> cookies)
    {
        if (cookies.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(x => $"{x.Key}={x.Value}")));
    }

    private static Dictionary<string, string> ReadCookies(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return result;
        if (!SetCookieHeaderValue.TryParseList(values.ToArray(), out var parsed))
            throw new KratosUnavailableException();
        foreach (var cookie in parsed)
        {
            if (!IsFlowCookie(cookie.Name.ToString())) continue;
            result[cookie.Name.ToString()] = cookie.MaxAge <= TimeSpan.Zero || cookie.Expires <= DateTimeOffset.UtcNow
                ? "" : cookie.Value.ToString();
        }
        return result;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > BodyLimit) throw new KratosUnavailableException();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > BodyLimit) throw new KratosUnavailableException();
            buffer.Write(chunk, 0, read);
        }
        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        return document.RootElement.Clone();
    }

    private static async Task<AccountFlowException?> ReadSettingsErrorAsync(
        HttpResponseMessage response, Guid? id, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            return null;
        try
        {
            var payload = await ReadJsonAsync(response, cancellationToken);
            if (payload.ValueKind != JsonValueKind.Object)
                return null;
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // The redirect and continuity cookie belong to Kratos. Require a fresh Selfrun sign-in and form instead.
                if (payload.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("id", out var errorId) && errorId.ValueKind == JsonValueKind.String &&
                    errorId.ValueEquals("session_refresh_required"))
                    return new AccountFlowException("session_refresh_required", 403, "Log out and sign in again before updating account settings.");
            }
            else if (payload.Deserialize<UpstreamFlow>(JsonOptions) is { Type: "browser", Ui.Nodes: { } nodes } flow &&
                Guid.TryParse(flow.Id, out var returnedId) && returnedId == id &&
                nodes.Any(node => node is { Group: "password", Attributes.Name: "password" } &&
                    node.Messages.Any(message => message?.Type == "error")))
            {
                return new AccountFlowException("auth_password_rejected", 400, "The password was rejected. Choose a different password and try again.");
            }
        }
        catch (JsonException)
        {
            // Unrecognized error bodies still use the sanitized status-based rejection below.
        }
        return null;
    }

    /// <summary>
    /// Recognizes Kratos v1.3.1's default JSON recovery-code completion (422 <c>browser_location_change_required</c>
    /// targeting the configured dashboard settings route) without following or exposing the upstream redirect URL.
    /// </summary>
    private async Task<bool> IsRecoverySettingsTransitionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            return false;
        var payload = await ReadJsonAsync(response, cancellationToken);
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("id", out var errorId) || errorId.ValueKind != JsonValueKind.String ||
            !errorId.ValueEquals("browser_location_change_required"))
            return false;
        if (!payload.TryGetProperty("redirect_browser_to", out var target) || target.ValueKind != JsonValueKind.String)
            throw new KratosUnavailableException();
        ValidateRecoverySettingsRedirect(target.GetString());
        return true;
    }

    private void ValidateRecoverySettingsRedirect(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > ProviderValueLimit ||
            !Uri.TryCreate(_options.DashboardOrigin, UriKind.Absolute, out var dashboard) ||
            dashboard.Scheme != Uri.UriSchemeHttps || dashboard.GetLeftPart(UriPartial.Authority) != _options.DashboardOrigin ||
            !Uri.TryCreate(value, UriKind.Absolute, out var target) ||
            target.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(target.UserInfo) ||
            !string.IsNullOrEmpty(target.Fragment) ||
            target.GetLeftPart(UriPartial.Authority) != _options.DashboardOrigin ||
            !string.Equals(target.AbsolutePath, "/settings/account", StringComparison.Ordinal))
            throw new KratosUnavailableException();
    }

    private static AccountFlowMessage[] SanitizeMessages(UpstreamMessage[] messages) =>
        messages.Where(message => message is not null).Select(message => new AccountFlowMessage(message.Id,
            message.Type == "error" ? "Check your account details." : "Continue with the account form.",
            message.Type == "error" ? "error" : "info")).ToArray();

    private static AccountFlowException InvalidFlow() => new("auth_flow_invalid", 400, "The account form is invalid.");
    private static AccountFlowException ExpiredFlow() => new("auth_flow_expired", 410, "The account form expired. Start a new form.");

    private sealed record UpstreamFlow(string Id, string Type, [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt, UpstreamUi Ui);
    private sealed record UpstreamUi(string Action, string Method, UpstreamNode[] Nodes, UpstreamMessage[] Messages)
    {
        public UpstreamMessage[] Messages { get; init; } = Messages ?? [];
    }
    private sealed record UpstreamNode(string Group, UpstreamAttributes Attributes, UpstreamMessage[] Messages, UpstreamMeta? Meta = null)
    {
        public UpstreamMessage[] Messages { get; init; } = Messages ?? [];
    }
    private sealed record UpstreamAttributes(string Name, string Type, JsonElement Value, bool Required, bool Disabled = false);
    private sealed record UpstreamMeta(UpstreamLabel? Label);
    private sealed record UpstreamLabel(int Id, string? Text, string? Type, JsonElement? Context);
    private sealed record UpstreamMessage(int Id, string Type);
}
