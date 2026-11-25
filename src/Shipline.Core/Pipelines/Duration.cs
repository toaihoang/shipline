using System.Globalization;
using System.Text.RegularExpressions;

namespace Shipline.Core.Pipelines;

/// <summary>Reads and writes durations such as "45s", "30m", "2h" and "1h30m".</summary>
public static partial class Duration
{
    /// <summary>The longest duration accepted. A longer timeout would be a typo, not a plan.</summary>
    public static readonly TimeSpan MaxValue = TimeSpan.FromDays(30);

    public static bool TryParse(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        Match match = DurationPattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        if (!TryReadPart(match.Groups["hours"], out int hours))
        {
            return false;
        }

        if (!TryReadPart(match.Groups["minutes"], out int minutes))
        {
            return false;
        }

        if (!TryReadPart(match.Groups["seconds"], out int seconds))
        {
            return false;
        }

        // long arithmetic, so a huge number of hours cannot overflow before the range check.
        long totalSeconds = (hours * 3600L) + (minutes * 60L) + seconds;
        if (totalSeconds <= 0 || totalSeconds > MaxValue.TotalSeconds)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(totalSeconds);
        return true;
    }

    /// <summary>Formats a duration for people: "850ms", "12.4s", "3m 05s", "1h 02m".</summary>
    public static string Format(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMilliseconds}ms");
        }

        if (duration.TotalMinutes < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0}s");
        }

        if (duration.TotalHours < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes}m {duration.Seconds:00}s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}h {duration.Minutes:00}m");
    }

    private static bool TryReadPart(Group group, out int value)
    {
        value = 0;
        if (!group.Success)
        {
            return true;
        }

        return int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    // Each part is optional, but the lookahead requires at least one character.
    [GeneratedRegex(@"^(?=.)(?:(?<hours>\d+)h)?(?:(?<minutes>\d+)m)?(?:(?<seconds>\d+)s)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
}
