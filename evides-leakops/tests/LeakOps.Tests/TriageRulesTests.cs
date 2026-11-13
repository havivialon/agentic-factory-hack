using LeakOps.Contracts;
using LeakOps.Contracts.Logic;
using LeakOps.Seeder;
using Xunit;

namespace LeakOps.Tests;

public class TriageRulesTests
{
    private static readonly AnomalyAlarm Alarm =
        SeedDataLoader.Load(SeedDataLoader.FindDataDirectory()).Scenarios.Single(s => s.Id == "scn-burst-cast-iron").Alarm;

    private static readonly EvidenceItem Leak = new("get-sensor-readings", "PS-101 fell from 4.86 to 2.85 bar in 30 minutes.", "leak");
    private static readonly EvidenceItem MoreLeak = new("get-sensor-readings", "Inlet flow rose from 40 to 121 m3/h.", "leak");
    private static readonly EvidenceItem NoLeak = new("get-planned-works", "Valve work PW-7781 is in progress.", "no_leak");

    private static TriageVerdict Verdict(string verdict, double confidence, params EvidenceItem[] evidence) => new(
        AlarmId: "wrong", DistrictId: "wrong", SensorId: "wrong", TriggeredAtUtc: default,
        Verdict: verdict, Confidence: confidence, Summary: "Summary.", Evidence: evidence,
        SuspectedSegmentId: "SEG-0412", NextStep: "whatever");

    [Fact]
    public void A_well_supported_leak_keeps_its_verdict_and_gets_identity_and_next_step_from_the_rules()
    {
        var result = TriageRules.Normalize(Verdict("genuine_leak", 0.93, Leak, MoreLeak), Alarm);

        Assert.Equal("genuine_leak", result.Verdict);
        Assert.Equal(0.93, result.Confidence);
        Assert.Equal("diagnose", result.NextStep);
        Assert.Equal("SEG-0412", result.SuspectedSegmentId);
        Assert.Equal((Alarm.AlarmId, Alarm.DistrictId, Alarm.SensorId, Alarm.TriggeredAtUtc),
            (result.AlarmId, result.DistrictId, result.SensorId, result.TriggeredAtUtc));   // the model cannot change which alarm this is
        Assert.Equal(TimeSpan.Zero, result.TriggeredAtUtc.Offset);

        var sentInLocalTime = Alarm with { TriggeredAtUtc = Alarm.TriggeredAtUtc.ToOffset(TimeSpan.FromHours(1)) };
        Assert.Equal(TimeSpan.Zero, TriageRules.Normalize(Verdict("genuine_leak", 0.9, Leak, MoreLeak), sentInLocalTime).TriggeredAtUtc.Offset);   // same instant, always +00:00
    }

    [Fact]
    public void A_verdict_without_matching_evidence_becomes_uncertain_and_goes_to_a_person()
    {
        var noLeakEvidence = TriageRules.Normalize(Verdict("genuine_leak", 0.9, NoLeak, NoLeak), Alarm);
        var singleItem = TriageRules.Normalize(Verdict("false_alarm", 0.9, NoLeak), Alarm);

        foreach (var result in new[] { noLeakEvidence, singleItem })
        {
            Assert.Equal("uncertain", result.Verdict);
            Assert.True(result.Confidence <= 0.4);
            Assert.Equal("manual_review", result.NextStep);
            Assert.Null(result.SuspectedSegmentId);
            Assert.StartsWith("Downgraded to uncertain", result.Summary);
        }
    }

    [Fact]
    public void Invalid_model_output_is_repaired_rather_than_passed_on()
    {
        var unknown = TriageRules.Normalize(Verdict("probably a leak", 0.9, Leak, MoreLeak), Alarm);
        var outOfRange = TriageRules.Normalize(Verdict("genuine_leak", 1.7, Leak, MoreLeak), Alarm);
        var notANumber = TriageRules.Normalize(Verdict("uncertain", double.NaN), Alarm);
        var blankEvidence = TriageRules.Normalize(Verdict("genuine_leak", 0.8, Leak, MoreLeak, new EvidenceItem("x", "  ", "leak")), Alarm);

        Assert.Equal("uncertain", unknown.Verdict);
        Assert.Equal(1.0, outOfRange.Confidence);
        Assert.Equal(0.0, notANumber.Confidence);
        Assert.Equal(2, blankEvidence.Evidence.Count);
    }

    [Fact]
    public void False_alarm_with_an_explanation_closes_the_alarm()
    {
        var onTheAlarmSensor = new EvidenceItem("get-sensor-readings", "PS-101 fell to 2.85 bar but inlet flow fell from 160 to 112 m3/h.", "no_leak");
        var result = TriageRules.Normalize(Verdict("false_alarm", 0.88, NoLeak, onTheAlarmSensor), Alarm);

        Assert.Equal("false_alarm", result.Verdict);
        Assert.Equal("close_alarm", result.NextStep);
        Assert.Null(result.SuspectedSegmentId);   // only meaningful for a leak
    }

    [Fact]
    public void The_alarm_sensor_is_recognised_despite_typographic_hyphens_and_spacing()
    {
        // Models often write "PS-101" with a non-breaking hyphen (U+2011), an en dash, or a space.
        foreach (var written in new[] { "PS\u2011101", "PS\u2013101", "ps 101", "PS-101" })
        {
            var evidence = new[]
            {
                new EvidenceItem("get-sensor-readings", $"{written} fell from 4.86 to 2.85 bar.", "leak"),
                MoreLeak,
            };
            Assert.Equal("genuine_leak", TriageRules.Normalize(Verdict("genuine_leak", 0.9, evidence), Alarm).Verdict);
        }
    }

    [Fact]
    public void The_alarm_sensors_readings_must_be_in_the_evidence_but_the_label_can_be_neutral()
    {
        // A pressure drop alone fits both a leak and valve work, so models often label it neutral. That is fine.
        var neutralReading = new EvidenceItem("get-sensor-readings", "PS-101 fell from 4.86 to 2.85 bar over 30 minutes.", "neutral");
        var accepted = TriageRules.Normalize(Verdict("genuine_leak", 0.9, neutralReading, Leak with { Finding = "Inlet flow rose from 40 to 121 m3/h." }, MoreLeak), Alarm);
        Assert.Equal("genuine_leak", accepted.Verdict);

        // Naming the sensor without any reading ("does not exist") is not evidence about it.
        var noReading = new EvidenceItem("list-sensors", "PS-101 does not exist in district D-01.", "neutral");
        var rejected = TriageRules.Normalize(Verdict("genuine_leak", 0.9, noReading, MoreLeak with { Finding = "Inlet flow rose from 40 to 121 m3/h." }, MoreLeak), Alarm);
        Assert.Equal("uncertain", rejected.Verdict);
    }

    [Fact]
    public void A_verdict_that_never_looked_at_the_alarm_sensor_is_not_trusted()
    {
        // Evidence about other sensors only: the alarm came from PS-101 but nothing in the evidence is about PS-101.
        var aboutOthers = new[]
        {
            new EvidenceItem("get-sensor-readings", "PS-103 fell from 4.84 to 3.62 bar.", "leak"),
            new EvidenceItem("get-sensor-readings", "Inlet flow rose from 40 to 121 m3/h.", "leak"),
        };

        var result = TriageRules.Normalize(Verdict("genuine_leak", 0.97, aboutOthers), Alarm);

        Assert.Equal("uncertain", result.Verdict);
        Assert.Contains("PS-101", result.Summary);
        Assert.Equal("manual_review", result.NextStep);
    }
}
