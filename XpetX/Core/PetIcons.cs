using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XpetX;

/// <summary>自定占位图标（宠物脸）。</summary>
internal static class PetIcons
{
    /// <summary>64x64 宠物脸（WPF 用，菜单图标/窗口图标）。</summary>
    public static BitmapSource FaceImage()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var blue = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));
            blue.Freeze();
            var white = Brushes.White;
            var black = Brushes.Black;

            // 左耳
            var leftEar = new StreamGeometry();
            using (var ctx = leftEar.Open())
            {
                ctx.BeginFigure(new Point(14, 26), true, true);
                ctx.LineTo(new Point(20, 4), true, false);
                ctx.LineTo(new Point(29, 22), true, false);
            }
            leftEar.Freeze();
            // 右耳
            var rightEar = new StreamGeometry();
            using (var ctx = rightEar.Open())
            {
                ctx.BeginFigure(new Point(35, 22), true, true);
                ctx.LineTo(new Point(44, 4), true, false);
                ctx.LineTo(new Point(50, 26), true, false);
            }
            rightEar.Freeze();

            dc.DrawGeometry(blue, null, leftEar);
            dc.DrawGeometry(blue, null, rightEar);
            dc.DrawEllipse(blue, null, new Point(32, 38), 21, 19); // 脸

            dc.DrawEllipse(white, null, new Point(23, 36), 5.2, 6.2); // 左眼白
            dc.DrawEllipse(white, null, new Point(41, 36), 5.2, 6.2); // 右眼白
            dc.DrawEllipse(black, null, new Point(24, 37), 2.4, 3);   // 左瞳孔
            dc.DrawEllipse(black, null, new Point(42, 37), 2.4, 3);   // 右瞳孔
            dc.DrawEllipse(white, null, new Point(32, 45), 4.6, 3.4); // 嘴
        }
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>托盘用 Icon（32x32，System.Drawing 绘制）。</summary>
    public static System.Drawing.Icon FaceIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var blue = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 0, 120, 215));
            g.FillPolygon(blue, new[]
            {
                new System.Drawing.PointF(7, 15), new System.Drawing.PointF(10, 2), new System.Drawing.PointF(17, 13),
            });
            g.FillPolygon(blue, new[]
            {
                new System.Drawing.PointF(20, 13), new System.Drawing.PointF(25, 2), new System.Drawing.PointF(30, 15),
            });
            g.FillEllipse(blue, 3, 9, 26, 21);
            g.FillEllipse(System.Drawing.Brushes.White, 10, 15, 5, 6);
            g.FillEllipse(System.Drawing.Brushes.White, 19, 15, 5, 6);
            g.FillEllipse(System.Drawing.Brushes.Black, 11.6f, 16.6f, 2.4f, 3f);
            g.FillEllipse(System.Drawing.Brushes.Black, 20.6f, 16.6f, 2.4f, 3f);
            g.FillEllipse(System.Drawing.Brushes.White, 14, 23, 6, 4);
        }
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
                        DestroyIcon(hIcon);
        }
    }

    private static string cachedPath = "";
    private static BitmapSource? cachedImage;
    private static System.Drawing.Icon? cachedIcon;

    /// <summary>当前图标（WPF 位图）：配置 iconPath 存在时用自定义图，否则内置宠物脸。</summary>
    public static BitmapSource CurrentImage()
    {
        string path = ConfigManager.Current.IconPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            if (path != cachedPath || cachedImage == null)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.EndInit();
                    bmp.Freeze();
                    cachedImage = bmp;
                    cachedPath = path;
                }
                catch
                {
                    cachedImage = FaceImage();
                    cachedPath = path;
                }
            }
            return cachedImage;
        }
        return FaceImage();
    }

    /// <summary>当前图标（托盘 Icon）：配置 iconPath 存在时用自定义图，否则内置宠物脸。</summary>
    public static System.Drawing.Icon CurrentIcon()
    {
        string path = ConfigManager.Current.IconPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            if (path != cachedPath || cachedIcon == null)
            {
                try
                {
                    using var bmp = new System.Drawing.Bitmap(path);
                    IntPtr handle = bmp.GetHicon();
                    try
                    {
                        cachedIcon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
                    }
                    finally
                    {
                        DestroyIcon(handle);
                    }
                    cachedPath = path;
                }
                catch
                {
                    cachedIcon = FaceIcon();
                    cachedPath = path;
                }
            }
            return cachedIcon;
        }
        return FaceIcon();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}