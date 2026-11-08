using System.Security.Claims;
using PostGISApi.Configuration;
using PostGISApi.Infrastructure;

namespace PostGISApi.Auth;

/// <summary>Role and per-resource access checks.</summary>
public static class AccessControl
{
    public const string LayerClaim = "pgapi:layer";
    public const string QueryClaim = "pgapi:query";
    public const string AuthMethodClaim = "pgapi:auth";

    public const string ReadPolicy = "read";
    public const string WritePolicy = "write";
    public const string AdminPolicy = "admin";

    public static bool CanRead(ClaimsPrincipal u) => u.IsInRole("read") || u.IsInRole("write") || u.IsInRole("admin");
    public static bool CanWrite(ClaimsPrincipal u) => u.IsInRole("write") || u.IsInRole("admin");

    /// <summary>Throws 401/403/404 if the user may not use the layer.</summary>
    public static LayerConfig RequireLayer(ConfigStore store, ClaimsPrincipal user, string name, bool write)
    {
        var layer = store.Current.FindLayer(name) ?? throw ApiException.NotFound($"Layer '{name}' not found.");
        if (!write && layer.Public) return layer;

        RequireAuthenticated(user);
        if (!IsAllowed(user, LayerClaim, layer.Name)) throw ApiException.NotFound($"Layer '{name}' not found.");
        if (write)
        {
            if (!CanWrite(user)) throw ApiException.Forbidden("Role 'write' required.");
            if (layer.ReadOnly) throw ApiException.Forbidden($"Layer '{layer.Name}' is read-only.");
        }
        else if (!CanRead(user))
        {
            throw ApiException.Forbidden("Role 'read' required.");
        }
        return layer;
    }

    public static NamedQueryConfig RequireQuery(ConfigStore store, ClaimsPrincipal user, string name)
    {
        var q = store.Current.FindQuery(name) ?? throw ApiException.NotFound($"Query '{name}' not found.");
        if (!q.Public || q.AllowWrite)
        {
            RequireAuthenticated(user);
            if (!(q.AllowWrite ? CanWrite(user) : CanRead(user)))
                throw ApiException.Forbidden($"Role '{(q.AllowWrite ? "write" : "read")}' required.");
            if (!IsAllowed(user, QueryClaim, q.Name)) throw ApiException.NotFound($"Query '{name}' not found.");
        }
        return q;
    }

    public static bool CanSeeLayer(ClaimsPrincipal user, LayerConfig l) =>
        l.Public || (CanRead(user) && IsAllowed(user, LayerClaim, l.Name));

    public static bool CanSeeQuery(ClaimsPrincipal user, NamedQueryConfig q) =>
        (q.Public && !q.AllowWrite) || ((q.AllowWrite ? CanWrite(user) : CanRead(user)) && IsAllowed(user, QueryClaim, q.Name));

    /// <summary>No restriction claims = access to everything; admins are never restricted.</summary>
    private static bool IsAllowed(ClaimsPrincipal user, string claimType, string name)
    {
        if (user.IsInRole("admin")) return true;
        var allowed = user.FindAll(claimType).Select(c => c.Value).ToList();
        return allowed.Count == 0 || allowed.Any(a => a == "*" || a.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void RequireAuthenticated(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) throw ApiException.Unauthorized();
    }
}
