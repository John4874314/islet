using System;
using System.IO;
using Islet.Core;

namespace Islet.App.Services
{
    /// <summary>
    /// 设置的持久化。配置文件放在 %LOCALAPPDATA%\Islet\settings.ini，
    /// 与日志同目录，卸载时用户数据可一并清理。
    ///
    /// 容错原则：读失败回退默认值，写失败只记日志，绝不让配置问题拖垮主程序。
    /// </summary>
    internal sealed class SettingsStore
    {
        private readonly string _path;
        private IsletSettings _current;

        public SettingsStore()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Islet");
            _path = Path.Combine(dir, "settings.ini");
            _current = new IsletSettings();
        }

        public string FilePath
        {
            get { return _path; }
        }

        public IsletSettings Current
        {
            get { return _current; }
        }

        /// <summary>设置变化时触发，参数是新的设置对象。</summary>
        public event EventHandler<SettingsChangedEventArgs> Changed;

        public IsletSettings Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    string text = File.ReadAllText(_path);
                    _current = SettingsCodec.Parse(text);
                    Log.Info("设置已加载: " + _path +
                             " 来源数=" + _current.EnabledSourceCount);
                }
                else
                {
                    Log.Info("无配置文件，使用默认设置");
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取设置失败，回退默认值", ex);
                _current = new IsletSettings();
            }
            return _current;
        }

        /// <summary>保存并广播变化。返回是否成功落盘。</summary>
        public bool Save(IsletSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");

            _current = settings;

            bool ok;
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_path, SettingsCodec.Serialize(settings));
                ok = true;
                Log.Info("设置已保存");
            }
            catch (Exception ex)
            {
                Log.Error("保存设置失败", ex);
                ok = false;
            }

            EventHandler<SettingsChangedEventArgs> h = Changed;
            if (h != null) h(this, new SettingsChangedEventArgs(settings));
            return ok;
        }
    }

    internal sealed class SettingsChangedEventArgs : EventArgs
    {
        public SettingsChangedEventArgs(IsletSettings settings)
        {
            Settings = settings;
        }

        public IsletSettings Settings { get; private set; }
    }
}
