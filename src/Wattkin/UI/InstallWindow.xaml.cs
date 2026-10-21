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
            Title = Strings.Install_UpdateTitle;
            Headline.Text = string.Format(Strings.Install_UpdateHeadline, Installation.InstalledVersion!.ToString(3), version);
            Body.Text = Strings.Install_UpdateBody;
            Autostart.Visibility = Visibility.Collapsed;
            InstallButton.Content = Strings.Install_Update;
        }
        else
        {
            Headline.Text = string.Format(Strings.Install_Headline, version);
            Body.Text = Strings.Install_Body;
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

        ErrorText.Text = code is null ? Strings.Install_NeedsAdmin : string.Format(Strings.Install_Failed, code);
        ErrorText.Visibility = Visibility.Visible;
        InstallButton.IsEnabled = true;
    }
}
