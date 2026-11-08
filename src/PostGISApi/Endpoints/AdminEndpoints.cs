using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using PostGISApi.Auth;
using PostGISApi.Configuration;
using PostGISApi.Data;
using PostGISApi.Infrastructure;

namespace PostGISApi.Endpoints;

/// <summary>Configuration backend: connections, layers, queries, API keys, OIDC and settings.</summary>
public static class AdminEndpoints
{
    public const string PasswordMask = "********";

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin")
            .RequireAuthorization(AccessControl.AdminPolicy)
            .WithGroupName("admin");

        MapConnections(admin.MapGroup("/connections").WithTags("Admin: connections"));
        MapLayers(admin.MapGroup("/layers").WithTags("Admin: layers"));
        MapQueries(admin.MapGroup("/queries").WithTags("Admin: queries"));
        MapApiKeys(admin.MapGroup("/apikeys").WithTags("Admin: API keys"));

        var system = admin.MapGroup("").WithTags("Admin: system");

        system.MapGet("/oidc", (ConfigStore s) => s.Current.Security.Oidc).WithName("AdminGetOidc").WithSummary("Get OIDC settings");
        system.MapPut("/oidc", (OidcConfig body, ConfigStore s, IServiceProvider sp) =>
            {
                var updated = s.Update(c => c.Security.Oidc = body);
                AuthSetup.ResetJwtOptions(sp);
                return updated.Security.Oidc;
            })
            .WithName("AdminPutOidc").WithSummary("Update OIDC settings (applied immediately)");

        system.MapGet("/settings", (ConfigStore s) => s.Current.Settings).WithName("AdminGetSettings").WithSummary("Get general settings");
        system.MapPut("/settings", (GeneralSettings body, ConfigStore s) => s.Update(c => c.Settings = body).Settings)
            .WithName("AdminPutSettings").WithSummary("Update general settings");

        system.MapPost("/reload", (ConfigStore s, LayerMetadataService meta, IServiceProvider sp) =>
            {
                s.Reload();
                meta.Invalidate();
                AuthSetup.ResetJwtOptions(sp);
                var c = s.Current;
                return new ReloadResult(c.Connections.Count, c.Layers.Count, c.Queries.Count);
            })
            .WithName("AdminReload").WithSummary("Reload config files from disk and clear caches");

        system.MapGet("/whoami", (HttpContext ctx) => new
            {
                name = ctx.User.Identity?.Name,
                auth = ctx.User.FindFirst(AccessControl.AuthMethodClaim)?.Value,
                roles = ctx.User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(r => r.Value),
            })
            .WithName("AdminWhoAmI").WithSummary("Current identity");

        return app;
    }

    // ------------------------------------------------------------------ connections

    private static void MapConnections(RouteGroupBuilder g)
    {
        g.MapGet("/", (ConfigStore s) => s.Current.Connections.Select(Mask).ToList())
            .WithName("AdminListConnections").WithSummary("List connections (passwords masked)");

        g.MapGet("/{name}", (string name, ConfigStore s) =>
                Mask(s.Current.FindConnection(name) ?? throw ApiException.NotFound($"Connection '{name}' not found.")))
            .WithName("AdminGetConnection");

        g.MapPut("/{name}", (string name, ConnectionConfig body, ConfigStore s) =>
            {
                body.Name = name;
                s.Update(c =>
                {
                    var existing = c.FindConnection(name);
                    if (body.Password == PasswordMask) body.Password = existing?.Password;
                    if (existing is not null) c.Connections[c.Connections.IndexOf(existing)] = body;
                    else c.Connections.Add(body);
                });
                return Mask(body);
            })
            .WithName("AdminPutConnection").WithSummary("Create or update a connection");

        g.MapDelete("/{name}", (string name, ConfigStore s) =>
            {
                s.Update(c =>
                {
                    var existing = c.FindConnection(name) ?? throw ApiException.NotFound($"Connection '{name}' not found.");
                    var users = c.Layers.Where(l => l.Connection.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(l => "layer " + l.Name)
                        .Concat(c.Queries.Where(q => q.Connection.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(q => "query " + q.Name))
                        .ToList();
                    if (users.Count > 0) throw ApiException.Conflict($"Connection is used by: {string.Join(", ", users)}.");
                    c.Connections.Remove(existing);
                    if (string.Equals(c.Settings.DefaultConnection, name, StringComparison.OrdinalIgnoreCase)) c.Settings.DefaultConnection = null;
                });
                return Results.NoContent();
            })
            .WithName("AdminDeleteConnection");

        g.MapPost("/test", async (ConnectionConfig body, ConfigStore s, CancellationToken ct) =>
            {
                if (body.Password == PasswordMask) body.Password = s.Current.FindConnection(body.Name)?.Password;
                return await TestAsync(body, ct);
            })
            .WithName("AdminTestConnectionConfig").WithSummary("Test connection settings before saving");

        g.MapPost("/{name}/test", async (string name, ConfigStore s, CancellationToken ct) =>
                await TestAsync(s.Current.FindConnection(name) ?? throw ApiException.NotFound($"Connection '{name}' not found."), ct))
            .WithName("AdminTestConnection").WithSummary("Test a saved connection");

        g.MapGet("/{name}/tables", async (string name, DataSourceRegistry db, CancellationToken ct) =>
            {
                await using var conn = await db.OpenAsync(name, ct);
                await using var cmd = new NpgsqlCommand(
                    """
                    SELECT g.f_table_schema::text, g.f_table_name::text, g.f_geometry_column::text, g.srid, g.type,
                           (SELECT a.attname::text FROM pg_index i
                              JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
                             WHERE i.indrelid = c.oid AND i.indisprimary LIMIT 1) AS pk,
                           c.relkind IN ('v', 'm') AS is_view
                    FROM geometry_columns g
                    JOIN pg_namespace n ON n.nspname = g.f_table_schema
                    JOIN pg_class c ON c.relnamespace = n.oid AND c.relname = g.f_table_name
                    ORDER BY 1, 2, 3
                    """, conn);
                var list = new List<GeometryTableInfo>();
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    list.Add(new GeometryTableInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
                        r.IsDBNull(5) ? null : r.GetString(5), r.GetBoolean(6)));
                return list;
            })
            .WithName("AdminDiscoverTables").WithSummary("Discover tables/views with geometry columns");

        g.MapGet("/{name}/tables/{schema}/{table}/columns", async (string name, string schema, string table, DataSourceRegistry db, CancellationToken ct) =>
            {
                await using var conn = await db.OpenAsync(name, ct);
                await using var cmd = new NpgsqlCommand(
                    """
                    SELECT a.attname::text, pg_catalog.format_type(a.atttypid, a.atttypmod),
                           EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = a.attrelid AND i.indisprimary AND a.attnum = ANY(i.indkey))
                    FROM pg_attribute a
                    WHERE a.attrelid = to_regclass(@rel) AND a.attnum > 0 AND NOT a.attisdropped
                    ORDER BY a.attnum
                    """, conn);
                cmd.Parameters.AddWithValue("rel", Sql.Table(schema, table));
                var list = new List<ColumnView>();
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) list.Add(new ColumnView(r.GetString(0), r.GetString(1), r.GetBoolean(2)));
                return list.Count == 0 ? throw ApiException.NotFound($"Table {schema}.{table} not found.") : list;
            })
            .WithName("AdminListColumns").WithSummary("Columns of a table");
    }

    private static ConnectionConfig Mask(ConnectionConfig c) => new()
    {
        Name = c.Name, Host = c.Host, Port = c.Port, Database = c.Database, Username = c.Username,
        Password = string.IsNullOrEmpty(c.Password) ? null : PasswordMask,
        PasswordEnvVar = c.PasswordEnvVar, SslMode = c.SslMode, MaxPoolSize = c.MaxPoolSize,
        CommandTimeoutSeconds = c.CommandTimeoutSeconds,
    };

    private static async Task<ConnectionTestResult> TestAsync(ConnectionConfig config, CancellationToken ct)
    {
        try
        {
            ConfigStore.Validate(new ConfigData { Connections = [config] });
            await using var ds = NpgsqlDataSource.Create(DataSourceRegistry.BuildConnectionString(config));
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT current_setting('server_version'), (SELECT extversion FROM pg_extension WHERE extname = 'postgis')", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var postgis = r.IsDBNull(1) ? null : r.GetString(1);
            return new ConnectionTestResult(true, r.GetString(0), postgis, postgis is null ? "PostGIS extension is not installed in this database." : null);
        }
        catch (Exception ex) when (ex is NpgsqlException or ApiException or ArgumentException or TimeoutException)
        {
            return new ConnectionTestResult(false, null, null, ex.Message);
        }
    }

    // ------------------------------------------------------------------ layers

    private static void MapLayers(RouteGroupBuilder g)
    {
        g.MapGet("/", (ConfigStore s) => s.Current.Layers).WithName("AdminListLayers");

        g.MapGet("/{name}", (string name, ConfigStore s) =>
                s.Current.FindLayer(name) ?? throw ApiException.NotFound($"Layer '{name}' not found."))
            .WithName("AdminGetLayer");

        g.MapPut("/{name}", async (string name, LayerConfig body,
                [FromQuery, Description("Save even if the table cannot be validated against the database.")] bool? force,
                ConfigStore s, LayerMetadataService meta, CancellationToken ct) =>
            {
                body.Name = name;
                ConfigStore.Validate(new ConfigData { Connections = s.Current.Connections, Layers = [body] });
                if (force != true) await meta.LoadAsync(body, ct); // throws 400 with a helpful message
                s.Update(c =>
                {
                    var existing = c.FindLayer(name);
                    if (existing is not null) c.Layers[c.Layers.IndexOf(existing)] = body;
                    else c.Layers.Add(body);
                });
                return body;
            })
            .WithName("AdminPutLayer").WithSummary("Create or update a layer (validated against the database)");

        g.MapDelete("/{name}", (string name, ConfigStore s) =>
            {
                s.Update(c =>
                {
                    var l = c.FindLayer(name) ?? throw ApiException.NotFound($"Layer '{name}' not found.");
                    c.Layers.Remove(l);
                });
                return Results.NoContent();
            })
            .WithName("AdminDeleteLayer");
    }

    // ------------------------------------------------------------------ queries

    private static void MapQueries(RouteGroupBuilder g)
    {
        g.MapGet("/", (ConfigStore s) => s.Current.Queries).WithName("AdminListQueries");

        g.MapGet("/{name}", (string name, ConfigStore s) =>
                s.Current.FindQuery(name) ?? throw ApiException.NotFound($"Query '{name}' not found."))
            .WithName("AdminGetQuery");

        g.MapPut("/{name}", (string name, NamedQueryConfig body, ConfigStore s) =>
            {
                body.Name = name;
                s.Update(c =>
                {
                    var existing = c.FindQuery(name);
                    if (existing is not null) c.Queries[c.Queries.IndexOf(existing)] = body;
                    else c.Queries.Add(body);
                });
                return body;
            })
            .WithName("AdminPutQuery").WithSummary("Create or update a named query");

        g.MapDelete("/{name}", (string name, ConfigStore s) =>
            {
                s.Update(c =>
                {
                    var q = c.FindQuery(name) ?? throw ApiException.NotFound($"Query '{name}' not found.");
                    c.Queries.Remove(q);
                });
                return Results.NoContent();
            })
            .WithName("AdminDeleteQuery");
    }

    // ------------------------------------------------------------------ API keys

    private static void MapApiKeys(RouteGroupBuilder g)
    {
        g.MapGet("/", (ConfigStore s) => s.Current.Security.ApiKeys.Select(ApiKeyView.From).ToList())
            .WithName("AdminListApiKeys").WithSummary("List API keys (without secrets)");

        g.MapPost("/", (ApiKeyCreateRequest body, ConfigStore s) =>
            {
                if (string.IsNullOrWhiteSpace(body.Name)) throw ApiException.BadRequest("name is required.");
                var key = ApiKeys.Generate();
                var entry = new ApiKeyConfig
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Name = body.Name.Trim(),
                    KeyHash = ApiKeys.Hash(key),
                    KeyPrefix = ApiKeys.DisplayPrefix(key),
                    Roles = body.Roles.Distinct().ToList(),
                    AllowedLayers = body.AllowedLayers,
                    AllowedQueries = body.AllowedQueries,
                    ExpiresUtc = body.ExpiresUtc,
                };
                s.Update(c => c.Security.ApiKeys.Add(entry));
                return Results.Created($"/api/admin/apikeys/{entry.Id}", new ApiKeyCreated(key, ApiKeyView.From(entry)));
            })
            .WithName("AdminCreateApiKey").WithSummary("Create an API key. The key is returned only in this response.")
            .Produces<ApiKeyCreated>(201);

        g.MapPut("/{id}", (string id, ApiKeyUpdateRequest body, ConfigStore s, HttpContext ctx) =>
            {
                ApiKeyView? view = null;
                s.Update(c =>
                {
                    var k = c.Security.ApiKeys.FirstOrDefault(x => x.Id == id) ?? throw ApiException.NotFound($"API key '{id}' not found.");
                    if (body.Name is not null) k.Name = body.Name;
                    if (body.Roles is not null) k.Roles = body.Roles.Distinct().ToList();
                    if (body.AllowedLayers is not null) k.AllowedLayers = body.AllowedLayers;
                    if (body.AllowedQueries is not null) k.AllowedQueries = body.AllowedQueries;
                    if (body.Enabled is not null) k.Enabled = body.Enabled.Value;
                    if (body.ExpiresUtc is not null) k.ExpiresUtc = body.ExpiresUtc;
                    if (body.ClearExpiry) k.ExpiresUtc = null;
                    EnsureAnotherAdmin(c, ctx);
                    view = ApiKeyView.From(k);
                });
                return view!;
            })
            .WithName("AdminUpdateApiKey").WithSummary("Update roles, restrictions, enabled state or expiry of an API key");

        g.MapDelete("/{id}", (string id, ConfigStore s, HttpContext ctx) =>
            {
                s.Update(c =>
                {
                    var k = c.Security.ApiKeys.FirstOrDefault(x => x.Id == id) ?? throw ApiException.NotFound($"API key '{id}' not found.");
                    c.Security.ApiKeys.Remove(k);
                    EnsureAnotherAdmin(c, ctx);
                });
                return Results.NoContent();
            })
            .WithName("AdminDeleteApiKey");
    }

    /// <summary>Prevents locking yourself out: an API-key admin must keep at least one enabled admin key.</summary>
    private static void EnsureAnotherAdmin(ConfigData c, HttpContext ctx)
    {
        var viaApiKey = ctx.User.FindFirst(AccessControl.AuthMethodClaim)?.Value == "apikey" && ctx.User.Identity?.Name != "bootstrap-admin";
        if (viaApiKey && !c.Security.ApiKeys.Any(k => k.Enabled && k.Roles.Contains("admin")))
            throw ApiException.Conflict("At least one enabled API key with role 'admin' must remain.");
    }
}
