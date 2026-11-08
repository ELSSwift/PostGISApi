using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using PostGISApi.Configuration;

namespace PostGISApi.Endpoints;

// ---- GeoJSON (documentation of the raw JSON responses) ----

public sealed class GeoJsonGeometry
{
    [Description("Point, LineString, Polygon, MultiPoint, MultiLineString, MultiPolygon, GeometryCollection")]
    public string Type { get; set; } = "Point";
    public JsonElement Coordinates { get; set; }
}

public sealed class GeoJsonFeature
{
    public string Type { get; set; } = "Feature";
    public JsonElement? Id { get; set; }
    public GeoJsonGeometry? Geometry { get; set; }
    public Dictionary<string, JsonElement?> Properties { get; set; } = [];
}

public sealed class GeoJsonFeatureCollection
{
    public string Type { get; set; } = "FeatureCollection";
    public int NumberReturned { get; set; }

    [Description("Only present when count=true.")]
    public long? NumberMatched { get; set; }
    public List<GeoJsonFeature> Features { get; set; } = [];
}

// ---- Public API ----

public sealed record LayerSummary(string Name, string? Title, string? Description, bool Public, bool Writable, bool Tiles);

public sealed record PropertyInfo(string Name, string Type);

public sealed record LayerDetails(
    string Name, string? Title, string? Description, bool Public, bool Writable, bool Tiles,
    int Srid, string GeometryType, string? IdColumn, IReadOnlyList<PropertyInfo> Properties,
    int DefaultLimit, int MaxFeatures);

public sealed record CountResult(long Count);

public sealed record ExtentResult(double[]? Bbox, int Srid, long Count);

public sealed record InsertResult(int Inserted, List<JsonNode?> Ids);

public sealed record QuerySummary(string Name, string? Title, string? Description, string Format, bool Public, IReadOnlyList<QueryParameterConfig> Parameters);

public sealed record GeometryOperationResult(string Operation, JsonElement Result, int? Srid);

public sealed record HealthResult(string Status, string Version);

public sealed record DbHealthResult(string Status, Dictionary<string, bool> Connections);

// ---- Admin ----

public sealed class ApiKeyCreateRequest
{
    public string Name { get; set; } = "";

    [Description("read, write, admin")]
    public List<string> Roles { get; set; } = ["read"];
    public List<string> AllowedLayers { get; set; } = [];
    public List<string> AllowedQueries { get; set; } = [];
    public DateTimeOffset? ExpiresUtc { get; set; }
}

public sealed class ApiKeyUpdateRequest
{
    public string? Name { get; set; }
    public List<string>? Roles { get; set; }
    public List<string>? AllowedLayers { get; set; }
    public List<string>? AllowedQueries { get; set; }
    public bool? Enabled { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public bool ClearExpiry { get; set; }
}

public sealed record ApiKeyView(
    string Id, string Name, string KeyPrefix, List<string> Roles, List<string> AllowedLayers, List<string> AllowedQueries,
    bool Enabled, DateTimeOffset CreatedUtc, DateTimeOffset? ExpiresUtc)
{
    public static ApiKeyView From(ApiKeyConfig k) =>
        new(k.Id, k.Name, k.KeyPrefix, k.Roles, k.AllowedLayers, k.AllowedQueries, k.Enabled, k.CreatedUtc, k.ExpiresUtc);
}

public sealed record ApiKeyCreated([property: Description("The API key. It is shown only once!")] string Key, ApiKeyView ApiKey);

public sealed record ConnectionTestResult(bool Ok, string? PostgresVersion, string? PostgisVersion, string? Error);

public sealed record GeometryTableInfo(string Schema, string Table, string GeometryColumn, int Srid, string GeometryType, string? PrimaryKey, bool IsView);

public sealed record ColumnView(string Name, string Type, bool IsPrimaryKey);

public sealed record ReloadResult(int Connections, int Layers, int Queries);
