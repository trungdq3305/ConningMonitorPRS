using System;
using System.IO;
using System.Windows.Forms;

namespace ConningMonitorPRS.Services
{
    public static class SystemLogger
    {
        private static readonly object _lockObj = new();
        private static readonly string _logPath =
            Path.Combine(Application.StartupPath, "Logs", "system.log");

        public static void LogInfo(string message)  => Write("INFO",  message);
        public static void LogError(string context, Exception ex) => Write("ERROR", $"[{context}] {ex.Message}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (_lockObj)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                    File.AppendAllText(_logPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
