using System.Globalization;

namespace LeakOps.Contracts.Logic;

/// <summary>Time helpers. Everything is stored in UTC; "local" is only used for day type and rush hours.</summary>
public static class LeakOpsTime
{
    // November in the Netherlands is UTC+1. Fixed on purpose so results do not depend on the machine's time zone.
    public const int LocalOffsetHours = 1;

    /// <summary>The same instant with a zero offset. Cosmos DB compares timestamps as strings, so queries must always use this form.</summary>
    public static DateTimeOffset Utc(DateTimeOffset value) => new(value.UtcDateTime, TimeSpan.Zero);

    public static DateTimeOffset ToLocal(DateTimeOffset utc) => Utc(utc).ToOffset(TimeSpan.FromHours(LocalOffsetHours));

    public static string DayType(DateTimeOffset utc) =>
        ToLocal(utc).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "weekend" : "weekday";

    /// <summary>Parses an ISO timestamp. Text without an offset is treated as UTC.</summary>
    public static bool TryParseUtc(string? text, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;
        utc = Utc(parsed);
        return true;
    }
}
