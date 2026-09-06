using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ConningMonitorPRS.Services
{
    public class DataLogger : IDisposable
    {
        private readonly ConcurrentQueue<string> _queue = new();
        private readonly System.Timers.Timer _flushTimer;

        private const int LogRetentionDays = 90;

        private static readonly string _header =
            "Time,Type,SpeedKnot,HeadingDeg,RollDeg,PitchDeg,HeaveCm,HeavePeriodSec," +
            "WindSpeedMs,WindDirDeg,AlarmId,AlarmState,Value,Limit,Raw,GpsLat,GpsLon";

        public DataLogger()
        {
            // Recursive delete of stale log folders is pure file I/O with no UI dependency —
            // don't block the constructor (called from MainForm's constructor, before the
            // window is shown) on it.
            Task.Run(CleanOldLogs);
            _flushTimer = new System.Timers.Timer(10_000);
            _flushTimer.Elapsed += (s, e) => Flush();
            _flushTimer.Start();
        }

        public void LogSnapshot(double speed, double hdg, double roll, double pitch, double heave,
            double period, double wSpd, double wDir, string lat, string lon)
        {
            _queue.Enqueue(
                $"{DateTime.Now:HH:mm:ss.fff},DATA," +
                $"{speed:0.00},{hdg:0.0},{roll:0.0},{pitch:0.0},{heave:0.0},{period:0.0}," +
                $"{wSpd:0.0},{wDir:0},,,,,,{lat},{lon}");
        }

        public void LogAlarmEvent(string evt, string id, string state, double value, double limit)
        {
            _queue.Enqueue(
                $"{DateTime.Now:HH:mm:ss.fff},ALARM,,,,,,,,," +
                $"{id},{state},{value:0.00},{limit:0.00},{evt},,");
        }

        private void Flush()
        {
            if (_queue.IsEmpty) return;
            try
            {
                string file = GetCurrentFile();
                bool needHeader = !File.Exists(file);
                using var sw = new StreamWriter(file, append: true);
                if (needHeader) sw.WriteLine(_header);
                while (_queue.TryDequeue(out string? line))
                    sw.WriteLine(line);
            }
            catch (Exception ex) { SystemLogger.LogError("DataLogger.Flush", ex); }
        }

        private string GetCurrentFile()
        {
            var now    = DateTime.Now;
            string dir = Path.Combine(Application.StartupPath, "Logs", now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dir);
            int block  = (now.Minute >= 30) ? 30 : 0;
            return Path.Combine(dir, $"Log_{now:yyyyMMdd_HH}{block:00}.csv");
        }

        private static void CleanOldLogs()
        {
            try
            {
                string root = Path.Combine(Application.StartupPath, "Logs");
                if (!Directory.Exists(root)) return;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (DateTime.TryParseExact(Path.GetFileName(dir), "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out DateTime d))
                    {
                        if ((DateTime.Today - d).TotalDays > LogRetentionDays)
                            Directory.Delete(dir, recursive: true);
                    }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _flushTimer.Stop();
            _flushTimer.Dispose();
            Flush();
        }
    }
}
