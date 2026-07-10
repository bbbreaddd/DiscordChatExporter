using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DiscordChatExporter.Cli.Configuration;

// Resolves a full-scan window bound (`after`/`before`) to a concrete instant: either a relative
// duration subtracted from `now` -- "30d", "12h", "2w", "90m" (days/hours/weeks/minutes) -- or an
// absolute ISO-8601 date/time. Throws FormatException on anything else. Kept separate so the config
// loader can validate the string at load time using the exact logic the watcher applies at run time.
public static partial class TimeWindow
{
    public static DateTimeOffset Resolve(string value, DateTimeOffset now)
    {
        var v = value.Trim();

        var m = DurationRegex().Match(v);
        if (m.Success)
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return m.Groups[2].Value switch
            {
                "m" => now.AddMinutes(-n),
                "h" => now.AddHours(-n),
                "d" => now.AddDays(-n),
                "w" => now.AddDays(-7 * n),
                _ => throw new FormatException($"Unknown duration unit in '{value}'."),
            };
        }

        if (
            DateTimeOffset.TryParse(
                v,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var dt
            )
        )
            return dt;

        throw new FormatException(
            $"'{value}' is not a valid duration (e.g. 30d, 12h, 2w, 90m) or ISO-8601 date."
        );
    }

    [GeneratedRegex(@"^(\d+)([mhdw])$")]
    private static partial Regex DurationRegex();
}
