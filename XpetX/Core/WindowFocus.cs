using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace XpetX;

/// <summary>前台窗口判定辅助（Win32）。</summary>
internal static class WindowFocus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out RECT point);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>点击穿透热键：Ctrl+Alt+P（穿透开启后菜单不可点，用热键切回）。</summary>
    public const int ClickThroughHotKeyId = 0x5850;
    public const uint ClickThroughHotKeyModifiers = 0x0002 | 0x0001; // MOD_ALT | MOD_CONTROL
    public const uint ClickThroughHotKeyVk = 0x50; // 'P'

    /// <summary>启用/关闭点击穿透（WS_EX_TRANSPARENT）。</summary>
    public static void SetClickThrough(Window window, bool enabled)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            IntPtr style = IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE) : GetWindowLong32(hwnd, GWL_EXSTYLE);
            long value = enabled ? (style.ToInt64() | WS_EX_TRANSPARENT) : (style.ToInt64() & ~WS_EX_TRANSPARENT);
            IntPtr result = IntPtr.Size == 8
                ? SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(value))
                : SetWindowLong32(hwnd, GWL_EXSTYLE, new IntPtr(value));
        }
        catch
        {
        }
    }

    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_THICKFRAME = 0x00040000;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrStyle(IntPtr hWnd, int nIndex);

    /// <summary>
    /// 计算文件掉落的"地面"Y：寻找 dropX 下方最近的有边框窗口顶部（宠物在其上方时）；
    /// 无边框窗口不算地面，继续下落到任务栏（工作区底部）。
    /// </summary>
    public static double FindGroundY(IntPtr petHwnd, double dropScreenX, double petScreenTop, double petScreenBottom)
    {
        double fallback = SystemParameters.WorkArea.Bottom;
        var windows = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        foreach (IntPtr hwnd in windows)
        {
            if (!IsWindowVisible(hwnd)) continue;
            if (hwnd == petHwnd) continue;
            if (!GetWindowRect(hwnd, out RECT r)) continue;
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) continue;
            if (dropScreenX < r.Left || dropScreenX > r.Right) continue; // 水平包含
            double top = r.Top;
            if (top < petScreenTop - 4) continue; // 在宠物上方的不算
            if (top < petScreenBottom - 100) continue; // 与宠物重叠较多的窗口不算"地面"
            long style = GetWindowLongPtrStyle(hwnd, GWL_STYLE).ToInt64();
            bool bordered = (style & WS_CAPTION) != 0 || (style & WS_THICKFRAME) != 0;
            if (bordered) return top;
        }
        return fallback;
    }

    public static void RegisterClickThroughHotKey(IntPtr hwnd)
    {
        try
        {
            RegisterHotKey(hwnd, ClickThroughHotKeyId, ClickThroughHotKeyModifiers, ClickThroughHotKeyVk);
        }
        catch
        {
        }
    }

    public static void UnregisterClickThroughHotKey(IntPtr hwnd)
    {
        try
        {
            UnregisterHotKey(hwnd, ClickThroughHotKeyId);
        }
        catch
        {
        }
    }

    public static bool IsForeground(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            return handle != IntPtr.Zero && GetForegroundWindow() == handle;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前鼠标在屏幕上的坐标（物理像素）。</summary>
    public static Point GetCursorScreen()
    {
        try
        {
            return GetCursorPos(out RECT point)
                ? new Point(point.Left, point.Top)
                : new Point(0, 0);
        }
        catch
        {
            return new Point(0, 0);
        }
    }

    /// <summary>前台窗口是否几乎铺满主屏工作区（视为游戏/沉浸式应用）。</summary>
    public static bool IsForegroundFullscreen()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || !GetWindowRect(fg, out RECT rect)) return false;
            Rect work = SystemParameters.WorkArea;
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            return w >= work.Width - 8 && h >= work.Height - 8;
        }
        catch
        {
            return false;
        }
    }
}