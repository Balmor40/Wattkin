using System.Windows;
using System.Windows.Interop;
using Wattkin.Platform;

namespace Wattkin.UI;

/// <summary>Shown when Wattkin.exe runs from outside Program Files: installs it, or updates the installed copy.</summary>
public partial class InstallWindow : Window
{
    private readonly bool _update;

    public InstallWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.SetDarkTitleBar(new WindowInteropHelper(this).Handle);
        var version = Installation.CurrentVersion.ToString(3);
        _update = Installation.InstalledVersion is { } installed;
        if (_update)
        {
            Title = "Update Wattkin";
            Headline.Text = $"Update Wattkin {Installation.InstalledVersion!.ToString(3)} to {version}?";
            Body.Text = "Your settings are kept. Wattkin closes for a moment and starts again.";
            Autostart.Visibility = Visibility.Collapsed;
            InstallButton.Content = "Update";
        }
        else
        {
            Headline.Text = $"Install Wattkin {version}?";
            Body.Text = "Wattkin installs into Program Files and asks for administrator rights once, so it can control the laptop's battery, fans and keyboard. It keeps itself up to date, and you can uninstall it any time from Settings › Apps.";
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        var arguments = "--install" + (_update ? " --keep-autostart" : Autostart.IsChecked == true ? "" : " --no-autostart");
        var code = await Task.Run(() => Elevation.RunElevatedAndWait(arguments));
        if (code == 0)
        {
            Close();
            return;
        }

        ErrorText.Text = code is null
            ? "Wattkin needs administrator rights to install. Click Install and choose Yes."
            : $"Installing didn't finish (code {code}). The log in %LOCALAPPDATA%\\Wattkin\\logs has the details.";
        ErrorText.Visibility = Visibility.Visible;
        InstallButton.IsEnabled = true;
    }
}
