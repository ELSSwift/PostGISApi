namespace PostGISApi.Infrastructure;

/// <summary>An error that is returned to the client as RFC 7807 problem details.</summary>
public sealed class ApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public static ApiException BadRequest(string message) => new(StatusCodes.Status400BadRequest, message);
    public static ApiException NotFound(string message) => new(StatusCodes.Status404NotFound, message);
    public static ApiException Conflict(string message) => new(StatusCodes.Status409Conflict, message);
    public static ApiException Unauthorized() => new(StatusCodes.Status401Unauthorized, "Authentication required.");
    public static ApiException Forbidden(string message = "Access denied.") => new(StatusCodes.Status403Forbidden, message);
}
