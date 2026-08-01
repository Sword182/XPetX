# XpetX 配置说明

XpetX 有两份配置：全局配置 `config.json` 与宠物窗口配置 `pet.config.json`。两者都在程序运行目录（`bin\Debug\net8.0-windows\`）下，**首次启动自动生成**，修改后**热加载即时生效**（无需重启）。

空配置模板见仓库根目录 [`config.example.json`](../config.example.json)。

---

## 一、全局配置 config.json

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `alwaysOnTop` | `true` | 宠物窗口是否置顶 |
| `decaySpeed` | `1.0` | 数值衰减速度倍率（饱食/快乐/精力每秒 -0.05 × decaySpeed） |
| `moveSpeed` | `1.0` | 行走速度倍率 |
| `activity` | `1.0` | 活跃度：越高走动越频繁（待机间隔 3~8 秒 ÷ activity） |
| `taskbarOffset` | `0` | 宠物底部距任务栏/屏幕底部的附加偏移（像素，**负值=更贴任务栏**） |
| `walkArea` | `"taskbar"` | 行走区域：`"taskbar"`=沿任务栏横走（默认），`"screen"`=全屏自由走动 |
| `walkMirror` | `true` | 走路镜像：没有左右行走动画时，反向行走自动水平翻转 |
| `facingRight` | `true` | 默认朝向（`true`=朝右） |
| `cursorTracking` | `true` | 光标追踪总开关（见下方"无特殊配置回退规则"） |
| `headFollowSpeed` | `5.0` | 头部追踪平滑速度 |
| `hungryThreshold` | `80` | 饥饿阈值：饱食度低于该值才会捡地上的食物 |
| `deleteMode` | `"recycle"` | 文件删除方式：`"recycle"`=回收站（默认），`"delete"`=永久删除（**启用有警告**） |
| `allowDangerousFiles` | `false` | 危险类型强制进食（**仅能改配置文件开启**，避免小白误开；开启有警告） |
| `dislikedExtensions` | 见模板 | 危险扩展名列表（永不进食，除非开启 `allowDangerousFiles`） |

## 二、宠物窗口配置 pet.config.json

| 键 | 说明 |
| --- | --- |
| `Mode` | 性能模式：`FocusPriority`（优先当前任务）/ `PetPriority`（优先桌宠）/ `Balanced`（两者均衡）/ `Auto`（自动判定，默认） |
| `HideInFullscreen` | `true` 时前台全屏自动隐藏桌宠 |
| `ClickThrough` | `true` 时鼠标点击穿透宠物（用全局热键 Ctrl+Alt+P 切回） |

## 三、无特殊配置时的回退规则（手势 ↔ 动画/骨骼名）

素材**没有专门配置**时，XpetX 按以下约定回退：

### 行为 → 动画映射

| 行为/手势 | 优先动画 | 说明 |
| --- | --- | --- |
| 默认待机 | `idle` → `Relax` → `Default` → 素材第一个动画 | 按优先级依次回退 |
| 行走 | `Move` | 没有左右行走动画时用 `walkMirror` 水平翻转 |
| 睡觉 | `Sleep` | 精力 < 20 自动触发 |
| 坐下 | `Sit` | 右键菜单/托盘触发 |
| 开心/互动 | `Interact` | 点击头部、喂食后 |
| 好奇 | `Interact` | 光标悬停触发（当前默认关闭） |
| 进食中 | `Default` | 站定不动、停顿后开吃 |

### 骨骼名约定（光标追踪）

1. **追踪目标**：优先取名称包含 `eye`（不区分大小写）的骨骼（眼珠追踪）；**没有眼珠骨骼**时回退到名称包含 `head` 的骨骼（头部追踪）。
2. **头部骨骼**：优先精确匹配 `F_Head`，其次名称包含 `head`。
3. **嘴部位置**：暂用头部骨骼屏幕坐标下方偏移（+10, +26）；**异形生物的嘴定义后续补**（代码入口：`PetFileManager.GetMouthPosition()`）。

### 其他回退

- 动画不存在时 `PlayAnimation` 返回 `false`，不崩溃、保持当前动画。
- 喂食动画无配置时走默认"直接出现在嘴边 → 大(100%) → 中(60%) → 小(30%) 硬切"。
- 地面落点：下方最近的有边框窗口顶部；无边框窗口不算地面，继续下落到宠物脚底附近（保证图标可见）。

## 四、热加载

`config.json` 修改保存后立即生效；`pet.config.json` 在下次启动生效。
