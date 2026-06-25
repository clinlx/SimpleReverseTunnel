using System;
using System.Runtime.CompilerServices;

namespace SimpleReverseTunnel
{
    public static class Logger
    {
        private const string TimeZoneEnvironmentVariable = "TZ";

        public enum LogLevel
        {
            Info = 0,
            Warn = 1,
            Error = 2,
            None = 3
        }

        // 默认日志级别
        public static LogLevel CurrentLevel { get; set; } = LogLevel.Info;

        public static bool IsInfoEnabled => CurrentLevel <= LogLevel.Info;
        public static bool IsWarnEnabled => CurrentLevel <= LogLevel.Warn;
        public static bool IsErrorEnabled => CurrentLevel <= LogLevel.Error;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Info(string message)
        {
            if (IsInfoEnabled)
            {
                Log(ConsoleColor.Cyan, "INFO", message);
            }
        }
        
        // 如果需要极致优化，建议调用方检查 IsInfoEnabled
        // 示例: if (Logger.IsInfoEnabled) Logger.Info($"...");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Warn(string message)
        {
            if (IsWarnEnabled)
            {
                Log(ConsoleColor.Yellow, "WARN", message);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Error(string message)
        {
            if (IsErrorEnabled)
            {
                Log(ConsoleColor.Red, "ERROR", message);
            }
        }

        private static readonly object _lock = new object();

        private static void Log(ConsoleColor color, string level, string message)
        {
            // 简单的锁防止多线程输出乱序（虽然 Console.WriteLine 内部有锁，但颜色切换没有）
            lock (_lock)
            {
                Console.ForegroundColor = color;
                Console.Write($"[{GetCurrentLogTime():yyyy-MM-dd HH:mm:ss}] [{level}] ");
                Console.WriteLine(message);
                Console.ResetColor();
            }
        }

        private static DateTimeOffset GetCurrentLogTime()
        {
            TimeZoneInfo timeZone = GetConfiguredTimeZone();
            return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        }

        private static TimeZoneInfo GetConfiguredTimeZone()
        {
            string? timeZoneId = Environment.GetEnvironmentVariable(TimeZoneEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(timeZoneId))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                }
                catch (TimeZoneNotFoundException)
                {
                }
                catch (InvalidTimeZoneException)
                {
                }
            }

            return TimeZoneInfo.Local;
        }
    }
}
