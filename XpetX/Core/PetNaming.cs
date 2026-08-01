using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace XpetX;

/// <summary>宠物命名规则：文件夹名仅限英文/数字/_-；真实名称读取包内 name.json。</summary>
public static class PetNaming
{
    private static readonly Regex FolderNamePattern = new Regex("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    /// <summary>文件夹名是否合法（仅英文、数字、_、-）。</summary>
    public static bool IsValidFolderName(string name)
    {
        return !string.IsNullOrEmpty(name) && FolderNamePattern.IsMatch(name);
    }

    /// <summary>
    /// 读取宠物包的注册名称文件 name.json，返回本地化显示名；
    /// 无文件或解析失败时回退文件夹名。
    /// </summary>
    public static string GetDisplayName(string petDirectory)
    {
        try
        {
            string file = Path.Combine(petDirectory, "name.json");
            if (!File.Exists(file)) return Path.GetFileName(petDirectory);
            var entries = JsonSerializer.Deserialize<List<NameEntry>>(File.ReadAllText(file), NameJsonOptions);
            var entry = entries?.FirstOrDefault();
            if (entry == null) return Path.GetFileName(petDirectory);

            string lang = PickLanguage();
            string? value = lang switch
            {
                "CN" => entry.CN,
                "EN" => entry.EN,
                "JP" => entry.JP,
                "KR" => entry.KR,
                "TW" => entry.TW,
                _ => entry.EN,
            };
            if (!string.IsNullOrWhiteSpace(value)) return value;
            if (!string.IsNullOrWhiteSpace(entry.Reg)) return entry.Reg;
        }
        catch
        {
        }
        return Path.GetFileName(petDirectory);
    }

    private static string PickLanguage()
    {
        string name = CultureInfo.CurrentUICulture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return name.Contains("TW") || name.Contains("HK") || name.Contains("MO") ? "TW" : "CN";
        if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "JP";
        if (name.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return "KR";
        return "EN";
    }

    private static readonly JsonSerializerOptions NameJsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class NameEntry
    {
        [JsonPropertyName("reg")] public string Reg { get; set; } = "";
        public string CN { get; set; } = "";
        public string EN { get; set; } = "";
        public string JP { get; set; } = "";
        public string KR { get; set; } = "";
        public string TW { get; set; } = "";
    }
}