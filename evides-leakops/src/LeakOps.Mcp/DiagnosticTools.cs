using LeakOps.Contracts;
using LeakOps.Contracts.Logic;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging;

namespace LeakOps.Mcp;

/// <summary>Tools for the Diagnostic agent: GIS (pipe segments, valves, positions) and repair knowledge.</summary>
public class DiagnosticTools(LeakOpsStore store, ILogger<DiagnosticTools> logger)
{
    [Function(nameof(GetPipeSegment))]
    public async Task<string> GetPipeSegment(
        [McpToolTrigger("get-pipe-segment", "Gets GIS data of a pipe segment: street, material, diameter, install year, depth, length, pressure class, start/end coordinates, customers served, critical customers, postal codes, burst history, surface and road type, plus the sensors and isolation valves on it.")]
        ToolInvocationContext context,
        [McpToolProperty("segmentId", "Pipe segment id, for example SEG-0412", isRequired: true)] string segmentId)
    {
        var segment = await store.GetSegmentAsync(segmentId);
        if (segment is null) return ToolJson.Error($"Unknown segment '{segmentId}'.");

        var sensors = await store.ListSensorsOnSegmentAsync(segment.DistrictId, segment.Id);
        var valves = await store.ListValvesAsync(segment.DistrictId, segment.Id);
        logger.LogInformation("get-pipe-segment {SegmentId}", segmentId);

        return ToolJson.Ok(new
        {
            segment,
            sensors = sensors.Select(s => new { s.Id, s.Metric, s.ChainageM, s.AlarmLow, s.AlarmHigh }),
            valves = valves.Select(v => new { v.Id, v.Type, v.DiameterMm, v.ChainageM, v.Status, v.TurnsToClose, v.LastOperatedUtc }),
        });
    }

    [Function(nameof(GetPointOnSegment))]
    public async Task<string> GetPointOnSegment(
        [McpToolTrigger("get-point-on-segment", "Converts a position along a pipe segment (distance in metres from the segment start) into coordinates, and returns the isolation valves with their distance along the pipe, the nearest sensors, and what is affected (customers, critical customers, postal codes).")]
        ToolInvocationContext context,
        [McpToolProperty("segmentId", "Pipe segment id, for example SEG-0412", isRequired: true)] string segmentId,
        [McpToolProperty("chainageM", "Distance in metres from the segment start (0 to the segment length)", isRequired: true)] double? chainageM)
    {
        if (chainageM is null) return ToolJson.Error("'chainageM' is required.");
        var segment = await store.GetSegmentAsync(segmentId);
        if (segment is null) return ToolJson.Error($"Unknown segment '{segmentId}'.");
        if (chainageM < 0 || chainageM > segment.LengthM)
            return ToolJson.Error($"'chainageM' must be between 0 and {segment.LengthM} for {segmentId}.");

        var at = chainageM.Value;
        var location = Geo.PointAt(segment, at);
        var sensors = await store.ListSensorsOnSegmentAsync(segment.DistrictId, segment.Id);
        var valves = await store.ListValvesAsync(segment.DistrictId, segment.Id);
        logger.LogInformation("get-point-on-segment {SegmentId} at {Chainage} m", segmentId, at);

        return ToolJson.Ok(new
        {
            segmentId,
            street = segment.Street,
            chainageM = at,
            location,
            material = segment.Material,
            diameterMm = segment.DiameterMm,
            depthM = segment.DepthM,
            surface = segment.Surface,
            roadType = segment.RoadType,
            isolationValves = valves
                .Select(v => new { v.Id, v.Type, v.Status, v.TurnsToClose, v.LastOperatedUtc, v.Location, distanceAlongPipeM = Math.Round(Math.Abs(v.ChainageM - at)) })
                .OrderBy(v => v.distanceAlongPipeM),
            nearestSensors = sensors
                .Select(s => new { s.Id, s.Metric, distanceAlongPipeM = Math.Round(Math.Abs(s.ChainageM - at)) })
                .OrderBy(s => s.distanceAlongPipeM),
            affected = new { segment.CustomersServed, segment.CriticalCustomers, segment.PostalCodes },
        });
    }

    [Function(nameof(GetNearbyValves))]
    public async Task<string> GetNearbyValves(
        [McpToolTrigger("get-nearby-valves", "Finds valves within a radius of a coordinate, nearest first, with their pipe segment and diameter. Useful to find which valves to close around a leak.")]
        ToolInvocationContext context,
        [McpToolProperty("lat", "Latitude in degrees", isRequired: true)] double? lat,
        [McpToolProperty("lon", "Longitude in degrees", isRequired: true)] double? lon,
        [McpToolProperty("radiusM", "Search radius in metres. Default 500, maximum 5000.", isRequired: false)] double? radiusM)
    {
        if (lat is null || lon is null) return ToolJson.Error("'lat' and 'lon' are required.");
        var radius = Math.Clamp(radiusM ?? 500, 10, 5000);
        var here = new GeoPoint(lat.Value, lon.Value);

        var valves = (await store.ListAllValvesAsync())
            .Select(v => new { Valve = v, Distance = Geo.Metres(here, v.Location) })
            .Where(x => x.Distance <= radius)
            .OrderBy(x => x.Distance)
            .Select(x => new
            {
                x.Valve.Id, x.Valve.SegmentId, x.Valve.DiameterMm, x.Valve.Type, x.Valve.Status, x.Valve.TurnsToClose, x.Valve.Location,
                distanceM = Math.Round(x.Distance),
            })
            .ToList();
        logger.LogInformation("get-nearby-valves {Lat},{Lon} r={Radius} -> {Count}", lat, lon, radius, valves.Count);

        return ToolJson.Ok(new { center = here, radiusM = radius, count = valves.Count, valves });
    }

    [Function(nameof(GetRepairProcedure))]
    public async Task<string> GetRepairProcedure(
        [McpToolTrigger("get-repair-procedure", "Gets the standard repair procedure for a pipe material, diameter and breach type: required technician certifications, parts (with conditions), equipment, estimated repair hours, and notes.")]
        ToolInvocationContext context,
        [McpToolProperty("material", "Pipe material: cast_iron, ductile_iron, pvc, pe, asbestos_cement or steel", isRequired: true)] string material,
        [McpToolProperty("diameterMm", "Nominal diameter in mm, for example 300", isRequired: true)] int? diameterMm,
        [McpToolProperty("breachType", "burst or leak. Default burst.", isRequired: false)] string? breachType)
    {
        if (diameterMm is null) return ToolJson.Error("'diameterMm' is required.");
        var breach = string.IsNullOrWhiteSpace(breachType) ? "burst" : breachType.Trim().ToLowerInvariant();

        var procedure = await store.FindProcedureAsync(material.Trim().ToLowerInvariant(), diameterMm.Value, breach);
        logger.LogInformation("get-repair-procedure {Material} DN{Diameter} {Breach} -> {Found}", material, diameterMm, breach, procedure?.Id);
        if (procedure is not null) return ToolJson.Ok(procedure);

        var known = (await store.ListProceduresAsync(material.Trim().ToLowerInvariant()))
            .Select(p => new { p.Id, p.BreachType, p.MinDiameterMm, p.MaxDiameterMm });
        return ToolJson.Error($"No procedure for {material} DN{diameterMm} ({breach}).", new { proceduresForThisMaterial = known });
    }
}
