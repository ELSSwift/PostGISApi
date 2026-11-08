using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PostGISApi.Auth;
using PostGISApi.Configuration;
using PostGISApi.Data;
using PostGISApi.Infrastructure;

namespace PostGISApi.Endpoints;

public static class QueryAndGeometryEndpoints
{
    public static RouteGroupBuilder MapQueryEndpoints(this RouteGroupBuilder api)
    {
        var queries = api.MapGroup("/queries").WithTags("Named queries");

        queries.MapGet("/", (HttpContext ctx, ConfigStore store) =>
                store.Current.Queries
                    .Where(q => AccessControl.CanSeeQuery(ctx.User, q))
                    .Select(q => new QuerySummary(q.Name, q.Title, q.Description, q.Format, q.Public, q.Parameters))
                    .ToList())
            .WithName("ListQueries")
            .WithSummary("List predefined queries and their parameters");

        queries.MapGet("/{name}", async (string name, HttpContext ctx, ConfigStore store, NamedQueryService svc, CancellationToken ct) =>
            {
                var q = AccessControl.RequireQuery(store, ctx.User, name);
                var input = ctx.Request.Query
                    .Where(kv => kv.Key != ApiKeyAuthenticationHandler.QueryName)
                    .ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value.ToString()));
                return Result(q, await svc.ExecuteAsync(q, input, ct));
            })
            .WithName("ExecuteQueryGet")
            .WithSummary("Execute a predefined query, parameters as query string (?name=value)")
            .Produces<GeoJsonFeatureCollection>(200, "application/json", "application/geo+json");

        queries.MapPost("/{name}", async (string name, [FromBody] Dictionary<string, JsonElement>? body, HttpContext ctx,
                ConfigStore store, NamedQueryService svc, CancellationToken ct) =>
            {
                var q = AccessControl.RequireQuery(store, ctx.User, name);
                return Result(q, await svc.ExecuteAsync(q, body ?? [], ct));
            })
            .WithName("ExecuteQueryPost")
            .WithSummary("Execute a predefined query, parameters as JSON object {\"name\": value}")
            .Produces<GeoJsonFeatureCollection>(200, "application/json", "application/geo+json");

        return api;
    }

    public static RouteGroupBuilder MapGeometryEndpoints(this RouteGroupBuilder api)
    {
        var geometry = api.MapGroup("/geometry").WithTags("Geometry operations").RequireAuthorization(AccessControl.ReadPolicy);

        geometry.MapGet("/", () => GeometryService.Operations)
            .WithName("ListGeometryOperations")
            .WithSummary("List available geometry operations");

        geometry.MapPost("/{operation}", async (string operation, GeometryOperationRequest body,
                [FromQuery, Description("Connection to run the operation on. Default: settings.defaultConnection.")] string? connection,
                ConfigStore store, GeometryService svc, CancellationToken ct) =>
            {
                if (connection is not null && store.Current.FindConnection(connection) is null)
                    throw ApiException.NotFound($"Connection '{connection}' not found.");
                return Results.Text(await svc.ExecuteAsync(operation, body, connection, ct), "application/json", Encoding.UTF8);
            })
            .WithName("GeometryOperation")
            .WithSummary("Run a PostGIS function on GeoJSON input")
            .WithDescription("Operations: " + string.Join(", ", GeometryService.Operations) +
                             ". Measurements are in meters when geography=true.")
            .Produces<GeometryOperationResult>();

        return api;
    }

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        var health = app.MapGroup("/health").WithTags("Health").WithGroupName("v1");

        health.MapGet("/", () => new HealthResult("ok", version))
            .WithName("Health").WithSummary("Liveness check");

        health.MapGet("/db", async (ConfigStore store, DataSourceRegistry db, CancellationToken ct) =>
            {
                var result = new Dictionary<string, bool>();
                foreach (var c in store.Current.Connections)
                {
                    try
                    {
                        await using var conn = await db.OpenAsync(c.Name, ct);
                        result[c.Name] = true;
                    }
                    catch (Exception)
                    {
                        result[c.Name] = false;
                    }
                }
                var ok = result.Values.All(v => v);
                return Results.Json(new DbHealthResult(ok ? "ok" : "degraded", result), statusCode: ok ? 200 : 503);
            })
            .RequireAuthorization(AccessControl.ReadPolicy)
            .WithName("DbHealth").WithSummary("Checks all database connections (role read)")
            .Produces<DbHealthResult>().Produces<DbHealthResult>(503);

        return app;
    }

    private static IResult Result(NamedQueryConfig q, string json) =>
        Results.Text(json, q.Format == "geojson" ? "application/geo+json" : "application/json", Encoding.UTF8);
}
