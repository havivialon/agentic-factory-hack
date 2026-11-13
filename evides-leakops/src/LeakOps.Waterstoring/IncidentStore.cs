using LeakOps.Contracts;

namespace LeakOps.Waterstoring;

/// <summary>In-memory incident list of the fake site (a restart empties it; use POST /api/admin/reset to clear it on demand).</summary>
public class IncidentStore(TimeProvider time)
{
    // C# note: "(TimeProvider time)" after the class name is a primary constructor; "time" is usable in every method.
    private readonly object _gate = new();
    private readonly Dictionary<string, WaterstoringIncident> _incidents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the incident, or replaces it when the IncidentId already exists (so retries are safe).</summary>
    public (WaterstoringIncident Incident, bool Created) Upsert(WaterstoringIncidentRequest request)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var entry = new WaterstoringTimelineEntry(now, request.Status, request.Message, request.MessageEn, request.ExpectedResolutionUtc);

            if (_incidents.TryGetValue(request.IncidentId, out var existing))
            {
                var replaced = FromRequest(request, existing.CreatedUtc, now, [.. existing.Timeline, entry]);
                _incidents[request.IncidentId] = replaced;
                return (replaced, false);
            }

            var created = FromRequest(request, now, now, [entry]);
            _incidents[request.IncidentId] = created;
            return (created, true);
        }
    }

    public WaterstoringIncident? Update(string id, WaterstoringIncidentUpdate update)
    {
        lock (_gate)
        {
            if (!_incidents.TryGetValue(id, out var current)) return null;

            var now = time.GetUtcNow();
            var status = update.Status ?? current.Status;
            var message = update.Message ?? current.Message;
            var messageEn = update.MessageEn ?? current.MessageEn;
            var eta = update.ExpectedResolutionUtc ?? current.ExpectedResolutionUtc;

            var updated = current with
            {
                Status = status,
                Message = message,
                MessageEn = messageEn,
                ExpectedResolutionUtc = eta,
                UpdatedUtc = now,
                Timeline = [.. current.Timeline, new WaterstoringTimelineEntry(now, status, message, messageEn, eta)],
            };
            _incidents[id] = updated;
            return updated;
        }
    }

    public WaterstoringIncident? Get(string id)
    {
        lock (_gate) return _incidents.GetValueOrDefault(id);
    }

    /// <summary>Active incidents first (newest update first), then resolved ones.</summary>
    public IReadOnlyList<WaterstoringIncident> List(bool activeOnly)
    {
        lock (_gate)
        {
            return _incidents.Values
                .Where(i => !activeOnly || WaterstoringStatus.IsActive(i.Status))
                .OrderBy(i => WaterstoringStatus.IsActive(i.Status) ? 0 : 1)
                .ThenByDescending(i => i.UpdatedUtc)
                .ToList();
        }
    }

    /// <summary>Finds active incidents by postcode (4 digits or full), street or place.</summary>
    public IReadOnlyList<WaterstoringIncident> Search(string? query)
    {
        var text = (query ?? "").Trim();
        if (text.Length < 3) return [];

        var compact = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var looksLikePostcode = compact.Length >= 4 && compact.Take(4).All(char.IsDigit);

        return List(activeOnly: true).Where(i => looksLikePostcode
            ? i.AffectedPostalCodes.Any(p => Compact(p).StartsWith(compact, StringComparison.Ordinal))
            : i.Place.Contains(text, StringComparison.OrdinalIgnoreCase)
              || i.AffectedStreets.Any(s => s.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public void Clear()
    {
        lock (_gate) _incidents.Clear();
    }

    private static string Compact(string postcode) => postcode.Replace(" ", "").ToUpperInvariant();

    private static WaterstoringIncident FromRequest(
        WaterstoringIncidentRequest r, DateTimeOffset created, DateTimeOffset updated, IReadOnlyList<WaterstoringTimelineEntry> timeline) =>
        new(r.IncidentId, r.Title, r.Message, r.TitleEn, r.MessageEn, r.Status, r.Place, r.AffectedStreets, r.AffectedPostalCodes,
            r.Location, r.AffectedPath, r.CustomersAffected, r.ExpectedResolutionUtc, r.Advice, r.AdviceEn, created, updated, timeline);
}
