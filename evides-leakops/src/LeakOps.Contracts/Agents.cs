namespace LeakOps.Contracts;

/// <summary>One piece of evidence behind a verdict. Every claim an agent makes must show its source and the numbers.</summary>
public record EvidenceItem(
    string Source,                   // the tool the finding came from, for example get-sensor-readings
    string Finding,                  // one sentence with the actual numbers
    string Supports);                // leak | no_leak | neutral

/// <summary>Output of the Triage agent: is this alarm a real leak?</summary>
public record TriageVerdict(
    string AlarmId,
    string DistrictId,
    string SensorId,
    DateTimeOffset TriggeredAtUtc,
    string Verdict,                  // genuine_leak | false_alarm | uncertain
    double Confidence,               // 0 to 1
    string Summary,
    IReadOnlyList<EvidenceItem> Evidence,
    string? SuspectedSegmentId,
    string NextStep);                // diagnose | close_alarm | manual_review

public record ToolCallTrace(string Name, string Arguments);

/// <summary>What happened during an agent run, so a person can see how the answer came about.</summary>
public record AgentTrace(
    string Agent,
    string Model,
    long DurationMs,
    long? InputTokens,
    long? OutputTokens,
    IReadOnlyList<ToolCallTrace> ToolCalls);

public record AgentResult<T>(T Result, AgentTrace Trace);
