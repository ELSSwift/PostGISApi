using Microsoft.AspNetCore.Cors.Infrastructure;
using PostGISApi.Configuration;

namespace PostGISApi.Infrastructure;

/// <summary>CORS policy from settings.json, changes apply without restart.</summary>
public sealed class DynamicCorsPolicyProvider(ConfigStore store) : ICorsPolicyProvider
{
    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var origins = store.Current.Settings.CorsOrigins;
        if (origins.Count == 0) return Task.FromResult<CorsPolicy?>(null);

        var b = new CorsPolicyBuilder().AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Type");
        if (origins.Contains("*")) b.AllowAnyOrigin();
        else b.WithOrigins([.. origins]);
        return Task.FromResult<CorsPolicy?>(b.Build());
    }
}
