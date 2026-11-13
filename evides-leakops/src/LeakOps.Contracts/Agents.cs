using LeakOps.Contracts.Logic;

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
    string Place,
    IReadOnlyList<string> ServedStreets,
    double ChainageM,
    double UncertaintyM,
    GeoPoint Location,
    IReadOnlyList<GeoPoint> SegmentPath,   // start and end of the pipe segment, to draw it on a map
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

// ---- Communications agent ---------------------------------------------------------------------------------------

/// <summary>Input of the Communications agent: the diagnosis. A report that needs review is not published.</summary>
public record CommunicationsRequest(DiagnosticReport Report);

/// <summary>What the model writes for residents, in Dutch and English. Nothing else: all facts are added by code.</summary>
public record PublicNotice(
    string TitleNl,
    string MessageNl,
    string AdviceNl,
    string TitleEn,
    string MessageEn,
    string AdviceEn);

/// <summary>Outcome of the Communications step.</summary>
public record CommunicationsResult(
    string IncidentId,
    PublicNotice Notice,
    bool UsedFallbackText,                 // true when the model's draft was rejected twice and the safe template was published instead
    IReadOnlyList<string> RejectedDraftIssues,
    WaterstoringIncident Published,
    string PublicUrl);

// ---- Resource Matchmaker agent ----------------------------------------------------------------------------------

/// <summary>Input of the Resource Matchmaker: the diagnosis. A report that needs review gets nobody dispatched.</summary>
public record MatchmakerRequest(DiagnosticReport Report);

/// <summary>What the model decides: who goes first and who is the backup, and why. Eligibility is checked by code.</summary>
public record MatchJudgement(
    string PrimaryTechnicianId,
    string? BackupTechnicianId,
    string Summary);

/// <summary>How one technician was assessed against this repair, so a dispatcher can see why somebody was or was not chosen.</summary>
public record CandidateAssessment(
    string TechnicianId,
    string Name,
    bool Eligible,                   // on shift, not busy and holding every required certification
    string Reason,
    int? TravelMinutes,
    double? DistanceKm);

/// <summary>Output of the Resource Matchmaker agent: the technician to send, a backup and the assessment of everybody else.</summary>
public record ResourceMatch(
    string IncidentId,
    string AlarmId,
    string SegmentId,
    GeoPoint Site,
    IReadOnlyList<string> RequiredCertifications,
    string? RepairProcedureId,
    double EstimatedRepairHours,
    bool NeedsExcavator,
    TechnicianStatus? Primary,
    TechnicianStatus? Backup,
    IReadOnlyList<CandidateAssessment> Candidates,
    string Summary,
    IReadOnlyList<string> Warnings,        // every correction of the model's answer and every point a dispatcher should know
    bool NeedsReview,                      // true when nobody can be dispatched without a person deciding
    IReadOnlyList<string> ReviewReasons);

