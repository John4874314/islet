using System;
using System.Collections.Generic;
using System.IO;

namespace Islet.App.Services
{
    /// <summary>收纳站里的一条暂存文件。</summary>
    internal sealed class StashItem
    {
        /// <summary>会话内唯一 id，用于定位与删除。</summary>
        public string Id;
        /// <summary>展示用的原始文件名（不含路径）。</summary>
        public string Name;
        /// <summary>暂存副本的完整路径，拖出时交给 FileDrop 的就是它。</summary>
        public string Path;
        public long Size;
        public DateTime AddedAt;
    }

    /// <summary>
    /// 临时存放站的数据层。
    ///
    /// 三条设计约束（都来自用户确认）：
    ///   1. **复制副本**：把原文件复制进应用自己的暂存目录，原文件自始至终不动。
    ///      这样原文件被删/改名/拔 U 盘都不影响，也不会发生「拖出去把原件剪切走」。
    ///   2. **只存副本路径**：条目只在内存里，不落盘清单。
    ///   3. **重启即清空**：构造与 Dispose 时清空整个暂存目录。
    ///
    /// 写文件范围严格限制在 %LOCALAPPDATA%\Islet\stash\ —— 应用自己的数据目录，
    /// 与 settings.ini 同级。不碰系统目录，不碰用户其它目录。
    ///
    /// 删除时只删这个目录里的**普通文件**，逐个 Delete，不递归、不删子目录、
    /// 不用通配符 —— 保证即使目录里混进了别的东西也不会被误伤。
    /// </summary>
    internal sealed class StashStore : IDisposable
    {
        private readonly List<StashItem> _items = new List<StashItem>();
        private readonly object _gate = new object();
        private readonly string _dir;
        private bool _disposed;
        /// <summary>暂存目录不可用时为 false。此时 Add 全部拒绝，但应用不受影响。</summary>
        private bool _available;

        /// <summary>单个文件大小上限：200 MB。避免误拖大文件把磁盘吃满。</summary>
        private const long MaxFileSize = 200L * 1024 * 1024;

        /// <summary>一次最多暂存多少个文件。</summary>
        private const int MaxItems = 30;

        /// <summary>可指定目录（探针用来避开受限路径）；正式代码用无参构造。</summary>
        public StashStore(string overrideDir = null)
        {
            try
            {
                _dir = overrideDir ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Islet", "stash");
                Directory.CreateDirectory(_dir);

                // 上次退出可能没来得及清理（崩溃 / 强杀），启动时补一次。
                // 内容本来就要求「重启即清空」，所以这里清空是预期行为，不是破坏。
                ClearCore();
                _available = true;
            }
            catch (Exception ex)
            {
                // ⚠️ 绝不能让「暂存目录建不起来」拖垮整个应用启动。
                // 实测从受限会话启动的进程对 %LOCALAPPDATA% 下新建目录会抛
                // UnauthorizedAccessException —— 只收纳站不可用，别的功能照常。
                _available = false;
                _dir = null;
                Log.Info("收纳站目录不可用，功能已禁用: " + ex.Message);
            }
        }

        public string DirectoryPath { get { return _dir; } }
        public bool Available { get { return _available; } }

        /// <summary>当前条目的只读快照。</summary>
        public IList<StashItem> Snapshot()
        {
            lock (_gate) { return new List<StashItem>(_items); }
        }

        public int Count
        {
            get { lock (_gate) { return _items.Count; } }
        }

        /// <summary>
        /// 把一批文件复制进暂存站。返回成功加入的条目；
        /// 单个文件失败不影响其它文件，失败原因写进 errors。
        /// </summary>
        public IList<StashItem> Add(string[] sourcePaths, IList<string> errors)
        {
            List<StashItem> added = new List<StashItem>();
            if (sourcePaths == null) return added;
            if (!_available)
            {
                if (errors != null) errors.Add("暂存目录不可用");
                return added;
            }

            foreach (string src in sourcePaths)
            {
                if (string.IsNullOrEmpty(src)) continue;

                lock (_gate)
                {
                    if (_items.Count >= MaxItems)
                    {
                        if (errors != null) errors.Add("最多暂存 " + MaxItems + " 个文件");
                        break;
                    }
                }

                try
                {
                    if (!File.Exists(src))
                    {
                        // 目录暂不支持：递归复制会带来一堆边界问题，明确拒绝。
                        if (errors != null) errors.Add("暂不支持文件夹: " + Path.GetFileName(src));
                        continue;
                    }

                    FileInfo fi = new FileInfo(src);
                    if (fi.Length > MaxFileSize)
                    {
                        if (errors != null)
                            errors.Add(Path.GetFileName(src) + " 超过 200 MB，已跳过");
                        continue;
                    }

                    string name = Path.GetFileName(src);
                    string dest = UniquePath(name);

                    File.Copy(src, dest, false);

                    StashItem item = new StashItem
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = name,
                        Path = dest,
                        Size = fi.Length,
                        AddedAt = DateTime.Now
                    };

                    lock (_gate) { _items.Add(item); }
                    added.Add(item);
                }
                catch (Exception ex)
                {
                    if (errors != null)
                        errors.Add(Path.GetFileName(src) + ": " + ex.Message);
                }
            }

            return added;
        }

        /// <summary>移除一条：只删暂存副本，绝不动原文件。</summary>
        public bool Remove(string id)
        {
            StashItem found = null;
            lock (_gate)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Id == id) { found = _items[i]; _items.RemoveAt(i); break; }
                }
            }
            if (found == null) return false;

            TryDelete(found.Path);
            return true;
        }

        /// <summary>按 id 找条目（用于拖出后判断是否要删副本）。</summary>
        public StashItem Find(string id)
        {
            lock (_gate)
            {
                foreach (StashItem s in _items) if (s.Id == id) return s;
            }
            return null;
        }

        /// <summary>
        /// 拖出结束后的收尾。
        ///
        /// Windows 的拖放效果由**接收方**决定：资源管理器默认 Copy（按 Shift 才 Move），
        /// 聊天框一般只收 Copy。只有真正返回 Move 才说明接收方把文件移走了，
        /// 此时把对应条目从列表移除 —— 注意接收方移走的是**暂存副本**，
        /// 不是用户的原文件。
        /// </summary>
        public void OnDragCompleted(string id, bool moved)
        {
            if (!moved) return;
            // 只把条目从列表移除。文件本身已被接收方移走（路径不存在了），
            // 不必再删；万一接收方只给了 Move 标志却没真移，残留副本也会在
            // 退出时由 ClearCore 清掉，不会泄漏。
            lock (_gate)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Id == id) { _items.RemoveAt(i); break; }
                }
            }
        }

        /// <summary>清空全部条目与副本。</summary>
        public void Clear()
        {
            lock (_gate) { _items.Clear(); }
            ClearCore();
        }

        private void ClearCore()
        {
            try
            {
                // 只判断目录是否有效，**不要**判断 _available：
                // 构造函数里 ClearCore() 是在 _available=true 之前调用的，
                // 用 _available 做守卫会让「启动清残留」永远被跳过（实测踩到）。
                if (_dir == null || !Directory.Exists(_dir)) return;
                // 只删本目录下的普通文件，不递归、不删子目录
                foreach (string f in Directory.GetFiles(_dir))
                {
                    TryDelete(f);
                }
            }
            catch (Exception ex)
            {
                Log.Info("收纳站清空失败: " + ex.Message);
            }
        }

        /// <summary>生成一个不冲突的副本文件名，保留原名便于拖出后识别。</summary>
        private string UniquePath(string name)
        {
            string clean = name;
            foreach (char c in Path.GetInvalidFileNameChars())
                clean = clean.Replace(c, '_');
            if (string.IsNullOrEmpty(clean)) clean = "file";

            string candidate = Path.Combine(_dir, clean);
            if (!File.Exists(candidate)) return candidate;

            string stem = Path.GetFileNameWithoutExtension(clean);
            string ext = Path.GetExtension(clean);
            for (int i = 2; i < 10000; i++)
            {
                candidate = Path.Combine(_dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(_dir, Guid.NewGuid().ToString("N") + ext);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Log.Info("收纳站删除副本失败: " + ex.Message); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
    }
}
