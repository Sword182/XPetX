using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic.FileIO;

namespace XpetX;

public enum DroppedFileState
{
    /// <summary>下落动画中。</summary>
    Falling,
    /// <summary>已落地，等待宠物（饿了才会来吃）。</summary>
    Waiting,
    /// <summary>直接喂食后宠物"注意到"，站定等待片刻再开吃。</summary>
    Noticed,
    /// <summary>正在被吃。</summary>
    BeingEaten,
    /// <summary>已消失（被吃掉或被 Windows 小人收走）。</summary>
    Removed,
}

/// <summary>掉落在宠物附近的一个文件。</summary>
public sealed class DroppedFile
{
    public string Path { get; }
    public string Extension { get; }
    public bool Edible { get; set; }
    public double ScreenX { get; set; }
    public double ScreenY { get; set; }
    public double GroundY { get; set; }
    public bool DirectlyToPet { get; set; }
    public DroppedFileState State { get; set; } = DroppedFileState.Falling;
    public BitmapSource? Icon { get; set; }
    public double Timer { get; set; }


    /// <summary>进食动画缩放（缩小两次）。</summary>
    public double EatScale { get; set; } = 1.0;

    /// <summary>进食动画透明度。</summary>
    public double EatOpacity { get; set; } = 1.0;

    public DroppedFile(string path)
    {
        Path = path;
        Extension = System.IO.Path.GetExtension(path)?.ToLowerInvariant() ?? "";
    }
}

/// <summary>
/// 文件喂食管理：接收拖放、下落动画、地面判定、进食（删除）、
/// 类型偏好记录与 Windows 小人收走不可吃文件。
/// </summary>
public sealed class PetFileManager
{
    private readonly PetInstance owner;
    private readonly Random random = new Random();
    private readonly List<DroppedFile> files = new List<DroppedFile>();

    public IReadOnlyList<DroppedFile> Files { get { return files; } }

    /// <summary>文件状态变化时通知 UI 刷新图标层。</summary>
    public event Action<DroppedFile>? FileChanged;

    public PetFileManager(PetInstance owner)
    {
        this.owner = owner;
    }

    /// <summary>接收一个拖放的文件。</summary>
    public void DropFile(string path, Point screenDrop, bool directlyToPet)
    {
        try
        {
            bool folder = Directory.Exists(path);
            bool dangerous = !folder
                && ConfigManager.Current.DislikedExtensions.Contains(System.IO.Path.GetExtension(path)?.ToLowerInvariant() ?? "")
                && !ConfigManager.Current.AllowDangerousFiles;

            var file = new DroppedFile(path)
            {
                Edible = !folder && !dangerous,
                DirectlyToPet = directlyToPet,
                ScreenX = screenDrop.X,
                ScreenY = screenDrop.Y,
                Icon = FileIconCache.GetIcon(path),
            };
            file.GroundY = ComputeGroundY(file.ScreenX);

            // 直接喂：图标从出现起就直接在嘴边（不经过拖放点、无飞行）。
            if (file.DirectlyToPet && file.Edible)
            {
                Point mouth = GetMouthPosition();
                file.ScreenX = mouth.X;
                file.ScreenY = mouth.Y;
            }

            files.Add(file);
            FileChanged?.Invoke(file);

            if (file.DirectlyToPet && file.Edible)
            {
                // 宠物先"注意到"——停止走动、站定片刻，再开始吃（图标一直在嘴边）。
                file.State = DroppedFileState.Noticed;
                file.Timer = 0;
                owner.AI.IsEating = true;
                owner.PlayAnimation("Default", true);
                FileChanged?.Invoke(file);
            }
            else if (!file.Edible)
            {
                // 不能吃（危险类型/文件夹）：稍后 Windows 小人收走，文件不删除。
                file.Timer = 0;
            }
            // 可吃但掉在地上：等宠物饿了来捡；饱了就一直躺着。
        }
        catch
        {
        }
    }

    /// <summary>每帧推进下落动画与小人计时。</summary>
    public void Update(float delta)
    {
        for (int i = files.Count - 1; i >= 0; i--)
        {
            var f = files[i];
            if (f.State == DroppedFileState.Falling)
            {
                f.Timer += delta;
                double t = Math.Min(1.0, f.Timer / 0.6);
                f.ScreenY = f.ScreenY + (f.GroundY - f.ScreenY) * t;
                if (t >= 1.0)
                {
                    f.State = DroppedFileState.Waiting;
                    f.ScreenY = f.GroundY;
                    FileChanged?.Invoke(f);
                }
            }
            else if (!f.Edible && f.State == DroppedFileState.Waiting)
            {
                f.Timer += delta;
                if (f.Timer >= 1.4)
                {
                    // Windows 小人收走：图标消失，文件本身不删除。
                    f.State = DroppedFileState.Removed;
                    files.RemoveAt(i);
                    FileChanged?.Invoke(f);
                }
            }
            else if (f.State == DroppedFileState.Noticed)
            {
                // 等一小会儿（0.8s）让宠物"反应"过来，再开始吃。
                f.Timer += delta;
                if (f.Timer >= 0.8)
                {
                    EatFile(f);
                }
            }
            else if (f.State == DroppedFileState.BeingEaten)
            {
                f.Timer += delta;
                UpdateEatAnimation(f);
                if (f.Timer >= 1.0)
                {
                    f.State = DroppedFileState.Removed;
                    files.RemoveAt(i);
                    owner.AI.IsEating = false;
                    owner.AI.NotifyHappy(); // 吃完开心一下
                    FileChanged?.Invoke(f);
                }
            }
        }
    }

    /// <summary>AI 用：寻找最近的可吃落地文件（饥饿时）。</summary>
    public bool TryGetNearestWaiting(double petScreenX, double hungryThreshold, out DroppedFile? target, out double distance)
    {
        target = null;
        distance = double.MaxValue;
        if (owner.Stats.Hunger >= hungryThreshold) return false;
        foreach (var f in files)
        {
            if (f.State != DroppedFileState.Waiting || !f.Edible) continue;
            double d = Math.Abs(f.ScreenX - petScreenX);
            if (d < distance)
            {
                distance = d;
                target = f;
            }
        }
        return target != null;
    }

    /// <summary>进食：改数值、记录类型偏好、删除文件（回收站/永久）。</summary>
    public void EatFile(DroppedFile file)
    {
        if (file.State == DroppedFileState.Removed || file.State == DroppedFileState.BeingEaten) return;
        if (!file.Edible) return; // 安全兜底：不可吃绝不删除

        file.State = DroppedFileState.BeingEaten;
        file.Timer = 0;
        file.EatScale = 1.0;
        file.EatOpacity = 1.0;
        // 没有飞行动画：图标直接出现在嘴边。
        Point mouth = GetMouthPosition();
        file.ScreenX = mouth.X;
        file.ScreenY = mouth.Y;

        // 数值与删除在开吃时生效；图标随后播放"飞到嘴边→缩小两次→消失"动画。
        float preference = GetOrCreatePreference(file.Extension);
        float gain = (file.DirectlyToPet ? 12f : 6f) * preference;
        owner.Stats.Eat(gain);
        DeleteFile(file.Path);

        // 进食期间人物站定不动，用静态姿势。
        owner.AI.IsEating = true;
        owner.PlayAnimation("Default", true);
        FileChanged?.Invoke(file);
    }

    /// <summary>第一次吃某类型时记录喜爱倾向（0.4~1.0），之后同类型按此加成。</summary>
    private float GetOrCreatePreference(string extension)
    {
        if (owner.FoodPreferences.TryGetValue(extension, out float existing)) return existing;
        float preference = 0.4f + (float)(random.NextDouble() * 0.6);
        owner.FoodPreferences[extension] = preference;
        return preference;
    }

    private double ComputeGroundY(double screenX)
    {
        double ground = SystemParameters.WorkArea.Bottom;
        try
        {
            ground = WindowFocus.FindGroundY(
                owner.WindowHandle,
                screenX,
                owner.Y,
                owner.Y + owner.WindowHeight);
        }
        catch
        {
        }
        // 保证落点可见：不高于宠物顶部、不低于宠物脚底附近（宠物被拖高时图标不会落到窗外）。
        double top = owner.Y + 8;
        double feet = owner.Y + owner.WindowHeight - 4;
        if (ground < top) ground = top;
        if (ground > feet) ground = feet;
        return ground;
    }

    /// <summary>
    /// 进食动画：图标在嘴边直接大→中→小硬切，无飞行过程、无过渡帧。
    /// </summary>
    private void UpdateEatAnimation(DroppedFile f)
    {
        double t = f.Timer;
        if (t < 0.30)
        {
            f.EatScale = 1.0; // 大
        }
        else if (t < 0.55)
        {
            f.EatScale = 0.6; // 中（硬切）
        }
        else
        {
            f.EatScale = 0.3; // 小（硬切）
        }
    }

    /// <summary>
    /// 嘴部位置：暂用头部骨骼下方偏移；异形生物的嘴如何定义后续再补。
    /// </summary>
    private Point GetMouthPosition()
    {
        return new Point(
            owner.X + (owner.Renderer?.HeadScreenX ?? 0) + 10,
            owner.Y + (owner.Renderer?.HeadScreenY ?? 0) + 26);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            bool permanent = string.Equals(ConfigManager.Current.DeleteMode, "delete", StringComparison.OrdinalIgnoreCase);
            if (permanent)
            {
                File.Delete(path);
            }
            else
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
        }
        catch
        {
            // 删除失败不崩溃。
        }
    }
}

/// <summary>文件类型图标缓存（按扩展名）。</summary>
internal static class FileIconCache
{
    private static readonly Dictionary<string, BitmapSource?> Cache = new Dictionary<string, BitmapSource?>();
    private static readonly System.Collections.Generic.HashSet<string> ImageExtensions =
        new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".ico",
        };

    public static BitmapSource? GetIcon(string path)
    {
        try
        {
            string ext = System.IO.Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            // 图片文件按路径缓存（内容各不相同）；其他类型按扩展名缓存。
            string key = ImageExtensions.Contains(ext) ? path : ext;
            if (Cache.TryGetValue(key, out BitmapSource? cached)) return cached;

            BitmapSource? icon = null;
            if (ImageExtensions.Contains(ext))
            {
                // 图片文件直接显示图片内容（缩略图）。
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.DecodePixelWidth = 128;
                    bmp.EndInit();
                    bmp.Freeze();
                    icon = bmp;
                }
                catch
                {
                    icon = null;
                }
            }
            if (icon == null)
            {
                try
                {
                    using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (sysIcon != null)
                    {
                        using var bmp = sysIcon.ToBitmap();
                        icon = FromHbitmap(bmp);
                    }
                }
                catch
                {
                    icon = null;
                }
                if (icon == null) icon = DrawGenericIcon();
            }
            Cache[key] = icon;
            return icon;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? FromHbitmap(System.Drawing.Bitmap bmp)
    {
        IntPtr hbmp = bmp.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hbmp);
        }
    }

    private static BitmapSource? DrawGenericIcon()
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Transparent);
                using var pageBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 90, 160, 240));
                g.FillRectangle(pageBrush, 4, 4, 24, 28);
                g.DrawString("F", new System.Drawing.Font("Segoe UI", 14, System.Drawing.FontStyle.Bold),
                    System.Drawing.Brushes.White, 10, 9);
            }
            return FromHbitmap(bmp);
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}