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

#if DUO_GPS_ENABLED
        public static bool   DuoGpsEnabled     { get; set; } = false;
        public static double GpsDuoDivergenceM { get; set; } = 50.0;
#endif

        // PRS-GNSS-01 (GNSS health module) — see AppConfig.cs for doc-rule-5 rationale.
        public static double GnssDataTimeoutSeconds    { get; set; } = 3.0;
        public static double GnssConfirmSeconds        { get; set; } = 2.0;
        public static double GnssHdopDegraded          { get; set; } = 1.5;
        public static double GnssHdopWarning           { get; set; } = 2.5;
        public static double GnssHdopInvalid           { get; set; } = 4.0;
        public static double GnssPdopDegraded          { get; set; } = 2.5;
        public static double GnssPdopInvalid           { get; set; } = 4.0;
        public static double GnssCorrectionAgeDegraded { get; set; } = 5.0;
        public static double GnssCorrectionAgeWarning  { get; set; } = 10.0;
        public static double GnssCorrectionAgeInvalid  { get; set; } = 20.0;
        public static double GnssPositionJumpWarningM  { get; set; } = 3.0;

        // PRS-PQE-01 (position quality module) — see AppConfig.cs for doc-rule-5 rationale.
        public static int    PqeWarmupSamples         { get; set; } = 30;
        public static double PqeRms30Degraded         { get; set; } = 0.5;
        public static double PqeRms30Warning          { get; set; } = 1.0;
        public static double PqeRms30Invalid          { get; set; } = 2.0;
        public static double PqeR95Degraded           { get; set; } = 1.0;
        public static double PqeR95Warning            { get; set; } = 2.0;
        public static double PqeR95Invalid            { get; set; } = 3.0;
        public static double PqeDriftDegraded         { get; set; } = 0.10;
        public static double PqeDriftWarning          { get; set; } = 0.25;
        public static double PqeDriftInvalid          { get; set; } = 0.50;
        public static double PqeJumpDegraded          { get; set; } = 1.0;
        public static double PqeJumpWarning           { get; set; } = 2.0;
        public static double PqeJumpInvalid           { get; set; } = 3.0;
        public static double PqeFreezeThresholdM      { get; set; } = 0.2;
        public static double PqeVelMismatchDegradedKn { get; set; } = 0.3;
        public static double PqeVelMismatchWarningKn  { get; set; } = 0.8;

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

#if DUO_GPS_ENABLED
            DuoGpsEnabled     = cfg.DuoGpsEnabled;
            GpsDuoDivergenceM = cfg.GpsDuoDivergenceM > 0 ? cfg.GpsDuoDivergenceM : 50.0;
#endif

            GnssDataTimeoutSeconds    = Math.Clamp(cfg.GnssDataTimeoutSeconds, 0.5, 60.0);
            GnssConfirmSeconds        = Math.Clamp(cfg.GnssConfirmSeconds, 0.0, 30.0);
            GnssHdopDegraded          = Math.Clamp(cfg.GnssHdopDegraded, 0.1, 50.0);
            GnssHdopWarning           = Math.Clamp(cfg.GnssHdopWarning, 0.1, 50.0);
            GnssHdopInvalid           = Math.Clamp(cfg.GnssHdopInvalid, 0.1, 50.0);
            GnssPdopDegraded          = Math.Clamp(cfg.GnssPdopDegraded, 0.1, 50.0);
            GnssPdopInvalid           = Math.Clamp(cfg.GnssPdopInvalid, 0.1, 50.0);
            GnssCorrectionAgeDegraded = Math.Clamp(cfg.GnssCorrectionAgeDegraded, 0.0, 300.0);
            GnssCorrectionAgeWarning  = Math.Clamp(cfg.GnssCorrectionAgeWarning, 0.0, 300.0);
            GnssCorrectionAgeInvalid  = Math.Clamp(cfg.GnssCorrectionAgeInvalid, 0.0, 300.0);
            GnssPositionJumpWarningM  = Math.Clamp(cfg.GnssPositionJumpWarningM, 0.1, 1000.0);

            PqeWarmupSamples         = Math.Clamp(cfg.PqeWarmupSamples, 3, 300);
            PqeRms30Degraded         = Math.Clamp(cfg.PqeRms30Degraded, 0.01, 50.0);
            PqeRms30Warning          = Math.Clamp(cfg.PqeRms30Warning, 0.01, 50.0);
            PqeRms30Invalid          = Math.Clamp(cfg.PqeRms30Invalid, 0.01, 50.0);
            PqeR95Degraded           = Math.Clamp(cfg.PqeR95Degraded, 0.01, 50.0);
            PqeR95Warning            = Math.Clamp(cfg.PqeR95Warning, 0.01, 50.0);
            PqeR95Invalid            = Math.Clamp(cfg.PqeR95Invalid, 0.01, 50.0);
            PqeDriftDegraded         = Math.Clamp(cfg.PqeDriftDegraded, 0.01, 50.0);
            PqeDriftWarning          = Math.Clamp(cfg.PqeDriftWarning, 0.01, 50.0);
            PqeDriftInvalid          = Math.Clamp(cfg.PqeDriftInvalid, 0.01, 50.0);
            PqeJumpDegraded          = Math.Clamp(cfg.PqeJumpDegraded, 0.01, 1000.0);
            PqeJumpWarning           = Math.Clamp(cfg.PqeJumpWarning, 0.01, 1000.0);
            PqeJumpInvalid           = Math.Clamp(cfg.PqeJumpInvalid, 0.01, 1000.0);
            PqeFreezeThresholdM      = Math.Clamp(cfg.PqeFreezeThresholdM, 0.01, 50.0);
            PqeVelMismatchDegradedKn = Math.Clamp(cfg.PqeVelMismatchDegradedKn, 0.01, 50.0);
            PqeVelMismatchWarningKn  = Math.Clamp(cfg.PqeVelMismatchWarningKn, 0.01, 50.0);
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

#if DUO_GPS_ENABLED
            DuoGpsEnabled     = DuoGpsEnabled,
            GpsDuoDivergenceM = GpsDuoDivergenceM,
#endif

            GnssDataTimeoutSeconds    = GnssDataTimeoutSeconds,
            GnssConfirmSeconds        = GnssConfirmSeconds,
            GnssHdopDegraded          = GnssHdopDegraded,
            GnssHdopWarning           = GnssHdopWarning,
            GnssHdopInvalid           = GnssHdopInvalid,
            GnssPdopDegraded          = GnssPdopDegraded,
            GnssPdopInvalid           = GnssPdopInvalid,
            GnssCorrectionAgeDegraded = GnssCorrectionAgeDegraded,
            GnssCorrectionAgeWarning  = GnssCorrectionAgeWarning,
            GnssCorrectionAgeInvalid  = GnssCorrectionAgeInvalid,
            GnssPositionJumpWarningM  = GnssPositionJumpWarningM,

            PqeWarmupSamples         = PqeWarmupSamples,
            PqeRms30Degraded         = PqeRms30Degraded,
            PqeRms30Warning          = PqeRms30Warning,
            PqeRms30Invalid          = PqeRms30Invalid,
            PqeR95Degraded           = PqeR95Degraded,
            PqeR95Warning            = PqeR95Warning,
            PqeR95Invalid            = PqeR95Invalid,
            PqeDriftDegraded         = PqeDriftDegraded,
            PqeDriftWarning          = PqeDriftWarning,
            PqeDriftInvalid          = PqeDriftInvalid,
            PqeJumpDegraded          = PqeJumpDegraded,
            PqeJumpWarning           = PqeJumpWarning,
            PqeJumpInvalid           = PqeJumpInvalid,
            PqeFreezeThresholdM      = PqeFreezeThresholdM,
            PqeVelMismatchDegradedKn = PqeVelMismatchDegradedKn,
            PqeVelMismatchWarningKn  = PqeVelMismatchWarningKn,
        };
    }
}
