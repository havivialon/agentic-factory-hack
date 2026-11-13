using System.Text.Json;
using LeakOps.Contracts;
using LeakOps.Contracts.Logic;
using LeakOps.Workflow;

// Workflow host: runs the six agents in order for one alarm and streams the progress to the operator page as server-sent events.
// The agents are the HTTP functions of LeakOps.Agents; this host only orchestrates and never touches data itself.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(WorkflowSettings.From(builder.Configuration));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ScenarioCatalog>();
builder.Services.AddSingleton<RunLog>();
// The agents take a while (a model call each), so the HTTP timeout is generous.
builder.Services.AddHttpClient<IAgentGateway, AgentGateway>(client => client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddTransient<WorkflowRunner>();

var app = builder.Build();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "workflow" }));
app.MapGet("/api/config", (WorkflowSettings settings) => Results.Ok(new { waterstoringUrl = settings.WaterstoringPublicUrl }));
app.MapGet("/api/scenarios", (ScenarioCatalog catalog) => Results.Ok(catalog.Scenarios));
app.MapGet("/api/runs", (RunLog log) => Results.Ok(log.Recent()));
app.MapGet("/api/runs/{runId}", (string runId, RunLog log) => log.Find(runId) is { } run ? Results.Ok(run) : Results.NotFound());

// POST /api/runs  { "scenarioId": "..." } or { "alarm": { ... } }
//   default       -> a text/event-stream with one event per step (run_started, step_started, step_completed, step_failed, run_completed)
//   ?wait=true    -> waits for the run and returns the summary as JSON (handy for scripts)
app.MapPost("/api/runs", async (HttpContext context, RunRequest request, ScenarioCatalog catalog, WorkflowRunner runner, RunLog log,
    IHostApplicationLifetime lifetime, bool? wait) =>
{
    var alarm = request.Alarm ?? catalog.Scenarios.FirstOrDefault(s => s.Id == request.ScenarioId)?.Alarm;
    if (alarm is null) return Results.BadRequest(new { error = "Give a scenarioId of /api/scenarios, or an alarm." });
    var problem = ValidateAlarm(alarm);
    if (problem is not null) return Results.BadRequest(new { error = problem });

    // The run is not tied to the browser connection: closing the page must not leave a half-booked incident behind.
    var token = lifetime.ApplicationStopping;

    if (wait == true)
    {
        var summary = await runner.RunAsync(alarm, _ => ValueTask.CompletedTask, token);
        log.Add(summary);
        return Results.Ok(summary);
    }

    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    var clientGone = false;

    async ValueTask Send(WorkflowEvent e)
    {
        if (clientGone) return;
        try
        {
            await context.Response.WriteAsync($"event: {e.Type}\ndata: {JsonSerializer.Serialize(e, json)}\n\n", token);
            await context.Response.Body.FlushAsync(token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            clientGone = true;
        }
    }

    log.Add(await runner.RunAsync(alarm, Send, token));
    return Results.Empty;
});

app.Run();

static string? ValidateAlarm(AnomalyAlarm alarm)
{
    if (string.IsNullOrWhiteSpace(alarm.AlarmId)) return "alarmId is required.";
    if (string.IsNullOrWhiteSpace(alarm.SensorId)) return "sensorId is required.";
    if (string.IsNullOrWhiteSpace(alarm.DistrictId)) return "districtId is required.";
    if (alarm.TriggeredAtUtc == default) return "triggeredAtUtc is required (ISO 8601 UTC).";
    return null;
}

record RunRequest(string? ScenarioId, AnomalyAlarm? Alarm);
