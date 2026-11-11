using System;
using UnityEngine;

namespace QoL
{
    /// <summary>
    /// 统一日志：全部走 Unity 引擎日志（Player.log / 控制台），带 [QoL] 前缀与级别。
    ///
    /// 这里**不写任何文件**。原因：写文件需要向游戏声明 `files`（"读取或写入此电脑上任意位置的文件"）
    /// 能力，而这个能力在 2026-12-10 之后会让工坊模组必须通过开发者验证才能加载。
    /// 引擎日志本身已经落到 `Player.log`，排查问题完全够用。
    /// </summary>
    internal static class QLog
    {
        private const string Tag = "[QoL] ";

        /// <summary>同一条警告/错误在这个窗口内只输出一次，避免每帧异常把日志刷爆。</summary>
        private const float RepeatWindow = 10f;

        private static readonly System.Collections.Generic.Dictionary<string, float> Recent =
            new System.Collections.Generic.Dictionary<string, float>();

        private static bool _initialized;

        /// <summary>是否开启详细/调试级日志输出。</summary>
        internal static bool IsVerbose
        {
            get
            {
                QoLSettings s = ModEntry.Settings;
                return s != null && s.VerboseLogging != null && s.VerboseLogging.Value;
            }
        }

        internal static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            Info($"==================== QoL 日志初始化 ({DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}) ====================");
        }

        /// <summary>常规信息日志。</summary>
        internal static void Info(string message)
        {
            Debug.Log(Tag + message);
        }

        /// <summary>调试/详细日志（仅在设置中开启“详细日志记录”时生效）。</summary>
        internal static void DebugLog(string message)
        {
            if (!IsVerbose) return;
            Debug.Log(Tag + "[DEBUG] " + message);
        }

        /// <summary>警告日志（异常预警或安全降级）。</summary>
        internal static void Warn(string message)
        {
            if (IsRepeat("WARN", message)) return;
            Debug.LogWarning(Tag + message);
        }

        /// <summary>错误日志（严重故障或未捕获异常）。</summary>
        internal static void Error(string message, Exception ex = null)
        {
            if (IsRepeat("ERROR", message)) return;
            Debug.LogError(Tag + (ex != null ? $"{message}\n[Exception] {ex}" : message));
        }

        /// <summary>
        /// 同一条消息在 RepeatWindow 内只记一次。
        /// 每帧路径 / 每件物品的失败如果连续报同样的错，只留第一条。
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

        /// <summary>记录游戏与模组环境，排查兼容性问题用。</summary>
        internal static void DumpEnvironment()
        {
            try
            {
                Info("--- 环境信息 ---\n" +
                     $"游戏版本: {Application.version}\n" +
                     $"Unity 引擎版本: {Application.unityVersion}\n" +
                     $"系统平台: {Application.platform}\n" +
                     $"模组版本: {ModEntry.Version}\n" +
                     $"模组 ID: {ModEntry.ModId}");
            }
            catch (Exception e)
            {
                Warn("读取环境信息失败: " + e.Message);
            }
        }
    }
}
