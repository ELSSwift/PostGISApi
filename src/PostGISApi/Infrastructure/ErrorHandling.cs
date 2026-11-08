using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using PostGISApi.Configuration;

namespace PostGISApi.Infrastructure;

/// <summary>Maps exceptions to RFC 7807 problem details.</summary>
public sealed class ApiExceptionHandler(ConfigStore store, ILogger<ApiExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var detailed = store.Current.Settings.DetailedErrors;
        (int status, string title, string? detail) = ex switch
        {
            ApiException a => (a.StatusCode, Title(a.StatusCode), a.Message),
            PostgresException pg when IsClientError(pg) => (400, "Invalid request", pg.MessageText),
            PostgresException pg => (500, "Database error", detailed ? $"{pg.SqlState}: {pg.MessageText}" : null),
            NpgsqlException n => (503, "Database unavailable", detailed ? n.Message : null),
            BadHttpRequestException b => (b.StatusCode, "Bad request", b.Message),
            System.Text.Json.JsonException j => (400, "Invalid JSON", j.Message),
            OperationCanceledException => (499, "Request cancelled", null),
            _ => (500, "Internal server error", detailed ? ex.Message : null),
        };

        if (status >= 500) logger.LogError(ex, "Request {Method} {Path} failed", ctx.Request.Method, ctx.Request.Path);
        else logger.LogDebug("Request {Method} {Path} -> {Status}: {Message}", ctx.Request.Method, ctx.Request.Path, status, ex.Message);

        if (ctx.Response.HasStarted) return true;
        ctx.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = ctx,
            Exception = ex,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail },
        });
    }

    /// <summary>Errors caused by bad input values (invalid GeoJSON, casts, constraints, unknown SRID...).</summary>
    private static bool IsClientError(PostgresException pg) =>
        pg.SqlState.StartsWith("22", StringComparison.Ordinal)      // data exception (invalid input syntax, ...)
        || pg.SqlState.StartsWith("23", StringComparison.Ordinal)   // integrity constraint violation
        || pg.SqlState == "25006"                                   // read only transaction
        || pg.SqlState == "XX000";                                  // PostGIS input errors (unknown GeoJSON type, SRID ...)

    private static string Title(int status) => status switch
    {
        400 => "Bad request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not found",
        409 => "Conflict",
        _ => "Error",
    };
}
