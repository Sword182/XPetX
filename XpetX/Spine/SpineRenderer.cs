using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Spine;

namespace XpetX;

/// <summary>
/// Spine 渲染控件。渲染（动画更新 + 软件光栅化）在后台线程执行，
/// UI 线程只负责把最新一帧写入 WriteableBitmap 并呈现。
/// 位图固定为窗口尺寸，分辨率变化只改变渲染区域，避免重建位图导致闪烁。
/// </summary>
public sealed class SpineRenderer : FrameworkElement, IDisposable
{
    private SpineAsset? asset;
    private WriteableBitmap? frame;
    private byte[] renderBuffer = Array.Empty<byte>();
    private byte[] presentBuffer = Array.Empty<byte>();

    private readonly object syncLock = new object();
    private Thread? renderThread;
    private bool hasNewFrame;
    private bool stopThread;
    private float[] worldVertices = new float[64];
    private int bitmapWidth, bitmapHeight;
    private int frameWidth, frameHeight;
    private long lastFrameTicks;
    private long renderedFrames;
    private long presentedFrames;
    private Bone? headBone;
    private readonly List<Bone> trackBones = new List<Bone>();
    private double headFollowCurrent;

    /// <summary>骨骼世界坐标到屏幕坐标的缩放。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>骨骼原点映射到的屏幕 X（向右）。</summary>
    public double OffsetX { get; set; }

    /// <summary>骨骼原点映射到的屏幕 Y（向下，Spine 的 Y 轴向上会被翻转）。</summary>
    public double OffsetY { get; set; }

    /// <summary>当前实际使用的渲染分辨率比例（自适应调整，范围见 MinRenderScale / MaxRenderScale）。</summary>
    public double RenderScale { get; set; } = 0.6;

    /// <summary>渲染分辨率的动态下限（不再降到更低，保证桌宠清晰度）。</summary>
    public double MinRenderScale { get; set; } = 0.6;

    /// <summary>渲染分辨率的动态上限：空闲时使用满分辨率。</summary>
    public double MaxRenderScale { get; set; } = 1.0;

    /// <summary>性能模式：决定与前台应用争抢资源时如何取舍。</summary>
    public PetPerformanceMode PerformanceMode { get; set; } = PetPerformanceMode.Auto;

    /// <summary>宠物窗口是否拥有焦点（由宿主周期刷新）。</summary>
    public bool IsForeground { get; set; }

    /// <summary>前台窗口是否处于全屏/沉浸模式（由宿主周期刷新）。</summary>
    public bool IsForegroundImmersive { get; set; }

    /// <summary>是否在渲染结果之上绘制骨骼结构调试线。</summary>
    public bool ShowBones { get; set; }
    /// <summary>暂停后台渲染（窗口隐藏时置 true，节省 CPU）。</summary>
    public bool Paused { get; set; }

    /// <summary>渲染线程最近实测的每帧渲染耗时（EMA，毫秒）。</summary>
    public double RenderMsEma { get { return renderMsEma; } }

    /// <summary>后台线程累计完成的渲染帧数（诊断用）。</summary>
    public long RenderedFrames { get { return Interlocked.Read(ref renderedFrames); } }

    /// <summary>UI 线程累计呈现到屏幕的帧数（诊断用）。</summary>
    public long PresentedFrames { get { return Interlocked.Read(ref presentedFrames); } }
    /// <summary>是否找到头部骨骼（点击头部互动的命中基准）。</summary>
    public bool HasHeadBone { get { return headBone != null; } }

    /// <summary>头部骨骼当前的屏幕 X（后台线程每帧更新，窗口坐标）。</summary>
    public double HeadScreenX { get; private set; }

    /// <summary>头部骨骼当前的屏幕 Y（后台线程每帧更新，窗口坐标）。</summary>
    public double HeadScreenY { get; private set; }

    /// <summary>是否启用鼠标 Y 轴头部追踪（接口保留，可对单个宠物关闭）。</summary>
    public bool HeadFollowEnabled { get; set; }
    /// <summary>头部追踪状态开关：由 AI 控制，只有待机(Idle)时为 true（接口保留）。</summary>
    public bool HeadFollowStateActive { get; set; }

    /// <summary>头部追踪平滑速度（来自全局配置 headFollowSpeed）。</summary>
    public double HeadFollowSpeed { get; set; } = 5.0;

    /// <summary>窗口屏幕 X（宿主移动窗口时刷新，头部追踪换算用）。</summary>
    public double WindowScreenX { get; set; }

    /// <summary>窗口屏幕 Y（宿主移动窗口时刷新，头部追踪换算用）。</summary>
    public double WindowScreenY { get; set; }
    /// <summary>水平镜像（走路方向翻面用；ScaleX = -1 实现）。</summary>
    public bool FlipX { get; set; }

    private double renderMsEma = -1;
    private int adaptCounter;

    public void Attach(SpineAsset value)
    {
        asset = value;
        headBone = value.Skeleton.FindBone("F_Head");
        if (headBone == null)
        {
            foreach (Bone bone in value.Skeleton.Bones)
            {
                if (bone.Data.Name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    headBone = bone;
                    break;
                }
            }
        }

        // 光标追踪目标：优先眼珠骨骼；没有眼珠骨骼时退回头部。
        trackBones.Clear();
        foreach (Bone bone in value.Skeleton.Bones)
        {
            if (bone.Data.Name.IndexOf("eye", StringComparison.OrdinalIgnoreCase) >= 0)
                trackBones.Add(bone);
        }
        if (trackBones.Count == 0 && headBone != null) trackBones.Add(headBone);
        EnsureFrame();
        StartRenderLoop();
        InvalidateVisual();
    }

    /// <summary>UI 线程每帧调用：请求后台渲染一帧，并把已完成的最新帧呈现到屏幕。</summary>
    public void Tick(float delta)
    {
        EnsureFrame();
        if (asset == null) return;

        lock (syncLock)
        {
            if (hasNewFrame && frame != null &&
                presentBuffer.Length >= frameWidth * frameHeight * 4)
            {
                hasNewFrame = false;
                frame.WritePixels(
                    new Int32Rect(0, 0, frameWidth, frameHeight),
                    presentBuffer, frameWidth * 4, 0);
                presentedFrames++;
            }
        }
        InvalidateVisual();
    }

    private void StartRenderLoop()
    {
        if (renderThread != null) return;
        stopThread = false;
        renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "SpineRender",
            Priority = ThreadPriority.BelowNormal,
        };
        renderThread.Start();
    }

    private void RenderLoop()
    {
        var sw = Stopwatch.StartNew();
        lastFrameTicks = sw.ElapsedTicks;
        long frameTicks = Stopwatch.Frequency / 60;
        while (!stopThread)
        {
            if (Paused || asset == null)
            {
                Thread.Sleep(50);
                continue;
            }

            long now = sw.ElapsedTicks;
            float delta = (float)(now - lastFrameTicks) / (float)Stopwatch.Frequency;
            lastFrameTicks = now;
            if (delta <= 0f) continue;
            if (delta > 0.1f) delta = 0.1f;

            ApplyThreadPriority();

            lock (asset.AnimationLock)
            {
                asset.AnimationState.Update(delta);
                asset.AnimationState.Apply(asset.Skeleton);

                // 走路镜像：整体水平翻转。
                asset.Skeleton.ScaleX = FlipX ? -1f : 1f;

                // 光标追踪（优先眼珠骨骼，无眼珠骨骼时用头部）：按与宠物的相对位置计算角度，
                // 光标在身后时宠物转向；非追踪状态平滑衰减角度，避免抽动。
                if (trackBones.Count > 0)
                {
                    if (HeadFollowEnabled && HeadFollowStateActive)
                    {
                        double cursorX = GetCursorScreenX() - WindowScreenX;
                        double cursorY = GetCursorScreenY() - WindowScreenY;
                        Bone primary = trackBones[0];
                        double boneX = OffsetX + primary.WorldX * Scale; // 上一帧世界坐标，滞后一帧可接受
                        double boneY = OffsetY - primary.WorldY * Scale;
                        bool cursorOnRight = cursorX >= boneX;
                        bool facingRight = !FlipX;
                        if (cursorOnRight != facingRight) FlipX = cursorOnRight;
                        double desired = Math.Clamp((boneY - cursorY) * 0.12, -30.0, 30.0);
                        double k = Math.Min(1.0, HeadFollowSpeed * delta);
                        headFollowCurrent += (desired - headFollowCurrent) * k;
                    }
                    else
                    {
                        // 平滑衰减回 0，把头部交还给动画，避免瞬间回弹。
                        headFollowCurrent *= Math.Max(0.0, 1.0 - 10.0 * delta);
                    }
                    if (Math.Abs(headFollowCurrent) > 0.3f)
                    {
                        foreach (Bone bone in trackBones) bone.Rotation = (float)headFollowCurrent;
                    }
                }

                asset.Skeleton.Update(delta);
                asset.Skeleton.UpdateWorldTransform();
            }

            if (headBone != null)
            {
                HeadScreenX = OffsetX + headBone.WorldX * Scale;
                HeadScreenY = OffsetY - headBone.WorldY * Scale;
            }

            var renderSw = Stopwatch.StartNew();
            byte[]? rendered = RenderFrame();
            renderSw.Stop();
            AdaptRenderScale(renderSw.Elapsed.TotalMilliseconds);
            if (rendered == null) continue;
            renderedFrames++;

            lock (syncLock)
            {
                // 仅当缓冲未被 UI 线程重建时才交换；重建期间该帧直接丢弃，避免空帧闪烁。
                if (ReferenceEquals(rendered, renderBuffer))
                {
                    renderBuffer = presentBuffer;
                    presentBuffer = rendered;
                    hasNewFrame = true;
                }
            }

            // 按 60Hz 自定节奏：渲染耗时不足一帧时休眠补齐，动画时间仍按真实时钟推进。
            long spent = sw.ElapsedTicks - now;
            long remain = frameTicks - spent;
            if (remain > 0)
            {
                long remainMs = remain * 1000 / Stopwatch.Frequency;
                if (remainMs > 2) Thread.Sleep((int)(remainMs - 1));
                while (sw.ElapsedTicks - now < frameTicks)
                {
                    // 短促自旋等待，避免 Sleep 粒度不足导致帧率抖动。
                }
            }
        }
    }

    /// <summary>按性能模式调整后台线程优先级：游戏优先时让出 CPU，桌宠优先时正常调度。</summary>
    private void ApplyThreadPriority()
    {
        if (renderThread == null) return;
        ThreadPriority priority = PerformanceMode == PetPerformanceMode.PetPriority
            || (PerformanceMode == PetPerformanceMode.FocusPriority && IsForeground)
            ? ThreadPriority.Normal
            : ThreadPriority.BelowNormal;
        if (renderThread.Priority != priority) renderThread.Priority = priority;
    }

    private void AdaptRenderScale(double renderMs)
    {
        renderMsEma = renderMsEma < 0 ? renderMs : renderMsEma * 0.9 + renderMs * 0.1;
        adaptCounter++;
        if (adaptCounter < 20) return;
        adaptCounter = 0;

        switch (PerformanceMode)
        {
            case PetPerformanceMode.PetPriority:
                RenderScale = MaxRenderScale;
                break;

            case PetPerformanceMode.FocusPriority:
                // 游戏对焦时：质量上限压到 0.75，把更多 CPU 让给游戏，但不再暴跌分辨率。
                AdaptBalanced(renderMsEma, IsForeground ? MaxRenderScale : Math.Min(0.75, MaxRenderScale));
                break;

            case PetPerformanceMode.Auto:
                AdaptBalanced(renderMsEma,
                    (!IsForeground && IsForegroundImmersive) ? Math.Min(0.75, MaxRenderScale) : MaxRenderScale);
                break;

            default:
                AdaptBalanced(renderMsEma, MaxRenderScale);
                break;
        }
    }

    private void AdaptBalanced(double ema, double cap)
    {
        if (ema > 14.0 && RenderScale > MinRenderScale)
            RenderScale = Math.Max(MinRenderScale, RenderScale * 0.9);
        else if (ema < 6.0 && RenderScale < cap)
            RenderScale = Math.Min(cap, RenderScale * 1.1);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        EnsureFrame();
    }

    /// <summary>
    /// 位图固定为窗口整尺寸（永不重建，避免闪烁）；
    /// 当前渲染区域大小随 RenderScale 变化，绘制到缓冲的前 frameWidth×frameHeight 区域。
    /// </summary>
    private void EnsureFrame()
    {
        int w = Math.Max(1, (int)Math.Ceiling(Math.Max(1, ActualWidth)));
        int h = Math.Max(1, (int)Math.Ceiling(Math.Max(1, ActualHeight)));
        lock (syncLock)
        {
            if (frame == null || bitmapWidth != w || bitmapHeight != h)
            {
                bitmapWidth = w;
                bitmapHeight = h;
                frame = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                hasNewFrame = false;
            }

            int rw = Math.Max(1, (int)Math.Ceiling(w * Math.Max(0.05, RenderScale)));
            int rh = Math.Max(1, (int)Math.Ceiling(h * Math.Max(0.05, RenderScale)));
            int newW = Math.Min(rw, w);
            int newH = Math.Min(rh, h);
            if (newW != frameWidth || newH != frameHeight)
            {
                frameWidth = newW;
                frameHeight = newH;
                int bytes = newW * newH * 4;
                renderBuffer = new byte[bytes];
                presentBuffer = new byte[bytes];
                hasNewFrame = false;
            }
        }
    }

    private byte[]? RenderFrame()
    {
        if (asset == null || frame == null) return null;

        // 在同一把锁下取得一致的尺寸与缓冲快照，避免 UI 重建缓冲时越界。
        int fw, fh;
        byte[] buffer;
        lock (syncLock)
        {
            fw = frameWidth;
            fh = frameHeight;
            buffer = renderBuffer;
        }
        if (fw <= 0 || fh <= 0 || buffer.Length < fw * fh * 4) return null;
        Array.Clear(buffer, 0, fw * fh * 4);

        Skeleton skeleton = asset.Skeleton;
        float tintR = skeleton.R, tintG = skeleton.G, tintB = skeleton.B, tintA = skeleton.A;
        double scale = Scale;

        foreach (Slot slot in skeleton.DrawOrder)
        {
            Attachment? attachment = slot.Attachment;
            if (attachment == null) continue;
            if (slot.A * tintA <= 0.001f) continue;

            if (attachment is RegionAttachment region)
                DrawRegion(slot, region, tintR, tintG, tintB, tintA, scale, buffer, fw, fh);
            else if (attachment is MeshAttachment mesh)
                DrawMesh(slot, mesh, tintR, tintG, tintB, tintA, scale, buffer, fw, fh);
        }
        return buffer;
    }

    private void DrawRegion(Slot slot, RegionAttachment region, float tintR, float tintG, float tintB, float tintA, double scale, byte[] buffer, int fw, int fh)
    {
        if (!(region.RendererObject is AtlasRegion atlasRegion) ||
            !(atlasRegion.page.rendererObject is SpineTexture texture)) return;

        float[] vertices = worldVertices;
        if (vertices.Length < 8) vertices = worldVertices = new float[8];
        region.ComputeWorldVertices(slot.Bone, vertices, 0, 2);
        float[] uvs = region.UVs;
        if (uvs.Length < 8) return;

        float r = region.R * tintR;
        float g = region.G * tintG;
        float b = region.B * tintB;
        float a = region.A * slot.A * tintA;
        if (a <= 0.001f) return;

        float ax = (float)((OffsetX + vertices[0] * scale) * RenderScale), ay = (float)((OffsetY - vertices[1] * scale) * RenderScale);
        float bx = (float)((OffsetX + vertices[2] * scale) * RenderScale), by = (float)((OffsetY - vertices[3] * scale) * RenderScale);
        float cx = (float)((OffsetX + vertices[4] * scale) * RenderScale), cy = (float)((OffsetY - vertices[5] * scale) * RenderScale);
        float dx = (float)((OffsetX + vertices[6] * scale) * RenderScale), dy = (float)((OffsetY - vertices[7] * scale) * RenderScale);

        FillTriangle(texture,
            ax, ay, uvs[0], uvs[1],
            bx, by, uvs[2], uvs[3],
            cx, cy, uvs[4], uvs[5], r, g, b, a, buffer, fw, fh);
        FillTriangle(texture,
            ax, ay, uvs[0], uvs[1],
            cx, cy, uvs[4], uvs[5],
            dx, dy, uvs[6], uvs[7], r, g, b, a, buffer, fw, fh);
    }

    private void DrawMesh(Slot slot, MeshAttachment mesh, float tintR, float tintG, float tintB, float tintA, double scale, byte[] buffer, int fw, int fh)
    {
        if (!(mesh.RendererObject is AtlasRegion atlasRegion) ||
            !(atlasRegion.page.rendererObject is SpineTexture texture)) return;

        int vertexCount = mesh.WorldVerticesLength;
        float[] uvs = mesh.UVs;
        int[] triangles = mesh.Triangles;
        if (uvs == null || uvs.Length < vertexCount || triangles == null) return;

        float[] vertices = worldVertices;
        if (vertices.Length < vertexCount) vertices = worldVertices = new float[vertexCount];
        mesh.ComputeWorldVertices(slot, 0, vertexCount, vertices, 0, 2);

        float r = mesh.R * tintR;
        float g = mesh.G * tintG;
        float b = mesh.B * tintB;
        float a = mesh.A * slot.A * tintA;
        if (a <= 0.001f) return;

        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            int va = triangles[i] * 2, vb = triangles[i + 1] * 2, vc = triangles[i + 2] * 2;
            if (vc + 1 >= vertexCount || va < 0 || vb < 0 || vc < 0) continue;
            FillTriangle(texture,
                (float)((OffsetX + vertices[va] * scale) * RenderScale), (float)((OffsetY - vertices[va + 1] * scale) * RenderScale), uvs[va], uvs[va + 1],
                (float)((OffsetX + vertices[vb] * scale) * RenderScale), (float)((OffsetY - vertices[vb + 1] * scale) * RenderScale), uvs[vb], uvs[vb + 1],
                (float)((OffsetX + vertices[vc] * scale) * RenderScale), (float)((OffsetY - vertices[vc + 1] * scale) * RenderScale), uvs[vc], uvs[vc + 1],
                r, g, b, a, buffer, fw, fh);
        }
    }

    private void FillTriangle(SpineTexture texture,
        float ax, float ay, float au, float av,
        float bx, float by, float bu, float bv,
        float cx, float cy, float cu, float cv,
        float tintR, float tintG, float tintB, float tintA, byte[] buffer, int fw, int fh)
    {
        float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (Math.Abs(area) < 0.0001f) return;
        if (area < 0)
        {
            (bx, cx) = (cx, bx); (by, cy) = (cy, by);
            (bu, cu) = (cu, bu); (bv, cv) = (cv, bv);
            area = -area;
        }

        int minX = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
        int maxX = Math.Min(fw - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
        int minY = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy))));
        int maxY = Math.Min(fh - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
        if (minX > maxX || minY > maxY) return;

        float invArea = 1f / area;
        int rowBytes = fw * 4;
        int texW = texture.Width, texH = texture.Height;
        byte[] texels = texture.Pixels;
        int texRowBytes = texW * 4;

        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            int row = y * rowBytes;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float lambdaA = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) * invArea;
                float lambdaB = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) * invArea;
                float lambdaC = 1f - lambdaA - lambdaB;
                if (lambdaA < -0.0001f || lambdaB < -0.0001f || lambdaC < -0.0001f) continue;

                float u = lambdaA * au + lambdaB * bu + lambdaC * cu;
                float v = lambdaA * av + lambdaB * bv + lambdaC * cv;
                Sample(texture, texels, texW, texH, texRowBytes, u, v,
                    out float sr, out float sg, out float sb, out float sa);

                float srcA = sa * tintA;
                if (srcA <= 0.002f) continue;
                int idx = row + (x << 2);
                float dstA = buffer[idx + 3] / 255f;
                float outA = srcA + dstA * (1f - srcA);
                if (outA <= 0.003f) continue;
                float oneMinusSrc = 1f - srcA;
                float dr = buffer[idx] / 255f;
                float dg = buffer[idx + 1] / 255f;
                float db = buffer[idx + 2] / 255f;
                buffer[idx] = (byte)(((sr * tintR * srcA + dr * dstA * oneMinusSrc) / outA) * 255f);
                buffer[idx + 1] = (byte)(((sg * tintG * srcA + dg * dstA * oneMinusSrc) / outA) * 255f);
                buffer[idx + 2] = (byte)(((sb * tintB * srcA + db * dstA * oneMinusSrc) / outA) * 255f);
                buffer[idx + 3] = (byte)(outA * 255f);
            }
        }
    }

    private static void Sample(SpineTexture texture, byte[] texels, int texW, int texH, int texRowBytes,
        float u, float v, out float r, out float g, out float b, out float a)
    {
        float fx = u * texW - 0.5f;
        float fy = v * texH - 0.5f;
        int x0 = (int)Math.Floor(fx);
        int y0 = (int)Math.Floor(fy);
        float tx = fx - x0;
        float ty = fy - y0;
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        int x1 = x0 + 1 >= texW ? x0 : x0 + 1;
        int y1 = y0 + 1 >= texH ? y0 : y0 + 1;

        int i00 = y0 * texRowBytes + x0 * 4;
        int i10 = y0 * texRowBytes + x1 * 4;
        int i01 = y1 * texRowBytes + x0 * 4;
        int i11 = y1 * texRowBytes + x1 * 4;

        float w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
        r = (texels[i00] * w00 + texels[i10] * w10 + texels[i01] * w01 + texels[i11] * w11) / 255f;
        g = (texels[i00 + 1] * w00 + texels[i10 + 1] * w10 + texels[i01 + 1] * w01 + texels[i11 + 1] * w11) / 255f;
        b = (texels[i00 + 2] * w00 + texels[i10 + 2] * w10 + texels[i01 + 2] * w01 + texels[i11 + 2] * w11) / 255f;
        a = (texels[i00 + 3] * w00 + texels[i10 + 3] * w10 + texels[i01 + 3] * w01 + texels[i11 + 3] * w11) / 255f;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (frame != null && frameWidth > 0 && frameHeight > 0)
        {
            // 只把渲染区域（0,0,frameWidth,frameHeight）拉伸到整个控件，位图本身永不重建。
            double sx = ActualWidth / frameWidth;
            double sy = ActualHeight / frameHeight;
            dc.PushTransform(new ScaleTransform(sx, sy));
            dc.DrawImage(frame, new Rect(0, 0, frameWidth, frameHeight));
            dc.Pop();
        }
        if (ShowBones && asset != null)
            DrawBones(dc);
    }

    private void DrawBones(DrawingContext dc)
    {
        var pen = new Pen(Brushes.Lime, 1);
        pen.Freeze();
        double scale = Scale;
        foreach (Bone bone in asset!.Skeleton.Bones)
        {
            if (bone.Parent == null) continue;
            Point from = new Point(
                OffsetX + bone.Parent.WorldX * scale,
                OffsetY - bone.Parent.WorldY * scale);
            Point to = new Point(
                OffsetX + bone.WorldX * scale,
                OffsetY - bone.WorldY * scale);
            dc.DrawLine(pen, from, to);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    private static double GetCursorScreenX()
    {
        try
        {
            return GetCursorPos(out POINT point) ? point.X : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static double GetCursorScreenY()
    {
        try
        {
            return GetCursorPos(out POINT point) ? point.Y : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>判断窗口坐标点是否落在宠物本体上（用最新帧像素的 alpha 判定）。</summary>
    public bool IsPointOnPet(Point windowPoint)
    {
        lock (syncLock)
        {
            if (frameWidth <= 0 || frameHeight <= 0 || presentBuffer.Length < frameWidth * frameHeight * 4)
                return false;
            double aw = Math.Max(1, ActualWidth);
            double ah = Math.Max(1, ActualHeight);
            int px = (int)(windowPoint.X * frameWidth / aw);
            int py = (int)(windowPoint.Y * frameHeight / ah);
            if (px < 0 || py < 0 || px >= frameWidth || py >= frameHeight) return false;
            return presentBuffer[(py * frameWidth + px) * 4 + 3] > 30;
        }
    }

    public void Dispose()
    {
        stopThread = true;
        try
        {
            renderThread?.Join(1000);
        }
        catch
        {
            // 线程可能尚未启动或已退出。
        }
        renderThread = null;
    }
}