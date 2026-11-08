using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PostGISApi.Configuration;
using PostGISApi.Infrastructure;

namespace PostGISApi.Data;

/// <summary>Executes admin defined, parameterised SQL queries.</summary>
public sealed class NamedQueryService(DataSourceRegistry db)
{
    public async Task<string> ExecuteAsync(NamedQueryConfig q, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var unknown = input.Keys.Where(k => !q.Parameters.Any(p => p.Name.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0) throw ApiException.BadRequest($"Unknown parameters: {string.Join(", ", unknown)}.");

        var inner = q.Sql.Trim().TrimEnd(';').Trim();
        var sql = q.Format == "geojson" ? WrapGeoJson(q, inner) : $"SELECT COALESCE(json_agg(q), '[]'::json)::text FROM ({inner}) q";

        await using var conn = await db.OpenAsync(q.Connection, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!q.AllowWrite)
        {
            await using var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx);
            await ro.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var def in q.Parameters)
        {
            var match = input.FirstOrDefault(kv => kv.Key.Equals(def.Name, StringComparison.OrdinalIgnoreCase));
            string? raw = match.Key is null ? def.Default : SqlParams.JsonToText(match.Value);
            if (raw is null && def.Required && def.Default is null)
                throw ApiException.BadRequest($"Parameter '{def.Name}' is required.");
            cmd.Parameters.Add(CreateParameter(def, raw));
        }

        var result = (string?)await cmd.ExecuteScalarAsync(ct);
        await tx.CommitAsync(ct);
        return result ?? "[]";
    }

    private static string WrapGeoJson(NamedQueryConfig q, string inner)
    {
        var geom = q.GeometryColumn!;
        var exclude = $" - {Sql.Literal(geom)}";
        var id = "";
        if (!string.IsNullOrEmpty(q.IdColumn))
        {
            id = $"'id', q.{Sql.Ident(q.IdColumn)}, ";
            exclude += $" - {Sql.Literal(q.IdColumn)}";
        }
        return $"""
            SELECT json_build_object('type', 'FeatureCollection', 'numberReturned', count(*), 'features', COALESCE(json_agg(json_build_object(
                'type', 'Feature', {id}'geometry', ST_AsGeoJSON(q.{Sql.Ident(geom)})::json, 'properties', to_jsonb(q){exclude})), '[]'::json))::text
            FROM ({inner}) q
            """;
    }

    private static NpgsqlParameter CreateParameter(QueryParameterConfig def, string? raw)
    {
        object? value;
        NpgsqlDbType type;
        try
        {
            (value, type) = def.Type switch
            {
                "integer" => (raw is null ? null : int.Parse(raw, CultureInfo.InvariantCulture), NpgsqlDbType.Integer),
                "bigint" => (raw is null ? null : long.Parse(raw, CultureInfo.InvariantCulture), NpgsqlDbType.Bigint),
                "number" => (raw is null ? null : double.Parse(raw, CultureInfo.InvariantCulture), NpgsqlDbType.Double),
                "boolean" => (raw is null ? null : bool.Parse(raw), NpgsqlDbType.Boolean),
                "date" => (raw is null ? null : DateOnly.Parse(raw, CultureInfo.InvariantCulture), NpgsqlDbType.Date),
                "timestamp" => (raw is null ? null : DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture).UtcDateTime, NpgsqlDbType.TimestampTz),
                "uuid" => (raw is null ? null : Guid.Parse(raw), NpgsqlDbType.Uuid),
                "json" => (raw, NpgsqlDbType.Jsonb),
                _ => ((object?)raw, NpgsqlDbType.Text), // string, geojson (use ST_GeomFromGeoJSON(@p) in SQL)
            };
        }
        catch (FormatException)
        {
            throw ApiException.BadRequest($"Parameter '{def.Name}' is not a valid {def.Type}.");
        }
        catch (OverflowException)
        {
            throw ApiException.BadRequest($"Parameter '{def.Name}' is out of range for {def.Type}.");
        }
        return new NpgsqlParameter(def.Name, type) { Value = value ?? DBNull.Value };
    }
}
