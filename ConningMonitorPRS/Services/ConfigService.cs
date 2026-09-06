using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using ConningMonitorPRS.Core.Models;

namespace ConningMonitorPRS.Services
{
    public static class ConfigService
    {
        private static readonly string _path =
            Path.Combine(Application.StartupPath, "Config", "config.json");

        private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(_path))
                    return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path)) ?? new AppConfig();
            }
            catch (Exception ex) { SystemLogger.LogError("ConfigService.Load", ex); }
            return new AppConfig();
        }

        public static void Save(AppConfig cfg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(cfg, _opts));
            }
            catch (Exception ex) { SystemLogger.LogError("ConfigService.Save", ex); }
        }
    }
}
