using System.Collections.Concurrent;
using Npgsql;
using PostGISApi.Configuration;
using PostGISApi.Infrastructure;

namespace PostGISApi.Data;

public sealed record ColumnInfo(string Name, string SqlType, string TypeName)
{
    public bool IsArray => TypeName.StartsWith('_');
    public bool IsSpatial => TypeName is "geometry" or "geography" or "raster" or "_geometry" or "_geography";
}

/// <summary>Database metadata of a layer (columns, SRID, geometry type).</summary>
public sealed record LayerMetadata(
    LayerConfig Layer,
    IReadOnlyDictionary<string, ColumnInfo> Columns,
    IReadOnlyList<string> PropertyColumns,
    int Srid,
    string GeometryType)
{
    public string TableSql => Sql.Table(Layer.Schema, Layer.Table);
    public string GeomSql => "t." + Sql.Ident(Layer.GeometryColumn);
    public ColumnInfo? IdColumn => Layer.IdColumn is { Length: > 0 } id && Columns.TryGetValue(id, out var c) ? c : null;
    public bool IsMulti => GeometryType.StartsWith("MULTI", StringComparison.OrdinalIgnoreCase);

    /// <summary>Columns a client may filter or sort on.</summary>
    public bool IsQueryable(string column) =>
        PropertyColumns.Contains(column, StringComparer.Ordinal) || column == Layer.IdColumn;

    public ColumnInfo RequireQueryableColumn(string column) =>
        IsQueryable(column) && Columns.TryGetValue(column, out var c)
            ? c
            : throw ApiException.BadRequest($"Unknown or not exposed column '{column}'.");
}

/// <summary>Reads and caches layer metadata from the PostgreSQL catalog.</summary>
public sealed class LayerMetadataService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private readonly DataSourceRegistry _db;
    private readonly ConcurrentDictionary<string, (DateTime Loaded, LayerMetadata Meta)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LayerMetadataService(DataSourceRegistry db, ConfigStore store)
    {
        _db = db;
        store.Changed += _ => _cache.Clear();
    }

    public void Invalidate() => _cache.Clear();

    public async Task<LayerMetadata> GetAsync(LayerConfig layer, CancellationToken ct)
    {
        if (_cache.TryGetValue(layer.Name, out var hit) && ReferenceEquals(hit.Meta.Layer, layer) && DateTime.UtcNow - hit.Loaded < Ttl)
            return hit.Meta;
        var meta = await LoadAsync(layer, ct);
        _cache[layer.Name] = (DateTime.UtcNow, meta);
        return meta;
    }

    /// <summary>Loads metadata without caching (used to validate a layer before saving).</summary>
    public async Task<LayerMetadata> LoadAsync(LayerConfig layer, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(layer.Connection, ct);

        var columns = new Dictionary<string, ColumnInfo>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod), t.typname
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_type t ON t.oid = a.atttypid
            WHERE a.attrelid = to_regclass(@rel) AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """, conn))
        {
            cmd.Parameters.AddWithValue("rel", Sql.Table(layer.Schema, layer.Table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                columns[r.GetString(0)] = new ColumnInfo(r.GetString(0), r.GetString(1), r.GetString(2));
        }

        if (columns.Count == 0)
            throw ApiException.BadRequest($"Layer '{layer.Name}': table {layer.Schema}.{layer.Table} not found or no permission.");
        if (!columns.TryGetValue(layer.GeometryColumn, out var geomCol) || geomCol.TypeName != "geometry")
            throw ApiException.BadRequest($"Layer '{layer.Name}': '{layer.GeometryColumn}' is not a geometry column.");
        if (!string.IsNullOrEmpty(layer.IdColumn) && !columns.ContainsKey(layer.IdColumn))
            throw ApiException.BadRequest($"Layer '{layer.Name}': id column '{layer.IdColumn}' does not exist.");

        int srid = layer.Srid;
        string geometryType = "GEOMETRY";
        await using (var cmd = new NpgsqlCommand(
            "SELECT srid, type FROM geometry_columns WHERE f_table_schema = @s AND f_table_name = @t AND f_geometry_column = @g", conn))
        {
            cmd.Parameters.AddWithValue("s", layer.Schema);
            cmd.Parameters.AddWithValue("t", layer.Table);
            cmd.Parameters.AddWithValue("g", layer.GeometryColumn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                if (srid == 0) srid = r.GetInt32(0);
                geometryType = r.GetString(1);
            }
        }

        if (srid == 0)
        {
            // untyped column (e.g. a view): look at the data
            await using var cmd = new NpgsqlCommand(
                $"SELECT ST_SRID({Sql.Ident(layer.GeometryColumn)}) FROM {Sql.Table(layer.Schema, layer.Table)} WHERE {Sql.Ident(layer.GeometryColumn)} IS NOT NULL LIMIT 1", conn);
            if (await cmd.ExecuteScalarAsync(ct) is int s) srid = s;
        }

        List<string> properties;
        if (layer.Properties.Count > 0)
        {
            var missing = layer.Properties.Where(p => !columns.ContainsKey(p)).ToList();
            if (missing.Count > 0)
                throw ApiException.BadRequest($"Layer '{layer.Name}': unknown property columns: {string.Join(", ", missing)}.");
            properties = layer.Properties.Where(p => p != layer.GeometryColumn).Distinct().ToList();
        }
        else
        {
            properties = columns.Values.Where(c => !c.IsSpatial).Select(c => c.Name).ToList();
        }

        return new LayerMetadata(layer, columns, properties, srid, geometryType);
    }
}
