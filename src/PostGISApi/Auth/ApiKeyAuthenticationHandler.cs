using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PostGISApi.Configuration;

namespace PostGISApi.Auth;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>Optional key from environment/appsettings (PostGISApi:AdminApiKey) with admin role.</summary>
    public string? BootstrapAdminKey { get; set; }
}

/// <summary>
/// Authenticates requests with an API key sent as <c>X-API-Key</c> header,
/// <c>Authorization: ApiKey &lt;key&gt;</c> or (if enabled) <c>?api_key=</c>.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ConfigStore store)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-API-Key";
    public const string QueryName = "api_key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = ReadKey();
        if (string.IsNullOrEmpty(key)) return Task.FromResult(AuthenticateResult.NoResult());

        var hash = ApiKeys.Hash(key);

        if (!string.IsNullOrEmpty(Options.BootstrapAdminKey) && ApiKeys.HashEquals(ApiKeys.Hash(Options.BootstrapAdminKey), hash))
            return Task.FromResult(Success("bootstrap-admin", ["read", "write", "admin"], [], []));

        var match = store.Current.Security.ApiKeys.FirstOrDefault(k => ApiKeys.HashEquals(k.KeyHash, hash));
        if (match is null) return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        if (!match.Enabled) return Task.FromResult(AuthenticateResult.Fail("API key is disabled."));
        if (match.ExpiresUtc is { } exp && exp < DateTimeOffset.UtcNow) return Task.FromResult(AuthenticateResult.Fail("API key has expired."));

        return Task.FromResult(Success(match.Name, match.Roles, match.AllowedLayers, match.AllowedQueries));
    }

    private string? ReadKey()
    {
        if (Request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrWhiteSpace(header))
            return header.ToString().Trim();

        var auth = Request.Headers.Authorization.ToString();
        if (auth.StartsWith(SchemeName + " ", StringComparison.OrdinalIgnoreCase))
            return auth[(SchemeName.Length + 1)..].Trim();

        if (store.Current.Settings.AllowApiKeyInQuery && Request.Query.TryGetValue(QueryName, out var q) && !string.IsNullOrWhiteSpace(q))
            return q.ToString().Trim();

        return null;
    }

    private AuthenticateResult Success(string name, IEnumerable<string> roles, IEnumerable<string> layers, IEnumerable<string> queries)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name), new(AccessControl.AuthMethodClaim, "apikey") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(layers.Select(l => new Claim(AccessControl.LayerClaim, l)));
        claims.AddRange(queries.Select(q => new Claim(AccessControl.QueryClaim, q)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
