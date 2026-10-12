using System.Text;
using System.Threading;

namespace Remake;

/// <summary>保留 [Global] 与 [Keys] 的一次完整配置读取结果。</summary>
internal sealed class SettingsSnapshot(
    IReadOnlyDictionary<string, string> global,
    IReadOnlyDictionary<string, string> keys)
{
    public IReadOnlyDictionary<string, string> Global { get; } = global;
    public IReadOnlyDictionary<string, string> Keys { get; } = keys;
}

/// <summary>读取 CapsLock+settings.ini，并把有效键位表原子切换给 hook。</summary>
internal sealed class SettingsManager : IDisposable
{
    private const int ReloadDebounceMilliseconds = 100;
    private const string SettingsFileName = "CapsLock+settings.ini";
    private const string DefaultSettings = ";------------ Encoding: UTF-16 ------------\r\n[Global]\r\nallowClipboard=1\r\n\r\n[Keys]\r\n";

    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
    private readonly object _reloadLock = new();
    private readonly Timer _reloadTimer;
    private FileSystemWatcher? _watcher;
    private SettingsSnapshot _current = EmptySnapshot();
    private bool _disposed;

    public SettingsManager()
    {
        _reloadTimer = new Timer(ReloadAfterChange, null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            EnsureSettingsFile();
            _current = ReadSettings();
            DefaultKeyBindings.ReplaceOverrides(_current.Keys);
            StartWatching();
            AppLog.Info($"运行时设置已载入：{_settingsPath}。");
        }
        catch (Exception ex)
        {
            // 设置文件不可用时使用内置默认键位，不能让启动或 watchdog 循环失败。
            AppLog.Error("设置初始化失败，继续使用默认键位", ex);
            DefaultKeyBindings.ReplaceOverrides(EmptySnapshot().Keys);
        }
    }

    public SettingsSnapshot Current => Volatile.Read(ref _current);

    private void EnsureSettingsFile()
    {
        if (File.Exists(_settingsPath))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, DefaultSettings, Encoding.Unicode);
    }

    private void StartWatching()
    {
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(_settingsPath)!, SettingsFileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        watcher.Changed += OnSettingsChanged;
        watcher.Created += OnSettingsChanged;
        watcher.Deleted += OnSettingsChanged;
        watcher.Renamed += OnSettingsRenamed;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    private void OnSettingsChanged(object sender, FileSystemEventArgs args) => ScheduleReload();

    private void OnSettingsRenamed(object sender, RenamedEventArgs args) => ScheduleReload();

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        AppLog.Error("设置文件 watcher 出错", args.GetException());
        ScheduleReload();
    }

    private void ScheduleReload()
    {
        if (Volatile.Read(ref _disposed))
            return;

        try
        {
            _reloadTimer.Change(ReloadDebounceMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 关闭时到达的最后一个 watcher 通知无需再排队。
        }
    }

    private void ReloadAfterChange(object? state)
    {
        lock (_reloadLock)
        {
            if (_disposed)
                return;

            if (!File.Exists(_settingsPath))
            {
                AppLog.Warn("设置文件暂时不存在，保留上一次有效配置。");
                return;
            }

            try
            {
                SettingsSnapshot next = ReadSettings();
                DefaultKeyBindings.ReplaceOverrides(next.Keys);
                Volatile.Write(ref _current, next);
                AppLog.Info($"运行时设置热重载完成：Keys 覆盖数={next.Keys.Count}。");
            }
            catch (Exception ex)
            {
                // 半写入或无效文件保留上一次快照，下一次文件事件可再次触发读取。
                AppLog.Warn($"设置热重载失败，保留上一次配置：{ex.Message}");
            }
        }
    }

    private SettingsSnapshot ReadSettings()
    {
        string content = File.ReadAllText(_settingsPath);
        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? section = null;

        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } rawLine)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (key.Length == 0)
                continue;

            if (string.Equals(section, "Global", StringComparison.OrdinalIgnoreCase))
                global[key] = value;
            else if (string.Equals(section, "Keys", StringComparison.OrdinalIgnoreCase) &&
                     value.StartsWith("keyFunc_", StringComparison.OrdinalIgnoreCase))
                keys[DefaultKeyBindings.NormalizeSettingKey(key)] = value;
        }

        return new SettingsSnapshot(global, keys);
    }

    private static SettingsSnapshot EmptySnapshot() => new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public void Dispose()
    {
        lock (_reloadLock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _watcher?.Dispose();
        _reloadTimer.Dispose();
    }
}
