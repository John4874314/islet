# 浮屿 Islet

Windows 桌面端的「原子岛」式实时任务胶囊。

> **浮屿是 Windows 上唯一一个只显示「此刻最该被看见、且点一下就能办完事」的地方。**

不是仪表盘，不是通知中心，不做搜索框。它是一个**单槽优先级仲裁器**：同一时刻只呈现一件事，其余排队。

---

## 现在能跑的东西

| 能力 | 状态 | 验证方式 |
|---|---|---|
| 胶囊窗口（三态：点 / 条 / 卡） | ✅ | 真实桌面截图 |
| 优先级仲裁引擎 | ✅ | **23 项单测全绿** |
| 媒体控制（SMTC） | ✅ | 读到真实播放会话 |
| 倒计时 / 番茄钟 | ✅ | 硬抢占 + 8 秒 TTL |
| 下载 / 文件进度 | ✅ | 监视下载目录，静止零开销 |
| **系统体征异常上岛** | ✅ | 内存 / CPU / 电量 / 磁盘，带滞回 |
| **剪贴板内容识别** | ✅ | **端到端实测：复制 831402 → 岛上出现「识别到验证码」** |
| 全屏自动隐身 | ✅ | 实测：全屏播放时自动消失 |
| 多显示器跟随 | ✅ | 鼠标所在屏，PerMonitorV2 |
| 托盘 + 全局热键 | ✅ | `Ctrl+Alt+I` 显隐 |

### 剪贴板识别实拍

![识别到验证码](docs/screenshots/clipboard-island.png)

复制 `831402` 到剪贴板 → 胶囊立刻显示「识别到验证码 · 831402 · 点击复制」，并带一个可点的「复制」按钮。
**全程本地正则匹配，不联网、不落盘、不留历史。**

支持五类：验证码（4-8 位纯数字）、快递单号（SF/YT/ZTO/JD 等前缀）、URL、手机号、金额。

---

## 快速开始

```powershell
# 构建
powershell -NoProfile -ExecutionPolicy Bypass -File .\islet\build.ps1

# 构建 + 单测（23 项）
powershell -NoProfile -ExecutionPolicy Bypass -File .\islet\build.ps1 -Test

# 构建 + 端到端验证（往剪贴板写内容，验证识别 + 上岛 + 渲染）
powershell -NoProfile -ExecutionPolicy Bypass -File .\islet\build.ps1 -SelfTest
```

产物在 `build\`：`Islet.exe`（主程序）、`Islet.Core.dll`（引擎）、`Islet.Core.Tests.exe`（单测）。

**操作**：启动后胶囊出现在屏幕顶部居中。悬停展开成卡片，移开 400ms 收回。
无内容时缩成呼吸点。`Ctrl+Alt+I` 全局显隐。托盘右键退出。

---

## 项目结构

```
islet/
├── docs/PRD.md                     需求文档（定位、原则、评分模型、验收清单）
├── README.md
├── build.ps1                       构建脚本（无 SDK，直接调 csc）
├── src/
│   ├── Islet.Core/                 纯逻辑，零 UI 依赖，可单测
│   │   ├── IslandEvent.cs          事件模型（不可变 + 流式构造器）
│   │   ├── IslandScore.cs          评分公式与抢占判定
│   │   ├── IslandSlotManager.cs    ★ 仲裁引擎，产品灵魂
│   │   └── ClipboardRecognizer.cs  剪贴板识别规则（纯函数）
│   ├── Islet.App/                  WPF 外壳（纯 C# 构建，无 XAML）
│   │   ├── Views/IslandWindow.cs   三态胶囊、动效、无焦点置顶
│   │   ├── Services/               全屏检测、托盘
│   │   ├── Interop/                Win32 / DWM 互操作
│   │   ├── IsletHost.cs            装配仲裁器与数据源
│   │   ├── Log.cs                  文件日志（%LOCALAPPDATA%\Islet\islet.log）
│   │   └── SelfTest.cs             无头自检：渲染 / 宿主 / 剪贴板端到端
│   └── Islet.Sources/              数据源
│       ├── MediaProvider.cs        SMTC 媒体（反射桥接）
│       ├── TimerProvider.cs        倒计时
│       ├── ProgressProvider.cs     文件进度
│       ├── HealthProvider.cs       系统体征（带滞回）
│       ├── ClipboardProvider.cs    剪贴板识别
│       └── ClipboardWatcher.cs     原生剪贴板监听（消息驱动，非轮询）
└── tests/Islet.Core.Tests/         零依赖测试宿主（23 项）
```

---

## 核心设计：优先级仲裁

这一条是产品与「Rainmeter 皮肤」的分界线。评分公式：

```
score = BaseWeight[来源]
      + Urgency       × 0.40
      + Actionability × 0.30
      + Pinned(200)
      + 新鲜加成(最多 10，60 秒线性衰减)
      - 冷却惩罚(25)
```

**BaseWeight**：会议 80 / 健康 70 / 计时 55 / 进度 50 / 剪贴板 45 / **媒体 10**

**抢占规则**：

- **硬抢占**：`HardPreempt = true`，无视分差（会议临场、电量告急、计时到点）
- **软抢占**：`新分 > 当前分 + 15`（滞回带，防抖动）
- **不抢占**：`Pinned`，或用户正在展开卡片（`SuspendPreemption`）
- **退场**：TTL 到期 → 交还槽位 → 队列补位 → 回落媒体 → 隐藏点

---

## 开发过程中修掉的真实缺陷

这些不是笔误，是设计层面的坑，记录在此避免复犯：

| 缺陷 | 后果 | 修法 |
|---|---|---|
| 同 Id 刷新会刷新创建时间 | 「新鲜度加成」被无限续期，长期霸占槽位 | 更新时保留原始 `CreatedAt` |
| 未切回 UI 线程更新控件 | 后台数据源发布的事件**静默不显示** | `Dispatcher.CheckAccess` + `BeginInvoke` |
| 验证码被 MinLength 挡住 | 4-6 位验证码永远上不了岛 | 验证码走独立长度区间 |
| 进度条在布局前算宽度 | 首帧进度线宽度为 0 | `UpdateLayout()` 后补算 |
| 2px 进度线被抗锯齿吃掉 | 实测几乎不可见 | 加粗到 3px |
| 中文按钮渲染成方框 | 字体按字形回退不稳定 | 窗口级固定 `Microsoft YaHei UI` |
| Grid 重叠子元素遮挡进度线 | 内容行盖住底层进度条 | 显式设 `ZIndex` |
| 分层窗口截图全黑 | 无法用 `CopyFromScreen` 验证 UI | 改走 WPF `RenderTargetBitmap` |

---

## 已知限制

**这台机器的环境限制**（影响技术选型，不影响产品可用）：

- 没有 .NET SDK / MSBuild → 不用 csproj，直接调 `csc.exe`
- 没有 XAML 编译器 → 全部 UI 由 C# 代码构建
- csc 仅支持 **C# 5** → 无插值字符串、无 `?.`、无 `nameof`
- 无 Windows SDK 投影 → SMTC 走 `Type.GetType` + `AsTask` 反射桥接（已验证）

**尚未实现**（PRD 中的 P1/P2 剩余项）：

- 会议临场提醒（需读日历）
- 麦克风 / 摄像头占用指示
- 专注模式联动
- 手机联动、插件机制、主题自定义

---

## 下一步建议

按「投入产出比」排序：

1. **麦克风/摄像头占用指示** —— 隐私类状态天生适合胶囊，`IAudioSessionManager` 一个接口搞定
2. **剪贴板识别加动作** —— 快递单号直接「查物流」、金额直接「记一笔」，把 45 分抬高到 70 分
3. **会议提醒** —— 只需解析 Outlook/Teams 的日历 URL，不需账号授权
