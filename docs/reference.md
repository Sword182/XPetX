# XpetX 对照表（简洁版）

参照 Yes Steve Model / Touhou Maid Custom Model 文档风格整理的快速对照表。

## 一、动画名对照

| 行为 | 期望动画名 | 回退顺序 |
| --- | --- | --- |
| 待机 | `idle` | `idle` → `Relax` → `Default` → 素材第一个动画 |
| 行走 | `Move` | 无左右变体时用 `walkMirror` 翻转 |
| 睡觉 | `Sleep` | — |
| 坐下 | `Sit` | — |
| 开心/互动 | `Interact` | — |
| 好奇 | `Interact` | 当前默认关闭 |
| 进食中 | `Default` | 站定 + 大中小硬切 |

## 二、骨骼名对照

| 用途 | 命名约定 | 回退 |
| --- | --- | --- |
| 眼珠追踪 | 名称含 `eye` | 无眼珠骨骼 → 头部追踪 |
| 头部追踪 | `F_Head` 优先 | 名称含 `head` |
| 嘴部位置 | 头部骨骼下方偏移 (+10, +26) | 异形生物定义待补 |

## 三、配置键对照（config.json）

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `alwaysOnTop` | `true` | 窗口置顶 |
| `decaySpeed` | `1.0` | 数值衰减倍率 |
| `moveSpeed` | `1.0` | 行走速度倍率 |
| `activity` | `1.0` | 活跃度（越高走越勤） |
| `taskbarOffset` | `0` | 底部偏移（负值贴任务栏） |
| `walkArea` | `"taskbar"` | `taskbar` / `screen` |
| `walkMirror` | `true` | 反向行走翻转 |
| `facingRight` | `true` | 默认朝向 |
| `cursorTracking` | `true` | 光标追踪开关 |
| `headFollowSpeed` | `5.0` | 头部追踪速度 |
| `hungryThreshold` | `80` | 捡食饥饿阈值 |
| `deleteMode` | `"recycle"` | `recycle` / `delete` |
| `allowDangerousFiles` | `false` | 危险类型进食（仅配置文件） |
| `iconPath` | `""` | 自定义图标路径 |
| `dislikedExtensions` | 危险扩展名表 | 永不进食 |

## 四、宠物包文件对照

| 文件 | 必需 | 说明 |
| --- | --- | --- |
| `spine\*.skel` | ✅ | 骨骼数据（3.8 二进制） |
| `spine\*.atlas` | ✅ | 图集 |
| `spine\*.png` | ✅ | 图集纹理 |
| `preview.png` | 可选 | 专属预览图，无则用蓝色小狼占位 |
| `@版权方` | 受版权角色必需 | 空的版权声明文件 |
| `gestures.json` | 可选 | 手势→动画自定义映射（预留） |
| `save.json` | 自动 | 运行期存档，勿手动编辑 |

详细说明见 [config.md](config.md) 与 [UGC.md](UGC.md)。
