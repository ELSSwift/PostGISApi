using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PostGISApi.Configuration;

namespace PostGISApi.Auth;

public static class AuthSetup
{
    public const string SmartScheme = "ApiKeyOrBearer";

    public static IServiceCollection AddPostGisAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtFromStore>();

        services.AddAuthentication(SmartScheme)
            .AddPolicyScheme(SmartScheme, "API key or OIDC bearer token", o =>
            {
                o.ForwardDefaultSelector = ctx =>
                {
                    var auth = ctx.Request.Headers.Authorization.ToString();
                    return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? JwtBearerDefaults.AuthenticationScheme
                        : ApiKeyAuthenticationHandler.SchemeName;
                };
            })
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, o =>
            {
                o.BootstrapAdminKey = configuration["PostGISApi:AdminApiKey"];
            })
            .AddJwtBearer();

        services.AddAuthorizationBuilder()
            .AddPolicy(AccessControl.ReadPolicy, p => p.RequireRole("read", "write", "admin"))
            .AddPolicy(AccessControl.WritePolicy, p => p.RequireRole("write", "admin"))
            .AddPolicy(AccessControl.AdminPolicy, p => p.RequireRole("admin"));

        return services;
    }

    /// <summary>Call after the OIDC settings changed so that the next request uses the new settings.</summary>
    public static void ResetJwtOptions(IServiceProvider sp) =>
        sp.GetRequiredService<IOptionsMonitorCache<JwtBearerOptions>>().TryRemove(JwtBearerDefaults.AuthenticationScheme);

    /// <summary>Generates an initial admin key on first start if no admin access exists.</summary>
    public static void EnsureAdminAccess(ConfigStore store, IConfiguration configuration, ILogger logger)
    {
        if (!string.IsNullOrEmpty(configuration["PostGISApi:AdminApiKey"])) return;
        if (store.Current.Security.ApiKeys.Any(k => k.Enabled && k.Roles.Contains("admin"))) return;

        var key = ApiKeys.Generate();
        store.Update(c => c.Security.ApiKeys.Add(new ApiKeyConfig
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = "initial-admin",
            KeyHash = ApiKeys.Hash(key),
            KeyPrefix = ApiKeys.DisplayPrefix(key),
            Roles = ["read", "write", "admin"],
        }));
        logger.LogWarning(
            "No admin API key configured. Generated initial admin key (shown only once, store it safely): {ApiKey}", key);
    }

    private sealed class ConfigureJwtFromStore(ConfigStore store) : IConfigureNamedOptions<JwtBearerOptions>
    {
        public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

        public void Configure(string? name, JwtBearerOptions o)
        {
            if (name != JwtBearerDefaults.AuthenticationScheme) return;
            var oidc = store.Current.Security.Oidc;
            o.MapInboundClaims = false;
            o.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
            if (oidc.Enabled && !string.IsNullOrWhiteSpace(oidc.Authority))
            {
                o.Authority = oidc.Authority;
                o.Audience = string.IsNullOrWhiteSpace(oidc.Audience) ? null : oidc.Audience;
            }
            else
            {
                // OIDC disabled: no signing keys -> every bearer token is rejected
                o.Authority = null;
                o.Configuration = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration();
            }
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = !string.IsNullOrWhiteSpace(oidc.Audience),
                ValidAudience = oidc.Audience,
                ValidateIssuer = true,
                NameClaimType = "preferred_username",
                RoleClaimType = ClaimTypes.Role,
            };
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = ctx =>
                {
                    if (ctx.Principal?.Identity is ClaimsIdentity identity)
                        MapRoles(identity, store.Current.Security.Oidc);
                    return Task.CompletedTask;
                },
            };
        }

        private static void MapRoles(ClaimsIdentity identity, OidcConfig oidc)
        {
            var external = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in ReadClaim(identity, oidc.RoleClaimType)) external.Add(v);
            if (oidc.UseScopes)
            {
                foreach (var c in identity.FindAll("scope").Concat(identity.FindAll("scp")))
                foreach (var s in c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    external.Add(s);
            }

            var roles = new HashSet<string>(oidc.DefaultRoles);
            if (oidc.ReadRoles.Any(external.Contains)) roles.Add("read");
            if (oidc.WriteRoles.Any(external.Contains)) roles.Add("write");
            if (oidc.AdminRoles.Any(external.Contains)) roles.Add("admin");

            foreach (var r in roles) identity.AddClaim(new Claim(ClaimTypes.Role, r));
            identity.AddClaim(new Claim(AccessControl.AuthMethodClaim, "oidc"));
        }

        /// <summary>Reads a claim; supports dotted paths into JSON claims (e.g. realm_access.roles).</summary>
        private static IEnumerable<string> ReadClaim(ClaimsIdentity identity, string path)
        {
            var direct = identity.FindAll(path).Select(c => c.Value).ToList();
            if (direct.Count > 0 || !path.Contains('.')) return direct;

            var parts = path.Split('.');
            var result = new List<string>();
            foreach (var claim in identity.FindAll(parts[0]))
            {
                try
                {
                    using var doc = JsonDocument.Parse(claim.Value);
                    var el = doc.RootElement;
                    var ok = true;
                    foreach (var part in parts.Skip(1))
                    {
                        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(part, out el)) { ok = false; break; }
                    }
                    if (!ok) continue;
                    if (el.ValueKind == JsonValueKind.Array)
                        result.AddRange(el.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                    else if (el.ValueKind == JsonValueKind.String)
                        result.Add(el.GetString()!);
                }
                catch (JsonException)
                {
                    // not a JSON claim
                }
            }
            return result;
        }
    }
}
