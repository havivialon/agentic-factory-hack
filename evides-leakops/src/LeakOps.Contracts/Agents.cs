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

// ---- Diagnostic agent -------------------------------------------------------------------------------------------

/// <summary>Input of the Diagnostic agent: the alarm and the Triage verdict that confirmed a leak.</summary>
public record DiagnosticRequest(AnomalyAlarm Alarm, TriageVerdict Triage);

/// <summary>What the model decides. Everything factual (material, diameter, customers, coordinates, severity) is added from GIS data and rules.</summary>
public record DiagnosisJudgement(
    string SegmentId,                // the segment the breach is on: normally the one Triage suspected, but verified against the data
    double ChainageM,                // best estimate of the breach position along the segment, from estimate-leak-position
    double UncertaintyM,
    string BreachType,               // burst | leak
    IReadOnlyList<string> IsolationValveIds,   // valves to close, in the order to close them
    string Summary,
    IReadOnlyList<EvidenceItem> Evidence);

/// <summary>The largest pressure drop on any sensor of a segment, from the rank-segments-by-pressure-drop tool.</summary>
public record SegmentDrop(string SegmentId, double MaxDropBar);

/// <summary>A valve on the segment, as returned by the get-point-on-segment tool.</summary>
public record IsolationValveInfo(
    string Id,
    string Type,
    string Status,
    int TurnsToClose,
    DateTimeOffset? LastOperatedUtc,
    GeoPoint Location,
    double DistanceAlongPipeM);

public record IsolationStep(int Order, string ValveId, string Action, int TurnsToClose, string Status, GeoPoint Location);

/// <summary>Output of the Diagnostic agent: where the breach is, what the pipe is, how serious it is and how to isolate it.</summary>
public record DiagnosticReport(
    string AlarmId,
    string DistrictId,
    DateTimeOffset TriggeredAtUtc,
    string SegmentId,
    string Street,
    double ChainageM,
    double UncertaintyM,
    GeoPoint Location,
    string Material,
    int DiameterMm,
    int InstallYear,
    double DepthM,
    string Surface,
    string RoadType,
    string BreachType,
    string Severity,
    IReadOnlyList<string> SeverityReasons,
    int CustomersAffected,
    IReadOnlyList<string> CriticalCustomers,
    IReadOnlyList<string> PostalCodes,
    IReadOnlyList<IsolationStep> IsolationPlan,
    string? RepairProcedureId,
    string Summary,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<string> Warnings,        // every correction or gap the rules found in the model's answer
    bool NeedsReview,                      // true when a person should check the diagnosis before anyone is sent out
    IReadOnlyList<string> ReviewReasons);
