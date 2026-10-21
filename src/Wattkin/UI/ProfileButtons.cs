using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Wattkin.Features;
using Wattkin.Settings;

namespace Wattkin.UI;

/// <summary>
/// The four profile slots as buttons, one row (two when the names don't fit): a click applies the
/// profile; the gear inside a button opens its editor (<see cref="ProfileEditorWindow"/>) without
/// applying anything. The flyout and the settings window both use it.
/// </summary>
public sealed class ProfileButtons : UniformGrid
{
    private string _key = "";
    private bool _updating;
    private double _widest; // the longest name, in pixels: one row when every button fits it

    public ProfileButtons()
    {
        Rows = 1;
    }

    /// <summary>The user picked a profile (raised after it's applied).</summary>
    public event Action<Profile>? Picked;

    /// <summary>The gear of a slot was clicked (the window hosting the buttons may want to step aside).</summary>
    public event Action<int>? EditRequested;

    /// <summary>Builds the buttons when the names changed, and shows which profile is active.</summary>
    public void Refresh(AppSettings settings, HardwareState state)
    {
        _updating = true;
        try
        {
            var key = string.Join("|", settings.Profiles.Select(p => p.Name));
            if (key != _key)
            {
                _key = key;
                Build(settings);
            }

            // Until the EC has been read (needs admin) the real settings are unknown: highlight nothing.
            var known = state.Available && settings.HardwareAdopted;
            foreach (RadioButton button in Children)
            {
                button.IsChecked = known && string.Equals((string)button.Tag, settings.ActiveProfile, StringComparison.OrdinalIgnoreCase);
                button.IsEnabled = state.CanWrite;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>One row when every name (with its gear and padding) fits; otherwise two rows, still filling the width.</summary>
    private void FitRows()
    {
        var available = (ActualWidth > 0 ? ActualWidth : 330) / Math.Max(1, Children.Count);
        var rows = _widest + 46 <= available ? 1 : 2;
        if (rows == Rows)
        {
            return;
        }

        Rows = rows;
        foreach (FrameworkElement button in Children)
        {
            button.Margin = new Thickness(2, 0, 2, rows > 1 ? 4 : 0);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged)
        {
            Dispatcher.BeginInvoke(FitRows); // not during this layout pass
        }
    }

    private void Build(AppSettings settings)
    {
        Children.Clear();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _widest = settings.Profiles.Max(p => new FormattedText(p.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, Brushes.Black, dpi).Width);
        Rows = 0; // FitRows decides below

        for (var slot = 0; slot < settings.Profiles.Count; slot++)
        {
            var name = settings.Profiles[slot].Name;
            var index = slot;
            var gear = new Button { Style = (Style)FindResource("Lc.GearButton"), Margin = new Thickness(4, 0, -2, 0), ToolTip = $"Edit {name}" };
            gear.Click += (_, e) =>
            {
                e.Handled = true;
                EditRequested?.Invoke(index);
                App.Current.EditProfile(index);
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(gear);

            var button = new RadioButton
            {
                Content = content,
                Style = (Style)FindResource("Lc.Segment"),
                GroupName = "profile-" + GetHashCode().ToString(CultureInfo.InvariantCulture),
                Tag = name,
                Margin = new Thickness(2, 0, 2, 0),
                Padding = new Thickness(6, 5, 4, 5),
            };
            button.Checked += async (_, _) =>
            {
                if (_updating)
                {
                    return;
                }

                await App.Current.Hardware.ApplyProfileAsync(name);
                if (App.Current.Store.Current.FindProfile(name) is { } picked)
                {
                    Picked?.Invoke(picked);
                }
            };
            Children.Add(button);
        }

        FitRows();
    }
}
