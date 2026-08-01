using System;
using System.Windows;

namespace XpetX;

/// <summary>AI 状态。</summary>
public enum PetAiState
{
    Idle, Walk, Eat, Happy, Sad, Sleep, Curious, Dizzy, Disgust,
}

/// <summary>
/// 宠物 AI：状态机、随机走动、低属性气泡提示、光标悬停好奇、
/// 以及精力过低自动睡觉。
/// </summary>
public sealed class PetAI
{
    private readonly PetInstance owner;
    private readonly Random random = new Random();

    private double idleTimer;
    private double walkDuration;
    private double walkAngle;
    private double stateTimer;
    private double hoverTimer;
    private double bubbleCooldown;
    private bool hungerBubbleShown;
    private bool happinessBubbleShown;
    private bool walkingToFood;

    public PetAiState State { get; private set; } = PetAiState.Idle;

    /// <summary>移动速度倍率（来自全局配置 moveSpeed）。</summary>
    public double MoveSpeed { get; set; } = 1.0;

    /// <summary>活跃度：越高走动越频繁（来自全局配置 activity）。</summary>
    public double Activity { get; set; } = 1.0;

    /// <summary>走路镜像：未配备左右行走动画时，反向行走自动水平翻转。</summary>
    public bool WalkMirror { get; set; } = true;

    /// <summary>默认朝向（true=朝右）。</summary>
    public bool FacingRight { get; set; } = true;

    /// <summary>光标悬停触发 Curious（接口保留，默认关闭）。</summary>
    public bool HoverCuriousEnabled { get; set; }
    /// <summary>是否处于坐下状态（AI 停止走动，保持 Sit 动画）。</summary>
    public bool IsSitting { get; private set; }

    /// <summary>行走区域：taskbar=沿任务栏横走（默认），screen=全屏自由走动（接口保留）。</summary>
    public string WalkArea { get; set; } = "taskbar";
    /// <summary>宠物底部距任务栏的附加偏移（像素，负值=更贴任务栏）。</summary>
    public double TaskbarOffset { get; set; }
    /// <summary>取消当前行走并回到待机（拖拽开始前调用，避免松手后立刻又走）。</summary>
    public void CancelWalk()
    {
        walkDuration = 0;
        idleTimer = NextIdleDelay();
        walkingToFood = false;
        if (State == PetAiState.Walk) SetState(PetAiState.Idle);
    }

    /// <summary>是否正在被用户拖动（拖动期间 AI 暂停移动，避免重影/瞬移）。</summary>
    public bool IsDragging { get; set; }
    /// <summary>进食中（动画期间人物站定不动）。</summary>
    public bool IsEating { get; set; }

    /// <summary>饥饿阈值：饱食度低于该值才会去捡地上的食物。</summary>
    public double HungryThreshold { get; set; } = 80;

    /// <summary>由宿主提供窗口屏幕矩形（悬停检测与屏幕边缘判定用）。</summary>
    public Func<Rect>? WindowBoundsProvider { get; set; }

    /// <summary>请求显示气泡（文字）。</summary>
    public event Action<string>? BubbleRequested;

    public PetAI(PetInstance owner)
    {
        this.owner = owner;
        idleTimer = NextIdleDelay();
    }

    public void Update(float delta)
    {
        if (IsDragging || IsEating) return; // 拖动/进食期间冻结 AI
        if (bubbleCooldown > 0) bubbleCooldown -= delta;
        if (owner.Renderer != null)
        {
            // 只有待机时才看鼠标。
            owner.Renderer.HeadFollowStateActive = State == PetAiState.Idle;
        }
        UpdateBubbles();
        UpdateHover(delta);

        switch (State)
        {
            case PetAiState.Idle:
                if (IsSitting) break; // 坐下时不走动
                if (TryGoEatGroundFile(delta)) break; // 饿了先去吃地上的食物
                idleTimer -= delta;
                if (idleTimer <= 0) StartWalk();
                break;

            case PetAiState.Walk:
                UpdateWalk(delta);
                break;

            case PetAiState.Curious:
            case PetAiState.Happy:
            case PetAiState.Eat:
                stateTimer -= delta;
                if (stateTimer <= 0) SetState(PetAiState.Idle);
                break;

            case PetAiState.Sleep:
                stateTimer -= delta;
                if (stateTimer <= 0 || owner.Stats.Energy >= 80f) SetState(PetAiState.Idle);
                break;

            case PetAiState.Sad:
            case PetAiState.Dizzy:
            case PetAiState.Disgust:
                stateTimer -= delta;
                if (stateTimer <= 0) SetState(PetAiState.Idle);
                break;
        }

        // 精力过低自动睡觉（最高优先级）。
        if (State != PetAiState.Sleep && owner.Stats.Energy < 20f)
        {
            EnterSleep();
        }
    }

    /// <summary>喂食事件（外部/部件事件调用）。</summary>
    public void NotifyEat()
    {
        SetState(PetAiState.Eat);
        stateTimer = 2.5;
        BubbleRequested?.Invoke("好吃！");
    }

    /// <summary>互动/开心事件（如点击头部）。</summary>
    public void NotifyHappy()
    {
        SetState(PetAiState.Happy);
        stateTimer = 1.2;
        idleTimer = NextIdleDelay();
        // 互动动画只播一次，避免“按一下触发多次动画”。
        owner.PlayAnimation("Interact", false);
    }

    /// <summary>主动睡觉（不重复加精力，由调用方决定数值变化）。</summary>
    public void NotifySleep()
    {
        SetState(PetAiState.Sleep);
        stateTimer = 10.0;
    }

    /// <summary>坐下：停止走动，播放 Sit 动画。</summary>
    public void NotifySit()
    {
        IsSitting = true;
        idleTimer = double.MaxValue;
        walkDuration = 0;
        State = PetAiState.Idle;
        owner.PlayAnimation("Sit", true);
    }

    /// <summary>站立：恢复待机。</summary>
    public void NotifyStand()
    {
        IsSitting = false;
        idleTimer = NextIdleDelay();
        walkDuration = 0;
        State = PetAiState.Idle;
        owner.PlayAnimation("Relax", true);
    }

    private void EnterSleep()
    {
        SetState(PetAiState.Sleep);
        stateTimer = 10.0;
        owner.Stats.Sleep();
    }

    /// <summary>饥饿且有落地食物时：走过去吃掉；饱了返回 false。</summary>
    private bool TryGoEatGroundFile(float delta)
    {
        if (!owner.Files.TryGetNearestWaiting(owner.X, HungryThreshold, out DroppedFile? target, out double distance))
        {
            walkingToFood = false;
            return false;
        }
        if (target == null) return false;

        if (distance <= 26)
        {
            walkingToFood = false;
            owner.Files.EatFile(target);
            idleTimer = NextIdleDelay();
            return true; // 进食动画期间 AI 冻结，吃完由管理器触发开心
        }

        double speed = 70.0 * MoveSpeed;
        double step = speed * delta;
        double nx = target.ScreenX > owner.X ? owner.X + step : owner.X - step;
        if (Math.Abs(target.ScreenX - nx) < step) nx = target.ScreenX;
        owner.SetPosition(nx, owner.Y);
        if (owner.Renderer != null && WalkMirror)
        {
            owner.Renderer.FlipX = FacingRight ? target.ScreenX < owner.X : target.ScreenX > owner.X;
        }
        if (!walkingToFood)
        {
            walkingToFood = true;
            owner.PlayAnimation("Move", true);
        }
        return true;
    }

    private void StartWalk()
    {
        walkAngle = random.NextDouble() * Math.PI * 2;
        walkDuration = 1.0 + random.NextDouble() * 4.0; // 1-5 秒

        // 任务栏模式只横向走：若随机方向几乎垂直，重掷为横向，避免原地踏步。
        if (WalkArea != "screen")
        {
            for (int i = 0; i < 6 && Math.Abs(Math.Cos(walkAngle)) < 0.4; i++)
                walkAngle = random.NextDouble() * Math.PI * 2;
            if (Math.Abs(Math.Cos(walkAngle)) < 0.4)
                walkAngle = random.NextDouble() < 0.5 ? 0.0 : Math.PI;
        }

        // 靠近屏幕边缘时把方向折射回屏幕内，避免“起步即撞墙”的顿挫，
        // 也让宠物能横跨整个屏幕而不是困在某一边。
        Rect bounds = WindowBoundsProvider?.Invoke() ?? Rect.Empty;
        if (!bounds.IsEmpty)
        {
            Rect work = SystemParameters.WorkArea;
            double cos = Math.Cos(walkAngle);
            double sin = Math.Sin(walkAngle);
            double maxX = work.Right - bounds.Width;
            double maxY = work.Bottom - bounds.Height;
            if (owner.X - work.Left < 40 && cos < 0) walkAngle = Math.PI - walkAngle;
            else if (maxX - owner.X < 40 && cos > 0) walkAngle = Math.PI - walkAngle;
            if (WalkArea == "screen")
            {
                if (owner.Y - work.Top < 40 && sin < 0) walkAngle = -walkAngle;
                else if (maxY - owner.Y < 40 && sin > 0) walkAngle = -walkAngle;
            }
        }
        SetState(PetAiState.Walk);
    }

    private void UpdateWalk(float delta)
    {
        if (IsDragging) return; // 拖动期间冻结位置更新
        walkDuration -= delta;
        if (walkDuration <= 0)
        {
            SetState(PetAiState.Idle);
            return;
        }

        Rect bounds = WindowBoundsProvider?.Invoke() ?? Rect.Empty;
        if (bounds.IsEmpty)
        {
            SetState(PetAiState.Idle);
            return;
        }

        double speed = 60.0 * MoveSpeed;
        double dx = Math.Cos(walkAngle) * speed * delta;
        double dy = Math.Sin(walkAngle) * speed * delta;

        if (owner.Renderer != null && WalkMirror)
        {
            bool movingLeft = Math.Cos(walkAngle) < 0;
            owner.Renderer.FlipX = FacingRight ? movingLeft : !movingLeft;
        }
        Rect work = SystemParameters.WorkArea;
        bool taskbarOnly = WalkArea != "screen";
        double minX = work.Left;
        double maxX = work.Right - bounds.Width;
        double targetY = taskbarOnly ? owner.Y : owner.Y + dy; // 任务栏模式保持当前高度行走，不强制贴底
        double minY = taskbarOnly ? targetY : work.Top;
        double maxY = taskbarOnly ? targetY : work.Bottom - bounds.Height;

        double nx = owner.X + dx;
        double ny = targetY;
        // 任务栏模式 Y 固定，只在 X 轴判边缘；自由模式两轴都判。
        bool edgeHit = nx <= minX || nx >= maxX;
        if (!taskbarOnly && (ny <= minY || ny >= maxY)) edgeHit = true;
        if (edgeHit)
        {
            // 碰到屏幕边缘，停下回到待机。
            owner.SetPosition(Math.Clamp(nx, minX, maxX), Math.Clamp(ny, minY, maxY));
            SetState(PetAiState.Idle);
            return;
        }
        owner.SetPosition(nx, ny);
    }

    private void UpdateHover(float delta)
    {
        if (State == PetAiState.Sleep || WindowBoundsProvider == null || !HoverCuriousEnabled)
        {
            hoverTimer = 0;
            return;
        }

        Rect bounds = WindowBoundsProvider();
        Point cursor = WindowFocus.GetCursorScreen();
        if (bounds.Contains(cursor))
        {
            hoverTimer += delta;
            if (hoverTimer >= 1.5 && State != PetAiState.Curious)
            {
                hoverTimer = 0;
                SetState(PetAiState.Curious);
                stateTimer = 3.5;
            }
        }
        else
        {
            hoverTimer = 0;
            if (State == PetAiState.Curious) SetState(PetAiState.Idle);
        }
    }

    private void UpdateBubbles()
    {
        var stats = owner.Stats;
        if (stats.Hunger >= 35f) hungerBubbleShown = false;
        if (stats.Happiness >= 35f) happinessBubbleShown = false;
        if (bubbleCooldown > 0) return;

        if (stats.Hunger < 30f && !hungerBubbleShown)
        {
            hungerBubbleShown = true;
            bubbleCooldown = 20.0;
            BubbleRequested?.Invoke("好饿~");
        }
        else if (stats.Happiness < 30f && !happinessBubbleShown)
        {
            happinessBubbleShown = true;
            bubbleCooldown = 20.0;
            BubbleRequested?.Invoke("好无聊...");
        }
    }

    private void SetState(PetAiState state)
    {
        if (State == state) return;
        State = state;
        // 镜像由 UpdateWalk 按行走方向控制；离开走路时保留最后朝向，避免原地回弹。
        owner.PlayAnimation(AnimationFor(state), true);
        // 坐下时回到待机保持坐姿。
        if (IsSitting && state == PetAiState.Idle) owner.PlayAnimation("Sit", true);
    }

    /// <summary>AI 状态到可用动画的映射（素材没有对应动画时 PlayAnimation 自动忽略）。</summary>
    private static string AnimationFor(PetAiState state)
    {
        switch (state)
        {
            case PetAiState.Idle: return "Relax";
            case PetAiState.Walk: return "Move";
            case PetAiState.Eat: return "Default";
            case PetAiState.Happy: return "Interact";
            case PetAiState.Sad: return "Default";
            case PetAiState.Sleep: return "Sleep";
            case PetAiState.Curious: return "Interact";
            case PetAiState.Dizzy: return "Default";
            case PetAiState.Disgust: return "Default";
            default: return "Relax";
        }
    }

    private double NextIdleDelay()
    {
        // 活跃度越高，待机间隔越短（走得更频繁）。
        return (3.0 + random.NextDouble() * 5.0) / Math.Max(0.1, Activity);
    }
}