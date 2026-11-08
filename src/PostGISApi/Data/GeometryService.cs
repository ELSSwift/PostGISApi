using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using PostGISApi.Configuration;
using PostGISApi.Infrastructure;

namespace PostGISApi.Data;

public sealed class GeometryOperationRequest
{
    [Description("Input geometry (GeoJSON geometry or Feature).")]
    public JsonElement Geometry { get; set; }

    [Description("Second geometry for distance, union, intersection, difference, intersects, contains, within.")]
    public JsonElement? Geometry2 { get; set; }

    [Description("SRID of the input geometries. Default 4326.")]
    public int Srid { get; set; } = 4326;

    [Description("SRID of the output geometry (transform: required). Default = srid.")]
    public int? TargetSrid { get; set; }

    [Description("Buffer distance (meters if geography=true).")]
    public double? Distance { get; set; }

    [Description("Tolerance for simplify (in units of srid).")]
    public double? Tolerance { get; set; }

    [Description("Measure on the spheroid in meters (area, length, perimeter, distance, buffer).")]
    public bool Geography { get; set; } = true;

    public int Precision { get; set; } = 6;
}

/// <summary>Stateless PostGIS geometry functions executed on the default connection.</summary>
public sealed class GeometryService(DataSourceRegistry db, ConfigStore store)
{
    private enum Kind { Geometry, Number, Boolean, Json }

    public static readonly string[] Operations =
    [
        "transform", "buffer", "area", "length", "perimeter", "distance", "centroid", "pointonsurface",
        "envelope", "convexhull", "simplify", "makevalid", "isvalid", "union", "intersection", "difference",
        "intersects", "contains", "within", "geometrytype", "npoints",
    ];

    public async Task<string> ExecuteAsync(string operation, GeometryOperationRequest r, string? connection, CancellationToken ct)
    {
        var p = new SqlParams();
        var g = FeatureService.GeoJsonToGeometry(r.Geometry, r.Srid, p);
        string G2()
        {
            if (r.Geometry2 is not { ValueKind: JsonValueKind.Object } g2) throw ApiException.BadRequest($"'{operation}' requires geometry2.");
            return FeatureService.GeoJsonToGeometry(g2, r.Srid, p);
        }
        string Geog(string e) => $"ST_Transform({e}, 4326)::geography";

        (string Expr, Kind Kind) op = operation.ToLowerInvariant() switch
        {
            "transform" => (g, Kind.Geometry),
            "buffer" => r.Distance is not { } d
                ? throw ApiException.BadRequest("buffer requires distance.")
                : (r.Geography ? $"ST_Transform(ST_Buffer({Geog(g)}, {p.Add(d)})::geometry, {r.Srid})" : $"ST_Buffer({g}, {p.Add(d)})", Kind.Geometry),
            "area" => (r.Geography ? $"ST_Area({Geog(g)})" : $"ST_Area({g})", Kind.Number),
            "length" => (r.Geography ? $"ST_Length({Geog(g)})" : $"ST_Length({g})", Kind.Number),
            "perimeter" => (r.Geography ? $"ST_Perimeter({Geog(g)})" : $"ST_Perimeter({g})", Kind.Number),
            "distance" => (r.Geography ? $"ST_Distance({Geog(g)}, {Geog(G2())})" : $"ST_Distance({g}, {G2()})", Kind.Number),
            "centroid" => ($"ST_Centroid({g})", Kind.Geometry),
            "pointonsurface" => ($"ST_PointOnSurface({g})", Kind.Geometry),
            "envelope" => ($"ST_Envelope({g})", Kind.Geometry),
            "convexhull" => ($"ST_ConvexHull({g})", Kind.Geometry),
            "simplify" => r.Tolerance is not { } t
                ? throw ApiException.BadRequest("simplify requires tolerance.")
                : ($"ST_SimplifyPreserveTopology({g}, {p.Add(t)})", Kind.Geometry),
            "makevalid" => ($"ST_MakeValid({g})", Kind.Geometry),
            "isvalid" => ($"json_build_object('valid', ST_IsValid({g}), 'reason', ST_IsValidReason({g}))", Kind.Json),
            "union" => ($"ST_Union({g}, {G2()})", Kind.Geometry),
            "intersection" => ($"ST_Intersection({g}, {G2()})", Kind.Geometry),
            "difference" => ($"ST_Difference({g}, {G2()})", Kind.Geometry),
            "intersects" => ($"ST_Intersects({g}, {G2()})", Kind.Boolean),
            "contains" => ($"ST_Contains({g}, {G2()})", Kind.Boolean),
            "within" => ($"ST_Within({g}, {G2()})", Kind.Boolean),
            "geometrytype" => ($"GeometryType({g})", Kind.Json),
            "npoints" => ($"ST_NPoints({g})", Kind.Number),
            _ => throw ApiException.NotFound($"Unknown operation '{operation}'. Available: {string.Join(", ", Operations)}."),
        };

        if (operation.Equals("transform", StringComparison.OrdinalIgnoreCase) && r.TargetSrid is null)
            throw ApiException.BadRequest("transform requires targetSrid.");

        var outSrid = r.TargetSrid ?? r.Srid;
        string result = op.Kind switch
        {
            Kind.Geometry => $"ST_AsGeoJSON({(outSrid != r.Srid ? $"ST_Transform({op.Expr}, {outSrid.ToString(CultureInfo.InvariantCulture)})" : op.Expr)}, {Math.Clamp(r.Precision, 0, 15)})::json",
            Kind.Json => $"to_json({op.Expr})",
            _ => op.Expr,
        };
        var sql = $"SELECT json_build_object('operation', {Sql.Literal(operation.ToLowerInvariant())}, 'result', {result}" +
                  (op.Kind == Kind.Geometry ? $", 'srid', {outSrid.ToString(CultureInfo.InvariantCulture)}" : "") + ")::text";

        var conn = connection ?? store.Current.Settings.DefaultConnection ?? store.Current.Connections.FirstOrDefault()?.Name
            ?? throw ApiException.BadRequest("No database connection configured.");
        await using var c = await db.OpenAsync(conn, ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        p.ApplyTo(cmd);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
