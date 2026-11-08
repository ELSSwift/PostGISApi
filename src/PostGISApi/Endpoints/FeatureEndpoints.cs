using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PostGISApi.Auth;
using PostGISApi.Configuration;
using PostGISApi.Data;
using PostGISApi.Infrastructure;

namespace PostGISApi.Endpoints;

/// <summary>Query string parameters shared by the feature endpoints.</summary>
public sealed class FeatureQueryParameters
{
    [FromQuery(Name = "bbox"), Description("Bounding box filter: minx,miny,maxx,maxy")]
    public string? Bbox { get; set; }

    [FromQuery(Name = "bboxSrid"), Description("SRID of bbox. Default 4326.")]
    public int? BboxSrid { get; set; }

    [FromQuery(Name = "filter"), Description("Attribute filter column:op:value (repeatable, AND). Operators: eq, neq, lt, lte, gt, gte, like, ilike, in (comma separated values), isnull, notnull. Example: population:gt:10000")]
    public string[]? Filter { get; set; }

    [FromQuery(Name = "properties"), Description("Comma separated list of properties to return. Default: all exposed properties.")]
    public string? Properties { get; set; }

    [FromQuery(Name = "orderBy"), Description("Sort column.")]
    public string? OrderBy { get; set; }

    [FromQuery(Name = "desc"), Description("Sort descending.")]
    public bool? Desc { get; set; }

    [FromQuery(Name = "limit"), Description("Max. number of features (capped by the layer's maxFeatures).")]
    public int? Limit { get; set; }

    [FromQuery(Name = "offset"), Description("Number of features to skip (paging).")]
    public int? Offset { get; set; }

    [FromQuery(Name = "srid"), Description("SRID of the returned geometries. Default 4326.")]
    public int? Srid { get; set; }

    [FromQuery(Name = "precision"), Description("Number of decimal places of the coordinates. Default 6.")]
    public int? Precision { get; set; }

    [FromQuery(Name = "simplify"), Description("Simplify geometries with this tolerance (layer units).")]
    public double? Simplify { get; set; }

    [FromQuery(Name = "geometry"), Description("Include geometries. Default true.")]
    public bool? Geometry { get; set; }

    [FromQuery(Name = "count"), Description("Include numberMatched (total count). Default false.")]
    public bool? Count { get; set; }

    public FeatureQuery ToQuery()
    {
        var q = new FeatureQuery
        {
            Bbox = ParseBbox(Bbox),
            BboxSrid = BboxSrid ?? 4326,
            Filters = (Filter ?? []).Select(ParseFilter).ToList(),
            Properties = SplitList(Properties),
            OrderBy = OrderBy,
            Descending = Desc ?? false,
            Limit = Limit,
            Offset = Offset ?? 0,
            OutSrid = Srid,
            Precision = Precision ?? 6,
            Simplify = Simplify,
            WithGeometry = Geometry ?? true,
            CountMatched = Count ?? false,
        };
        return q;
    }

    public static double[]? ParseBbox(string? bbox)
    {
        if (string.IsNullOrWhiteSpace(bbox)) return null;
        var parts = bbox.Split(',');
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                throw ApiException.BadRequest("bbox must contain numbers: minx,miny,maxx,maxy.");
        return values;
    }

    public static AttributeFilter ParseFilter(string f)
    {
        var parts = f.Split(':', 3);
        if (parts.Length < 2) throw ApiException.BadRequest($"Invalid filter '{f}'. Expected column:op:value.");
        return new AttributeFilter(parts[0], parts[1], parts.Length == 3 ? parts[2] : null);
    }

    public static List<string>? SplitList(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public static class FeatureEndpoints
{
    private const string GeoJson = "application/geo+json";
    private const string Mvt = "application/vnd.mapbox-vector-tile";

    public static RouteGroupBuilder MapFeatureEndpoints(this RouteGroupBuilder api)
    {
        var layers = api.MapGroup("/layers").WithTags("Layers");

        layers.MapGet("/", (HttpContext ctx, ConfigStore store) =>
                store.Current.Layers
                    .Where(l => AccessControl.CanSeeLayer(ctx.User, l))
                    .Select(l => new LayerSummary(l.Name, l.Title, l.Description, l.Public, !l.ReadOnly, l.EnableTiles))
                    .ToList())
            .WithName("ListLayers")
            .WithSummary("List all layers visible to the caller");

        layers.MapGet("/{layer}", async (string layer, HttpContext ctx, ConfigStore store, LayerMetadataService meta, CancellationToken ct) =>
            {
                var l = AccessControl.RequireLayer(store, ctx.User, layer, write: false);
                var m = await meta.GetAsync(l, ct);
                return new LayerDetails(l.Name, l.Title, l.Description, l.Public, !l.ReadOnly, l.EnableTiles, m.Srid, m.GeometryType,
                    m.IdColumn?.Name, m.PropertyColumns.Select(p => new PropertyInfo(p, m.Columns[p].SqlType)).ToList(),
                    l.DefaultLimit, l.MaxFeatures);
            })
            .WithName("GetLayer")
            .WithSummary("Layer metadata (SRID, geometry type, properties)");

        layers.MapGet("/{layer}/features", async (string layer, [AsParameters] FeatureQueryParameters query, HttpContext ctx,
                ConfigStore store, LayerMetadataService meta, FeatureService features, CancellationToken ct) =>
            {
                var m = await meta.GetAsync(AccessControl.RequireLayer(store, ctx.User, layer, write: false), ct);
                return GeoJsonResult(await features.QueryAsync(m, query.ToQuery(), ct));
            })
            .WithName("GetFeatures")
            .WithSummary("Query features as GeoJSON FeatureCollection")
            .WithDescription("Supports bbox, attribute filters, paging, sorting, property selection, re-projection and simplification.")
            .Produces<GeoJsonFeatureCollection>(200, GeoJson);

        layers.MapGet("/{layer}/features/{id}", async (string layer, string id,
                [FromQuery, Description("SRID of the returned geometry. Default 4326.")] int? srid,
                [FromQuery, Description("Comma separated list of properties.")] string? properties,
                [FromQuery] int? precision,
                HttpContext ctx, ConfigStore store, LayerMetadataService meta, FeatureService features, CancellationToken ct) =>
            {
                var m = await meta.GetAsync(AccessControl.RequireLayer(store, ctx.User, layer, write: false), ct);
                var q = new FeatureQuery { OutSrid = srid, Properties = FeatureQueryParameters.SplitList(properties), Precision = precision ?? 6 };
                var json = await features.GetByIdAsync(m, id, q, ct) ?? throw ApiException.NotFound($"Feature '{id}' not found.");
                return GeoJsonResult(json);
            })
            .WithName("GetFeature")
            .WithSummary("Get a single feature by id")
            .Produces<GeoJsonFeature>(200, GeoJson)
            .ProducesProblem(404);

        layers.MapPost("/{layer}/query", async (string layer, FeatureQueryRequest body, HttpContext ctx,
                ConfigStore store, LayerMetadataService meta, FeatureService features, CancellationToken ct) =>
            {
                var m = await meta.GetAsync(AccessControl.RequireLayer(store, ctx.User, layer, write: false), ct);
                var q = new FeatureQuery
                {
                    Spatial = body.Spatial, Bbox = body.Bbox, BboxSrid = body.BboxSrid, Filters = body.Filters ?? [],
                    Properties = body.Properties, OrderBy = body.OrderBy, Descending = body.Descending, Limit = body.Limit,
                    Offset = body.Offset, OutSrid = body.Srid, Precision = body.Precision, Simplify = body.Simplify,
                    WithGeometry = body.WithGeometry, CountMatched = body.Count,
                };
                return GeoJsonResult(await features.QueryAsync(m, q, ct));
            })
            .WithName("QueryFeatures")
            .WithSummary("Spatial query with an input geometry (intersects, within, contains, dwithin, ...)")
            .Produces<GeoJsonFeatureCollection>(200, GeoJson);

        layers.MapGet("/{layer}/count", async (string layer, [AsParameters] FeatureQueryParameters query, HttpContext ctx,
                ConfigStore store, LayerMetadataService meta, FeatureService features, CancellationToken ct) =>
            {
                var m = await meta.GetAsync(AccessControl.RequireLayer(store, ctx.User, layer, write: false), ct);
                return new CountResult(await features.CountAsync(m, query.ToQuery(), ct));
            })
            .WithName("CountFeatures")
            .WithSummary("Count features (bbox and filter parameters apply)");

        layers.MapGet("/{layer}/extent", async (string layer, [AsParameters] FeatureQueryParameters query, HttpContext ctx,
                ConfigStore store, LayerMetadataService meta, FeatureService features, CancellationToken ct) =>
            {
                var m = await meta.GetAsync(AccessControl.RequireLayer(store, ctx.User, layer, write: false), ct);
                return Results.Text(await features.ExtentAsync(m, query.ToQuery(), query.Srid ?? 4326, ct), "application/json", Encoding.UTF8);
            })
            .WithName("GetExtent")
            .WithSummary("Bounding box of the (filtered) features")
            .Produces<ExtentResult>();

        layers.MapGet("/{layer}/nearest", async (string layer,
                [FromQuery, Description("X / longitude of the search point.")] double x,
                [FromQuery, Description("Y / latitude of the search point.")] double y,
                [FromQuery, Description("SRID of x/y. Default 4326.")] int? pointSrid,
                [FromQuery, Description("Number of features. Default 10.")] int? k,
                [FromQuery, Description("Max. distance in meters (optional).")] double? maxDistance,
                [AsParameters] FeatureQueryParameters query, HttpContext ctx,
                ConfigStore store, LayerMetadataService meta, FeatureService features, CancellationToken ct) =>
            {
                var m = await meta.GetAsync(AccessControl.RequireLayer(store, ctx.User, layer, write: false), ct);
                var q = query.ToQuery();
                q.Nearest = new NearestPoint(x, y, pointSrid ?? 4326, maxDistance);
                q.Limit = k ?? query.Limit ?? 10;
                return GeoJsonResult(await features.QueryAsync(m, q, ct));
            })
            .WithName("NearestFeatures")
            .WithSummary("k nearest features to a point (property _distance in meters)")
            .Produces<GeoJsonFeatureCollection>(200, GeoJson);

        layers.MapGet("/{layer}/tiles/{z:int}/{x:int}/{y:int}.mvt", async (string layer, int z, int x, int y,
                [FromQuery, Description("Comma separated list of properties.")] string? properties,
                [FromQuery, Description("Attribute filter column:op:value (repeatable).")] string[]? filter,
                HttpContext ctx, ConfigStore store, LayerMetadataService meta, FeatureService features, CancellationToken ct) =>
            {
                var l = AccessControl.RequireLayer(store, ctx.User, layer, write: false);
                if (!l.EnableTiles) throw ApiException.NotFound($"Tiles are disabled for layer '{layer}'.");
                var m = await meta.GetAsync(l, ct);
                var q = new FeatureQuery
                {
                    Properties = FeatureQueryParameters.SplitList(properties),
                    Filters = (filter ?? []).Select(FeatureQueryParameters.ParseFilter).ToList(),
                };
                var tile = await features.TileAsync(m, z, x, y, q, ct);
                ctx.Response.Headers.CacheControl = $"public, max-age={store.Current.Settings.TileCacheSeconds}";
                return tile.Length == 0 ? Results.NoContent() : Results.Bytes(tile, Mvt);
            })
            .WithName("GetTile")
            .WithSummary("Mapbox Vector Tile (EPSG:3857, XYZ scheme)")
            .Produces(200, contentType: Mvt)
            .Produces(204);

        // ---- write ----

        layers.MapPost("/{layer}/features", async (string layer, [FromBody] JsonElement body,
                [FromQuery, Description("SRID of the input geometries. Default 4326.")] int? srid,
                HttpContext ctx, ConfigStore store, LayerMetadataService meta, FeatureService features, CancellationToken ct) =>
            {
                var m = await meta.GetAsync(AccessControl.RequireLayer(store, ctx.User, layer, write: true), ct);
                var ids = await features.InsertAsync(m, body, srid ?? 4326, ct);
                return Results.Created($"{ctx.Request.Path}/{(ids.Count == 1 ? ids[0]?.ToString() : "")}", new InsertResult(ids.Count, ids));
            })
            .WithName("CreateFeatures")
            .WithSummary("Insert a GeoJSON Feature or FeatureCollection (role write)")
            .Accepts<GeoJsonFeatureCollection>("application/json", GeoJson)
            .Produces<InsertResult>(201);

        layers.MapPut("/{layer}/features/{id}", async (string layer, string id, [FromBody] JsonElement body,
                [FromQuery, Description("SRID of the input geometry. Default 4326.")] int? srid,
                HttpContext ctx, ConfigStore store, LayerMetadataService meta, FeatureService features, CancellationToken ct) =>
            {
                var m = await meta.GetAsync(AccessControl.RequireLayer(store, ctx.User, layer, write: true), ct);
                return await features.UpdateAsync(m, id, body, srid ?? 4326, ct)
                    ? Results.NoContent()
                    : throw ApiException.NotFound($"Feature '{id}' not found.");
            })
            .WithName("UpdateFeature")
            .WithSummary("Update a feature (role write). Only supplied properties are changed; geometry if present.")
            .Accepts<GeoJsonFeature>("application/json", GeoJson)
            .Produces(204)
            .ProducesProblem(404);

        layers.MapDelete("/{layer}/features/{id}", async (string layer, string id, HttpContext ctx,
                ConfigStore store, LayerMetadataService meta, FeatureService features, CancellationToken ct) =>
            {
                var m = await meta.GetAsync(AccessControl.RequireLayer(store, ctx.User, layer, write: true), ct);
                return await features.DeleteAsync(m, id, ct)
                    ? Results.NoContent()
                    : throw ApiException.NotFound($"Feature '{id}' not found.");
            })
            .WithName("DeleteFeature")
            .WithSummary("Delete a feature (role write)")
            .Produces(204)
            .ProducesProblem(404);

        return api;
    }

    private static IResult GeoJsonResult(string json) => Results.Text(json, GeoJson, Encoding.UTF8);
}
