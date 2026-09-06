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

        // DUO mode: a second GPS receiver (task "GPS2") tracked independently; MainForm picks
        // whichever has the better fix quality and alarms if the two disagree by too much.
        public bool   DuoGpsEnabled      { get; set; } = false;
        public double GpsDuoDivergenceM  { get; set; } = 50.0;
    }
}
