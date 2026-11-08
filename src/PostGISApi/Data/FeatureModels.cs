using System.ComponentModel;
using System.Text.Json;

namespace PostGISApi.Data;

/// <summary>Attribute filter: column, operator, value.</summary>
public sealed record AttributeFilter(string Column, string Op, string? Value);

/// <summary>Spatial predicate against an input geometry.</summary>
public sealed class SpatialFilter
{
    [Description("Input geometry as GeoJSON geometry (or Feature).")]
    public JsonElement Geometry { get; set; }

    [Description("SRID of the input geometry. Default 4326.")]
    public int Srid { get; set; } = 4326;

    [Description("intersects, within, contains, touches, crosses, overlaps, disjoint, equals, dwithin")]
    public string Relation { get; set; } = "intersects";

    [Description("Distance for dwithin (meters if geography=true, otherwise layer units).")]
    public double? Distance { get; set; }

    [Description("Compute dwithin on the spheroid in meters.")]
    public bool Geography { get; set; } = true;
}

public sealed record NearestPoint(double X, double Y, int Srid, double? MaxDistanceMeters);

/// <summary>Everything that determines a feature selection.</summary>
public sealed class FeatureQuery
{
    public double[]? Bbox { get; set; }
    public int BboxSrid { get; set; } = 4326;
    public List<AttributeFilter> Filters { get; set; } = [];
    public SpatialFilter? Spatial { get; set; }
    public NearestPoint? Nearest { get; set; }
    public List<string>? Properties { get; set; }
    public string? OrderBy { get; set; }
    public bool Descending { get; set; }
    public int? Limit { get; set; }
    public int Offset { get; set; }
    public int? OutSrid { get; set; }
    public int Precision { get; set; } = 6;
    public double? Simplify { get; set; }
    public bool WithGeometry { get; set; } = true;
    public bool CountMatched { get; set; }
}

/// <summary>Body of POST /layers/{layer}/query.</summary>
public sealed class FeatureQueryRequest
{
    [Description("Spatial predicate (optional).")]
    public SpatialFilter? Spatial { get; set; }

    [Description("minx,miny,maxx,maxy (optional).")]
    public double[]? Bbox { get; set; }
    public int BboxSrid { get; set; } = 4326;

    [Description("Attribute filters, e.g. {\"column\":\"population\",\"op\":\"gt\",\"value\":\"10000\"}.")]
    public List<AttributeFilter>? Filters { get; set; }

    [Description("Subset of properties to return.")]
    public List<string>? Properties { get; set; }
    public string? OrderBy { get; set; }
    public bool Descending { get; set; }
    public int? Limit { get; set; }
    public int Offset { get; set; }

    [Description("Output SRID. Default 4326.")]
    public int? Srid { get; set; }
    public int Precision { get; set; } = 6;
    public double? Simplify { get; set; }
    public bool WithGeometry { get; set; } = true;
    public bool Count { get; set; }
}
