using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace XpetX;

/// <summary>单只宠物的独立窗口（支持多 pet 同时上桌）。</summary>
public sealed class PetWindow : Window
{
    private readonly string petDirectory;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly DispatcherTimer bubbleTimer;
    private TimeSpan lastRender = TimeSpan.Zero;
    private PetInstance? pet;
    private TextBlock? bubbleText;
    private Grid? rootGrid;
    private bool manualDragging;
    private Point dragOffsetDip;
    private DateTime lastInteractTime = DateTime.MinValue;
    private PetPerformanceMode performanceMode = PetPerformanceMode.Auto;
    private readonly System.Collections.Generic.List<MenuItem> sizeItems = new System.Collections.Generic.List<MenuItem>();
    private bool clickThrough;

    public PetWindow(string petDirectory)
    {
        this.petDirectory = petDirectory;
        Width = 440;
        Height = 520;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = ConfigManager.Current.AlwaysOnTop;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AllowDrop = true;

        rootGrid = new Grid();
        bubbleText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 14,
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
            Padding = new Thickness(8, 4, 8, 4),
            MaxWidth = 240,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 34, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        rootGrid.Children.Add(bubbleText);
        Content = rootGrid;

        bubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        bubbleTimer.Tick += (_, _) =>
        {
            bubbleTimer.Stop();
            if (bubbleText != null) bubbleText.Visibility = Visibility.Collapsed;
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        DragOver += OnDragOver;
        Drop += OnDrop;
        ContextMenu = BuildContextMenu();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            pet = new PetInstance
            {
                PetId = Path.GetFileName(petDirectory),
                DisplayName = Path.GetFileName(petDirectory),
                WindowWidth = Width,
                WindowHeight = Height,
            };
            pet.PositionChanged += OnPetPositionChanged;
            pet.AI.BubbleRequested += OnBubbleRequested;
            pet.Initialize(petDirectory);
            pet.FitToWindow(Width, Height);
            pet.WindowHandle = new WindowInteropHelper(this).Handle;
            pet.AI.WindowBoundsProvider = () => new Rect(pet.X, pet.Y, pet.WindowWidth, pet.WindowHeight);

            if (pet.Renderer != null)
            {
                pet.Renderer.PerformanceMode = performanceMode;
                pet.Renderer.HeadFollowEnabled = ConfigManager.Current.CursorTracking;
                pet.Renderer.HeadFollowSpeed = ConfigManager.Current.HeadFollowSpeed;
                rootGrid!.Children.Add(pet.Renderer);
            }
            pet.Stats.DecaySpeed = ConfigManager.Current.DecaySpeed;
            pet.AI.MoveSpeed = ConfigManager.Current.MoveSpeed;
            pet.AI.Activity = ConfigManager.Current.Activity;
            pet.AI.WalkMirror = ConfigManager.Current.WalkMirror;
            pet.AI.FacingRight = ConfigManager.Current.FacingRight;
            pet.AI.WalkArea = ConfigManager.Current.WalkArea;
            pet.AI.TaskbarOffset = ConfigManager.Current.TaskbarOffset;

            if (pet.Asset != null)
            {
                bool ok = pet.PlayAnimation("idle", true)
                    || pet.PlayAnimation("Relax", true)
                    || pet.PlayAnimation("Default", true)
                    || pet.PlayAnimation(pet.Asset.AnimationNames.FirstOrDefault() ?? "", true);
            }

            if (!pet.Load())
            {
                pet.SetPosition(
                    SystemParameters.WorkArea.Right - Width - 40 - (PetManager.WindowCount % 4) * 30,
                    SystemParameters.WorkArea.Bottom - Height);
            }

            lastRender = stopwatch.Elapsed;
            CompositionTarget.Rendering += OnRendering;
        }
        catch (Exception ex)
        {
            LogError("宠物窗口初始化失败", ex);
            Close();
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (pet == null) return;
        TimeSpan now = stopwatch.Elapsed;
        float delta = (float)(now - lastRender).TotalSeconds;
        lastRender = now;
        if (delta <= 0f) return;
        if (delta > 0.1f) delta = 0.1f;
        pet.Update(delta);
    }

    private void OnBubbleRequested(string text)
    {
        if (bubbleText == null) return;
        bubbleText.Text = text;
        bubbleText.Visibility = Visibility.Visible;
        bubbleTimer.Stop();
        bubbleTimer.Start();
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
        foreach (string path in paths) pet.Files.DropFile(path, screenPoint, directlyToPet);
        e.Handled = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // 管理模式：左键点击该宠物副本即删除。
        if (PetManager.ManagementMode)
        {
            Close();
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            Point cursorDip = ToDip(WindowFocus.GetCursorScreen());
            dragOffsetDip = new Point(cursorDip.X - Left, cursorDip.Y - Top);
            manualDragging = true;
            if (pet != null)
            {
                pet.AI.CancelWalk();
                pet.AI.IsDragging = true;
            }
            Mouse.Capture(this);
            e.Handled = true;
            return;
        }
        if (pet?.Renderer != null && IsHeadHit(e.GetPosition(this)))
        {
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

    private bool IsHeadHit(Point point)
    {
        if (pet?.Renderer == null || !pet.Renderer.HasHeadBone) return false;
        double dx = point.X - pet.Renderer.HeadScreenX;
        double dy = point.Y - pet.Renderer.HeadScreenY;
        return dx * dx + dy * dy <= 55.0 * 55.0;
    }

    private Point ToDip(Point screenPoint)
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget != null
            ? source.CompositionTarget.TransformFromDevice.Transform(screenPoint)
            : screenPoint;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var sit = new MenuItem { Header = "坐下", IsCheckable = true };
        sit.Click += (_, _) =>
        {
            if (pet == null) return;
            if (sit.IsChecked) pet.Sit();
            else pet.Stand();
        };
        var ct = new MenuItem { Header = "点击穿透", IsCheckable = true };
        ct.Click += (_, _) =>
        {
            clickThrough = ct.IsChecked;
            WindowFocus.SetClickThrough(this, clickThrough);
        };
        var mode = new MenuItem { Header = "性能模式" };
        foreach (var (label, tag) in new[]
        {
            ("优先当前任务", "FocusPriority"), ("优先桌宠", "PetPriority"),
            ("两者均衡", "Balanced"), ("自动判定", "Auto"),
        })
        {
            var item = new MenuItem { Header = label, Tag = tag, IsCheckable = true };
            item.Click += (_, _) =>
            {
                if (item.Tag is string t && Enum.TryParse<PetPerformanceMode>(t, out var m))
                {
                    performanceMode = m;
                    if (pet?.Renderer != null) pet.Renderer.PerformanceMode = m;
                    foreach (MenuItem other in mode.Items) other.IsChecked = ReferenceEquals(other, item);
                }
            };
            mode.Items.Add(item);
        }
        var size = new MenuItem { Header = "宠物大小" };
        foreach (double s in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            var item = new MenuItem { Header = $"{s * 100:F0}%", Tag = s, IsCheckable = true };
            item.Click += (_, _) =>
            {
                if (pet != null) pet.SetSizeMultiplier((double)item.Tag);
                UpdateSizeChecks();
            };
            size.Items.Add(item);
            sizeItems.Add(item);
        }
        menu.Opened += (_, _) => UpdateSizeChecks();

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(sit);
        menu.Items.Add(ct);
        menu.Items.Add(size);
        menu.Items.Add(mode);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private void UpdateSizeChecks()
    {
        foreach (var item in sizeItems)
        {
            if (item.Tag is double v)
                item.IsChecked = pet != null && Math.Abs(pet.SizeMultiplier - v) < 0.01;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        if (pet != null)
        {
            pet.AI.BubbleRequested -= OnBubbleRequested;
            pet.Save();
            pet.Dispose();
        }
        bubbleTimer.Stop();
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
        }
    }
}