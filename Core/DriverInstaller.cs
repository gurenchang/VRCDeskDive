using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VRCDeskDive.Core;

/// <summary>SteamVR へのドライバー登録（vrpathreg adddriver / removedriver）。</summary>
public static class DriverInstaller
{
    private static readonly string VrPathFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");

    /// <summary>アプリに同梱されたドライバーのフォルダ（driver\build.ps1 の出力）。</summary>
    public static string DriverDirectory => Path.Combine(AppContext.BaseDirectory, "driver", "vrcdeskdive");

    public static bool IsBundled => File.Exists(Path.Combine(DriverDirectory, "bin", "win64", "driver_vrcdeskdive.dll"));

    public static bool IsRegistered()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(VrPathFile));
            if (!doc.RootElement.TryGetProperty("external_drivers", out var drivers) || drivers.ValueKind != JsonValueKind.Array)
                return false;
            var target = Normalize(DriverDirectory);
            return drivers.EnumerateArray().Any(d => d.GetString() is { } p && Normalize(p) == target);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool Register() => RunVrPathReg("adddriver");

    public static bool Unregister() => RunVrPathReg("removedriver");

    private static bool RunVrPathReg(string command)
    {
        var exe = FindVrPathReg();
        if (exe is null) return false;
        using var p = Process.Start(new ProcessStartInfo(exe, $"{command} \"{DriverDirectory}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        p?.WaitForExit(10000);
        return p is { ExitCode: 0 };
    }

    private static string? FindVrPathReg()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(VrPathFile));
            foreach (var runtime in doc.RootElement.GetProperty("runtime").EnumerateArray())
            {
                var exe = Path.Combine(runtime.GetString() ?? "", "bin", "win64", "vrpathreg.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
}
