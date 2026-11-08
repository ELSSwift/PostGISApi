using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PostGISApi.Auth;
using PostGISApi.Configuration;
using PostGISApi.Data;
using PostGISApi.Endpoints;
using PostGISApi.Infrastructure;
using PostGISApi.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// ---- configuration store (JSON files in the config directory) ----
var configDir = builder.Configuration["PostGISApi:ConfigDirectory"] ?? "config";
if (!Path.IsPathRooted(configDir)) configDir = Path.Combine(builder.Environment.ContentRootPath, configDir);
builder.Services.AddSingleton(sp => new ConfigStore(configDir, sp.GetRequiredService<ILogger<ConfigStore>>()));

// ---- data access ----
builder.Services.AddSingleton<DataSourceRegistry>();
builder.Services.AddSingleton<LayerMetadataService>();
builder.Services.AddSingleton<FeatureService>();
builder.Services.AddSingleton<GeometryService>();
builder.Services.AddSingleton<NamedQueryService>();

// ---- web ----
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip;
    o.SerializerOptions.AllowTrailingCommas = true;
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddCors();
builder.Services.Replace(ServiceDescriptor.Singleton<ICorsPolicyProvider, DynamicCorsPolicyProvider>());
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.ResponseCompressionOptions>(o =>
    o.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes
        .Concat(["application/geo+json", "application/vnd.mapbox-vector-tile"]));
builder.Services.AddPostGisAuth(builder.Configuration);
builder.Services.AddPostGisOpenApi();

if (builder.Configuration.GetValue<bool>("PostGISApi:TrustForwardedHeaders"))
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

var app = builder.Build();

var store = app.Services.GetRequiredService<ConfigStore>();
AuthSetup.EnsureAdminAccess(store, app.Configuration, app.Logger);

if (app.Configuration.GetValue<bool>("PostGISApi:TrustForwardedHeaders")) app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseResponseCompression();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// ---- API ----
var api = app.MapGroup("/api/v1").WithGroupName("v1");
api.MapFeatureEndpoints();
api.MapQueryEndpoints();
api.MapGeometryEndpoints();
app.MapHealthEndpoints();
app.MapAdminEndpoints();

// ---- OpenAPI / Swagger UI ----
app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
app.Use(async (ctx, next) =>
{
    // Swagger UI can be switched off in settings.json
    if (ctx.Request.Path.StartsWithSegments("/swagger") && !store.Current.Settings.EnableSwaggerUi)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});
app.UseSwaggerUI(o =>
{
    o.RoutePrefix = "swagger";
    o.SwaggerEndpoint("/openapi/v1.json", "PostGIS API v1");
    o.SwaggerEndpoint("/openapi/admin.json", "Admin API");
    o.DocumentTitle = "PostGIS API";
    o.EnablePersistAuthorization();
});


app.MapGet("/admin", () => Results.Redirect("/admin/index.html")).ExcludeFromDescription();

app.Run();

public partial class Program;
