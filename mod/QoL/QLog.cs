using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 统一日志系统：
    /// 1. 控制台与 Unity 引擎日志（Player.log / GameLog）；
    /// 2. 独立文件日志（ModConfigs/local.qol.log），带时间戳与日志级别；
    /// 3. 支持 Debug/Verbose 级别开关控制，避免日常刷屏；
    /// 4. 自动捕获完整异常调用堆栈，便于排查具体报错代码行号；
    /// 5. 启动时自动记录模组版本、游戏环境及其他已装模组信息。
    /// </summary>
    internal static class QLog
    {
        private const string Tag = "[QoL] ";

        /// <summary>同一条警告/错误在这个窗口内只输出一次，避免每帧异常把日志刷爆、频繁写盘。</summary>
        private const float RepeatWindow = 10f;

        private static readonly System.Collections.Generic.Dictionary<string, float> Recent =
            new System.Collections.Generic.Dictionary<string, float>();

        private static string _logFilePath;
        private static readonly object _fileLock = new object();
        private static bool _initialized;

        /// <summary>
        /// 是否开启详细/调试级日志输出。
        /// </summary>
        internal static bool IsVerbose
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                return s != null && s.VerboseLogging != null && s.VerboseLogging.Value;
            }
        }

        /// <summary>
        /// 独立日志文件完整路径。
        /// </summary>
        internal static string LogFilePath
        {
            get
            {
                if (string.IsNullOrEmpty(_logFilePath))
                {
                    try
                    {
                        string dir = Path.Combine(Application.persistentDataPath, "ModConfigs");
                        if (!System.IO.Directory.Exists(dir))
                        {
                            System.IO.Directory.CreateDirectory(dir);
                        }
                        _logFilePath = Path.Combine(dir, "local.qol.log");
                    }
                    catch
                    {
                        _logFilePath = "local.qol.log";
                    }
                }
                return _logFilePath;
            }
        }

        internal static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                string path = LogFilePath;
                // 如果日志文件超过 2MB，自动重命名备份以防占用过多空间
                if (File.Exists(path))
                {
                    FileInfo fi = new FileInfo(path);
                    if (fi.Length > 2 * 1024 * 1024)
                    {
                        string prevPath = Path.Combine(Path.GetDirectoryName(path) ?? "", "local.qol-prev.log");
                        if (File.Exists(prevPath))
                        {
                            File.Delete(prevPath);
                        }
                        File.Move(path, prevPath);
                    }
                }

                WriteLine($"==================== QoL 日志初始化 ({DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}) ====================");
            }
            catch (Exception ex)
            {
                Debug.LogWarning(Tag + "初始化独立日志文件失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 常规信息日志（同时输出到 Unity 控制台和独立日志文件）。
        /// </summary>
        internal static void Info(string message)
        {
            Debug.Log(Tag + message);
            WriteFile("INFO", message);
        }

        /// <summary>
        /// 调试/详细日志（仅在设置中开启“详细日志记录”时生效）。
        /// </summary>
        internal static void DebugLog(string message)
        {
            if (!IsVerbose) return;
            Debug.Log(Tag + "[DEBUG] " + message);
            WriteFile("DEBUG", message);
        }

        /// <summary>
        /// 警告日志（异常预警或安全降级）。
        /// </summary>
        internal static void Warn(string message)
        {
            if (IsRepeat("WARN", message)) return;
            Debug.LogWarning(Tag + message);
            WriteFile("WARN", message);
        }

        /// <summary>
        /// 错误日志（严重故障或未捕获异常）。
        /// </summary>
        internal static void Error(string message, Exception ex = null)
        {
            if (IsRepeat("ERROR", message)) return;
            string fullMsg = ex != null ? $"{message}\n[Exception] {ex}" : message;
            Debug.LogError(Tag + fullMsg);
            WriteFile("ERROR", fullMsg);
        }

        /// <summary>
        /// 同一条消息在 RepeatWindow 内只记一次。
        /// 每帧路径 / 每件物品的失败如果连续报同样的错，只留第一条，避免写盘风暴。
        /// </summary>
        private static bool IsRepeat(string level, string message)
        {
            try
            {
                string text = message ?? "";
                if (text.Length > 100) text = text.Substring(0, 100);
                string key = level + "|" + text;

                float now = Time.realtimeSinceStartup;
                if (Recent.TryGetValue(key, out float last) && now - last < RepeatWindow) return true;

                Recent[key] = now;
                if (Recent.Count > 200) Recent.Clear();
                return false;
            }
            catch
            {
                return false; // 限流本身失败就照常输出
            }
        }

        /// <summary>
        /// 记录已安装的其他模组清单，排查兼容性问题。
        /// </summary>
        internal static void DumpEnvironment()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("--- 环境与模组诊断信息 ---");
                sb.AppendLine($"游戏版本: {Application.version}");
                sb.AppendLine($"Unity 引擎版本: {Application.unityVersion}");
                sb.AppendLine($"系统平台: {Application.platform}");
                sb.AppendLine($"模组数据目录: {Application.persistentDataPath}");

                string configsDir = Path.Combine(Application.persistentDataPath, "ModConfigs");
                if (System.IO.Directory.Exists(configsDir))
                {
                    string[] configs = System.IO.Directory.GetFiles(configsDir, "*.json");
                    sb.AppendLine($"检测到已存在的模组配置文件 ({configs.Length} 个):");
                    foreach (string cfg in configs)
                    {
                        sb.AppendLine("  - " + Path.GetFileName(cfg));
                    }
                }

                Info(sb.ToString().TrimEnd());
            }
            catch (Exception e)
            {
                Warn("读取环境模组诊断失败: " + e.Message);
            }
        }

        private static void WriteFile(string level, string message)
        {
            try
            {
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
                WriteLine(line);
            }
            catch
            {
                // 日志写入失败忽略，绝不干扰游戏运行
            }
        }

        private static void WriteLine(string line)
        {
            lock (_fileLock)
            {
                try
                {
                    File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                }
            }
        }
    }
}
