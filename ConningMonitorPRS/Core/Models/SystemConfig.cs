using System;
using System.Collections.Generic;

namespace ConningMonitorPRS.Core.Models
{
    public static class SystemConfig
    {
        public static event Action? ThemeChanged;

        private static bool _isLightTheme;

        public static bool IsLightTheme
        {
            get => _isLightTheme;
            set { if (_isLightTheme == value) return; _isLightTheme = value; ThemeChanged?.Invoke(); }
        }

        public static bool   IsSimulationMode { get; set; } = true;
        public static string AdminPassword     { get; set; } = "123456";
        public static string ShipName          { get; set; } = "CONNING MONITOR";
        public static double WindMax           { get; set; } = 40.0;
        public static double RMax              { get; set; } = 3.0;
        public static double PMax              { get; set; } = 3.0;
        public static double HMax              { get; set; } = 2.0;
        public static double MotionConfirmSeconds { get; set; } = 1.5;
        public static double WindConfirmSeconds   { get; set; } = 2.5;
        public static double Loa               { get; set; } = 100.0;
        public static double Lob               { get; set; } = 20.0;
        public static double GpsOffset         { get; set; } = 0.0;

        public static bool   DriftWatchEnabled { get; set; } = false;
        public static double DriftRefLat       { get; set; } = 0.0;
        public static double DriftRefLon       { get; set; } = 0.0;
        public static double DriftRadiusM      { get; set; } = 500.0;

        public static List<TargetPoint> Targets { get; set; } = new();

        public static bool   DuoGpsEnabled     { get; set; } = false;
        public static double GpsDuoDivergenceM { get; set; } = 50.0;

        public static void Apply(AppConfig cfg)
        {
            IsSimulationMode = cfg.IsSimulationMode;
            IsLightTheme     = cfg.IsLightTheme;
            AdminPassword    = cfg.AdminPassword ?? "123456";
            ShipName         = cfg.ShipName     ?? "CONNING MONITOR";
            WindMax          = cfg.WindMax;
            RMax             = cfg.RMax;
            PMax             = cfg.PMax;
            HMax             = cfg.HMax;
            MotionConfirmSeconds = Math.Clamp(cfg.MotionConfirmSeconds, 0.0, 30.0);
            WindConfirmSeconds   = Math.Clamp(cfg.WindConfirmSeconds, 0.0, 30.0);
            Loa              = cfg.Loa > 0 ? cfg.Loa : 100.0;
            Lob              = cfg.Lob > 0 ? cfg.Lob : 20.0;
            GpsOffset        = cfg.GpsOffset;

            DriftWatchEnabled = cfg.DriftWatchEnabled;
            DriftRefLat       = cfg.DriftRefLat;
            DriftRefLon       = cfg.DriftRefLon;
            DriftRadiusM      = cfg.DriftRadiusM > 0 ? cfg.DriftRadiusM : 500.0;
            Targets           = cfg.Targets ?? new();

            DuoGpsEnabled     = cfg.DuoGpsEnabled;
            GpsDuoDivergenceM = cfg.GpsDuoDivergenceM > 0 ? cfg.GpsDuoDivergenceM : 50.0;
        }

        public static AppConfig Export() => new AppConfig
        {
            IsSimulationMode = IsSimulationMode,
            IsLightTheme     = IsLightTheme,
            AdminPassword    = AdminPassword,
            ShipName         = ShipName,
            WindMax          = WindMax,
            RMax             = RMax,
            PMax             = PMax,
            HMax             = HMax,
            MotionConfirmSeconds = MotionConfirmSeconds,
            WindConfirmSeconds   = WindConfirmSeconds,
            Loa              = Loa,
            Lob              = Lob,
            GpsOffset        = GpsOffset,

            DriftWatchEnabled = DriftWatchEnabled,
            DriftRefLat       = DriftRefLat,
            DriftRefLon       = DriftRefLon,
            DriftRadiusM      = DriftRadiusM,
            Targets           = Targets,

            DuoGpsEnabled     = DuoGpsEnabled,
            GpsDuoDivergenceM = GpsDuoDivergenceM,
        };
    }
}
