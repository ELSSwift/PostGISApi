using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using PostGISApi.Infrastructure;

namespace PostGISApi.Data;

/// <summary>Builds and executes the PostGIS statements for layer access.</summary>
public sealed class FeatureService(DataSourceRegistry db)
{
    private const int WebMercator = 3857;
    private const int Wgs84 = 4326;

    private static readonly Dictionary<string, string> Comparisons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eq"] = "=", ["neq"] = "<>", ["lt"] = "<", ["lte"] = "<=", ["gt"] = ">", ["gte"] = ">=",
    };

    private static readonly Dictionary<string, string> Relations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["intersects"] = "ST_Intersects", ["within"] = "ST_Within", ["contains"] = "ST_Contains",
        ["touches"] = "ST_Touches", ["crosses"] = "ST_Crosses", ["overlaps"] = "ST_Overlaps",
        ["disjoint"] = "ST_Disjoint", ["equals"] = "ST_Equals", ["covers"] = "ST_Covers", ["coveredby"] = "ST_CoveredBy",
    };

    // ---------------------------------------------------------------- read

    /// <summary>Returns a GeoJSON FeatureCollection as JSON text.</summary>
    public async Task<string> QueryAsync(LayerMetadata m, FeatureQuery q, CancellationToken ct)
    {
        var p = new SqlParams();
        var where = BuildWhere(m, q, p);
        string? extraProps = null;
        string? order;

        if (q.Nearest is { } n)
        {
            var pt = PointExpr(m, n, p);
            extraProps = $"jsonb_build_object('_distance', ST_Distance(ST_Transform({m.GeomSql}, {Wgs84})::geography, ST_Transform({pt}, {Wgs84})::geography))";
            order = $"{m.GeomSql} <-> {pt}";
        }
        else
        {
            order = OrderExpr(m, q);
        }

        var limit = Math.Clamp(q.Limit ?? m.Layer.DefaultLimit, 1, m.Layer.MaxFeatures);
        var offset = Math.Max(0, q.Offset);
        var feature = FeatureExpr(m, q, p, extraProps);

        var inner = new StringBuilder($"SELECT {feature} AS feature FROM {m.TableSql} t{where}");
        if (order is not null) inner.Append(" ORDER BY ").Append(order);
        inner.Append(CultureInfo.InvariantCulture, $" LIMIT {limit} OFFSET {offset}");

        var matched = q.CountMatched ? $", 'numberMatched', (SELECT count(*) FROM {m.TableSql} t{where})" : "";
        var sql = "SELECT json_build_object('type', 'FeatureCollection', 'numberReturned', count(*)" + matched +
                  ", 'features', COALESCE(json_agg(f.feature), '[]'::json))::text FROM (" + inner + ") f";

        return (string)(await ScalarAsync(m.Layer.Connection, sql, p, ct))!;
    }

    public async Task<string?> GetByIdAsync(LayerMetadata m, string id, FeatureQuery q, CancellationToken ct)
    {
        var idCol = RequireId(m);
        var p = new SqlParams();
        var sql = $"SELECT {FeatureExpr(m, q, p, null)}::text FROM {m.TableSql} t WHERE t.{Sql.Ident(idCol.Name)} = {p.AddTyped(id, idCol)}";
        return await ScalarAsync(m.Layer.Connection, sql, p, ct) as string;
    }

    public async Task<long> CountAsync(LayerMetadata m, FeatureQuery q, CancellationToken ct)
    {
        var p = new SqlParams();
        var sql = $"SELECT count(*) FROM {m.TableSql} t{BuildWhere(m, q, p)}";
        return Convert.ToInt64(await ScalarAsync(m.Layer.Connection, sql, p, ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Returns {"bbox":[minx,miny,maxx,maxy],"srid":n} or bbox null for empty layers.</summary>
    public async Task<string> ExtentAsync(LayerMetadata m, FeatureQuery q, int outSrid, CancellationToken ct)
    {
        var p = new SqlParams();
        var ext = $"ST_SetSRID(ST_Extent({m.GeomSql})::geometry, {m.Srid})";
        if (outSrid != m.Srid) ext = $"ST_Transform({ext}, {outSrid})";
        var sql = $"""
            SELECT json_build_object('bbox', CASE WHEN e IS NULL THEN NULL ELSE json_build_array(ST_XMin(e), ST_YMin(e), ST_XMax(e), ST_YMax(e)) END,
                                     'srid', {outSrid}, 'count', c)::text
            FROM (SELECT {ext} AS e, count(*) AS c FROM {m.TableSql} t{BuildWhere(m, q, p)}) x
            """;
        return (string)(await ScalarAsync(m.Layer.Connection, sql, p, ct))!;
    }

    /// <summary>Mapbox vector tile (EPSG:3857, z/x/y in XYZ scheme).</summary>
    public async Task<byte[]> TileAsync(LayerMetadata m, int z, int x, int y, FeatureQuery q, CancellationToken ct)
    {
        if (z is < 0 or > 24) throw ApiException.BadRequest("z must be between 0 and 24.");
        var max = 1L << z;
        if (x < 0 || y < 0 || x >= max || y >= max) throw ApiException.BadRequest("Tile x/y out of range.");

        var p = new SqlParams();
        var props = ResolveProperties(m, q.Properties);
        var columns = props.Count == 0 ? "" : ", " + string.Join(", ", props.Select(c => "t." + Sql.Ident(c)));
        var env = m.Srid == WebMercator ? "b.env" : $"ST_Transform(b.env, {m.Srid})";
        var geom = m.Srid == WebMercator ? m.GeomSql : $"ST_Transform({m.GeomSql}, {WebMercator})";
        var where = BuildWhere(m, q, p);
        var tileCond = $"{m.GeomSql} && {env} AND ST_Intersects({m.GeomSql}, {env})";
        where = where.Length == 0 ? " WHERE " + tileCond : where + " AND " + tileCond;

        var sql = $"""
            WITH b AS (SELECT ST_TileEnvelope({p.Add(z, NpgsqlDbType.Integer)}, {p.Add(x, NpgsqlDbType.Integer)}, {p.Add(y, NpgsqlDbType.Integer)}) AS env),
            mvt AS (
                SELECT ST_AsMVTGeom({geom}, b.env, 4096, 64, true) AS "__mvt_geom"{columns}
                FROM {m.TableSql} t, b{where}
                LIMIT {m.Layer.MaxTileFeatures}
            )
            SELECT ST_AsMVT(mvt.*, {p.Add(m.Layer.Name, NpgsqlDbType.Text)}, 4096, '__mvt_geom') FROM mvt
            """;
        return await ScalarAsync(m.Layer.Connection, sql, p, ct) as byte[] ?? [];
    }

    // ---------------------------------------------------------------- write

    /// <summary>Inserts a Feature or FeatureCollection, returns the new ids.</summary>
    public async Task<List<JsonNode?>> InsertAsync(LayerMetadata m, JsonElement body, int inSrid, CancellationToken ct)
    {
        var idCol = RequireId(m);
        var features = EnumerateFeatures(body).ToList();
        if (features.Count == 0) throw ApiException.BadRequest("No features in request.");
        if (features.Count > m.Layer.MaxFeatures) throw ApiException.BadRequest($"At most {m.Layer.MaxFeatures} features per request.");

        await using var conn = await db.OpenAsync(m.Layer.Connection, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ids = new List<JsonNode?>();
        foreach (var f in features)
        {
            var p = new SqlParams();
            var cols = new List<string>();
            var vals = new List<string>();

            if (f.TryGetProperty("id", out var idValue) && idValue.ValueKind is not JsonValueKind.Null)
            {
                cols.Add(Sql.Ident(idCol.Name));
                vals.Add(p.AddTyped(idValue, idCol));
            }
            foreach (var (col, value) in WritableProperties(m, f))
            {
                if (col.Name == idCol.Name) continue;
                cols.Add(Sql.Ident(col.Name));
                vals.Add(p.AddTyped(value, col));
            }
            if (f.TryGetProperty("geometry", out var g) && g.ValueKind is not JsonValueKind.Null)
            {
                cols.Add(Sql.Ident(m.Layer.GeometryColumn));
                vals.Add(GeometryForWrite(m, g, inSrid, p));
            }

            var sql = cols.Count == 0
                ? $"INSERT INTO {m.TableSql} AS t DEFAULT VALUES RETURNING to_jsonb(t.{Sql.Ident(idCol.Name)})::text"
                : $"INSERT INTO {m.TableSql} AS t ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)}) RETURNING to_jsonb(t.{Sql.Ident(idCol.Name)})::text";
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            p.ApplyTo(cmd);
            var id = (string?)await cmd.ExecuteScalarAsync(ct);
            ids.Add(id is null ? null : JsonNode.Parse(id));
        }
        await tx.CommitAsync(ct);
        return ids;
    }

    /// <summary>Updates given properties and (if present) the geometry of one feature.</summary>
    public async Task<bool> UpdateAsync(LayerMetadata m, string id, JsonElement feature, int inSrid, CancellationToken ct)
    {
        var idCol = RequireId(m);
        if (feature.ValueKind != JsonValueKind.Object) throw ApiException.BadRequest("Body must be a GeoJSON Feature.");
        var p = new SqlParams();
        var sets = new List<string>();
        foreach (var (col, value) in WritableProperties(m, feature))
        {
            if (col.Name == idCol.Name) continue;
            sets.Add($"{Sql.Ident(col.Name)} = {p.AddTyped(value, col)}");
        }
        if (feature.TryGetProperty("geometry", out var g))
        {
            sets.Add(g.ValueKind == JsonValueKind.Null
                ? $"{Sql.Ident(m.Layer.GeometryColumn)} = NULL"
                : $"{Sql.Ident(m.Layer.GeometryColumn)} = {GeometryForWrite(m, g, inSrid, p)}");
        }
        if (sets.Count == 0) throw ApiException.BadRequest("Nothing to update.");

        var sql = $"UPDATE {m.TableSql} AS t SET {string.Join(", ", sets)} WHERE t.{Sql.Ident(idCol.Name)} = {p.AddTyped(id, idCol)}";
        return await ExecuteAsync(m.Layer.Connection, sql, p, ct) > 0;
    }

    public async Task<bool> DeleteAsync(LayerMetadata m, string id, CancellationToken ct)
    {
        var idCol = RequireId(m);
        var p = new SqlParams();
        var sql = $"DELETE FROM {m.TableSql} AS t WHERE t.{Sql.Ident(idCol.Name)} = {p.AddTyped(id, idCol)}";
        return await ExecuteAsync(m.Layer.Connection, sql, p, ct) > 0;
    }

    // ---------------------------------------------------------------- SQL building

    private static string BuildWhere(LayerMetadata m, FeatureQuery q, SqlParams p)
    {
        var conds = new List<string>();

        if (q.Bbox is { } bbox)
        {
            if (bbox.Length != 4) throw ApiException.BadRequest("bbox must have 4 values: minx,miny,maxx,maxy.");
            var env = $"ST_MakeEnvelope({p.Add(bbox[0])}, {p.Add(bbox[1])}, {p.Add(bbox[2])}, {p.Add(bbox[3])}, {p.Add(q.BboxSrid, NpgsqlDbType.Integer)})";
            if (q.BboxSrid != m.Srid) env = $"ST_Transform({env}, {m.Srid})";
            conds.Add($"ST_Intersects({m.GeomSql}, {env})");
        }

        foreach (var f in q.Filters) conds.Add(FilterExpr(m, f, p));

        if (q.Spatial is { } s)
        {
            var g = GeometryInput(m, s.Geometry, s.Srid, p);
            if (s.Relation.Equals("dwithin", StringComparison.OrdinalIgnoreCase))
            {
                if (s.Distance is not { } d || d < 0) throw ApiException.BadRequest("dwithin requires a non-negative distance.");
                conds.Add(s.Geography
                    ? $"ST_DWithin(ST_Transform({m.GeomSql}, {Wgs84})::geography, ST_Transform({g}, {Wgs84})::geography, {p.Add(d)})"
                    : $"ST_DWithin({m.GeomSql}, {g}, {p.Add(d)})");
            }
            else if (Relations.TryGetValue(s.Relation, out var fn))
            {
                conds.Add($"{fn}({m.GeomSql}, {g})");
            }
            else
            {
                throw ApiException.BadRequest($"Unknown relation '{s.Relation}'. Allowed: {string.Join(", ", Relations.Keys)}, dwithin.");
            }
        }

        if (q.Nearest is { MaxDistanceMeters: { } maxDist } n)
        {
            var pt = PointExpr(m, n, p);
            conds.Add($"ST_DWithin(ST_Transform({m.GeomSql}, {Wgs84})::geography, ST_Transform({pt}, {Wgs84})::geography, {p.Add(maxDist)})");
        }

        return conds.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conds);
    }

    private static string FilterExpr(LayerMetadata m, AttributeFilter f, SqlParams p)
    {
        var col = m.RequireQueryableColumn(f.Column);
        var c = "t." + Sql.Ident(col.Name);
        var op = f.Op.ToLowerInvariant();

        if (op == "isnull") return $"{c} IS NULL";
        if (op == "notnull") return $"{c} IS NOT NULL";
        if (f.Value is null) throw ApiException.BadRequest($"Filter on '{f.Column}' with op '{f.Op}' requires a value.");

        if (Comparisons.TryGetValue(op, out var sqlOp)) return $"{c} {sqlOp} {p.AddTyped(f.Value, col)}";
        return op switch
        {
            "like" => $"{c}::text LIKE {p.Add(f.Value, NpgsqlDbType.Text)}",
            "ilike" => $"{c}::text ILIKE {p.Add(f.Value, NpgsqlDbType.Text)}",
            "in" => $"{c} = ANY(CAST({p.Add(f.Value.Split(','), NpgsqlDbType.Array | NpgsqlDbType.Text)} AS {col.SqlType}[]))",
            _ => throw ApiException.BadRequest($"Unknown filter operator '{f.Op}'. Allowed: eq, neq, lt, lte, gt, gte, like, ilike, in, isnull, notnull."),
        };
    }

    private static string? OrderExpr(LayerMetadata m, FeatureQuery q)
    {
        if (!string.IsNullOrEmpty(q.OrderBy))
        {
            var col = m.RequireQueryableColumn(q.OrderBy);
            return $"t.{Sql.Ident(col.Name)}{(q.Descending ? " DESC" : "")}";
        }
        // stable paging
        return m.IdColumn is { } id ? $"t.{Sql.Ident(id.Name)}" : null;
    }

    private static string FeatureExpr(LayerMetadata m, FeatureQuery q, SqlParams p, string? extraProps)
    {
        var props = Sql.JsonbObject(ResolveProperties(m, q.Properties));
        if (extraProps is not null) props = $"({props}) || {extraProps}";
        var id = m.IdColumn is { } idCol ? $"'id', t.{Sql.Ident(idCol.Name)}, " : "";
        return $"json_build_object('type', 'Feature', {id}'geometry', {GeometryOutput(m, q, p)}, 'properties', {props})";
    }

    private static string GeometryOutput(LayerMetadata m, FeatureQuery q, SqlParams p)
    {
        if (!q.WithGeometry) return "NULL::json";
        var expr = m.GeomSql;
        if (q.Simplify is { } tol and > 0) expr = $"ST_SimplifyPreserveTopology({expr}, {p.Add(tol)})";
        var outSrid = q.OutSrid ?? Wgs84;
        if (outSrid != m.Srid) expr = $"ST_Transform({expr}, {outSrid.ToString(CultureInfo.InvariantCulture)})";
        var precision = Math.Clamp(q.Precision, 0, 15);
        var options = outSrid == Wgs84 ? 0 : 2; // 2 = short CRS name for non-WGS84 output
        return $"ST_AsGeoJSON({expr}, {precision}, {options})::json";
    }

    private static List<string> ResolveProperties(LayerMetadata m, List<string>? requested)
    {
        if (requested is null || requested.Count == 0) return [.. m.PropertyColumns];
        var unknown = requested.Where(r => !m.PropertyColumns.Contains(r)).ToList();
        if (unknown.Count > 0) throw ApiException.BadRequest($"Unknown properties: {string.Join(", ", unknown)}.");
        return requested.Distinct().ToList();
    }

    private static string PointExpr(LayerMetadata m, NearestPoint n, SqlParams p)
    {
        var pt = $"ST_SetSRID(ST_MakePoint({p.Add(n.X)}, {p.Add(n.Y)}), {p.Add(n.Srid, NpgsqlDbType.Integer)})";
        return n.Srid == m.Srid ? pt : $"ST_Transform({pt}, {m.Srid})";
    }

    /// <summary>GeoJSON input geometry, transformed to the layer SRID.</summary>
    public static string GeometryInput(LayerMetadata m, JsonElement geojson, int inSrid, SqlParams p)
    {
        var expr = GeoJsonToGeometry(geojson, inSrid, p);
        return inSrid == m.Srid ? expr : $"ST_Transform({expr}, {m.Srid})";
    }

    public static string GeoJsonToGeometry(JsonElement geojson, int srid, SqlParams p) =>
        $"ST_SetSRID(ST_GeomFromGeoJSON({p.Add(ExtractGeometry(geojson).GetRawText(), NpgsqlDbType.Text)}), {p.Add(srid, NpgsqlDbType.Integer)})";

    private static string GeometryForWrite(LayerMetadata m, JsonElement g, int inSrid, SqlParams p)
    {
        var expr = GeometryInput(m, g, inSrid, p);
        return m.IsMulti ? $"ST_Multi({expr})" : expr;
    }

    /// <summary>Accepts a GeoJSON geometry or a Feature and returns the geometry object.</summary>
    public static JsonElement ExtractGeometry(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw ApiException.BadRequest("A GeoJSON geometry object is required.");
        if (type.GetString() == "Feature")
        {
            if (!e.TryGetProperty("geometry", out var g) || g.ValueKind != JsonValueKind.Object)
                throw ApiException.BadRequest("Feature has no geometry.");
            return g;
        }
        return e;
    }

    private static IEnumerable<JsonElement> EnumerateFeatures(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("type", out var type))
            throw ApiException.BadRequest("Body must be a GeoJSON Feature or FeatureCollection.");
        switch (type.GetString())
        {
            case "Feature":
                yield return body;
                break;
            case "FeatureCollection" when body.TryGetProperty("features", out var arr) && arr.ValueKind == JsonValueKind.Array:
                foreach (var f in arr.EnumerateArray()) yield return f;
                break;
            default:
                throw ApiException.BadRequest("Body must be a GeoJSON Feature or FeatureCollection.");
        }
    }

    private static IEnumerable<(ColumnInfo Column, JsonElement Value)> WritableProperties(LayerMetadata m, JsonElement feature)
    {
        if (!feature.TryGetProperty("properties", out var props) || props.ValueKind == JsonValueKind.Null) yield break;
        if (props.ValueKind != JsonValueKind.Object) throw ApiException.BadRequest("properties must be an object.");
        foreach (var prop in props.EnumerateObject())
        {
            if (!m.PropertyColumns.Contains(prop.Name) || !m.Columns.TryGetValue(prop.Name, out var col))
                throw ApiException.BadRequest($"Unknown or not writable property '{prop.Name}'.");
            yield return (col, prop.Value);
        }
    }

    private static ColumnInfo RequireId(LayerMetadata m) =>
        m.IdColumn ?? throw ApiException.BadRequest($"Layer '{m.Layer.Name}' has no idColumn configured.");

    private async Task<object?> ScalarAsync(string connection, string sql, SqlParams p, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(connection, ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        p.ApplyTo(cmd);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull ? null : result;
    }

    private async Task<int> ExecuteAsync(string connection, string sql, SqlParams p, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(connection, ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        p.ApplyTo(cmd);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
