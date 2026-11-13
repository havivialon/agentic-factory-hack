using System.Text.Json;
using LeakOps.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LeakOps.Agents;

/// <summary>HTTP entry points of the Agents function app. One function per agent; the workflow host calls them in order.</summary>
public class AgentFunctions(TriageAgent triage, ILogger<AgentFunctions> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Function("Health")]
    public IActionResult Health([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest request) =>
        new OkObjectResult(new { status = "Healthy", service = "leakops-agents" });

    /// <summary>POST /api/agents/triage with an AnomalyAlarm; returns the TriageVerdict and a trace of the agent run.</summary>
    [Function("Triage")]
    public async Task<IActionResult> Triage(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "agents/triage")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        AnomalyAlarm? alarm;
        try
        {
            alarm = await request.ReadFromJsonAsync<AnomalyAlarm>(Json, cancellationToken);
        }
        catch (JsonException ex)
        {
            return new BadRequestObjectResult(new { error = $"Body is not a valid alarm: {ex.Message}" });
        }

        var problem = Validate(alarm);
        if (problem is not null) return new BadRequestObjectResult(new { error = problem });

        try
        {
            var result = await triage.TriageAsync(alarm!, cancellationToken);
            logger.LogInformation("Triage {AlarmId}: {Verdict} ({Confidence})", alarm!.AlarmId, result.Result.Verdict, result.Result.Confidence);
            return new OkObjectResult(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The caller (workflow host) treats any non-200 as "triage failed" and routes the alarm to a person.
            // Azure's content safety can reject a prompt before the model sees it; report that distinctly.
            var blocked = ex.ToString().Contains("content_filter", StringComparison.OrdinalIgnoreCase);
            logger.LogError(ex, "Triage failed for alarm {AlarmId} ({Reason})", alarm!.AlarmId, blocked ? "content filter" : "agent error");
            return new ObjectResult(new
            {
                error = "The triage agent failed.",
                reason = blocked ? "blocked_by_content_filter" : "agent_error",
                detail = ex.Message,
            }) { StatusCode = StatusCodes.Status502BadGateway };
        }
    }

    private static string? Validate(AnomalyAlarm? alarm)
    {
        if (alarm is null) return "Body is required.";
        if (string.IsNullOrWhiteSpace(alarm.AlarmId)) return "alarmId is required.";
        if (string.IsNullOrWhiteSpace(alarm.SensorId)) return "sensorId is required.";
        if (string.IsNullOrWhiteSpace(alarm.DistrictId)) return "districtId is required.";
        if (alarm.TriggeredAtUtc == default) return "triggeredAtUtc is required (ISO 8601 UTC).";
        return null;
    }
}
