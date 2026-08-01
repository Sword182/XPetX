using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Spine;

namespace XpetX;

/// <summary>
/// 一份可渲染的 Spine 素材：Atlas + 骨骼数据 + Skeleton 实例 + AnimationState。
/// </summary>
public sealed class SpineAsset : IDisposable
{
    public Atlas Atlas { get; }
    public SkeletonData SkeletonData { get; }
    public Skeleton Skeleton { get; }
    public AnimationState AnimationState { get; }
    /// <summary>动画状态访问锁：渲染线程（Update/Apply）与 UI 线程（SetAnimation）共用，防止并发崩溃。</summary>
    public readonly object AnimationLock = new object();

    /// <summary>素材中可用的动画名称列表。</summary>
    public IReadOnlyList<string> AnimationNames { get; }

    public SpineAsset(Atlas atlas, SkeletonData data)
    {
        Atlas = atlas;
        SkeletonData = data;
        Skeleton = new Skeleton(data);
        var stateData = new AnimationStateData(data);
        // 所有动画切换默认 0.25 秒过渡，避免硬切造成的顿挫。
        stateData.DefaultMix = 0.25f;
        AnimationState = new AnimationState(stateData);
        var names = new List<string>();
        foreach (var anim in data.Animations) names.Add(anim.Name);
        AnimationNames = names;
    }

    public bool HasAnimation(string name)
    {
        foreach (var n in AnimationNames)
            if (n == name) return true;
        return false;
    }

    public void Dispose()
    {
        foreach (var page in Atlas.Pages)
            if (page.rendererObject is IDisposable d) d.Dispose();
    }
}

/// <summary>
/// 加载 Spine 3.8 素材：.skel（二进制）+ .atlas + .png 纹理。
/// </summary>
public static class SpineLoader
{
    /// <summary>加载骨骼数据并创建 Skeleton 与 AnimationState。</summary>
    public static SpineAsset Load(string skeletonFilePath, string atlasFilePath)
    {
        if (!File.Exists(skeletonFilePath)) throw new FileNotFoundException("找不到骨骼文件", skeletonFilePath);
        if (!File.Exists(atlasFilePath)) throw new FileNotFoundException("找不到图集文件", atlasFilePath);

        string atlasDirectory = Path.GetDirectoryName(Path.GetFullPath(atlasFilePath)) ?? ".";
        var loader = new WpfTextureLoader(atlasDirectory);
        var atlas = new Atlas(atlasFilePath, loader);
        var binary = new SkeletonBinary(atlas);
        SkeletonData data;
        using (var stream = File.OpenRead(skeletonFilePath))
            data = binary.ReadSkeletonData(stream);
        return new SpineAsset(atlas, data);
    }
}

/// <summary>
/// 将 Spine 图集中的 PNG 解码为 BGRA 像素（直通 alpha），供软件光栅化使用。
/// </summary>
public sealed class SpineTexture : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>BGRA 像素，行序从上到下，straight alpha。</summary>
    public byte[] Pixels { get; }

    private SpineTexture(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public static SpineTexture FromFile(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();

        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        int stride = w * 4;
        var raw = new byte[stride * h];
        bgra.CopyPixels(raw, stride, 0);
        // 像素已拷贝到 raw，不再持有 WPF 位图引用，降低内存占用。
        bitmap = null;
        bgra = null;

        // Spine 默认导出预乘 alpha 纹理；此处检测并还原为 straight alpha，
        // 光栅化阶段统一按 straight alpha 做 alpha 混合。
        bool premultiplied = IsPremultiplied(raw);
        if (premultiplied)
        {
            for (int i = 0; i < raw.Length; i += 4)
            {
                byte a = raw[i + 3];
                if (a == 0) continue;
                raw[i] = (byte)Math.Min(255, raw[i] * 255 / a);
                raw[i + 1] = (byte)Math.Min(255, raw[i + 1] * 255 / a);
                raw[i + 2] = (byte)Math.Min(255, raw[i + 2] * 255 / a);
            }
        }

        return new SpineTexture(w, h, raw);
    }

    private static bool IsPremultiplied(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            if (a == 0) continue;
            if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a)
                return false;
        }
        return true;
    }

    public void Dispose() { }
}

/// <summary>
/// WPF 实现的 Spine TextureLoader：把图集页解码为 <see cref="SpineTexture"/>。
/// </summary>
public sealed class WpfTextureLoader : TextureLoader
{
    private readonly string atlasDirectory;

    public WpfTextureLoader(string atlasDirectory)
    {
        this.atlasDirectory = atlasDirectory;
    }

    public void Load(AtlasPage page, string path)
    {
        string fullPath = Path.IsPathRooted(path) ? path : Path.Combine(atlasDirectory, path);
        page.rendererObject = SpineTexture.FromFile(fullPath);
    }

    public void Unload(object texture)
    {
        if (texture is IDisposable d) d.Dispose();
    }
}