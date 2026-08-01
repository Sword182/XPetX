using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;
using System.Windows.Threading;

namespace XpetX;

/// <summary>
/// 桌面宠物主窗口：透明、无边框、置顶。驱动动画循环、AI、数值系统，
/// 支持右键性能模式切换、全屏隐藏、头部点击互动、气泡提示与配置热加载。
/// </summary>
public partial class MainWindow : Window
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private TimeSpan lastRender = TimeSpan.Zero;
    private int focusRefreshCounter;
    private int fpsFrames;
    private TimeSpan fpsWindowStart = TimeSpan.Zero;
    private long fpsRenderStart;
    private long fpsPresentStart;
    private readonly DispatcherTimer bubbleTimer;
    private PetInstance? pet;
    private PetPerformanceMode performanceMode = PetPerformanceMode.Auto;
    private DateTime lastInteractTime = DateTime.MinValue;
    private bool hideInFullscreen;
    private bool clickThrough;
    private HwndSource? hwndSource;
    private WinForms.NotifyIcon? trayIcon;
    private System.Drawing.Bitmap? trayBitmap;
    private WinForms.ToolStripMenuItem? traySitItem;
    private WinForms.ToolStripMenuItem? trayClickThroughItem;
    private WinForms.ToolStripMenuItem? trayHideFsItem;
    private WinForms.ToolStripMenuItem[] trayModeItems = Array.Empty<WinForms.ToolStripMenuItem>();
    private bool manualHidden;
    private bool manualDragging;
    private Point dragOffsetDip;
    private bool deleteModeWarned;
    private bool dangerousWarned;
    private WinForms.ToolStripMenuItem? trayPetsItem;
    private WinForms.ToolStripMenuItem? trayStatsItem;
    private WinForms.ToolStripMenuItem[] traySizeItems = Array.Empty<WinForms.ToolStripMenuItem>();
    private readonly Dictionary<DroppedFile, System.Windows.Controls.Image> fileImages = new Dictionary<DroppedFile, System.Windows.Controls.Image>();
    private readonly Dictionary<DroppedFile, System.Windows.Controls.Image> mascotImages = new Dictionary<DroppedFile, System.Windows.Controls.Image>();
    private static readonly ImageSource MascotImage = CreateMascotImage();

    public MainWindow()
    {
        InitializeComponent();
        bubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        bubbleTimer.Tick += OnBubbleTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
        // 点击窗口外（桌面/其他应用）时强制关闭托盘菜单，避免"关不掉"。
        Deactivated += (_, _) =>
        {
            try
            {
                trayIcon?.ContextMenuStrip?.Close();
            }
            catch
            {
            }
        };
        CreateTrayIcon();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            performanceMode = PetSettings.LoadMode();
            UpdateModeChecks();
            hideInFullscreen = PetSettings.LoadHideInFullscreen();
            MiHideFullscreen.IsChecked = hideInFullscreen;
            clickThrough = PetSettings.LoadClickThrough();
            MiClickThrough.IsChecked = clickThrough;
            UpdateTrayMenuState();

            ConfigManager.Initialize();
            ConfigManager.Changed += OnConfigChanged;
        ConfigManager.Changed += _ => Dispatcher.BeginInvoke(ApplyPetIcon);
            ApplyPetIcon();

            string? petDirectory = PetManager.GetPetDirectories().FirstOrDefault();
            if (petDirectory == null)
            {
                ShowEmptyState();
                return; // 空状态：窗口显示提示、托盘保持可用，跳过宠物初始化。
            }

            pet = new PetInstance
            {
                PetId = Path.GetFileName(petDirectory),
                DisplayName = Path.GetFileName(petDirectory),
                WindowWidth = Width,
                WindowHeight = Height,
            };
            pet.PositionChanged += OnPetPositionChanged;
            pet.AI.BubbleRequested += OnBubbleRequested;
            pet.Stats.OnStatsChanged += OnStatsChanged;
            pet.Initialize(petDirectory);
            pet.FitToWindow(Width, Height);

            if (pet.Renderer != null)
            {
                pet.Renderer.PerformanceMode = performanceMode;
                RootGrid.Children.Add(pet.Renderer);
            }

            pet.AI.WindowBoundsProvider = () => new Rect(pet.X, pet.Y, pet.WindowWidth, pet.WindowHeight);
            pet.WindowHandle = new WindowInteropHelper(this).Handle;
            pet.Files.FileChanged += OnFileChanged;
            ApplyConfig();

            if (pet.Asset != null)
            {
                bool ok = pet.PlayAnimation("idle", true)
                    || pet.PlayAnimation("Relax", true)
                    || pet.PlayAnimation("Default", true)
                    || pet.PlayAnimation(pet.Asset.AnimationNames.FirstOrDefault() ?? "", true);
                if (!ok) LogError("没有可播放的动画", new InvalidOperationException("动画列表为空"));
            }

            // 启动时读档（含离线时间补偿）；没有存档则放到屏幕右下角。
            if (!pet.Load())
            {
                pet.SetPosition(
                    SystemParameters.WorkArea.Right - Width - 16,
                    SystemParameters.WorkArea.Bottom - Height + ConfigManager.Current.TaskbarOffset);
            }

            ApplyClickThrough(clickThrough);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            WindowFocus.RegisterClickThroughHotKey(hwnd);
            hwndSource = HwndSource.FromHwnd(hwnd);
            hwndSource?.AddHook(OnWndProc);


            lastRender = stopwatch.Elapsed;
            CompositionTarget.Rendering += OnRendering;
        }
        catch (Exception ex)
        {
            LogError("初始化宠物失败", ex);
        }
    }

    /// <summary>无宠物时的空状态：窗口显示提示 + 托盘气泡，托盘功能保持可用。</summary>
    private void ShowEmptyState()
    {
        EmptyHint.Text = "暂无宠物\n\n请从托盘「添加新 pet」导入，\n或把宠物文件夹放入 pets 目录";
        EmptyHint.Visibility = Visibility.Visible;
        try
        {
            trayIcon?.ShowBalloonTip(3000, "XpetX",
                "宠物目录为空：请从托盘「添加新 pet」导入，或把宠物文件夹放入 pets\\ 目录。",
                WinForms.ToolTipIcon.Warning);
        }
        catch
        {
        }
        LogError("宠物目录为空", new DirectoryNotFoundException("pets 目录下没有宠物子目录"));
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (pet == null) return;
        TimeSpan now = stopwatch.Elapsed;
        float delta = (float)(now - lastRender).TotalSeconds;
        lastRender = now;
        if (delta <= 0f) return;
        if (delta > 0.1f) delta = 0.1f;

        // 每约 0.5 秒刷新一次前台/全屏状态，供自动判定与全屏隐藏使用。
        if (pet.Renderer != null && ++focusRefreshCounter % 30 == 0)
        {
            ApplyWindowVisibility();
        }

        // 实时帧率 + 数值显示（调试用）。
        fpsFrames++;
        if (now - fpsWindowStart >= TimeSpan.FromMilliseconds(500))
        {
            double seconds = (now - fpsWindowStart).TotalSeconds;
            double uiFps = fpsFrames / seconds;
            fpsFrames = 0;
            if (pet.Renderer != null)
            {
                double renderFps = (pet.Renderer.RenderedFrames - fpsRenderStart) / seconds;
                double presentFps = (pet.Renderer.PresentedFrames - fpsPresentStart) / seconds;
                fpsRenderStart = pet.Renderer.RenderedFrames;
                fpsPresentStart = pet.Renderer.PresentedFrames;
                FpsText.Text = string.Format(
                    "UI {0:F0} 渲 {1:F0} 呈 {2:F0}  x{3:F2}  {4:F0}ms",
                    uiFps, renderFps, presentFps, pet.Renderer.RenderScale,
                    pet.Renderer.RenderMsEma < 0 ? 0 : pet.Renderer.RenderMsEma);
                if (HoverStatsText.Visibility == Visibility.Visible)
                {
                    HoverStatsText.Text = string.Format("饱 {0:F0}  乐 {1:F0}  精 {2:F0}",
                        pet.Stats.Hunger, pet.Stats.Happiness, pet.Stats.Energy);
                }

            }
            else
            {
                FpsText.Text = $"{uiFps:F0} FPS";
            }
            fpsWindowStart = now;
        }

        UpdateFileLayer();
        pet.Update(delta);
    }

    private void OnStatsChanged()
    {
        // 数值每帧都会变化；悬浮窗每 0.5 秒读取最新值，这里只需确保 UI 线程有更新入口。
    }

    private void OnBubbleRequested(string text)
    {
        BubbleText.Text = text;
        BubbleText.Visibility = Visibility.Visible;
        bubbleTimer.Stop();
        bubbleTimer.Start();
    }

    private void OnBubbleTimerTick(object? sender, EventArgs e)
    {
        bubbleTimer.Stop();
        BubbleText.Visibility = Visibility.Collapsed;
    }

    private void OnConfigChanged(AppConfig config)
    {
        Dispatcher.BeginInvoke(ApplyConfig);
    }

    /// <summary>应用自定义图标到托盘（可热加载更换）。</summary>
    private void ApplyPetIcon()
    {
        try
        {
            if (trayIcon != null) trayIcon.Icon = PetIcons.CurrentIcon();
        }
        catch
        {
        }
    }

    private void ApplyConfig()
    {
        if (pet == null) return;
        Topmost = ConfigManager.Current.AlwaysOnTop;
        if (pet.Renderer != null)
        {
            pet.Renderer.HeadFollowEnabled = ConfigManager.Current.CursorTracking;
            pet.Renderer.HeadFollowSpeed = ConfigManager.Current.HeadFollowSpeed;
        }
        pet.Stats.DecaySpeed = ConfigManager.Current.DecaySpeed;
        pet.AI.MoveSpeed = ConfigManager.Current.MoveSpeed;
        pet.AI.Activity = ConfigManager.Current.Activity;
        pet.AI.WalkMirror = ConfigManager.Current.WalkMirror;
        pet.AI.FacingRight = ConfigManager.Current.FacingRight;
        pet.AI.WalkArea = ConfigManager.Current.WalkArea;
        pet.AI.TaskbarOffset = ConfigManager.Current.TaskbarOffset;
        pet.AI.HungryThreshold = ConfigManager.Current.HungryThreshold;
        // 修改偏移后立即把宠物贴回任务栏基线（拖拽中不打扰），让手动调整即时可见。
        if (!pet.AI.IsDragging && pet.AI.WalkArea != "screen")
        {
            double baselineY = SystemParameters.WorkArea.Bottom - Height + ConfigManager.Current.TaskbarOffset;
            pet.SetPosition(pet.X, baselineY);
        }

        // 危险设置警告（仅配置文件可改，改到即提示一次）。
        bool deleteMode = string.Equals(ConfigManager.Current.DeleteMode, "delete", StringComparison.OrdinalIgnoreCase);
        bool dangerous = ConfigManager.Current.AllowDangerousFiles;
        if ((deleteMode && !deleteModeWarned) || (dangerous && !dangerousWarned))
        {
            var warnings = new System.Collections.Generic.List<string>();
            if (deleteMode) { warnings.Add("已开启「永久删除」：宠物吃掉的将无法恢复（建议用回收站）"); deleteModeWarned = true; }
            if (dangerous) { warnings.Add("已开启「危险类型进食」：.exe/.dll/.msi 等也可能被宠物删除"); dangerousWarned = true; }
            MessageBox.Show(this, string.Join(Environment.NewLine, warnings), "XpetX 警告", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string tag) ApplyPerformanceMode(tag);
    }

    private void ApplyPerformanceMode(string tag)
    {
        if (Enum.TryParse<PetPerformanceMode>(tag, out PetPerformanceMode mode))
        {
            performanceMode = mode;
            if (pet?.Renderer != null) pet.Renderer.PerformanceMode = mode;
            UpdateModeChecks();
            UpdateTrayMenuState();
            PetSettings.SaveMode(mode);
        }
    }

    private void UpdateModeChecks()
    {
        MiFocus.IsChecked = performanceMode == PetPerformanceMode.FocusPriority;
        MiPet.IsChecked = performanceMode == PetPerformanceMode.PetPriority;
        MiBalanced.IsChecked = performanceMode == PetPerformanceMode.Balanced;
        MiAuto.IsChecked = performanceMode == PetPerformanceMode.Auto;

    }

    private void ApplyWindowVisibility()
    {
        if (manualHidden) return; // 用户手动隐藏时，不自动显隐。
        if (pet?.Renderer == null) return;
        pet.Renderer.IsForeground = WindowFocus.IsForeground(this);
        pet.Renderer.IsForegroundImmersive = WindowFocus.IsForegroundFullscreen();

        if (!hideInFullscreen) return;
        bool shouldHide = !pet.Renderer.IsForeground && pet.Renderer.IsForegroundImmersive;
        if (shouldHide && pet.IsVisible)
        {
            pet.IsVisible = false;
            Visibility = Visibility.Collapsed;
            pet.Renderer.Paused = true;
        }
        else if (!shouldHide && !pet.IsVisible)
        {
            pet.IsVisible = true;
            Visibility = Visibility.Visible;
            pet.Renderer.Paused = false;
        }
    }

    private void OnSitClick(object sender, RoutedEventArgs e)
    {
        if (pet == null) return;
        SetSitting(MiSit.IsChecked);
    }

    private void SetSitting(bool sitting)
    {
        if (pet == null) return;
        if (sitting) pet.Sit();
        else pet.Stand();
        MiSit.IsChecked = sitting;
        UpdateTrayMenuState();
    }

    private void OnHideFullscreenClick(object sender, RoutedEventArgs e)
    {
        hideInFullscreen = MiHideFullscreen.IsChecked;
        PetSettings.SaveHideInFullscreen(hideInFullscreen);
        ApplyWindowVisibility();
        UpdateTrayMenuState();
    }

    private void OnClickThroughClick(object sender, RoutedEventArgs e)
    {
        clickThrough = MiClickThrough.IsChecked;
        ApplyClickThrough(clickThrough);
        PetSettings.SaveClickThrough(clickThrough);
        UpdateTrayMenuState();
    }

    private void ApplyClickThrough(bool enabled)
    {
        WindowFocus.SetClickThrough(this, enabled);
    }

    private IntPtr OnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == WindowFocus.ClickThroughHotKeyId)
        {
            clickThrough = !clickThrough;
            ApplyClickThrough(clickThrough);
            MiClickThrough.IsChecked = clickThrough;
            PetSettings.SaveClickThrough(clickThrough);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnFpsToggleClick(object sender, RoutedEventArgs e)
    {
        FpsText.Visibility = MiFps.IsChecked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CreateTrayIcon()
    {
        try
        {
            trayIcon = new WinForms.NotifyIcon
            {
                Icon = PetIcons.CurrentIcon(),
                Text = "XpetX 桌宠",
                Visible = true,
                ContextMenuStrip = BuildTrayMenu(),
            };
            // 左键单击托盘图标：菜单已开则关闭，否则打开（修复"关不掉"）。
            trayIcon.MouseClick += (_, e) =>
            {
                if (e.Button != WinForms.MouseButtons.Left || trayIcon.ContextMenuStrip == null) return;
                if (trayIcon.ContextMenuStrip.Visible) trayIcon.ContextMenuStrip.Close();
                else trayIcon.ContextMenuStrip.Show(WinForms.Cursor.Position);
            };
        }
        catch (Exception ex)
        {
            trayIcon = null;
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 托盘初始化失败{Environment.NewLine}{ex}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }

    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Opening += (_, _) => RefreshTrayMenu();

        var miVisibility = new WinForms.ToolStripMenuItem("显示/隐藏桌宠");
        miVisibility.Click += (_, _) => TogglePetVisibility();

        trayPetsItem = new WinForms.ToolStripMenuItem("宠物预览 · 添加到桌面");

        var miAddPet = new WinForms.ToolStripMenuItem("添加新 pet");
        miAddPet.Click += (_, _) => ImportPetFromTray();

        var miOpenFolder = new WinForms.ToolStripMenuItem("打开 pet 文件夹");
        miOpenFolder.Click += (_, _) => PetManager.OpenPetsFolder();

        var miGitHub = new WinForms.ToolStripMenuItem("GitHub");
        miGitHub.Click += (_, _) =>
            trayIcon?.ShowBalloonTip(2000, "XpetX", "GitHub 链接待补充", WinForms.ToolTipIcon.Info);

        var miManage = new WinForms.ToolStripMenuItem("管理模式（点击宠物删除）") { CheckOnClick = true };
        miManage.Click += (_, _) =>
        {
            PetManager.ManagementMode = miManage.Checked;
            trayIcon?.ShowBalloonTip(2000, "XpetX",
                miManage.Checked ? "管理模式已开启：左键点击宠物副本即可删除" : "管理模式已关闭",
                WinForms.ToolTipIcon.Info);
        };

        var miCloseAll = new WinForms.ToolStripMenuItem("关闭所有宠物（保留主宠物）");
        miCloseAll.Click += (_, _) => PetManager.CloseAllCopies();

        traySitItem = new WinForms.ToolStripMenuItem("坐下");
        traySitItem.Click += (_, _) => SetSitting(!(pet?.AI.IsSitting ?? false));

        trayClickThroughItem = new WinForms.ToolStripMenuItem("点击穿透");
        trayClickThroughItem.Click += (_, _) =>
        {
            clickThrough = !clickThrough;
            ApplyClickThrough(clickThrough);
            PetSettings.SaveClickThrough(clickThrough);
            MiClickThrough.IsChecked = clickThrough;
            UpdateTrayMenuState();
        };

        trayHideFsItem = new WinForms.ToolStripMenuItem("全屏时隐藏");
        trayHideFsItem.Click += (_, _) =>
        {
            hideInFullscreen = !hideInFullscreen;
            MiHideFullscreen.IsChecked = hideInFullscreen;
            PetSettings.SaveHideInFullscreen(hideInFullscreen);
            ApplyWindowVisibility();
            UpdateTrayMenuState();
        };

        trayStatsItem = new WinForms.ToolStripMenuItem("当前数值");

        var miSize = new WinForms.ToolStripMenuItem("宠物大小");
        var sizes = new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 };
        var sizeItems = new System.Collections.Generic.List<WinForms.ToolStripMenuItem>();
        foreach (double size in sizes)
        {
            var item = new WinForms.ToolStripMenuItem($"{size * 100:F0}%") { Tag = size };
            item.Click += (_, _) =>
            {
                if (pet != null) pet.SetSizeMultiplier((double)item.Tag);
                UpdateTrayMenuState();
            };
            miSize.DropDownItems.Add(item);
            sizeItems.Add(item);
        }
        traySizeItems = sizeItems.ToArray();

        var miMode = new WinForms.ToolStripMenuItem("性能模式");
        var modes = new[]
        {
            ("优先当前任务", "FocusPriority"),
            ("优先桌宠", "PetPriority"),
            ("两者均衡", "Balanced"),
            ("自动判定", "Auto"),
        };
        var items = new System.Collections.Generic.List<WinForms.ToolStripMenuItem>();
        foreach (var (label, tag) in modes)
        {
            var item = new WinForms.ToolStripMenuItem(label) { Tag = tag };
            item.Click += (_, _) => ApplyPerformanceMode((string)item.Tag);
            miMode.DropDownItems.Add(item);
            items.Add(item);
        }
        trayModeItems = items.ToArray();

        var miExit = new WinForms.ToolStripMenuItem("退出");
        miExit.Click += (_, _) => Close();

        menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            miVisibility,
            new WinForms.ToolStripSeparator(),
            trayPetsItem, miAddPet, miOpenFolder, miGitHub, miManage, miCloseAll,
            new WinForms.ToolStripSeparator(),
            traySitItem, trayClickThroughItem, trayHideFsItem,
            new WinForms.ToolStripSeparator(),
            trayStatsItem, miSize, miMode,
            new WinForms.ToolStripSeparator(),
            miExit,
        });
        return menu;
    }

    /// <summary>托盘菜单打开前刷新动态项（宠物列表、当前数值）。</summary>
    private void RefreshTrayMenu()
    {
        if (trayIcon?.ContextMenuStrip == null || pet == null) return;
        try
        {
            if (trayPetsItem != null)
            {
                trayPetsItem.DropDownItems.Clear();
                foreach (string dir in PetManager.GetPetDirectories())
                {
                    string name = PetNaming.GetDisplayName(dir);
                    var item = new WinForms.ToolStripMenuItem($"添加到桌面 · {name}");
                    item.Click += (_, _) => PetManager.SpawnPet(dir);
                    trayPetsItem.DropDownItems.Add(item);
                }
                trayPetsItem.Enabled = trayPetsItem.DropDownItems.Count > 0;
            }

            if (trayStatsItem != null)
            {
                trayStatsItem.DropDownItems.Clear();
                trayStatsItem.DropDownItems.Add(new WinForms.ToolStripMenuItem($"饱食 {pet.Stats.Hunger:F0}") { Enabled = false });
                trayStatsItem.DropDownItems.Add(new WinForms.ToolStripMenuItem($"快乐 {pet.Stats.Happiness:F0}") { Enabled = false });
                trayStatsItem.DropDownItems.Add(new WinForms.ToolStripMenuItem($"精力 {pet.Stats.Energy:F0}") { Enabled = false });
            }

            UpdateTrayMenuState();
        }
        catch
        {
        }
    }

    private void ImportPetFromTray()
    {
        using var dialog = new WinForms.FolderBrowserDialog { Description = "选择要添加的 pet 文件夹（含 spine 素材）" };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            PetManager.AddPetFolder(dialog.SelectedPath);
        }
    }

    private void UpdateTrayMenuState()
    {
        if (trayIcon == null) return;
        try
        {
            if (traySitItem != null) traySitItem.Checked = pet?.AI.IsSitting ?? false;
            if (trayClickThroughItem != null) trayClickThroughItem.Checked = clickThrough;
            if (trayHideFsItem != null) trayHideFsItem.Checked = hideInFullscreen;
            foreach (var item in trayModeItems)
            {
                if (item.Tag is string tag)
                    item.Checked = tag == performanceMode.ToString();
            }
            foreach (var item in traySizeItems)
            {
                if (item.Tag is double size && pet != null)
                    item.Checked = Math.Abs(pet.SizeMultiplier - size) < 0.01;
            }
        }
        catch
        {
        }
    }

    private void TogglePetVisibility()
    {
        if (pet == null) return;
        if (pet.IsVisible)
        {
            manualHidden = true;
            pet.IsVisible = false;
            Visibility = Visibility.Collapsed;
            if (pet.Renderer != null) pet.Renderer.Paused = true;
        }
        else
        {
            manualHidden = false;
            pet.IsVisible = true;
            Visibility = Visibility.Visible;
            if (pet.Renderer != null) pet.Renderer.Paused = false;
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnPetPositionChanged()
    {
        if (pet == null) return;
        Left = pet.X;
        Top = pet.Y;
        if (pet.Renderer != null)
        {
            pet.Renderer.WindowScreenX = pet.X;
            pet.Renderer.WindowScreenY = pet.Y;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        if (pet != null)
        {
            pet.AI.BubbleRequested -= OnBubbleRequested;
            pet.Files.FileChanged -= OnFileChanged;
            pet.Stats.OnStatsChanged -= OnStatsChanged;
            pet.Save();
            pet.Dispose();
        }
        ConfigManager.Changed -= OnConfigChanged;
        ConfigManager.Stop();
        bubbleTimer.Stop();
        if (hwndSource != null)
        {
            hwndSource.RemoveHook(OnWndProc);
            hwndSource = null;
        }
        WindowFocus.UnregisterClickThroughHotKey(new WindowInteropHelper(this).Handle);
        if (trayIcon != null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayIcon = null;
        }
        trayBitmap?.Dispose();
        trayBitmap = null;
    }

    private static void LogError(string message, Exception exception)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch
        {
            // 日志写入失败时不再抛出。
        }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        HoverStatsText.Visibility = Visibility.Visible;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        HoverStatsText.Visibility = Visibility.Collapsed;
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || pet == null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        Point windowPoint = e.GetPosition(this);
        Point screenPoint = PointToScreen(windowPoint);
        bool directlyToPet = pet.Renderer?.IsPointOnPet(windowPoint) ?? false;
        foreach (string path in paths)
        {
            pet.Files.DropFile(path, screenPoint, directlyToPet);
        }
        e.Handled = true;
    }

    private void OnFileChanged(DroppedFile file)
    {
        if (file.State == DroppedFileState.Removed)
        {
            RemoveFileImages(file);
            return;
        }
        EnsureFileImage(file);
    }

    /// <summary>文件图标尺寸：按宠物显示高度的 9% 适配（20~56px）。</summary>
    private double GetIconSize()
    {
        double charHeight = pet?.CharacterHeightPx ?? 320;
        return Math.Clamp(charHeight * 0.13, 24, 72);
    }

    private void EnsureFileImage(DroppedFile file)
    {
        if (!fileImages.TryGetValue(file, out var image))
        {
            double size = GetIconSize();
            image = new System.Windows.Controls.Image
            {
                Width = size,
                Height = size * 28.0 / 24.0,
                Stretch = Stretch.Uniform,
                Source = file.Icon,
                IsHitTestVisible = false,
            };
            fileImages[file] = image;
            FileLayer.Children.Add(image);
        }
    }

    private void RemoveFileImages(DroppedFile file)
    {
        if (fileImages.TryGetValue(file, out var image))
        {
            FileLayer.Children.Remove(image);
            fileImages.Remove(file);
        }
        if (mascotImages.TryGetValue(file, out var mascot))
        {
            FileLayer.Children.Remove(mascot);
            mascotImages.Remove(file);
        }
    }

    /// <summary>每帧把文件图标/小人摆到屏幕对应位置（相对宠物窗口）。</summary>
    private void UpdateFileLayer()
    {
        if (pet == null) return;
        foreach (var file in pet.Files.Files)
        {
            EnsureFileImage(file);
            double x = file.ScreenX - pet.X;
            double y = file.ScreenY - pet.Y;
            bool visible = x >= -40 && x <= Width + 40 && y >= -40 && y <= Height + 40;
            if (fileImages.TryGetValue(file, out var image))
            {
                double halfW = image.Width / 2;
                double fullH = image.Height;
                image.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                Canvas.SetLeft(image, x - halfW);
                Canvas.SetTop(image, y - fullH);
                if (file.State == DroppedFileState.BeingEaten)
                {
                    // 进食动画：飞到嘴边后缩小两次再消失。
                    image.RenderTransform = new ScaleTransform(file.EatScale, file.EatScale, halfW, fullH / 2);
                    image.Opacity = file.EatOpacity;
                }
                else
                {
                    image.RenderTransform = null;
                    image.Opacity = 1.0;
                }
            }

            // Windows 小人：不可吃文件落地 0.6 秒后滑过来收走。
            if (!file.Edible && file.State == DroppedFileState.Waiting && file.Timer >= 0.6 && file.Timer < 1.4)
            {
                if (!mascotImages.TryGetValue(file, out var mascot))
                {
                    double size = GetIconSize();
                    mascot = new System.Windows.Controls.Image
                    {
                        Width = size,
                        Height = size,
                        Source = MascotImage,
                        IsHitTestVisible = false,
                    };
                    mascotImages[file] = mascot;
                    FileLayer.Children.Add(mascot);
                }
                double t = (file.Timer - 0.6) / 0.8;
                double mascotX = x - 60 + 60 * t;
                Canvas.SetLeft(mascot, mascotX - mascot.Width / 2);
                Canvas.SetTop(mascot, y - mascot.Height);
                mascot.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (mascotImages.TryGetValue(file, out var oldMascot))
            {
                oldMascot.Visibility = Visibility.Collapsed;
            }
        }

        // 清理已不在列表中的残留图标。
        foreach (var stale in new List<DroppedFile>(fileImages.Keys))
        {
            if (!pet.Files.Files.Contains(stale)) RemoveFileImages(stale);
        }
    }

    private static ImageSource CreateMascotImage()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var blue = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));
            blue.Freeze();
            dc.DrawEllipse(blue, null, new Point(12, 9), 7, 7);
            dc.DrawRoundedRectangle(blue, null, new Rect(6, 15, 12, 12), 4, 4);
            var white = Brushes.White;
            dc.DrawEllipse(white, null, new Point(10, 8), 1.3, 1.3);
            dc.DrawEllipse(white, null, new Point(14, 8), 1.3, 1.3);
        }
        var bitmap = new RenderTargetBitmap(24, 28, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // Alt + 左键：手动拖动宠物（无屏幕限制，可拖到屏幕外/顶部），边拖边同步坐标。
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            Point cursorDip = ToDip(WindowFocus.GetCursorScreen());
            dragOffsetDip = new Point(cursorDip.X - Left, cursorDip.Y - Top);
            manualDragging = true;
            if (pet != null)
            {
                pet.AI.CancelWalk(); // 先取消行走，松手后停在落点
                pet.AI.IsDragging = true;
            }
            Mouse.Capture(this);
            e.Handled = true;
            return;
        }

        if (pet?.Renderer != null && IsHeadHit(e.GetPosition(this)))
        {
            // 防抖：500ms 内只响应一次，避免一次点击触发多次动画。
            if ((DateTime.Now - lastInteractTime).TotalMilliseconds < 500) return;
            lastInteractTime = DateTime.Now;
            pet.PlayInteract();
            e.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!manualDragging) return;
        Point cursorDip = ToDip(WindowFocus.GetCursorScreen());
        Left = cursorDip.X - dragOffsetDip.X;
        Top = cursorDip.Y - dragOffsetDip.Y;
        // 同步逻辑坐标，避免拖走后 AI 用旧坐标把宠物拽回原处。
        pet?.SyncPosition(Left, Top);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!manualDragging) return;
        manualDragging = false;
        if (pet != null)
        {
            pet.AI.IsDragging = false;
            pet.SetPosition(Left, Top);
        }
        Mouse.Capture(null);
    }

    /// <summary>物理屏幕坐标转 DIP（正确处理系统缩放）。</summary>
    private Point ToDip(Point screenPoint)
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget != null
            ? source.CompositionTarget.TransformFromDevice.Transform(screenPoint)
            : screenPoint;
    }

    /// <summary>点击位置是否落在头部骨骼附近（命中半径约 55 像素）。</summary>
    private bool IsHeadHit(Point point)
    {
        SpineRenderer renderer = pet!.Renderer!;
        if (!renderer.HasHeadBone) return false;
        double dx = point.X - renderer.HeadScreenX;
        double dy = point.Y - renderer.HeadScreenY;
        return dx * dx + dy * dy <= 55.0 * 55.0;
    }
}
