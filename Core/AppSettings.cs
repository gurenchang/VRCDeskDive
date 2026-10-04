using System;
using System.IO;
using System.Text.Json;

namespace VRCDeskDive.Core;

public sealed class AppSettings
{
    public string ToggleHotkey { get; set; } = "Ctrl+F12";
    public double MouseSensitivity { get; set; } = 1.0;
    public double KeyTurnSpeed { get; set; } = 0.6;
    public double PitchSensitivity { get; set; } = 1.0;
    public bool InvertPitch { get; set; }
    public bool LockViewPosition { get; set; } = true;
    public bool OnlyWhenVrcFocused { get; set; } = true;
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
