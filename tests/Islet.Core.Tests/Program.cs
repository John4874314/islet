using System;
using System.Collections.Generic;
using Islet.Core;

namespace Islet.Core.Tests
{
    /// <summary>
    /// 零依赖测试宿主（环境无 NuGet / 无测试框架，故自建）。
    /// 运行后打印 PASS/FAIL 汇总，失败时退出码 1。
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static int _failed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Run("空槽时首个事件立即上岛", EmptySlotAcceptsFirst);
            Run("同 Id 更新不新增槽位", SameIdUpdatesInPlace);
            Run("低分事件不抢占高分事件", LowScoreDoesNotPreempt);
            Run("高分事件软抢占", HighScoreSoftPreempts);
            Run("滞回带内不抢占", WithinHysteresisNoPreempt);
            Run("Pinned 阻止软抢占", PinnedBlocksSoftPreempt);
            Run("HardPreempt 无视 Pinned 以外一切", HardPreemptWins);
            Run("媒体是地板态", MediaIsFloor);
            Run("TTL 到期交还槽位", TtlExpiresAndFallsBack);
            Run("Dismiss 进入冷却不再上岛", DismissEntersCooldown);
            Run("Retract 移除当前并补位", RetractRefills);
            Run("RetractSource 批量移除", RetractSourceWorks);
            Run("队列溢出丢弃最低分", QueueOverflowDropsWorst);
            Run("被抢占者回到队列", PreemptedReturnsToQueue);
            Run("刷新不重复吃新鲜加成", RefreshDoesNotFarmFreshness);
            Run("暂停抢占期间只排队不夺槽", SuspendedPreemptionQueuesOnly);
            Run("剪贴板识别：验证码", ClipCode);
            Run("剪贴板识别：快递单号", ClipTracking);
            Run("剪贴板识别：链接", ClipUrl);
            Run("剪贴板识别：手机号", ClipPhone);
            Run("剪贴板识别：金额", ClipAmount);
            Run("剪贴板识别：普通文本不上岛", ClipIgnoresPlainText);
            Run("剪贴板识别：长度边界", ClipLengthBounds);
            Run("设置：默认值", SettingsDefaults);
            Run("设置：序列化往返", SettingsRoundTrip);
            Run("设置：容错解析", SettingsTolerantParsing);
            Run("设置：位置枚举文案", SettingsPositionText);
            Run("设置：数据源开关映射", SettingsSourceToggle);
            Run("显隐：隐藏后必须能恢复显示", VisibilityCanRestore);
            Run("显隐：用户隐藏优先于全屏判定", VisibilityUserHiddenWins);
            Run("显隐：状态一致时不重复动作", VisibilityIdempotent);
            Run("退场：TTL 到期且队列为空必须通知 UI", TtlExpiryRaisesEmpty);
            Run("退场：Retract 清空槽位必须通知 UI", RetractRaisesEmpty);

            Console.WriteLine();
            Console.WriteLine("----------------------------------------");
            Console.WriteLine("PASS=" + _passed + "  FAIL=" + _failed);
            if (_failed > 0)
            {
                Console.WriteLine("失败用例：");
                foreach (string f in _failures) Console.WriteLine("  - " + f);
                return 1;
            }
            Console.WriteLine("全部通过");
            return 0;
        }

        // ---------------------------------------------------------------
        // 用例
        // ---------------------------------------------------------------

        private static void EmptySlotAcceptsFirst()
        {
            IslandSlotManager m = new IslandSlotManager();
            IslandEvent got = null;
            m.CurrentChanged += (s, e) => got = e.Current;

            m.Publish(Ev("a", IslandSource.Media, "song"));
            Assert(got != null, "应当触发 CurrentChanged");
            Assert(m.Current != null && m.Current.Id == "a", "槽位应为 a");
        }

        private static void SameIdUpdatesInPlace()
        {
            IslandSlotManager m = new IslandSlotManager();
            int raises = 0;
            m.CurrentChanged += (s, e) => raises++;

            m.Publish(Ev("a", IslandSource.Progress, "下载"));
            m.Publish(Ev("a", IslandSource.Progress, "下载").WithProgress(0.5));

            Assert(m.Current != null && m.Current.Id == "a", "槽位仍应为 a");
            Assert(m.Current.Progress.HasValue && Math.Abs(m.Current.Progress.Value - 0.5) < 0.001,
                   "进度应更新为 0.5");
            Assert(m.QueueDepth == 0, "不应产生排队项");
            Assert(raises == 2, "两次发布都应通知 UI，实际=" + raises);
        }

        private static void LowScoreDoesNotPreempt()
        {
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("meet", IslandSource.Meeting, "会议").WithUrgency(90).WithActionability(90));
            m.Publish(Ev("clip", IslandSource.Clipboard, "单号").WithUrgency(10).WithActionability(20));

            Assert(m.Current.Id == "meet", "会议应继续占槽，实际=" + m.Current.Id);
            Assert(m.QueueDepth == 1, "剪贴板应排队");
        }

        private static void HighScoreSoftPreempts()
        {
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("media", IslandSource.Media, "song").WithUrgency(5).WithActionability(5));
            m.Publish(Ev("timer", IslandSource.Timer, "番茄钟").WithUrgency(80).WithActionability(70));

            Assert(m.Current.Id == "timer", "番茄钟应抢占，实际=" + m.Current.Id);
        }

        private static void WithinHysteresisNoPreempt()
        {
            // media:  10 + 100*0.4 + 100*0.3 = 80，加新鲜加成 ≈ 90
            // timer:  55 +  40*0.4 +  40*0.3 = 83，加新鲜加成 ≈ 93
            // 差距约 3，落在滞回带（15）内，不应抢占
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("media", IslandSource.Media, "song").WithUrgency(100).WithActionability(100));
            m.Publish(Ev("timer", IslandSource.Timer, "番茄钟").WithUrgency(40).WithActionability(40));

            Assert(m.Current.Id == "media", "差距在滞回带内不应抢占，实际=" + m.Current.Id);
        }

        private static void PinnedBlocksSoftPreempt()
        {
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("clip", IslandSource.Clipboard, "单号").WithPinned(true));
            m.Publish(Ev("meet", IslandSource.Meeting, "会议").WithUrgency(100).WithActionability(100));

            Assert(m.Current.Id == "clip", "固定项不应被软抢占，实际=" + m.Current.Id);
        }

        private static void HardPreemptWins()
        {
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("clip", IslandSource.Clipboard, "单号").WithPinned(true));
            m.Publish(Ev("health", IslandSource.Health, "电量 9%").WithHardPreempt(true)
                      .WithUrgency(1).WithActionability(1));

            Assert(m.Current.Id == "health", "硬抢占应夺槽，实际=" + m.Current.Id);
        }

        private static void MediaIsFloor()
        {
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("media", IslandSource.Media, "song"));
            Assert(m.Current.Id == "media", "无其他事件时媒体应占槽（地板态）");

            m.Publish(Ev("timer", IslandSource.Timer, "番茄钟").WithUrgency(50).WithActionability(50));
            Assert(m.Current.Id == "timer", "有更高分事件时媒体让位");

            m.Retract("timer");
            m.Tick(DateTimeOffset.Now);
            Assert(m.Current != null && m.Current.Id == "media", "高优先级退场后应回落媒体");
        }

        private static void TtlExpiresAndFallsBack()
        {
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("media", IslandSource.Media, "song"));
            m.Publish(Ev("note", IslandSource.Clipboard, "已复制").WithTtl(TimeSpan.FromSeconds(1))
                      .WithUrgency(99).WithActionability(99).WithHardPreempt(true));

            Assert(m.Current.Id == "note", "硬抢占应生效，实际=" + m.Current.Id);

            m.Tick(DateTimeOffset.Now.AddSeconds(2));
            Assert(m.Current != null && m.Current.Id == "media", "TTL 到期后应回落媒体，实际=" +
                   (m.Current == null ? "null" : m.Current.Id));
        }

        private static void DismissEntersCooldown()
        {
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("media", IslandSource.Media, "song"));
            m.Publish(Ev("timer", IslandSource.Timer, "番茄钟").WithUrgency(90).WithActionability(90));
            Assert(m.Current.Id == "timer", "前置：番茄钟应占槽");

            m.DismissCurrent();
            Assert(m.Current != null && m.Current.Id == "media", "关闭后应回落媒体");

            m.Publish(Ev("timer", IslandSource.Timer, "番茄钟").WithUrgency(90).WithActionability(90));
            Assert(m.Current.Id == "media", "冷却期内不应再上岛，实际=" + m.Current.Id);
        }

        private static void RetractRefills()
        {
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("media", IslandSource.Media, "song"));
            m.Publish(Ev("timer", IslandSource.Timer, "番茄钟").WithUrgency(90).WithActionability(90));
            Assert(m.Current.Id == "timer", "前置：番茄钟占槽");

            m.Retract("timer");
            Assert(m.Current != null && m.Current.Id == "media", "撤下后应补位媒体");
        }

        private static void RetractSourceWorks()
        {
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("p1", IslandSource.Progress, "下载1"));
            m.Publish(Ev("p2", IslandSource.Progress, "下载2"));
            m.RetractSource(IslandSource.Progress);

            Assert(m.Current == null, "同源事件应全部移除，实际=" +
                   (m.Current == null ? "null" : m.Current.Id));
            Assert(m.QueueDepth == 0, "队列也应清空");
        }

        private static void QueueOverflowDropsWorst()
        {
            IslandSlotManager m = new IslandSlotManager();
            // 先占住槽位，避免候选直接被选走
            m.Publish(Ev("media", IslandSource.Media, "song").WithPinned(true));

            for (int i = 0; i < IslandSlotManager.MaxQueue + 3; i++)
            {
                m.Publish(Ev("c" + i, IslandSource.Clipboard, "项" + i)
                          .WithUrgency(i).WithActionability(i));
            }

            Assert(m.QueueDepth <= IslandSlotManager.MaxQueue,
                   "队列不应超过上限，实际=" + m.QueueDepth);
        }

        private static void PreemptedReturnsToQueue()
        {
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("clip", IslandSource.Clipboard, "单号").WithUrgency(40).WithActionability(40));
            m.Publish(Ev("meet", IslandSource.Meeting, "会议").WithUrgency(95).WithActionability(90));

            Assert(m.Current.Id == "meet", "会议应抢占");
            Assert(m.QueueDepth == 1, "被抢占的剪贴板应回到队列，实际=" + m.QueueDepth);

            m.Retract("meet");
            Assert(m.Current != null && m.Current.Id == "clip", "会议退场后剪贴板应回槽");
        }

        private static void RefreshDoesNotFarmFreshness()
        {
            // 直接验证语义：同一 Id 反复发布，CreatedAt 必须保持首次，否则新鲜加成会被无限续期
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("bg", IslandSource.Progress, "后台").WithUrgency(1).WithActionability(1));

            DateTimeOffset firstCreatedAt = m.Current.CreatedAt;

            for (int i = 0; i < 5; i++)
            {
                m.Publish(Ev("bg", IslandSource.Progress, "后台").WithUrgency(1).WithActionability(1));
            }

            Assert(m.Current.CreatedAt == firstCreatedAt,
                   "反复刷新不应改变创建时间，首次=" + firstCreatedAt.ToString("O") +
                   " 现在=" + m.Current.CreatedAt.ToString("O"));

            // 新鲜加成确已衰减：120 秒前创建的事件只剩基础分
            double aged = IslandScore.Compute(
                new IslandEvent("x", IslandSource.Progress, "t")
                    .WithUrgency(1).WithActionability(1)
                    .WithCreatedAt(DateTimeOffset.Now.AddSeconds(-120)),
                DateTimeOffset.Now, false);

            double expected = IslandScore.BaseWeight(IslandSource.Progress) + 1 * 0.4 + 1 * 0.3;
            Assert(Math.Abs(aged - expected) < 0.01,
                   "新鲜加成应已衰减完，期望≈" + expected.ToString("F2") + " 实际=" + aged.ToString("F2"));
        }

        private static void SuspendedPreemptionQueuesOnly()
        {
            IslandSlotManager m = new IslandSlotManager();
            m.Publish(Ev("clip", IslandSource.Clipboard, "单号").WithUrgency(10).WithActionability(10));
            Assert(m.Current.Id == "clip", "前置：剪贴板占槽");

            // 模拟用户展开卡片
            m.SuspendPreemption();
            m.Publish(Ev("meet", IslandSource.Meeting, "会议").WithUrgency(100).WithActionability(100));

            Assert(m.Current.Id == "clip", "暂停期间不应夺槽，实际=" + m.Current.Id);
            Assert(m.QueueDepth == 1, "高分事件应排队等待，实际=" + m.QueueDepth);

            m.ResumePreemption();
            Assert(m.Current.Id == "meet", "恢复后应立即让高分事件夺槽，实际=" + m.Current.Id);
        }

        // ---------------------------------------------------------------
        // 剪贴板识别
        // ---------------------------------------------------------------

        private static void ClipCode()
        {
            ClipboardRecognizer.Result r = ClipboardRecognizer.Recognize("831402");
            Assert(r != null, "6 位纯数字应识别为验证码");
            Assert(r.Kind == "code", "Kind 应为 code，实际=" + r.Kind);
            Assert(r.Payload == "831402", "Payload 应保留原值，实际=" + r.Payload);
        }

        private static void ClipTracking()
        {
            ClipboardRecognizer.Result r =
                ClipboardRecognizer.Recognize("您的包裹已发出 SF1234567890123");
            Assert(r != null, "顺丰单号应被识别");
            Assert(r.Kind == "tracking", "Kind 应为 tracking，实际=" + r.Kind);
            Assert(r.Payload == "SF1234567890123", "单号应大写归一，实际=" + r.Payload);
        }

        private static void ClipUrl()
        {
            ClipboardRecognizer.Result r =
                ClipboardRecognizer.Recognize("https://www.vivo.com.cn/originos");
            Assert(r != null, "URL 应被识别");
            Assert(r.Kind == "url", "Kind 应为 url");
            Assert(r.IsUrl, "IsUrl 应为 true");
            Assert(r.Subtitle == "www.vivo.com.cn", "副标题应为域名，实际=" + r.Subtitle);
        }

        private static void ClipPhone()
        {
            ClipboardRecognizer.Result r = ClipboardRecognizer.Recognize("13812345678");
            Assert(r != null, "手机号应被识别");
            Assert(r.Kind == "phone", "Kind 应为 phone，实际=" + r.Kind);
            Assert(r.Payload == "13812345678", "号码应正确，实际=" + r.Payload);
        }

        private static void ClipAmount()
        {
            ClipboardRecognizer.Result r = ClipboardRecognizer.Recognize("订单合计 ¥128.50");
            Assert(r != null, "金额应被识别");
            Assert(r.Kind == "amount", "Kind 应为 amount，实际=" + r.Kind);
            Assert(r.Payload.IndexOf("128.50") >= 0, "金额应含 128.50，实际=" + r.Payload);
        }

        private static void ClipIgnoresPlainText()
        {
            Assert(ClipboardRecognizer.Recognize("今天下午三点开会讨论下季度预算安排") == null,
                   "普通中文段落不应上岛");
            Assert(ClipboardRecognizer.Recognize("hello world, nothing special here") == null,
                   "普通英文句子不应上岛");
        }

        private static void ClipLengthBounds()
        {
            // 验证码走独立长度区间：3 位太短、9 位太长，都不该是验证码
            ClipboardRecognizer.Result tooShort =
                ClipboardRecognizer.Recognize("123");
            Assert(tooShort == null, "3 位数字低于验证码下限，不应识别");

            ClipboardRecognizer.Result tooLong =
                ClipboardRecognizer.Recognize("123456789");
            Assert(tooLong == null ||
                   tooLong.Kind != "code",
                   "9 位数字不应被当作验证码");

            // 非验证码内容受 MinLength 约束
            Assert(ClipboardRecognizer.Recognize("abc") == null,
                   "3 字符低于 MinLength，不应识别");

            Assert(ClipboardRecognizer.Recognize(null) == null, "null 应安全返回 null");
            Assert(ClipboardRecognizer.Recognize("") == null, "空串应安全返回 null");

            // 超长文本即使含单号也不识别，避免把整篇文档搬上岛
            string longText = new string('x', ClipboardRecognizer.MaxLength + 10)
                              + " SF1234567890123";
            Assert(ClipboardRecognizer.Recognize(longText) == null,
                   "超过 MaxLength 不应识别");
        }

        // ---------------------------------------------------------------
        // 设置
        // ---------------------------------------------------------------

        private static void SettingsDefaults()
        {
            IsletSettings s = new IsletSettings();
            Assert(s.EnabledSourceCount == 7, "默认应启用全部 7 个数据源（含蓝牙耳机），实际=" +
                   s.EnabledSourceCount);
            Assert(s.Position == IslandPosition.Free, "默认应为自由拖动（固定角落会挡住浏览器关闭按钮）");
            // 产品要求：安装后默认开机自启。宿主会在首次启动时真正写入，
            // 写不进去则如实回写为 false 并提示用户。
            Assert(s.AutorunEnabled, "开机自启默认应为开启");
            Assert(s.HideInFullscreen, "全屏隐身默认开启");
            Assert(s.TrimMemoryWhenIdle, "空闲整理内存默认开启");
        }

        private static void SettingsRoundTrip()
        {
            IsletSettings a = new IsletSettings();
            a.MediaEnabled = false;
            a.HealthEnabled = false;
            a.Position = IslandPosition.TopRight;
            a.AutorunEnabled = true;
            a.HideInFullscreen = false;
            a.TrimMemoryWhenIdle = false;
            a.ClipboardTtlSeconds = 30;

            string text = SettingsCodec.Serialize(a);
            IsletSettings b = SettingsCodec.Parse(text);

            Assert(b.MediaEnabled == false, "媒体开关应往返一致");
            Assert(b.TimerEnabled == true, "未改的项应保持默认");
            Assert(b.HealthEnabled == false, "体征开关应往返一致");
            Assert(b.Position == IslandPosition.TopRight, "位置应往返一致，实际=" + b.Position);
            Assert(b.AutorunEnabled == true, "开机自启应往返一致");
            Assert(b.HideInFullscreen == false, "全屏隐身应往返一致");
            Assert(b.TrimMemoryWhenIdle == false, "内存开关应往返一致");
            Assert(b.ClipboardTtlSeconds == 30, "TTL 应往返一致，实际=" + b.ClipboardTtlSeconds);
        }

        private static void SettingsTolerantParsing()
        {
            // 空、垃圾、未知键、坏值，一律不得抛异常
            Assert(SettingsCodec.Parse(null) != null, "null 应返回默认设置");
            Assert(SettingsCodec.Parse("") != null, "空串应返回默认设置");

            IsletSettings s = SettingsCodec.Parse(
                "# 注释行\n" +
                "media = 垃圾值\n" +
                "unknownKey = 123\n" +
                "position = 火星\n" +
                "clipboardTtlSeconds = 99999\n" +
                "没有等号的一行\n" +
                "= 空键\n");

            Assert(s != null, "垃圾输入不应抛异常");
            Assert(s.MediaEnabled == true, "坏布尔值应回退默认 true");
            Assert(s.Position == IslandPosition.Free, "坏位置应回退默认");
            Assert(s.ClipboardTtlSeconds == 120, "超范围应被夹到上限 120，实际=" +
                   s.ClipboardTtlSeconds);

            // 各种真值写法
            Assert(SettingsCodec.Parse("media = TRUE").MediaEnabled, "TRUE 应识别为真");
            Assert(!SettingsCodec.Parse("media = off").MediaEnabled, "off 应识别为假");
            Assert(!SettingsCodec.Parse("media = 0").MediaEnabled, "0 应识别为假");
        }

        private static void SettingsPositionText()
        {
            Assert(SettingsCodec.Describe(IslandPosition.TopCenter) == "顶部居中", "文案不符");
            Assert(SettingsCodec.Describe(IslandPosition.TopLeft) == "左上角", "文案不符");
            Assert(SettingsCodec.Describe(IslandPosition.TopRight) == "右上角", "文案不符");
            Assert(SettingsCodec.AllPositions().Length == 4, "应有 4 个位置选项（含自由拖动）");
        }

        private static void SettingsSourceToggle()
        {
            IsletSettings s = new IsletSettings();
            Assert(s.SourceEnabled(IslandSource.Media), "默认应启用媒体");

            s.MediaEnabled = false;
            s.ClipboardEnabled = false;
            Assert(!s.SourceEnabled(IslandSource.Media), "关闭后应返回 false");
            Assert(!s.SourceEnabled(IslandSource.Clipboard), "关闭后应返回 false");
            Assert(s.SourceEnabled(IslandSource.Timer), "未关闭的仍应为 true");
            Assert(s.EnabledSourceCount == 5, "应剩 5 个，实际=" + s.EnabledSourceCount);

            // Clone 必须是深拷贝，否则设置页改了会污染运行中的实例
            IsletSettings copy = s.Clone();
            copy.TimerEnabled = false;
            Assert(s.TimerEnabled, "Clone 后改动不应影响原对象");
        }

        /// <summary>
        /// 回归用例：曾经把「当前是否隐藏」的标志在判定前清零，
        /// 导致恢复显示的分支永远不成立——用户按一次隐藏之后胶囊再也回不来。
        /// </summary>
        private static void VisibilityCanRestore()
        {
            // 初始可见
            bool hidden = false;

            // 用户点「隐藏」：应执行 Hide
            bool should = VisibilityPolicy.ShouldHide(true, false, false);
            VisibilityPolicy.Action a = VisibilityPolicy.Decide(hidden, should);
            Assert(a == VisibilityPolicy.Action.Hide, "首次隐藏应执行 Hide，实际=" + a);
            hidden = true;

            // 用户再点「显示」：必须执行 Show（这正是老代码丢掉的那一步）
            should = VisibilityPolicy.ShouldHide(false, false, false);
            a = VisibilityPolicy.Decide(hidden, should);
            Assert(a == VisibilityPolicy.Action.Show, "隐藏后必须能恢复显示，实际=" + a);
        }

        private static void VisibilityUserHiddenWins()
        {
            // 用户手动隐藏时，即便全屏判定说不用隐藏，也应保持隐藏
            bool should = VisibilityPolicy.ShouldHide(true, true, false);
            Assert(should, "用户隐藏应压过「非全屏」判定");

            // 全屏且开关打开时，应隐藏
            Assert(VisibilityPolicy.ShouldHide(false, true, true), "全屏 + 开关开 → 应隐藏");

            // 全屏开关关闭时，即便在全屏也不隐藏
            Assert(!VisibilityPolicy.ShouldHide(false, false, true), "全屏开关关 → 不应隐藏");
        }

        private static void VisibilityIdempotent()
        {
            // 状态一致时不该重复下发动作，避免无谓的 Show/Hide 抖动
            Assert(VisibilityPolicy.Decide(false, false) == VisibilityPolicy.Action.None,
                   "可见 + 应可见 → None");
            Assert(VisibilityPolicy.Decide(true, true) == VisibilityPolicy.Action.None,
                   "隐藏 + 应隐藏 → None");
        }

        /// <summary>
        /// 回归用例：TTL 到期且队列为空时，槽位从「有」变「无」也是一次状态变化，
        /// 必须触发 CurrentChanged(null)。老代码在这里返回 changed=false，
        /// UI 收不到通知，胶囊会一直挂着已经过期的事件（实测挂着欢迎提示好几分钟）。
        /// </summary>
        private static void TtlExpiryRaisesEmpty()
        {
            IslandSlotManager m = new IslandSlotManager();
            int nullRaises = 0;
            int total = 0;
            m.CurrentChanged += (s, e) =>
            {
                total++;
                if (e.Current == null) nullRaises++;
            };

            m.Publish(Ev("welcome", IslandSource.Clipboard, "就绪")
                .WithTtl(TimeSpan.FromSeconds(1)));
            Assert(total == 1 && nullRaises == 0, "首次上岛应通知一次且非空");

            // 推进到 TTL 之后
            m.Tick(DateTimeOffset.Now + TimeSpan.FromSeconds(5));

            Assert(m.Current == null, "槽位应已清空");
            Assert(nullRaises == 1,
                   "槽位变空必须通知 UI 一次，实际 nullRaises=" + nullRaises);
        }

        private static void RetractRaisesEmpty()
        {
            IslandSlotManager m = new IslandSlotManager();
            int nullRaises = 0;
            m.CurrentChanged += (s, e) => { if (e.Current == null) nullRaises++; };

            m.Publish(Ev("x", IslandSource.Progress, "下载"));
            m.Retract("x");

            Assert(m.Current == null, "槽位应已清空");
            Assert(nullRaises == 1, "Retract 清空槽位必须通知 UI，实际=" + nullRaises);
        }

        // ---------------------------------------------------------------
        // 辅助
        // ---------------------------------------------------------------
        private static IslandEvent Ev(string id, IslandSource src, string title)
        {
            return new IslandEvent(id, src, title);
        }        private static void Run(string name, Action body)
        {
            try
            {
                body();
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                _failures.Add(name + " :: " + ex.Message);
                Console.WriteLine("[FAIL] " + name + " -> " + ex.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
