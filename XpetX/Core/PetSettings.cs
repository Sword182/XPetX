using System;
using System.IO;
using System.Text.Json;

namespace XpetX;

/// <summary>桌宠性能模式：当桌宠与前台应用（如游戏）争抢 CPU 时如何取舍。</summary>
public enum PetPerformanceMode
{
    /// <summary>优先当前任务：桌宠没有焦点时降为最低占用，把资源让给游戏。</summary>
    FocusPriority,

    /// <summary>优先桌宠：始终满分辨率渲染，可接受掉帧。</summary>
    PetPriority,

    /// <summary>两者均衡：根据实测帧耗时自适应分辨率。</summary>
    Balanced,

    /// <summary>自动判定：前台全屏/沉浸时按 FocusPriority，否则按 Balanced。</summary>
    Auto,
}

/// <summary>性能模式的读写配置（pet.config.json，与 exe 同目录）。</summary>
public static class PetSettings
{
    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "pet.config.json");

    public static PetPerformanceMode LoadMode()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return PetPerformanceMode.Auto;
            var data = JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(ConfigPath));
            return data?.Mode ?? PetPerformanceMode.Auto;
        }
        catch
        {
            return PetPerformanceMode.Auto;
        }
    }

    public static void SaveMode(PetPerformanceMode mode)
    {
        try
        {
            var data = LoadConfig() ?? new ConfigData();
            data.Mode = mode;
            WriteConfig(data);
        }
        catch
        {
            // 保存失败不影响运行。
        }
    }

    public static bool LoadClickThrough()
    {
        return LoadConfig()?.ClickThrough ?? false;
    }

    public static void SaveClickThrough(bool clickThrough)
    {
        try
        {
            var data = LoadConfig() ?? new ConfigData();
            data.ClickThrough = clickThrough;
            WriteConfig(data);
        }
        catch
        {
        }
    }

    public static bool LoadHideInFullscreen()
    {
        return LoadConfig()?.HideInFullscreen ?? false;
    }

    public static void SaveHideInFullscreen(bool hide)
    {
        try
        {
            var data = LoadConfig() ?? new ConfigData();
            data.HideInFullscreen = hide;
            WriteConfig(data);
        }
        catch
        {
            // 保存失败不影响运行。
        }
    }

    private static ConfigData? LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            return JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(ConfigPath));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteConfig(ConfigData data)
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(data));
    }

    private sealed class ConfigData
    {
        public PetPerformanceMode Mode { get; set; } = PetPerformanceMode.Auto;

        public bool HideInFullscreen { get; set; }

        public bool ClickThrough { get; set; }
    }
}