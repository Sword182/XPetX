using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace XpetX;

/// <summary>
/// 一只桌面宠物的实例：负责加载 Spine 素材、控制动画与位置，
/// 持有数值系统（PetStats）与 AI（PetAI），并负责存档/读档。
/// </summary>
public sealed class PetInstance : IDisposable
{
    public string PetId { get; set; } = "pet";

    public string DisplayName { get; set; } = "宠物";

    /// <summary>窗口在屏幕上的 X 坐标。</summary>
    public double X { get; private set; }

    /// <summary>窗口在屏幕上的 Y 坐标。</summary>
    public double Y { get; private set; }

    /// <summary>宠物在窗口中的显示高度（像素，用于图标大小适配）。</summary>
    public double CharacterHeightPx { get; private set; }

    /// <summary>窗口尺寸（由宿主设置，用于 AI 边缘判定与存档位置夹取）。</summary>
    public double WindowWidth { get; set; } = 440;

    public double WindowHeight { get; set; } = 520;

    public bool IsVisible { get; set; } = true;

    public SpineAsset? Asset { get; private set; }

    public SpineRenderer? Renderer { get; private set; }

    /// <summary>数值系统。</summary>
    public PetStats Stats { get; } = new PetStats();

    /// <summary>文件喂食管理（拖放、落地、进食）。</summary>
    public PetFileManager Files { get; }

    /// <summary>各文件类型的喜爱倾向（save.json 的 foodPreferences）。</summary>
    public Dictionary<string, float> FoodPreferences { get; } = new Dictionary<string, float>();

    /// <summary>宠物窗口句柄（宿主设置，用于地面判定）。</summary>
    public IntPtr WindowHandle { get; set; }

    /// <summary>AI 行为状态机。</summary>
    public PetAI AI { get; }

    /// <summary>调用 <see cref="SetPosition"/> 后触发，用于移动窗口。</summary>
    public event Action? PositionChanged;

    /// <summary>宠物大小变化时触发（UI 据此更新悬浮按钮等）。</summary>
    public event Action? SizeChanged;

    /// <summary>存档路径 pets/{PetId}/save.json。</summary>
    public string? SavePath => savePath;

    private string? savePath;
    private float localAnimationRemaining;

    public PetInstance()
    {
        AI = new PetAI(this);
        Files = new PetFileManager(this);
    }

    /// <summary>
    /// 从宠物目录加载 Spine 素材。目录内应有 spine 子目录（.skel/.atlas/.png），
    /// 或者素材直接位于传入目录中。
    /// </summary>
    public void Initialize(string petDirectory)
    {
        // 真实显示名从宠物包内 name.json 读取（无则回退文件夹名）。
        DisplayName = PetNaming.GetDisplayName(petDirectory);

        string spineDir = Directory.Exists(Path.Combine(petDirectory, "spine"))
            ? Path.Combine(petDirectory, "spine")
            : petDirectory;

        string skeletonFile = Directory.GetFiles(spineDir, "*.skel").FirstOrDefault()
            ?? throw new FileNotFoundException($"目录中没有 .skel 文件: {spineDir}");
        string atlasFile = Directory.GetFiles(spineDir, "*.atlas").FirstOrDefault()
            ?? throw new FileNotFoundException($"目录中没有 .atlas 文件: {spineDir}");

        Asset = SpineLoader.Load(skeletonFile, atlasFile);
        Renderer = new SpineRenderer();
        Renderer.Attach(Asset);

        // 存档路径：pets/{petId}/save.json（与素材同级的 pets 根目录）。
        string petsRoot = Directory.GetParent(Path.GetFullPath(petDirectory))?.FullName ?? petDirectory;
        savePath = Path.Combine(petsRoot, PetId, "save.json");

        // 默认动画优先级：idle > Relax > Default > 素材中第一个动画（Default 可能是静态姿势）。
        string? defaultAnimation = FindDefaultAnimation();
        if (defaultAnimation != null)
            PlayAnimation(defaultAnimation, true);
    }

    /// <summary>按窗口尺寸缩放骨骼并把它放到窗口中央、底部对齐。</summary>
    public void FitToWindow(double windowWidth, double windowHeight, double margin = 24)
    {
        if (Asset == null || Renderer == null) return;

        // 用所有动画的并集包围盒适配，保证坐下/睡眠等姿势的脚部不会被裁掉。
        float[]? buffer = null;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        lock (Asset.AnimationLock)
        {
            var skeleton = Asset.Skeleton;
            var state = Asset.AnimationState;
            foreach (var anim in Asset.SkeletonData.Animations)
            {
                state.SetAnimation(0, anim.Name, true);
                state.Update(0.05f);
                state.Apply(skeleton);
                skeleton.Update(0.05f);
                skeleton.UpdateWorldTransform();
                skeleton.GetBounds(out float bx, out float by, out float bw, out float bh, ref buffer);
                minX = Math.Min(minX, bx);
                minY = Math.Min(minY, by);
                maxX = Math.Max(maxX, bx + bw);
                maxY = Math.Max(maxY, by + bh);
            }
        }
        float unionW = maxX - minX;
        float unionH = maxY - minY;
        if (unionW <= 0 || unionH <= 0) return;

        double scale = Math.Min(
            (windowWidth - margin * 2) / unionW,
            (windowHeight - margin * 2) / unionH);
        baseScale = scale;
        baseMinX = minX;
        baseUnionW = unionW;
        baseMinY = minY;
        baseMargin = margin;
        CharacterHeightPx = unionH * scale;
        ApplySize();
    }

    private double baseScale = 1;
    private float baseMinX;
    private float baseUnionW = 1;
    private float baseMinY;
    private double baseMargin = 24;
    private double sizeMultiplier = 1.0;

    /// <summary>当前大小倍率（0.5~2.0）。</summary>
    public double SizeMultiplier { get { return sizeMultiplier; } }

    /// <summary>调整宠物显示大小（围绕同一锚点缩放）。</summary>
    public void SetSizeMultiplier(double multiplier)
    {
        sizeMultiplier = Math.Clamp(multiplier, 0.5, 2.0);
        if (Renderer != null) ApplySize();
        SizeChanged?.Invoke();
    }

    private void ApplySize()
    {
        double scale = baseScale * sizeMultiplier;
        Renderer.Scale = scale;
        Renderer.OffsetX = WindowWidth / 2 - (baseMinX + baseUnionW / 2) * scale;
        Renderer.OffsetY = WindowHeight - baseMargin + baseMinY * scale;
    }

    /// <summary>全局动画：覆盖当前行为（轨道 0，AI 行为动画走这里）。名称不存在时返回 false。</summary>
    public bool PlayAnimation(string name, bool loop)
    {
        if (Asset == null || string.IsNullOrEmpty(name) || !Asset.HasAnimation(name))
            return false;
        lock (Asset.AnimationLock)
        {
            Asset.AnimationState.SetAnimation(0, name, loop);
        }
        return true;
    }

    /// <summary>
    /// 局部动画：叠加在当前行为上播放（轨道 1），不打断行为动画。
    /// 非循环动画播完自动结束；循环动画可指定自动停止时长（秒，0=一直播）。
    /// </summary>
    public bool PlayLocalAnimation(string name, bool loop = false, float duration = 0f)
    {
        if (Asset == null || string.IsNullOrEmpty(name) || !Asset.HasAnimation(name))
            return false;
        lock (Asset.AnimationLock)
        {
            Asset.AnimationState.SetAnimation(1, name, loop);
        }
        localAnimationRemaining = loop && duration > 0 ? duration : 0f;
        return true;
    }

    /// <summary>立即清除局部动画（轨道 1）。</summary>
    public void ClearLocalAnimation()
    {
        if (Asset == null) return;
        lock (Asset.AnimationLock)
        {
            Asset.AnimationState.ClearTrack(1);
        }
        localAnimationRemaining = 0f;
    }

    /// <summary>点击头部互动：修改数值并进入开心状态。</summary>
    public void PlayInteract()
    {
        if (Asset == null || !Asset.HasAnimation("Interact")) return;
        Stats.Play();
        AI.NotifyHappy();
    }

    /// <summary>喂食（部件/外部事件）。</summary>
    public void Feed()
    {
        Stats.Feed();
        AI.NotifyEat();
    }

    /// <summary>陪玩（部件/外部事件）。</summary>
    public void Play()
    {
        Stats.Play();
        AI.NotifyHappy();
    }

    /// <summary>睡觉（部件/外部事件）。</summary>
    public void Sleep()
    {
        Stats.Sleep();
        AI.NotifySleep();
    }

    /// <summary>坐下：播放 Sit 动画并停止走动。</summary>
    public void Sit()
    {
        AI.NotifySit();
    }

    /// <summary>站立：恢复待机。</summary>
    public void Stand()
    {
        AI.NotifyStand();
    }

    /// <summary>每帧调用：推进数值、AI 与渲染。</summary>
    public void Update(float deltaTime)
    {
        if (!IsVisible) return;
        Stats.Update(deltaTime);
        AI.Update(deltaTime);
        if (localAnimationRemaining > 0)
        {
            localAnimationRemaining -= deltaTime;
            if (localAnimationRemaining <= 0) ClearLocalAnimation();
        }
        Files.Update(deltaTime);
        Renderer?.Tick(deltaTime);
    }

    /// <summary>设置屏幕位置并通知窗口移动。</summary>
    public void SetPosition(double x, double y)
    {
        X = x;
        Y = y;
        PositionChanged?.Invoke();
    }

    /// <summary>静默同步位置（拖拽过程中用，不触发 PositionChanged，避免与窗口移动互相干扰）。</summary>
    internal void SyncPosition(double x, double y)
    {
        X = x;
        Y = y;
    }

    /// <summary>保存存档到 pets/{PetId}/save.json。</summary>
    public void Save()
    {
        try
        {
            if (string.IsNullOrEmpty(savePath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(savePath) ?? ".");
            var data = new SaveData
            {
                hunger = Stats.Hunger,
                happiness = Stats.Happiness,
                energy = Stats.Energy,
                positionX = (float)X,
                positionY = (float)Y,
                lastSaveTime = DateTime.Now,
                foodPreferences = new Dictionary<string, float>(FoodPreferences),
            };
            File.WriteAllText(savePath,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 存档失败不影响运行。
        }
    }

    /// <summary>
    /// 从 save.json 加载：恢复数值与位置，并按离线时长补偿衰减（上限 8 小时，避免长时间离线全部归零）。
    /// </summary>
    public bool Load()
    {
        try
        {
            if (string.IsNullOrEmpty(savePath) || !File.Exists(savePath)) return false;
            var data = JsonSerializer.Deserialize<SaveData>(File.ReadAllText(savePath));
            if (data == null) return false;

            Stats.SetValues(data.hunger, data.happiness, data.energy);
            FoodPreferences.Clear();
            if (data.foodPreferences != null)
            {
                foreach (var pair in data.foodPreferences)
                    FoodPreferences[pair.Key] = pair.Value;
            }
            double elapsed = Math.Min((DateTime.Now - data.lastSaveTime).TotalSeconds, 8 * 3600);
            if (elapsed > 0)
            {
                // 离线补偿：饱食/快乐按时间衰减但保底 30；离线视为休息，精力恢复到至少 60，
                // 避免一启动就因精力过低自动躺下。
                float decay = 0.05f * (float)Stats.DecaySpeed * (float)elapsed;
                Stats.SetValues(
                    Math.Max(30f, data.hunger - decay),
                    Math.Max(30f, data.happiness - decay),
                    Math.Max(60f, data.energy));
            }

            Rect work = SystemParameters.WorkArea;
            double px = Math.Clamp(data.positionX, work.Left, Math.Max(work.Left, work.Right - WindowWidth));
            double py = Math.Clamp(data.positionY, work.Top, Math.Max(work.Top, work.Bottom - WindowHeight));
            X = px;
            Y = py;
            PositionChanged?.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string? FindDefaultAnimation()
    {
        if (Asset == null) return null;
        if (Asset.HasAnimation("idle")) return "idle";
        if (Asset.HasAnimation("Relax")) return "Relax";
        if (Asset.HasAnimation("Default")) return "Default";
        return Asset.AnimationNames.FirstOrDefault();
    }

    public void Dispose()
    {
        Renderer?.Dispose();
        Renderer = null;
        Asset?.Dispose();
        Asset = null;
    }

    /// <summary>save.json 结构。</summary>
    private sealed class SaveData
    {
        public float hunger { get; set; } = 80;
        public float happiness { get; set; } = 80;
        public float energy { get; set; } = 80;
        public float positionX { get; set; }
        public float positionY { get; set; }
        public DateTime lastSaveTime { get; set; } = DateTime.Now;
        public Dictionary<string, float> foodPreferences { get; set; } = new Dictionary<string, float>();
    }
}