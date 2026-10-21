using System.Windows.Controls;
using Wattkin.Settings;

namespace Wattkin.UI;

/// <summary>Drop-down lists whose items carry the value they stand for in their Tag.</summary>
internal static class Choices
{
    public static ComboBoxItem Item(string text, object? tag) => new() { Content = text, Tag = tag };

    /// <summary>
    /// Selects the item for a value. A value the list doesn't offer is added (described by
    /// <paramref name="missing"/>, else as a percentage or as is), so saving can't silently erase it.
    /// </summary>
    public static void Select(ComboBox box, object? tag, Func<object, string>? missing = null)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (Equals(item.Tag, tag))
            {
                box.SelectedItem = item;
                return;
            }
        }

        if (tag is null)
        {
            box.SelectedIndex = 0;
            return;
        }

        var extra = Item(missing?.Invoke(tag) ?? (tag is int v ? Fmt.Percent(v) : tag.ToString() ?? "?"), tag);
        box.Items.Add(extra);
        box.SelectedItem = extra;
    }

    public static T? TagOf<T>(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: T value } ? value : default;

    /// <summary>A rule's profile list: "Leave as is" (stored as null), then every profile by name.</summary>
    public static void FillProfiles(ComboBox box, IEnumerable<string> names)
    {
        box.Items.Clear();
        box.Items.Add(Item(Strings.Common_LeaveAsIs, ""));
        foreach (var name in names)
        {
            box.Items.Add(Item(name, name));
        }
    }

    /// <summary>The profile a rule's list names, or null for "Leave as is".</summary>
    public static string? ProfileOf(ComboBox box) => TagOf<string>(box) is { Length: > 0 } name ? name : null;

    /// <summary>What the tray icon can show.</summary>
    public static void FillTrayDisplay(ComboBox box)
    {
        box.Items.Clear();
        box.Items.Add(Item(Strings.Tray_ShowLogo, TrayDisplay.Logo));
        box.Items.Add(Item(Strings.Tray_ShowWatts, TrayDisplay.Watts));
        box.Items.Add(Item(Strings.Tray_ShowCpuTemperature, TrayDisplay.CpuTemperature));
        box.Items.Add(Item(Strings.Tray_ShowCpuLoad, TrayDisplay.CpuLoad));
    }

    /// <summary>The interface languages, each in its own language: Windows' (stored as null), English, French.</summary>
    public static void FillLanguages(ComboBox box)
    {
        box.Items.Clear();
        box.Items.Add(Item(string.Format(Strings.Language_SameAsWindows, UiLanguage.NameOf(UiLanguage.WindowsLanguage)), null));
        foreach (var language in new[] { UiLanguage.English, UiLanguage.French })
        {
            box.Items.Add(Item(UiLanguage.NameOf(language), language));
        }
    }
}
