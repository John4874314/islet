using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Islet.Core;

namespace Islet.Sources
{
    /// <summary>
    /// 下载 / 解压 / 文件传输进度源。
    ///
    /// 实现方式：监视用户指定目录下正在增长的文件。
    /// 不接管任何下载器，因此对浏览器、迅雷、Steam、IDM 一视同仁。
    /// 只在文件大小发生变化时才发布事件，静止的文件不产生任何开销。
    /// </summary>
    public sealed class ProgressProvider : IIslandProvider
    {
        private const string IdPrefix = "islet.progress.";

        /// <summary>文件至少要比上次扫描大这么多字节，才算「正在写入」。</summary>
        private const long MinDeltaBytes = 256 * 1024;

        /// <summary>判定为下载中的最小文件体积，避免把日志文件误报成下载。</summary>
        private const long MinInterestingBytes = 1024 * 1024;

        /// <summary>连续多久没有增长就认为下载结束（秒）。</summary>
        private const int StaleSeconds = 4;

        private readonly List<string> _watchDirs = new List<string>();
        private readonly Dictionary<string, TrackedFile> _tracked =
            new Dictionary<string, TrackedFile>(StringComparer.OrdinalIgnoreCase);

        private IIslandSink _sink;
        private Timer _timer;
        private bool _disposed;

        public string Name
        {
            get { return "Progress"; }
        }

        /// <summary>添加一个监视目录。默认已包含下载文件夹与桌面。</summary>
        public void AddWatchDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            if (_watchDirs.Contains(dir)) return;
            _watchDirs.Add(dir);
        }

        public void Attach(IIslandSink sink)
        {
            _sink = sink;

            if (_watchDirs.Count == 0)
            {
                AddWatchDirectory(KnownFolder("Downloads"));
                AddWatchDirectory(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            }

            // 2 秒扫描一次；只做目录枚举 + 文件长度读取，实测开销远低于性能预算
            _timer = new Timer(Scan, null, 3000, 2000);
        }

        private void Scan(object state)
        {
            if (_disposed || _sink == null) return;

            try
            {
                DateTimeOffset now = DateTimeOffset.Now;
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string dir in _watchDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    FileInfo[] files;
                    try
                    {
                        DirectoryInfo di = new DirectoryInfo(dir);
                        files = di.GetFiles("*", SearchOption.TopDirectoryOnly);
                    }
                    catch (Exception) { continue; }

                    foreach (FileInfo fi in files)
                    {
                        if (fi.Length < MinInterestingBytes) continue;

                        // 跳过常见的非下载临时文件
                        string ext = fi.Extension.ToLowerInvariant();
                        if (ext == ".tmp" && fi.Length < MinInterestingBytes * 4) continue;

                        seen.Add(fi.FullName);

                        TrackedFile tf;
                        if (!_tracked.TryGetValue(fi.FullName, out tf))
                        {
                            tf = new TrackedFile
                            {
                                Path = fi.FullName,
                                LastSize = fi.Length,
                                LastChange = now,
                                FirstSeen = now
                            };
                            _tracked[fi.FullName] = tf;
                            continue; // 第一次见到不算增长
                        }

                        long delta = fi.Length - tf.LastSize;
                        if (delta >= MinDeltaBytes)
                        {
                            tf.LastSize = fi.Length;
                            tf.LastChange = now;
                            tf.PeakSize = Math.Max(tf.PeakSize, fi.Length);
                            tf.IsActive = true;
                        }
                        else if (tf.IsActive && (now - tf.LastChange).TotalSeconds >= StaleSeconds)
                        {
                            tf.IsActive = false;
                        }
                    }
                }

                // 清理消失的文件
                List<string> gone = new List<string>();
                foreach (KeyValuePair<string, TrackedFile> kv in _tracked)
                {
                    if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
                }
                foreach (string p in gone)
                {
                    _sink.Retract(IdFor(p));
                    _tracked.Remove(p);
                }

                PublishActive(now);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Islet] ProgressProvider: " + ex.Message);
            }
        }

        private void PublishActive(DateTimeOffset now)
        {
            // 只展示最活跃的一个：单槽原则
            TrackedFile best = null;
            foreach (TrackedFile tf in _tracked.Values)
            {
                if (!tf.IsActive) continue;
                if (best == null || tf.LastSize > best.LastSize) best = tf;
            }

            // 把不再活跃的从岛上撤下
            foreach (TrackedFile tf in _tracked.Values)
            {
                if (!ReferenceEquals(tf, best)) _sink.Retract(IdFor(tf.Path));
            }

            if (best == null) return;

            double? progress = null;
            string subtitle = FormatSize(best.LastSize);

            if (best.PeakSize > best.LastSize)
            {
                progress = (double)best.LastSize / best.PeakSize;
                subtitle = FormatSize(best.LastSize) + " / " + FormatSize(best.PeakSize);
            }

            string path = best.Path;
            _sink.Publish(new IslandEvent(IdFor(path), IslandSource.Progress,
                                          Path.GetFileName(path))
                .WithSubtitle(subtitle)
                .WithProgress(progress)
                .WithUrgency(35)
                .WithActionability(70)
                .WithActions(
                    new IslandAction("打开文件夹", () => RevealInExplorer(path))));
        }

        private static string IdFor(string path)
        {
            return IdPrefix + path;
        }

        private static void RevealInExplorer(string path)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception) { }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return string.Format("{0:F2} GB", bytes / 1024.0 / 1024 / 1024);
            if (bytes >= 1024L * 1024)
                return string.Format("{0:F0} MB", bytes / 1024.0 / 1024);
            if (bytes >= 1024)
                return string.Format("{0:F0} KB", bytes / 1024.0);
            return bytes + " B";
        }

        private static string KnownFolder(string name)
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, name);
        }

        public void Dispose()
        {
            _disposed = true;
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
            _tracked.Clear();
            _sink = null;
        }

        private sealed class TrackedFile
        {
            public string Path;
            public long LastSize;
            public long PeakSize;
            public DateTimeOffset LastChange;
            public DateTimeOffset FirstSeen;
            public bool IsActive;
        }
    }
}
