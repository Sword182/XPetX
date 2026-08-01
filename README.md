# XpetX — 桌面宠物

用 WPF + Spine 实现的桌面宠物。当前内置明日方舟干员（Skadi）作为演示宠物，支持 AI 行为、数值养成、文件喂食、多宠物、性能模式与数据持久化。

> 注意：本仓库素材（`pets\pet-001\`）为临时拉取的第三方模型（Spine 3.8.99 格式），仅供开发演示，请勿用于商业发布。

---

## 功能一览

- **Spine 渲染**：软件光栅化 region/mesh 附件，后台线程渲染，自适应分辨率 + 性能模式（优先游戏 / 优先桌宠 / 均衡 / 自动）
- **AI 行为**：状态机（Idle/Walk/Eat/Happy/Sad/Sleep/Curious/Dizzy/Disgust）、随机散步（任务栏或全屏）、低属性气泡、精力过低自动睡觉
- **数值系统**：饱食 / 快乐 / 精力（0-100，随时间衰减），交互会改变数值
- **互动**：点击头部触发动画、Alt+左键拖动、右键菜单、系统托盘、独立可视化菜单窗口
- **文件喂食**：拖文件到宠物身上/旁边，图标落地（有边框窗口顶部或任务栏），饿了才捡，吃=删除（默认回收站）；危险类型永不进食（Windows 小人收走）；图片文件显示内容缩略图；首次喂食记录类型偏好
- **数据持久化**：`save.json`（数值/位置/偏好）+ 离线时间补偿；`config.json` 热加载
- **多宠物**：可视化菜单预览、添加新 pet、多窗口同时上桌

## 项目结构

```
C:\ThePet\
├── XpetX.sln
├── pets\pet-001\spine\      # 演示宠物素材（Skadi, Spine 3.8.99）
├── XpetX\
│   ├── App.xaml(.cs)        # 入口、全局异常、软件渲染
│   ├── MainWindow.xaml(.cs) # 主宠物窗口：渲染循环、交互、喂食、托盘
│   ├── Spine\
│   │   ├── spine-csharp\    # Spine 3.8 运行时（官方源码，SDK 风格工程）
│   │   ├── SpineLoader.cs   # 素材加载（skel/atlas/png）
│   │   └── SpineRenderer.cs # 软件光栅化渲染器（后台线程）
│   └── Core\
│       ├── PetInstance.cs   # 宠物实例（素材/AI/数值/存档）
│       ├── PetAI.cs         # 行为状态机
│       ├── PetStats.cs      # 数值系统
│       ├── PetFileManager.cs# 文件喂食/落地/删除/偏好
│       ├── PetManager.cs    # 多宠物目录与实例管理
│       ├── PetWindow.cs     # 额外宠物的独立窗口
│       ├── MenuWindow.cs    # 独立可视化菜单窗口
│       ├── AppConfig.cs     # config.json 加载与热加载
│       ├── PetSettings.cs   # pet.config.json（性能模式等）
│       └── WindowFocus.cs   # 前台窗口/点击穿透/热键辅助
├── docs\
│   ├── config.md            # 配置与回退规则文档
│   └── UGC.md               # 用户生成内容规范
└── config.example.json      # 空配置模板
```

## 构建与运行

```powershell
dotnet build C:\ThePet\XpetX\XpetX.csproj
dotnet run --project C:\ThePet\XpetX\XpetX.csproj
# 或直接运行 bin\Debug\net8.0-windows\XpetX.exe
```

要求：.NET 8 SDK/运行时，Windows 10/11。

## 配置

- `config.json`（运行目录，首次启动自动生成，热加载）：见 [`docs/config.md`](docs/config.md)
- 模板：[`config.example.json`](config.example.json)
- 宠物素材/内容约定：见 [`docs/UGC.md`](docs/UGC.md)

## 常用操作

| 操作 | 方式 |
| --- | --- |
| 点击头部 | 互动动画 + 快乐上升 |
| 移动宠物 | Alt + 左键拖动 |
| 右键 | 宠物菜单（坐下/穿透/性能模式/退出） |
| 托盘 | 显示隐藏、可视化菜单、穿透、性能模式、退出 |
| 可视化菜单 | 悬停宠物出现「菜单」按钮，或托盘入口 |
| 喂食 | 从资源管理器拖文件到宠物身上/旁边 |
| 穿透开关 | 右键菜单或托盘，全局热键 Ctrl+Alt+P |

## 已知限制

- 软件光栅化帧率低于 GPU 渲染；游戏运行时用「优先当前任务」模式。
- 真全屏（独占）游戏中无法显示桌宠（Windows 平台行为）。
- 存档写在运行目录的 `pets\` 副本，重新编译会清空。
- 内置 Spine 3.8 运行时；4.x 素材需更换运行时。
- 嘴部位置暂用头部偏移定义（异形生物待补）；`UGC\` 扫描与 `gestures.json` 为预留功能。

## 许可

- 代码：本仓库。
- Spine 运行时：Esoteric Software Spine Runtimes License（需各自获取 Spine 编辑器许可）。
- 演示素材：第三方模型，仅限开发演示，商用请自行确认授权。