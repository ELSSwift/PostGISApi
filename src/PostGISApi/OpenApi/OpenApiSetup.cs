using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using PostGISApi.Auth;

namespace PostGISApi.OpenApi;

public static class OpenApiSetup
{
    public static IServiceCollection AddPostGisOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", o =>
        {
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info = new OpenApiInfo
                {
                    Title = "PostGIS API",
                    Version = "v1",
                    Description =
                        "Proxy API for PostGIS databases. Layers, named queries and geometry operations are configured in the admin backend (/admin).\n\n" +
                        "Authentication: API key in header `X-API-Key` (or `Authorization: ApiKey <key>`) or an OIDC access token " +
                        "(`Authorization: Bearer <token>`). Layers and queries flagged as public can be read anonymously.\n\n" +
                        "Geometries are GeoJSON in EPSG:4326 unless `srid` is given.",
                    License = new OpenApiLicense { Name = "See LICENSE" },
                };
                return Task.CompletedTask;
            });
            o.AddDocumentTransformer<SecurityTransformer>();
        });

        services.AddOpenApi("admin", o =>
        {
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info = new OpenApiInfo
                {
                    Title = "PostGIS API – Admin",
                    Version = "v1",
                    Description = "Configuration backend. Requires role 'admin'. All changes are stored in the JSON files of the config directory.",
                };
                return Task.CompletedTask;
            });
            o.AddDocumentTransformer<SecurityTransformer>();
        });

        return services;
    }

    private sealed class SecurityTransformer : IOpenApiDocumentTransformer
    {
        public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
            {
                ["ApiKey"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = ApiKeyAuthenticationHandler.HeaderName,
                    Description = "API key created in the admin backend.",
                },
                ["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "OAuth 2.0 / OpenID Connect access token (if OIDC is enabled).",
                },
            };
            document.Security =
            [
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", document)] = [] },
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] },
            ];
            return Task.CompletedTask;
        }
    }
}
