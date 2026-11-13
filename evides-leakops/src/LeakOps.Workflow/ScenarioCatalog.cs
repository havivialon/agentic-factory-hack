using System.Collections.Concurrent;
using System.Text.Json;
using LeakOps.Contracts;

namespace LeakOps.Workflow;

/// <summary>What the operator page may see of a scenario: never the ground truth.</summary>
public record ScenarioInfo(string Id, string Name, string Description, AnomalyAlarm Alarm);

public class ScenarioCatalog
{
    public IReadOnlyList<ScenarioInfo> Scenarios { get; }

    public ScenarioCatalog(IHostEnvironment environment)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "scenarios.json");
        if (!File.Exists(path)) path = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "data", "scenarios.json"));
        Scenarios = File.Exists(path)
            ? JsonSerializer.Deserialize<List<Scenario>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!
                .Select(s => new ScenarioInfo(s.Id, s.Name, s.Description, s.Alarm)).ToList()
            : [];
    }
}

/// <summary>The last runs, in memory, newest first. A restart empties it.</summary>
public class RunLog
{
    private const int Capacity = 25;
    private readonly ConcurrentQueue<WorkflowSummary> _runs = new();

    public void Add(WorkflowSummary summary)
    {
        _runs.Enqueue(summary);
        while (_runs.Count > Capacity) _runs.TryDequeue(out _);
    }

    public IReadOnlyList<WorkflowSummary> Recent() => _runs.Reverse().ToList();
    public WorkflowSummary? Find(string runId) => _runs.FirstOrDefault(r => r.RunId == runId);
}
