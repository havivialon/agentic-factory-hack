namespace LeakOps.Contracts.Logic;

public record TechnicianStatus(
    string Id,
    string Name,
    bool OnShift,
    DateTimeOffset? ShiftEndsUtc,
    DateTimeOffset? NextShiftStartsUtc,
    DateTimeOffset? BusyUntilUtc,
    string? BusyWith,
    bool Available,                          // on shift and not busy at the requested time
    bool HasAllRequiredCertifications,
    IReadOnlyList<string> MissingCertifications,
    IReadOnlyList<string> Certifications,
    GeoPoint LastKnownLocation,
    DateTimeOffset LocationAsOfUtc,
    double? StraightLineKm,
    int? EstimatedTravelMinutes,
    string Vehicle,
    bool CanTowExcavator);

public static class WorkforceAvailability
{
    /// <summary>Evaluates one technician at a moment in time against a list of required certifications and an optional site.</summary>
    public static TechnicianStatus Evaluate(Technician technician, DateTimeOffset atUtc, IReadOnlyCollection<string> requiredCertifications, GeoPoint? site)
    {
        var at = LeakOpsTime.Utc(atUtc);
        var shift = technician.Shifts.FirstOrDefault(s => s.StartUtc <= at && s.EndUtc > at);
        var busy = technician.BusyBlocks.FirstOrDefault(b => b.StartUtc <= at && b.EndUtc > at);
        var next = technician.Shifts.Where(s => s.StartUtc > at).OrderBy(s => s.StartUtc).FirstOrDefault();
        var missing = requiredCertifications.Where(c => !technician.Certifications.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

        return new TechnicianStatus(
            Id: technician.Id,
            Name: technician.Name,
            OnShift: shift is not null,
            ShiftEndsUtc: shift?.EndUtc,
            NextShiftStartsUtc: next?.StartUtc,
            BusyUntilUtc: busy?.EndUtc,
            BusyWith: busy?.Description,
            Available: shift is not null && busy is null,
            HasAllRequiredCertifications: missing.Count == 0,
            MissingCertifications: missing,
            Certifications: technician.Certifications,
            LastKnownLocation: technician.Location,
            LocationAsOfUtc: technician.LocationAsOfUtc,
            StraightLineKm: site is null ? null : Math.Round(Geo.Metres(technician.Location, site) / 1000.0, 1),
            EstimatedTravelMinutes: site is null ? null : TravelEstimator.Estimate(technician.Location, site, at).Minutes,
            Vehicle: $"{technician.Vehicle.Type} {technician.Vehicle.Plate}",
            CanTowExcavator: technician.Vehicle.CanTowExcavator);
    }
}
