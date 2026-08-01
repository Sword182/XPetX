using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace XpetX;

/// <summary>宠物目录与多实例管理。</summary>
public static class PetManager
{
    private static readonly List<PetWindow> Windows = new List<PetWindow>();

    /// <summary>管理模式：开启后左键点击宠物副本即可删除。</summary>
    public static bool ManagementMode { get; set; }

    /// <summary>关闭所有宠物副本（保留主宠物窗口）。</summary>
    public static void CloseAllCopies()
    {
        foreach (PetWindow window in Windows.ToList())
        {
            try
            {
                window.Close();
            }
            catch
            {
            }
        }
    }

    /// <summary>当前桌面上的宠物窗口数量。</summary>
    public static int WindowCount { get { return Windows.Count; } }

    /// <summary>宠物根目录（运行时素材副本所在）。</summary>
    public static string PetsRoot => Path.Combine(AppContext.BaseDirectory, "pets");

    public static IReadOnlyList<string> GetPetDirectories()
    {
        try
        {
            if (!Directory.Exists(PetsRoot)) return Array.Empty<string>();
            return Directory.GetDirectories(PetsRoot)
                .Where(d => PetNaming.IsValidFolderName(Path.GetFileName(d)))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>从外部文件夹导入一个新 pet（复制到 pets 目录），返回目标路径。</summary>
    public static string? AddPetFolder(string sourceFolder)
    {
        try
        {
            if (!Directory.Exists(sourceFolder)) return null;
            string name = Path.GetFileName(sourceFolder.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) return null;
            Directory.CreateDirectory(PetsRoot);
            string dest = Path.Combine(PetsRoot, name);
            int i = 2;
            while (Directory.Exists(dest)) dest = Path.Combine(PetsRoot, $"{name}-{i++}");
            CopyDirectory(sourceFolder, dest);
            return dest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把一只宠物放到桌面（新窗口）。</summary>
    public static void SpawnPet(string petDirectory)
    {
        if (!Directory.Exists(petDirectory)) return;
        var window = new PetWindow(petDirectory);
        Windows.Add(window);
        window.Closed += (_, _) => Windows.Remove(window);
        window.Show();
    }

    public static void OpenPetsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{PetsRoot}\"") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        }
        foreach (string dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}