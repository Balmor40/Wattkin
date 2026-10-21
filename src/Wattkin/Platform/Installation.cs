using System.Diagnostics;
using Microsoft.Win32;
using Wattkin.Features;
using Wattkin.Settings;

namespace Wattkin.Platform;

/// <summary>
/// Wattkin installs itself: one exe is the installer and the app. Installed means
/// %ProgramFiles%\Wattkin\Wattkin.exe (only administrators can change it, which matters because it
/// runs as administrator at logon), a Start-menu shortcut, the logon task, and an entry in
/// Settings › Apps whose uninstall runs "Wattkin.exe --uninstall". A newer build is installed by
/// running it (or install.ps1): this fork never updates itself.
/// </summary>
internal static class Installation
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Wattkin";
    private const string RepositoryUrl = "https://github.com/Balmor40/Wattkin";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wattkin");
    public static string Exe => Path.Combine(Folder, "Wattkin.exe");
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Wattkin.lnk");

    /// <summary>Per-user shortcut made by the old install.ps1.</summary>
    private static string LegacyShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Wattkin.lnk");

    public static bool IsInstalled => File.Exists(Exe);

    public static bool RunningInstalled => string.Equals(Environment.ProcessPath, Exe, StringComparison.OrdinalIgnoreCase);

    public static Version? InstalledVersion =>
        IsInstalled && AppVersion.TryParse(FileVersionInfo.GetVersionInfo(Exe).ProductVersion, out var v) ? v : null;

    /// <summary>
    /// Elevated. Copies this exe into Program Files and registers it. <paramref name="startWithWindows"/>
    /// null leaves the logon task as it is (an update). Returns an error message or null.
    /// </summary>
    public static string? Install(bool? startWithWindows)
    {
        try
        {
            StopRunningCopy();
            Directory.CreateDirectory(Folder);
            var source = Environment.ProcessPath!;
            if (!string.Equals(source, Exe, StringComparison.OrdinalIgnoreCase))
            {
                var staged = Exe + ".new";
                File.Copy(source, staged, overwrite: true);
                if (Swap(staged) is { } swapError)
                {
                    return swapError;
                }
            }

            CreateShortcut();
            Register();
            if (startWithWindows == true && StartupTask.Enable(Exe) is { } taskError)
            {
                Log.Warn(taskError);
            }

            // How Windows reaches MSI's firmware interface without any MSI software (also checked at every start).
            if (global::Wattkin.Hardware.WmiSchema.EnsureRegistered() is { } schemaError)
            {
                Log.Warn(schemaError);
            }

            Log.Info($"Installed Wattkin {CurrentVersion.ToString(3)} to {Folder}.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Install failed", ex);
            return string.Format(Strings.Install_CouldNotInstall, ex.Message);
        }
    }

    /// <summary>
    /// Elevated. Puts back what Wattkin changed (Windows power settings, a disabled NVIDIA GPU, the
    /// charge limit and the fans) and removes it, including its WMI classes.
    /// </summary>
    public static void Uninstall(bool removeData)
    {
        StopRunningCopy();
        Step("restore Windows settings", () =>
        {
            new SettingsStore().Update(WindowsPower.RestoreOriginals);
            global::Wattkin.Hardware.DiscreteGpu.Enable();
        });
        Step("laptop controller", ResetHardware);
        Step("power limits", RestorePowerLimits);
        Step("WMI classes", global::Wattkin.Hardware.WmiSchema.Remove); // after the controller, which needs them
        Step("logon task", () => StartupTask.Disable());
        Step("shortcuts", () =>
        {
            File.Delete(Shortcut);
            File.Delete(LegacyShortcut);
        });
        Step("Apps entry", () => Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false));
        if (removeData)
        {
            Step("settings and logs", () =>
            {
                foreach (var dir in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData }.Select(f => Path.Combine(Environment.GetFolderPath(f), "Wattkin")))
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
            });
        }

        Step("program files", () =>
        {
            if (RunningInstalled)
            {
                // This exe is the one being removed: delete the folder just after it exits.
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{Folder}\"") { CreateNoWindow = true, UseShellExecute = false });
            }
            else if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        });
    }

    /// <summary>
    /// The processor's power limits back to the BIOS's (PawnIO), by the app's own rules: only when the
    /// register still holds what Wattkin wrote in this session; otherwise it already holds the firmware's.
    /// </summary>
    private static void RestorePowerLimits()
    {
        using var cpu = global::Wattkin.Hardware.CpuPowerControl.Open(out _);
        if (cpu is not null)
        {
            HardwareService.ReadOnlyPowerLimitKeeper(cpu, new SettingsStore(readOnly: true).Current).RestoreBios("the BIOS's, on uninstall");
        }
    }

    /// <summary>
    /// Leaves the laptop as it came: charging to 100%, not running from battery on the charger, and the
    /// fans on Auto with the factory fan table back in the controller.
    /// </summary>
    private static void ResetHardware()
    {
        using var acpi = global::Wattkin.Hardware.MsiAcpi.TryOpen(out _);
        if (acpi is null)
        {
            return;
        }

        var ec = new global::Wattkin.Hardware.EmbeddedController(acpi);
        if (!ec.CanWrite)
        {
            return;
        }

        ec.SetSimulatedUnplug(false);
        ec.SetChargeLimit(100);
        ec.SetCoolerBoost(false);
        var settings = new SettingsStore(readOnly: true).Current;
        if (settings.FactoryFanTable is { } table && ec.HasFullFanTable)
        {
            ec.WriteFullFanTable(table, unrestricted: true);
        }
        else if (settings.FactoryFanCurve is { } factory && ec.HasFanCurves)
        {
            ec.WriteFanTable(factory);
        }

        ec.SetFanMode(global::Wattkin.Hardware.FanMode.Auto);
    }

    /// <summary>At startup of the installed copy: tidy up after an install over a running copy and keep the Apps entry current.</summary>
    public static void AfterStart()
    {
        if (!RunningInstalled || !Elevation.IsElevated)
        {
            return;
        }

        TryDelete(Exe + ".old");
        TryDelete(Exe + ".new");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
            if (key?.GetValue("DisplayVersion") as string != CurrentVersion.ToString(3))
            {
                Register();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Couldn't update the Apps entry: {ex.Message}");
        }
    }

    public static Version CurrentVersion => AppVersion.Normalize(typeof(Installation).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Running exes can be renamed but not overwritten: move the old one aside, then the new one in.</summary>
    private static string? Swap(string staged)
    {
        var old = Exe + ".old";
        TryDelete(old);
        var hadOld = File.Exists(Exe);
        if (hadOld)
        {
            File.Move(Exe, old);
        }

        try
        {
            File.Move(staged, Exe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (hadOld)
            {
                File.Move(old, Exe, overwrite: true);
            }

            return string.Format(Strings.Install_CouldNotReplace, ex.Message);
        }
    }

    /// <summary>Asks a running copy to exit cleanly (it releases the hardware first), then makes sure it has.</summary>
    public static void StopRunningCopy()
    {
        // Only the installed copy: the window that launched this installer is a "Wattkin" too.
        var self = Environment.ProcessId;
        Process[] Others() => [.. Process.GetProcessesByName("Wattkin").Where(p => p.Id != self && IsInstalledCopy(p))];
        if (Others().Length == 0)
        {
            return;
        }

        SingleInstance.RequestExit(App.InstanceName);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Others().Length > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
        }

        foreach (var p in Others())
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private static bool IsInstalledCopy(Process process)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // exited, or not ours to inspect
        }
    }

    private static void CreateShortcut()
    {
        if (Type.GetTypeFromProgID("WScript.Shell") is not { } type)
        {
            return;
        }

        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            var link = shell.CreateShortcut(Shortcut);
            link.TargetPath = Exe;
            link.WorkingDirectory = Folder;
            link.Description = "Laptop power, battery and hardware control";
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }

        TryDelete(LegacyShortcut);
    }

    private static void Register()
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Wattkin");
        key.SetValue("DisplayVersion", CurrentVersion.ToString(3));
        key.SetValue("Publisher", "Ethan Copping");
        key.SetValue("DisplayIcon", Exe);
        key.SetValue("InstallLocation", Folder);
        key.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{Exe}\" --uninstall --quiet");
        key.SetValue("URLInfoAbout", RepositoryUrl);
        key.SetValue("HelpLink", RepositoryUrl + "/issues");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
    }

    private static void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"Uninstall step '{name}' failed", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
