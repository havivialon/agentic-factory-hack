using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LeakOps.Contracts;
using LeakOps.Waterstoring;

// Fake Waterstoring.nl: a public page where residents look up disruptions, plus an API-key protected ingest API
// that the Communications and Scheduling agents call. This is a demo, not the real waterstoring.nl.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IncidentStore>();

var app = builder.Build();

// The key must be configured outside Development; Development falls back to a well-known demo key.
var apiKey = app.Configuration["WATERSTORING_API_KEY"] ?? (app.Environment.IsDevelopment() ? "dev-waterstoring-key" : null);
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("WATERSTORING_API_KEY is required outside Development.");

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "waterstoring" }));
app.MapGet("/evides", (IWebHostEnvironment env) => Results.File(Path.Combine(env.WebRootPath, "index.html"), "text/html"));

// ---- Public, read-only ------------------------------------------------------------------------------------------

app.MapGet("/api/incidents", (IncidentStore store, bool? active) => Results.Ok(store.List(active ?? false)));

app.MapGet("/api/incidents/{id}", (string id, IncidentStore store) =>
    store.Get(id) is { } incident ? Results.Ok(incident) : Results.NotFound());

app.MapGet("/api/search", (IncidentStore store, string? q) =>
{
    var incidents = store.Search(q);
    return Results.Ok(new { query = q, hasIncident = incidents.Count > 0, incidents });
});

// ---- Protected: header "X-Api-Key" ------------------------------------------------------------------------------

app.MapPost("/api/incidents", (WaterstoringIncidentRequest request, IncidentStore store, ILogger<Program> logger) =>
{
    var errors = Validate(request);
    if (errors.Count > 0) return Results.ValidationProblem(errors);

    var (incident, created) = store.Upsert(request);
    logger.LogInformation("Incident {IncidentId} {Action} with status {Status}", incident.IncidentId, created ? "created" : "replaced", incident.Status);
    return created ? Results.Created($"/api/incidents/{incident.IncidentId}", incident) : Results.Ok(incident);
}).AddEndpointFilter(RequireApiKey);

app.MapPatch("/api/incidents/{id}", (string id, WaterstoringIncidentUpdate update, IncidentStore store, ILogger<Program> logger) =>
{
    if (update.Status is not null && !WaterstoringStatus.IsValid(update.Status))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [$"Must be one of: {string.Join(", ", WaterstoringStatus.All)}"] });

    var incident = store.Update(id, update);
    if (incident is null) return Results.NotFound();

    logger.LogInformation("Incident {IncidentId} updated: status {Status}, expected resolution {Eta}", id, incident.Status, incident.ExpectedResolutionUtc);
    return Results.Ok(incident);
}).AddEndpointFilter(RequireApiKey);

app.MapPost("/api/admin/reset", (IncidentStore store) =>
{
    store.Clear();
    return Results.NoContent();
}).AddEndpointFilter(RequireApiKey);

app.Run();

// C# note: local functions can be declared after the statements that use them.
async ValueTask<object?> RequireApiKey(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    var supplied = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
    // FixedTimeEquals compares without leaking, through timing, how many characters matched.
    var valid = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(apiKey!));
    return valid ? await next(context) : Results.Json(new { error = "Missing or invalid X-Api-Key header." }, statusCode: StatusCodes.Status401Unauthorized);
}

static Dictionary<string, string[]> Validate(WaterstoringIncidentRequest r)
{
    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(r.IncidentId) || !Regex.IsMatch(r.IncidentId, "^[A-Za-z0-9._-]{1,64}$"))
        errors["incidentId"] = ["Required: 1-64 characters from A-Z a-z 0-9 . _ -"];
    if (string.IsNullOrWhiteSpace(r.Title)) errors["title"] = ["Required"];
    if (string.IsNullOrWhiteSpace(r.Message)) errors["message"] = ["Required"];
    if (!WaterstoringStatus.IsValid(r.Status)) errors["status"] = [$"Must be one of: {string.Join(", ", WaterstoringStatus.All)}"];
    if (string.IsNullOrWhiteSpace(r.Place)) errors["place"] = ["Required"];
    if ((r.AffectedStreets?.Count ?? 0) == 0 && (r.AffectedPostalCodes?.Count ?? 0) == 0)
        errors["affected"] = ["Give at least one affected street or postal code"];
    if (r.Location is null || r.Location.Lat is < -90 or > 90 || r.Location.Lon is < -180 or > 180)
        errors["location"] = ["Required: valid lat/lon"];
    if (r.CustomersAffected < 0) errors["customersAffected"] = ["Must be 0 or more"];
    return errors;
}

public partial class Program;   // makes the entry point visible to the integration tests
