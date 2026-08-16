namespace PersonalAssistant.Domain;

public static class TimeZoneResolver
{
    public static TimeZoneInfo Resolve(string timeZoneId)
    {
        if (TryParseFixedOffset(timeZoneId, out var offset))
            return TimeZoneInfo.CreateCustomTimeZone(timeZoneId, offset, timeZoneId, timeZoneId);

        return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    }

    public static bool TryParseFixedOffset(string value, out TimeSpan offset)
    {
        offset = default;
        if (!value.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
            return false;
        if (value.Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            offset = TimeSpan.Zero;
            return true;
        }

        var text = value[3..];
        if (text.Length != 6 || (text[0] != '+' && text[0] != '-') || text[3] != ':'
            || !int.TryParse(text[1..3], out var hours) || !int.TryParse(text[4..6], out var minutes)
            || hours > 14 || minutes > 59)
            return false;

        offset = new TimeSpan(hours, minutes, 0);
        if (text[0] == '-')
            offset = -offset;
        return true;
    }
}
