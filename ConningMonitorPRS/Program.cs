using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ConningMonitorPRS.Services;
using ConningMonitorPRS.UI.Forms;

namespace ConningMonitorPRS
{
    internal static class Program
    {
        // Guards against a crash-loop: if the app dies and relaunches itself over and over
        // within a short window (e.g. a deterministic startup bug — bad config, hardware
        // that throws every time), each restart backs off further instead of hammering the
        // machine at ~2 relaunches/sec forever. MainForm clears the counter once it has run
        // stably for a while, so a single transient crash still recovers at full speed.
        private static readonly string _crashGuardPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "crash_guard.txt");
        private static readonly TimeSpan RapidCrashWindow = TimeSpan.FromSeconds(30);
        private const int    MaxBackoffMs = 30_000;

        [STAThread]
        static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                SystemLogger.LogError("Application.ThreadException", e.Exception);
                RestartApp();
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    SystemLogger.LogError("AppDomain.UnhandledException", ex);
                if (e.IsTerminating) RestartApp();
            };

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }

        // Called by MainForm after it has been up and ticking for a while — a stable run
        // means the crash that led here (if any) was transient, so future crashes should
        // again restart at full speed rather than inheriting this run's backoff.
        public static void ClearCrashGuard()
        {
            try { File.Delete(_crashGuardPath); } catch { }
        }

        public static void RestartApp()
        {
            int delayMs = 500;
            try
            {
                int count = 1;
                DateTime now = DateTime.UtcNow;
                if (File.Exists(_crashGuardPath))
                {
                    string[] parts = File.ReadAllText(_crashGuardPath).Split(',');
                    if (parts.Length == 2 &&
                        long.TryParse(parts[0], out long lastTicks) &&
                        int.TryParse(parts[1], out int lastCount) &&
                        now - new DateTime(lastTicks, DateTimeKind.Utc) < RapidCrashWindow)
                    {
                        count = lastCount + 1;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_crashGuardPath)!);
                File.WriteAllText(_crashGuardPath, $"{now.Ticks},{count}");

                if (count > 1)
                {
                    delayMs = Math.Min(500 * (1 << Math.Min(count - 1, 6)), MaxBackoffMs);
                    SystemLogger.LogInfo($"[Restart] {count} crashes within {RapidCrashWindow.TotalSeconds:0}s — backing off {delayMs}ms before relaunch.");
                }

                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    Process.Start(exe);
            }
            catch (Exception ex) { SystemLogger.LogError("Program.RestartApp", ex); }

            Thread.Sleep(delayMs);
            Environment.Exit(1);
        }
    }
}
