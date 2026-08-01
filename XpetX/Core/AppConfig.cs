using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace XpetX;

/// <summary>全局配置（config.json，与 exe 同目录）。</summary>
public sealed class AppConfig
{
    public bool AlwaysOnTop { get; set; } = true;
    public double DecaySpeed { get; set; } = 1.0;
    public double MoveSpeed { get; set; } = 1.0;

    /// <summary>活跃度：越高走动越频繁。</summary>
    public double Activity { get; set; } = 1.0;

    /// <summary>宠物底部距任务栏/屏幕底部的附加偏移（像素，负值=更贴任务栏），用户可自行确认高度。</summary>
    public double TaskbarOffset { get; set; } = 0;

    /// <summary>行走区域：taskbar=沿任务栏横走（默认），screen=全屏自由走动。</summary>
    public string WalkArea { get; set; } = "taskbar";

    /// <summary>走路镜像：无左右动画时反向行走自动水平翻转。</summary>
    public bool WalkMirror { get; set; } = true;

    /// <summary>默认朝向（true=朝右）。</summary>
    public bool FacingRight { get; set; } = true;

    public bool CursorTracking { get; set; } = true;
    public double HeadFollowSpeed { get; set; } = 5.0;
    /// <summary>文件删除方式：recycle=回收站（默认），delete=永久删除（配置文件手动改，启用有警告）。</summary>
    public string DeleteMode { get; set; } = "recycle";

    /// <summary>危险类型强制进食（仅配置文件可改，避免误开启）：true 时 .exe/.dll 等也会被吃掉。</summary>
    public bool AllowDangerousFiles { get; set; }

    /// <summary>自定义图标路径（png/jpg/ico 等；留空用内置宠物脸占位图标）。</summary>
    public string IconPath { get; set; } = "";


    /// <summary>饥饿阈值：饱食度低于该值才会捡地上的食物。</summary>
    public double HungryThreshold { get; set; } = 80;

    public List<string> DislikedExtensions { get; set; } = new List<string>
    {
        ".exe", ".msi", ".bat", ".cmd", ".vbs", ".ps1", ".sh",
    };
}

/// <summary>config.json 的加载、首次自动生成与热加载。</summary>
public static class ConfigManager
{
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Current { get; private set; } = new AppConfig();

    /// <summary>配置热加载成功后触发。</summary>
    public static event Action<AppConfig>? Changed;

    private static FileSystemWatcher? watcher;
    private static DateTime lastReloadUtc = DateTime.MinValue;

    public static void Initialize()
    {
        if (watcher != null) return;
        Reload();
        try
        {
            watcher = new FileSystemWatcher(Path.GetDirectoryName(ConfigPath) ?? ".")
            {
                Filter = Path.GetFileName(ConfigPath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Changed += (_, _) => ScheduleReload();
            watcher.Created += (_, _) => ScheduleReload();
        }
        catch
        {
            watcher = null; // 监听失败不影响功能。
        }
    }

    public static void Stop()
    {
        watcher?.Dispose();
        watcher = null;
    }

    private static void ScheduleReload()
    {
        // 防抖：文件写入会触发多次事件。
        if ((DateTime.UtcNow - lastReloadUtc).TotalMilliseconds < 300) return;
        lastReloadUtc = DateTime.UtcNow;
        Reload();
    }

    private static void Reload()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                Save();
                return;
            }
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
            if (config == null) return;
            Current = config;
            Changed?.Invoke(Current);
        }
        catch
        {
            // 解析失败时保留旧配置，不中断运行。
        }
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(new AppConfig(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}