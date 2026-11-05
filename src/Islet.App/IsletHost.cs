using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using Islet.App.Services;
using Islet.App.Views;
using Islet.Core;
using Islet.Sources;

namespace Islet.App
{
    /// <summary>
    /// 应用宿主。职责：装配仲裁器与数据源、驱动 Tick、协调窗口显隐、应用设置。
    /// </summary>
    internal sealed class IsletHost : IDisposable
    {
        private readonly IslandSlotManager _slot = new IslandSlotManager();
        private readonly FullscreenWatcher _fullscreen = new FullscreenWatcher();
        private readonly SettingsStore _settingsStore = new SettingsStore();
        private readonly Dictionary<IslandSource, IIslandProvider> _providers =
            new Dictionary<IslandSource, IIslandProvider>();

        private readonly DispatcherTimer _tick;
        private readonly DispatcherTimer _visibility;
        private readonly DispatcherTimer _trim;

        private IslandWindow _window;
        private SettingsWindow _settingsWindow;
        private VolumePanel _volumePanel;
        private StashStore _stashStore;
        private TrayIcon _tray;
        private IsletSettings _settings;
        private bool _hiddenByFullscreen;
        private bool _userHidden;

        public IsletHost()
        {
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (s, e) => _slot.Tick(DateTimeOffset.Now);

            // 全屏检测低频（1.5 秒），避免频繁查询系统状态
            _visibility = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _visibility.Tick += (s, e) => EvaluateVisibility();

            // 空闲内存整理：每 30 秒看一次，只在长时间空闲时才真正动手
            _trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _trim.Tick += (s, e) => MaybeTrimMemory();
        }

        /// <summary>距上次有活动（上岛/展开）过了多久，用于判断「真空闲」。</summary>
        private DateTimeOffset _lastActivity = DateTimeOffset.Now;

        public IslandSlotManager Slot
        {
            get { return _slot; }
        }

        public IsletSettings Settings
        {
            get { return _settings; }
        }

        public SettingsStore SettingsStore
        {
            get { return _settingsStore; }
        }

        /// <summary>诊断用：当前窗口内部几何快照。</summary>
        public string WindowDiagnostics
        {
            get { return _window == null ? "(no window)" : _window.DiagnoseBar(); }
        }

        public IntPtr WindowHandle
        {
            get
            {
                if (_window == null) return IntPtr.Zero;
                return new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            }
        }

        public System.Windows.Media.Imaging.BitmapSource RenderWindow()
        {
            return _window == null ? null : _window.RenderToBitmap();
        }

        public TimerProvider Timer
        {
            get
            {
                IIslandProvider p;
                if (_providers.TryGetValue(IslandSource.Timer, out p))
                    return p as TimerProvider;
                return null;
            }
        }

        // ---------------------------------------------------------------
        // 生命周期
        // ---------------------------------------------------------------

        public void Start()
        {
            // 1. 先读设置，后面所有装配都依赖它
            _settings = _settingsStore.Load();

            // 2b. 临时存放站的数据层。
            //     中转区的 UI 在岛**内部**（玻璃右侧那一列），所以这里只建数据层，
            //     下面把它注入窗口即可，不存在第二个窗口。
            _stashStore = new StashStore();

            // 2c. 窗口
            _window = new IslandWindow();
            _window.AttachStashStore(_stashStore);
            _window.Position = _settings.Position;
            _window.FreeX = _settings.FreeX;
            _window.FreeY = _settings.FreeY;
            _window.SetGlassOpacity(_settings.Opacity);
            _window.ExpandedChanged += (s, e) =>
            {
                _lastActivity = DateTimeOffset.Now;
                // P3：用户正在看卡片时不得被抢占
                if (_window.IsExpanded) _slot.SuspendPreemption();
                else _slot.ResumePreemption();
            };
            // 用户拖动结束后立刻落盘，下次启动回到同一位置
            _window.PositionChanged += (s, e) =>
            {
                if (_settings == null) return;
                _settings.Position = _window.Position;
                _settings.FreeX = _window.FreeX;
                _settings.FreeY = _window.FreeY;
                _settingsStore.Save(_settings);
                Log.Info(string.Format("胶囊位置已保存: {0:F0},{1:F0}",
                                       _window.FreeX, _window.FreeY));
            };
            // 拖文件到岛上 → 复制副本进暂存站，岛会把中转区显示在玻璃右侧
            _window.FilesDropped += (paths) => StashFiles(paths);
            _window.Show();
            _window.ShowNoActivate();

            // 3. 托盘
            _tray = new TrayIcon();
            _tray.ToggleRequested += (s, e) => ToggleUserHidden();
            _tray.SettingsRequested += (s, e) => ShowSettings();
            _tray.ExitRequested += (s, e) => RequestExit();
            _tray.StartTimerRequested += (s, e) => StartPomodoro();
            _tray.VolumeRequested += (s, e) => ShowVolumePanel();
            _tray.Show();

            _slot.CurrentChanged += OnSlotChanged;

            // 4. 数据源
            BuildProviders();

            _tick.Start();
            _visibility.Start();
            if (_settings.TrimMemoryWhenIdle) _trim.Start();

            // 5. 同步开机自启。
            //    设计要求「默认开机自启」，所以这里不是在检测状态，而是**主动落实**：
            //    用户想要开 → 真正去写入；写不进去 → 如实回写并记日志，
            //    让设置页显示真实结果，而不是让开关变成一个骗人的绿灯。
            SyncAutorunOnStartup();

            // 6. 引导提示
            _slot.Publish(new IslandEvent("islet.welcome", IslandSource.Clipboard, "浮屿已就绪")
                .WithSubtitle("Ctrl+Alt+I 显示/隐藏")
                .WithUrgency(30)
                .WithActionability(20)
                .WithTtl(TimeSpan.FromSeconds(6)));

            Log.Info("宿主已就绪，启用数据源 " + _settings.EnabledSourceCount + " 个");

            // 内存策略（保守）：
            //   · 不做启动预裁 —— 预裁会把内存在托盘常驻期间换出，
            //     胶囊下次重绘要 page fault 换回来，代价落在动画顺滑度和耗电上，
            //     而私有内存并没有真正减少，只是搬到了页面文件。
            //   · 只在**真空闲**时裁：连续 3 分钟没有上岛活动、槽位为空、卡片未展开。
            //     这时裁掉的页短时间内不会被访问，换出是纯收益。
            if (_settings.TrimMemoryWhenIdle) _trim.Start();
        }

        /// <summary>按设置装配（或跳过）各数据源。</summary>
        private void BuildProviders()
        {
            if (_settings.MediaEnabled) Register(IslandSource.Media, new MediaProvider());

            // 设备变更通知由 AudioSessionProvider 在创建枚举器时注册
            // （coclass 只在那一侧定义一份，见 AudioDeviceWatcher 的类注释）。
            // 这里只记录结果，方便排查「插耳机岛上半分钟才更新」。
            Log.Info(Islet.Sources.AudioDeviceWatcher.IsActive
                ? "音频设备变更监听: 已注册（插拔耳机即时刷新）"
                : "音频设备变更监听: 未注册，退回 30 秒轮询");
            if (_settings.TimerEnabled) Register(IslandSource.Timer, new TimerProvider());
            if (_settings.ProgressEnabled) Register(IslandSource.Progress, new ProgressProvider());
            if (_settings.HealthEnabled) Register(IslandSource.Health, new HealthProvider());
            if (_settings.ClipboardEnabled)
                Register(IslandSource.Clipboard, new ClipboardProvider());
            // 隐私占用 + 蓝牙耳机连接，**合并到同一个 Provider**。
            //
            // 为什么不各自注册：_providers 是按 IslandSource 索引的字典，
            // 同一来源只能挂一个 —— 后注册的会把先注册的顶掉，
            // 表现为「开了蓝牙就看不到麦克风提示」，非常隐蔽。
            // 两者都属于「该让用户立刻知道的外设/隐私状态」，
            // 合成一个 Provider 既避免冲突，语义也说得通。
            if (_settings.PrivacyEnabled || _settings.BluetoothEnabled)
                Register(IslandSource.Privacy, new DeviceStatusProvider(
                    _settings.PrivacyEnabled, _settings.BluetoothEnabled));
        }

        private void Register(IslandSource source, IIslandProvider p)
        {
            p.Attach(_slot);
            _providers[source] = p;
        }

        // ---------------------------------------------------------------
        // 设置应用
        // ---------------------------------------------------------------

        /// <summary>
        /// 启动时落实开机自启。设计要求默认开启，因此这里会**主动写入**，
        /// 而不是被动检测。写不进去时把真实结果回写到设置，让开关不再骗人。
        /// </summary>
        private void SyncAutorunOnStartup()
        {
            try
            {
                if (_settings.AutorunEnabled)
                {
                    // 已存在的条目先纠正路径（程序可能被移动过）
                    if (AutoStart.IsEnabled()) AutoStart.RepairIfStale();

                    bool ok = AutoStart.Enable();
                    Log.Info("开机自启: 期望=开, 实际=" + ok +
                             ", 机制=" + AutoStart.ActiveMechanism());

                    if (!ok)
                    {
                        // 系统明确拒绝：如实回写，设置页会显示为关
                        _settings.AutorunEnabled = false;
                        _settingsStore.Save(_settings);
                        _autorunBlocked = true;
                    }
                }
                else
                {
                    AutoStart.Disable();
                    Log.Info("开机自启: 用户已关闭");
                }
            }
            catch (Exception ex)
            {
                Log.Error("同步开机自启失败", ex);
                _settings.AutorunEnabled = false;
                _autorunBlocked = true;
            }
        }

        /// <summary>开机自启被系统拒绝过，设置页据此给出提示。</summary>
        private bool _autorunBlocked;

        public bool AutorunBlocked
        {
            get { return _autorunBlocked; }
        }

        public string AutorunMechanism
        {
            get { return AutoStart.ActiveMechanism(); }
        }

        /// <summary>设置页每次改动都会走到这里：落盘 + 增量生效。</summary>
        public void ApplySettings(IsletSettings next)
        {
            if (next == null) return;

            IsletSettings previous = _settings;
            _settings = next;
            _settingsStore.Save(next);

            // 位置与透明度
            if (_window != null)
            {
                if (previous.Position != next.Position)
                {
                    _window.Position = next.Position;
                    // 从自由位切到固定角落时，把保存的自由坐标也同步过去，
                    // 否则再切回自由位会跳回一个很旧的位置。
                    if (next.Position == IslandPosition.Free)
                    {
                        _window.FreeX = next.FreeX;
                        _window.FreeY = next.FreeY;
                    }
                    _window.Reposition();
                }

                if (previous.Opacity != next.Opacity)
                {
                    _window.SetGlassOpacity(next.Opacity);
                    Log.Info("透明度已切换: " + next.Opacity);
                }
            }

            // 数据源开关：只处理发生了变化的
            SyncProvider(IslandSource.Media, previous.MediaEnabled, next.MediaEnabled,
                () => new MediaProvider());
            SyncProvider(IslandSource.Timer, previous.TimerEnabled, next.TimerEnabled,
                () => new TimerProvider());
            SyncProvider(IslandSource.Progress, previous.ProgressEnabled, next.ProgressEnabled,
                () => new ProgressProvider());
            SyncProvider(IslandSource.Health, previous.HealthEnabled, next.HealthEnabled,
                () => new HealthProvider());
            SyncProvider(IslandSource.Clipboard, previous.ClipboardEnabled, next.ClipboardEnabled,
                () => new ClipboardProvider());
            // 隐私与蓝牙合并成一个 Provider：任一开关变化都要整体重建，
            // 因为「开哪个子功能」是构造参数，不能单独增删。
            bool privacyChanged = previous.PrivacyEnabled != next.PrivacyEnabled;
            bool btChanged = previous.BluetoothEnabled != next.BluetoothEnabled;
            if (privacyChanged || btChanged)
            {
                SyncProvider(IslandSource.Privacy, true, false, null);   // 先拆掉旧的
                if (next.PrivacyEnabled || next.BluetoothEnabled)
                {
                    Register(IslandSource.Privacy, new DeviceStatusProvider(
                        next.PrivacyEnabled, next.BluetoothEnabled));
                }
            }

            // 开机自启
            if (previous.AutorunEnabled != next.AutorunEnabled)
            {
                bool actual = AutoStart.Apply(next.AutorunEnabled);

                // 只有「想要开但没开成」才算被拦；主动关掉是正常操作
                _autorunBlocked = next.AutorunEnabled && !actual;

                if (actual != next.AutorunEnabled)
                {
                    next.AutorunEnabled = actual;
                    _settingsStore.Save(next);
                    Log.Info("开机自启未能按预期切换，实际=" + actual);
                }
                else
                {
                    Log.Info("开机自启已切换为 " + actual +
                             "，机制=" + AutoStart.ActiveMechanism());
                }
            }

            // 空闲内存整理
            if (next.TrimMemoryWhenIdle) _trim.Start();
            else _trim.Stop();

            Log.Info("设置已应用，启用数据源 " + next.EnabledSourceCount + " 个");
        }

        private void SyncProvider(IslandSource source, bool before, bool after,
                                  Func<IIslandProvider> factory)
        {
            if (before == after) return;

            if (after)
            {
                Register(source, factory());
                Log.Info("数据源已启用: " + source);
                return;
            }

            IIslandProvider p;
            if (_providers.TryGetValue(source, out p))
            {
                try { p.Dispose(); }
                catch (Exception ex) { Log.Error("停用数据源失败: " + source, ex); }
                _providers.Remove(source);
            }
            _slot.RetractSource(source);
            Log.Info("数据源已停用: " + source);
        }

        public void ShowSettings()
        {
            if (_settingsWindow != null && _settingsWindow.IsVisible)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_settings, ApplySettings,
                                                 () => AutoStart.ActiveMechanism(),
                                                 _autorunBlocked);
            _settingsWindow.Closed += (s, e) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        /// <summary>
        /// 打开应用音量面板。
        ///
        /// 刻意做成**独立窗口**而不是岛内展开：岛是单槽仲裁，同一时刻只有一件事
        /// 占主位。音量调节是低频动作，常驻会长期占着主位，破坏这个模型。
        /// 面板自带生命周期，关掉即释放 COM，岛完全不受影响。
        /// </summary>
        public void ShowVolumePanel()
        {
            if (_volumePanel != null && _volumePanel.IsVisible)
            {
                _volumePanel.Activate();
                return;
            }

            _volumePanel = new VolumePanel();
            _volumePanel.Closed += (s, e) => _volumePanel = null;
            _volumePanel.Show();
            Log.Info("已打开应用音量面板");
        }

        /// <summary>
        /// 岛收到拖入的文件时调用：复制副本进暂存站，然后让岛刷新中转区。
        ///
        /// 注意这里**不打开任何窗口** —— 中转区就在岛的玻璃内部（右侧那一列），
        /// SetStashCount 换成 RefreshStash(true) 之后，岛会自己展开并把方框带出来。
        /// </summary>
        public void StashFiles(string[] paths)
        {
            if (_stashStore == null || paths == null || paths.Length == 0) return;

            List<string> errors = new List<string>();
            IList<StashItem> added = _stashStore.Add(paths, errors);

            if (_window != null) _window.RefreshStash(true);

            for (int i = 0; i < errors.Count; i++)
                Log.Info("暂存未成功: " + errors[i]);

            Log.Info("已暂存 " + added.Count + " 个文件，当前共 " + _stashStore.Count + " 个");
        }

        public void StartPomodoro()
        {
            TimerProvider t = Timer;
            if (t != null) t.Start("番茄钟", TimeSpan.FromMinutes(25));
            else Log.Info("计时器数据源已关闭，无法启动番茄钟");
        }

        // ---------------------------------------------------------------
        // 内存
        // ---------------------------------------------------------------

        /// <summary>
        /// 整理内存。只在**真空闲**时做：连续 3 分钟没有上岛活动、槽位为空、卡片未展开。
        ///
        /// 为什么要这么严格：EmptyWorkingSet 只是把页换到页面文件，
        /// 私有内存并没有减少。换出后一旦被访问就要 page fault 换回来，
        /// 对持续刷新的胶囊（音乐进度、倒计时）反而更耗电。
        /// 所以只有「确定短时间内不会碰这些页」时才值得换出。
        /// </summary>
        private void MaybeTrimMemory()
        {
            try
            {
                if (!_settings.TrimMemoryWhenIdle) return;
                if (_window == null) return;

                // 有内容在岛上、或用户正展开卡片，都不裁
                if (_slot.Current != null) return;
                if (_window.IsExpanded) return;

                double idleSeconds = (DateTimeOffset.Now - _lastActivity).TotalSeconds;
                if (idleSeconds < IdleTrimThresholdSeconds) return;

                double before = MemoryTrimmer.WorkingSetMB;
                MemoryTrimmer.Compact();
                double after = MemoryTrimmer.WorkingSetMB;

                Log.Info(string.Format("空闲整理: {0:F1} MB -> {1:F1} MB（已空闲 {2:F0} 秒）",
                                       before, after, idleSeconds));
            }
            catch (Exception ex)
            {
                Log.Error("内存整理失败", ex);
            }
        }

        /// <summary>判定「真空闲」的阈值。3 分钟足够避开绝大多数交互间隙。</summary>
        private const double IdleTrimThresholdSeconds = 180;

        public double WorkingSetMB
        {
            get { return MemoryTrimmer.WorkingSetMB; }
        }

        // ---------------------------------------------------------------
        // 事件与显隐
        // ---------------------------------------------------------------

        private void OnSlotChanged(object sender, IslandSlotChangedEventArgs e)
        {
            if (_window == null) return;
            _lastActivity = DateTimeOffset.Now;

            // 数据源在后台线程发布；WPF 控件必须在 UI 线程更新
            if (!_window.Dispatcher.CheckAccess())
            {
                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_window != null) _window.Render(e.Current);
                }));
                return;
            }

            _window.Render(e.Current);
        }

        private void EvaluateVisibility()
        {
            if (_window == null) return;

            // 判定逻辑抽在 VisibilityPolicy 里，可单测（曾经在这里踩过
            // 「先把隐藏标志清零再判定 → 永远恢复不了」的坑）。
            bool shouldHide = VisibilityPolicy.ShouldHide(
                _userHidden,
                _settings != null && _settings.HideInFullscreen,
                _settings != null && _settings.HideInFullscreen && _fullscreen.ShouldHide());

            VisibilityPolicy.Action action = VisibilityPolicy.Decide(_hiddenByFullscreen, shouldHide);

            if (action == VisibilityPolicy.Action.Hide)
            {
                _hiddenByFullscreen = true;
                _window.Hide();
            }
            else if (action == VisibilityPolicy.Action.Show)
            {
                _hiddenByFullscreen = false;
                _window.Show();
                _window.ShowNoActivate();
                _window.Reposition();
            }
        }

        private void ToggleUserHidden()
        {
            _userHidden = !_userHidden;

            // 这里曾经有一行 `_hiddenByFullscreen = false;`，是个致命 bug：
            // EvaluateVisibility() 靠这个标志判断「当前是隐藏态」，先把它清零，
            // 恢复显示的那条分支就永远不成立 —— 用户按一次隐藏之后，
            // 再怎么点「显示/隐藏」或按 Ctrl+Alt+I 都回不来。
            // 正确的做法是让 EvaluateVisibility() 自己按 shouldHide 收敛状态。
            EvaluateVisibility();

            Log.Info("用户切换显示: " + (_userHidden ? "隐藏" : "显示"));
        }

        /// <summary>全局热键 Ctrl+Alt+I 的入口：等价于点托盘里的「显示 / 隐藏」。</summary>
        public void ToggleVisible()
        {
            ToggleUserHidden();
        }

        /// <summary>
        /// 退出。必须绕开托盘菜单的模态消息泵：
        ///
        /// 用户点「退出」时，代码运行在 WinForms ContextMenuStrip 的嵌套消息循环里。
        /// 如果当场直接调 Application.Shutdown()，WPF 的关闭流程会和那个嵌套循环
        /// 互相等待，表现为「点了退出，程序还在」。
        ///
        /// 所以这里分两步：
        ///   1. BeginInvoke 把关闭动作排到 WPF 自己的队列里，等菜单循环先退出；
        ///   2. 起一个看门狗线程，如果 3 秒后进程还活着，直接 Environment.Exit 兜底。
        ///
        /// 兜底不是「掩盖问题」：Dispose 已经正常跑完时它根本不会触发；
        /// 它只保证用户点了退出就一定能退出，不会留一个看不见的僵尸进程。
        /// </summary>
        private void RequestExit()
        {
            Log.Info("用户请求退出");

            Dispatcher d = Application.Current != null ? Application.Current.Dispatcher : null;
            if (d != null && !d.CheckAccess())
            {
                d.BeginInvoke(new Action(ShutdownNow));
                return;
            }

            // 即便已在 UI 线程，也刻意推迟一拍：让当前菜单 Click 先返回
            if (d != null) d.BeginInvoke(new Action(ShutdownNow));
            else ShutdownNow();
        }

        private void ShutdownNow()
        {
            try
            {
                if (Application.Current != null)
                {
                    Application.Current.Shutdown();
                    StartExitWatchdog();
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("调用 Shutdown 失败", ex);
            }

            HardExit();
        }

        /// <summary>关闭请求发出后仍不退出时的最后兜底。</summary>
        private static void StartExitWatchdog()
        {
            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(3000);
                Log.Info("关闭流程未在 3 秒内结束，强制退出");
                HardExit();
            });
            t.IsBackground = true;
            t.Name = "Islet.ExitWatchdog";
            t.Start();
        }

        private static void HardExit()
        {
            try { Log.Info("强制结束进程"); } catch (Exception) { }
            Environment.Exit(0);
        }

        public void Dispose()
        {
            _tick.Stop();
            _visibility.Stop();
            _trim.Stop();

            foreach (IIslandProvider p in _providers.Values)
            {
                try { p.Dispose(); }
                catch (Exception ex) { Log.Error("释放数据源失败", ex); }
            }
            _providers.Clear();

            if (_tray != null) { _tray.Dispose(); _tray = null; }
            if (_settingsWindow != null) { _settingsWindow.Close(); _settingsWindow = null; }
            if (_volumePanel != null) { _volumePanel.Close(); _volumePanel = null; }
            // 用户选的是「重启即清空」：这里删掉全部暂存副本。
            // StashStore.ClearCore 只删自己目录里的普通文件，不递归、不碰原文件。
            if (_stashStore != null) { _stashStore.Dispose(); _stashStore = null; }
            if (_window != null) { _window.Close(); _window = null; }
        }
    }
}
