namespace Wattkin.Platform;

/// <summary>
/// Version numbers as Wattkin compares them: major.minor.build. This fork has no updater (no network
/// at all), so versions only decide whether running a newer build replaces the installed copy.
/// </summary>
public static class AppVersion
{
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>"v0.2.0", "0.2.0" or "0.2.0+commit" → 0.2.0.</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var s = text?.Trim() ?? "";
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s[..plus];
        }

        if (!Version.TryParse(s, out var parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }
}
