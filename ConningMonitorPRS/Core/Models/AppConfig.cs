using System.Collections.Generic;

namespace ConningMonitorPRS.Core.Models
{
    public class AppConfig
    {
        public List<DeviceTask> Tasks          { get; set; } = new();
        public bool             IsSimulationMode { get; set; } = true;
        public bool             IsLightTheme     { get; set; } = false;
        public string           AdminPassword    { get; set; } = "123456";
        public string           ShipName         { get; set; } = "CONNING MONITOR";
        public double           WindMax          { get; set; } = 40.0;
        public double           RMax             { get; set; } = 3.0;
        public double           PMax             { get; set; } = 3.0;
        public double           HMax             { get; set; } = 2.0;

        // On-delay confirm timer (seconds) before AL_ROLL/AL_PITCH/AL_HEAVE (Motion) and
        // AL_WIND raise — filters brief noise spikes without delaying genuine excursions.
        public double           MotionConfirmSeconds { get; set; } = 1.5;
        public double           WindConfirmSeconds   { get; set; } = 2.5;
        public double           Loa              { get; set; } = 100.0;
        public double           Lob              { get; set; } = 20.0;
        public double           GpsOffset        { get; set; } = 0.0;

        // Position watch (drift-off alarm) — reference point + safe radius, e.g. a DP setpoint.
        public bool             DriftWatchEnabled { get; set; } = false;
        public double           DriftRefLat       { get; set; } = 0.0;
        public double           DriftRefLon       { get; set; } = 0.0;
        public double           DriftRadiusM      { get; set; } = 500.0;

        // Up to 4 named targets — distance & bearing computed live from current GPS position.
        public List<TargetPoint> Targets          { get; set; } = new();

#if DUO_GPS_ENABLED
        // DUO mode: a second GPS receiver (task "GPS2") tracked independently; MainForm picks
        // whichever has the better fix quality and alarms if the two disagree by too much.
        // Disabled 2026-09-07 per user request (single-GPS only) — code kept, gated behind
        // DUO_GPS_ENABLED, so it can be re-enabled later by defining that constant in the
        // .csproj. See CLAUDE.md "DUO GPS mode" section.
        public bool   DuoGpsEnabled      { get; set; } = false;
        public double GpsDuoDivergenceM  { get; set; } = 50.0;
#endif

        // PRS-GNSS-01 (GNSS health module) thresholds — all configurable per DP-OA handover
        // doc rule #5 ("no hard-coded thresholds/timers/hysteresis"). Defaults follow the
        // doc's section-5 baseline rule matrix.
        public double GnssDataTimeoutSeconds     { get; set; } = 3.0;   // H1/H7
        public double GnssConfirmSeconds         { get; set; } = 2.0;   // shared persistence, H1-H5
        public double GnssHdopDegraded           { get; set; } = 1.5;   // H3
        public double GnssHdopWarning            { get; set; } = 2.5;   // H3
        public double GnssHdopInvalid            { get; set; } = 4.0;   // H3
        public double GnssPdopDegraded           { get; set; } = 2.5;   // H3
        public double GnssPdopInvalid            { get; set; } = 4.0;   // H3
        public double GnssCorrectionAgeDegraded  { get; set; } = 5.0;   // H5
        public double GnssCorrectionAgeWarning   { get; set; } = 10.0;  // H5
        public double GnssCorrectionAgeInvalid   { get; set; } = 20.0;  // H5
        public double GnssPositionJumpWarningM   { get; set; } = 3.0;   // H8

        // PRS-PQE-01 (position quality module) thresholds — section 7 baseline rule matrix.
        public int    PqeWarmupSamples             { get; set; } = 30;   // ~30s at 1Hz
        public double PqeRms30Degraded             { get; set; } = 0.5;  // m, Q2
        public double PqeRms30Warning              { get; set; } = 1.0;
        public double PqeRms30Invalid              { get; set; } = 2.0;
        public double PqeR95Degraded               { get; set; } = 1.0;  // m, Q3
        public double PqeR95Warning                { get; set; } = 2.0;
        public double PqeR95Invalid                { get; set; } = 3.0;
        public double PqeDriftDegraded             { get; set; } = 0.10; // m/min, Q4
        public double PqeDriftWarning              { get; set; } = 0.25;
        public double PqeDriftInvalid              { get; set; } = 0.50;
        public double PqeJumpDegraded              { get; set; } = 1.0;  // m, Q5
        public double PqeJumpWarning               { get; set; } = 2.0;
        public double PqeJumpInvalid               { get; set; } = 3.0;
        public double PqeFreezeThresholdM          { get; set; } = 0.2;  // m, Q5
        public double PqeVelMismatchDegradedKn     { get; set; } = 0.3;  // knot, Q6
        public double PqeVelMismatchWarningKn      { get; set; } = 0.8;
    }
}
