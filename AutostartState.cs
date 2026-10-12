using System.Text;

namespace Remake;

/// <summary>让周期恢复任务尊重托盘“退出”，并在下一次登录时重新启用。</summary>
internal static class AutostartState
{
    private static readonly string DisabledMarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CapsLockPlus",
        "autostart.disabled");

    public static bool IsDisabled => File.Exists(DisabledMarkerPath);

    public static void AllowAutomaticStart()
    {
        try
        {
            if (File.Exists(DisabledMarkerPath))
                File.Delete(DisabledMarkerPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"无法清除自启停用标记：{ex.Message}");
        }
    }

    public static void DisableUntilNextLogon()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DisabledMarkerPath)!);
            File.WriteAllText(DisabledMarkerPath, DateTimeOffset.UtcNow.ToString("O"), new UTF8Encoding(false));
            AppLog.Info("用户选择退出；自动恢复暂停到下一次登录或手动启动。");
        }
        catch (Exception ex)
        {
            AppLog.Error("无法写入自启停用标记；恢复任务可能再次启动程序", ex);
        }
    }
}
