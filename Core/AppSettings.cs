using System;
using System.IO;
using System.Text.Json;

namespace VRCDeskDive.Core;

public sealed class AppSettings
{
    /// <summary>マウス感度（左右・上下共通）。</summary>
    public double MouseSensitivity { get; set; } = 1.0;
    /// <summary>VRChat を左クリックするとマウスルック（MouseLookReleaseKey で解除）。</summary>
    public bool ClickToMouseLook { get; set; } = true;
    /// <summary>マウスルックを解除するキー（"Alt" / "Tab" / "Ctrl"）。Esc は VRChat のメニューに使うので選ばない。</summary>
    public string MouseLookReleaseKey { get; set; } = "Alt";
    public bool CompactMode { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool PlaySound { get; set; } = true;
    public string OscHost { get; set; } = "127.0.0.1";
    public int OscPort { get; set; } = 9000;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCDeskDive", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // 壊れた設定ファイルは無視して既定値で起動する
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
        }
    }
}
