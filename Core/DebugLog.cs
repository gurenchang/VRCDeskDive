using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace VRCDeskDive.Core;

/// <summary>
/// 不具合調査用のログ（%APPDATA%\VRCDeskDive\debug.log、起動ごとに作り直す）。
/// 入力フックから呼んでも遅くならないよう、Write はキューに積むだけで、Flush で書き出す。
/// </summary>
public static class DebugLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCDeskDive", "debug.log");

    private static readonly ConcurrentQueue<string> Pending = new();

    static DebugLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, string.Empty);
        }
        catch (Exception)
        {
        }
    }

    public static void Write(string message) =>
        Pending.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {message}");

    public static void Flush()
    {
        if (Pending.IsEmpty) return;
        var sb = new StringBuilder();
        while (Pending.TryDequeue(out var line)) sb.AppendLine(line);
        try
        {
            File.AppendAllText(FilePath, sb.ToString());
        }
        catch (Exception)
        {
        }
    }
}
