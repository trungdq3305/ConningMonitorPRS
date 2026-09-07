using System;
using System.Collections.Generic;
using ConningMonitorPRS.Core.Models;

namespace ConningMonitorPRS.Core.Data
{
    public class ConningDataHub
    {
        private static readonly Lazy<ConningDataHub> _instance = new(() => new ConningDataHub());
        public static ConningDataHub Instance => _instance.Value;

        private readonly object _lockData = new();

        private double _heading;
        private double _cog;
        private double _rot;           // deg/min, computed from heading delta
        private double _prevHdg;
        private DateTime _prevHdgTime;
        private double _windSpeedMs;
        private double _windDirDeg;
        private double _rollDeg;
        private double _pitchDeg;
        private double _heaveCm;
        private double _heavePeriodSec;
        private double _gpsSpeedKnot;
        private string _gpsLat  = "NO FIX";
        private string _gpsLon  = "NO FIX";
        private string _gpsFixQuality = "";
        private double _gpsLatDeg = double.NaN;
        private double _gpsLonDeg = double.NaN;
        private double _tempCelsius;
        private double _humidityPct;
        private double _pressureHPa;

        private readonly Dictionary<string, (int Count, int AvgSnr, DateTime Updated)> _satellites = new();
        private string _gsaFixType = "";
        private double _pdop, _hdop, _vdop;

        // PRS-GNSS-01 (GNSS health module) — extended GGA fields + evaluator output.
        private int    _satsUsed;
        private double _dgpsAgeSec = double.NaN;
        private string _dgpsStationId = "";
        private GnssHealthStatus? _gnssHealth;

        // PRS-PQE-01 (position quality module) + combined LÕI DP-OA interpretation.
        private PqeStatus?  _pqeStatus;
        private string      _combinedText     = "";
        private HealthState _combinedSeverity = HealthState.Unknown;

        private readonly Dictionary<string, string>   _rawStrings = new();
        private readonly Dictionary<string, DateTime> _lastUpdate = new();
        private readonly Dictionary<string, string>   _alarmState = new();

        private ConningDataHub() { }

        public void UpdateNumericData(string taskName, double v1, double v2 = 0, double v3 = 0)
        {
            lock (_lockData)
            {
                _lastUpdate[taskName] = DateTime.Now;
                switch (taskName)
                {
                    case "HEADING":
                        var now = DateTime.Now;
                        if (_prevHdgTime != default)
                        {
                            double dt = (now - _prevHdgTime).TotalMinutes;
                            if (dt > 0.0001 && dt < 0.05) // valid interval: 6ms–3s
                            {
                                double dh = v1 - _prevHdg;
                                if (dh >  180) dh -= 360;
                                if (dh < -180) dh += 360;
                                double rawRot = dh / dt;
                                // Exponential smoothing α=0.3
                                _rot = _rot * 0.7 + rawRot * 0.3;
                            }
                        }
                        _prevHdg     = v1;
                        _prevHdgTime = now;
                        _heading     = v1;
                        break;
                    case "WIND":
                        _windSpeedMs = v1;
                        _windDirDeg  = v2;
                        break;
                    case "R/P/H":
                        _rollDeg  = v1;
                        _pitchDeg = v2;
                        _heaveCm  = v3;
                        break;
                }
            }
        }

        public void UpdateCog(double cog)
        {
            lock (_lockData) { _cog = cog; }
        }

        public void UpdateGpsData(double speedKnot, string lat, string lon)
        {
            lock (_lockData)
            {
                _lastUpdate["GPS"] = DateTime.Now;
                _gpsSpeedKnot      = speedKnot;
                _gpsLat            = lat;
                _gpsLon            = lon;
            }
        }

        public void UpdateMeteoData(double temp, double humidity, double pressure)
        {
            lock (_lockData)
            {
                _lastUpdate["METEO"] = DateTime.Now;
                _tempCelsius         = temp;
                _humidityPct         = humidity;
                _pressureHPa         = pressure;
            }
        }

        public void UpdateGpsFixQuality(string quality)
        {
            lock (_lockData) { _gpsFixQuality = quality; }
        }

        public void UpdateGpsRaw(double latDeg, double lonDeg)
        {
            lock (_lockData) { _gpsLatDeg = latDeg; _gpsLonDeg = lonDeg; }
        }

        public void UpdateSatellites(string constellation, int count, int avgSnr)
        {
            lock (_lockData) { _satellites[constellation] = (count, avgSnr, DateTime.Now); }
        }

        public void UpdateGsa(string fixType, double pdop, double hdop, double vdop)
        {
            lock (_lockData) { _gsaFixType = fixType; _pdop = pdop; _hdop = hdop; _vdop = vdop; }
        }

        public void UpdateGgaExtended(int satsUsed, double dgpsAgeSec, string stationId)
        {
            lock (_lockData) { _satsUsed = satsUsed; _dgpsAgeSec = dgpsAgeSec; _dgpsStationId = stationId; }
        }

        // Called by DpOaCore.IngestGnssStatus, not directly by the evaluator or MainForm —
        // keeps the PRS-GNSS-01 → LÕI DP-OA → HMI ordering from the DP-OA handover doc intact.
        public void UpdateGnssHealth(GnssHealthStatus status)
        {
            lock (_lockData) { _gnssHealth = status; }
        }

        // Called by DpOaCore.IngestPqeStatus — same ordering rule as UpdateGnssHealth above.
        public void UpdatePqeStatus(PqeStatus status)
        {
            lock (_lockData) { _pqeStatus = status; }
        }

        // Called by DpOaCore.RecomputeCombined once both PRS-GNSS-01 and PRS-PQE-01 have
        // reported at least once — doc section 8's combined interpretation.
        public void UpdateCombinedStatus(string text, HealthState severity)
        {
            lock (_lockData) { _combinedText = text; _combinedSeverity = severity; }
        }

        public void UpdateHeavePeriod(double sec)
        {
            lock (_lockData) { _heavePeriodSec = sec; }
        }

        public void UpdateRawString(string taskName, string raw)
        {
            lock (_lockData)
            {
                _rawStrings[taskName] = raw;
                _lastUpdate[taskName] = DateTime.Now;
            }
        }

        public void UpdateAlarmState(string taskName, string alarmStr)
        {
            lock (_lockData) { _alarmState[taskName] = alarmStr; }
        }

        public Snapshot GetSnapshot()
        {
            lock (_lockData)
            {
                var now  = DateTime.Now;
                var rows = new List<SnapshotRow>();
                foreach (var taskName in new[] { "GPS", "WIND", "R/P/H", "HEADING", "METEO" })
                {
                    double age = _lastUpdate.TryGetValue(taskName, out var t)
                        ? (now - t).TotalSeconds : 9999;
                    _rawStrings.TryGetValue(taskName, out string? raw);
                    _alarmState.TryGetValue(taskName, out string? alm);
                    rows.Add(new SnapshotRow
                    {
                        TaskName    = taskName,
                        Value       = raw ?? "",
                        AlarmString = alm ?? "Normal",
                        Age         = age,
                        IsStale     = age > 2.0 && age <= 900
                    });
                }
                return new Snapshot
                {
                    TaskRows       = rows,
                    Heading        = _heading,
                    CogDeg         = _cog,
                    RotDegMin      = _rot,
                    WindSpeedMs    = _windSpeedMs,
                    WindDirDeg     = _windDirDeg,
                    RollDeg        = _rollDeg,
                    PitchDeg       = _pitchDeg,
                    HeaveCm        = _heaveCm,
                    HeavePeriodSec = _heavePeriodSec,
                    GpsSpeedKnot   = _gpsSpeedKnot,
                    GpsLat         = _gpsLat,
                    GpsLon         = _gpsLon,
                    GpsFixQuality  = _gpsFixQuality,
                    GpsLatDeg      = _gpsLatDeg,
                    GpsLonDeg      = _gpsLonDeg,
                    TempCelsius    = _tempCelsius,
                    HumidityPct    = _humidityPct,
                    PressureHPa    = _pressureHPa,
                    Satellites     = BuildSatelliteList(now),
                    GsaFixType     = _gsaFixType,
                    Pdop           = _pdop,
                    Hdop           = _hdop,
                    Vdop           = _vdop,
                    SatsUsed       = _satsUsed,
                    DgpsAgeSec     = _dgpsAgeSec,
                    DgpsStationId  = _dgpsStationId,
                    GnssHealth     = _gnssHealth,
                    PqeStatus        = _pqeStatus,
                    CombinedText     = _combinedText,
                    CombinedSeverity = _combinedSeverity,
                };
            }
        }

        // Caller already holds _lockData.
        private List<SatelliteInfo> BuildSatelliteList(DateTime now)
        {
            var list = new List<SatelliteInfo>();
            foreach (var kv in _satellites)
            {
                list.Add(new SatelliteInfo
                {
                    Constellation = kv.Key,
                    CountInView   = kv.Value.Count,
                    AvgSnr        = kv.Value.AvgSnr,
                    Age           = (now - kv.Value.Updated).TotalSeconds,
                });
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Constellation, b.Constellation));
            return list;
        }
    }

    public class Snapshot
    {
        public List<SnapshotRow> TaskRows       { get; set; } = new();
        public double Heading        { get; set; }
        public double CogDeg         { get; set; }
        public double RotDegMin      { get; set; }
        public double WindSpeedMs    { get; set; }
        public double WindDirDeg     { get; set; }
        public double RollDeg        { get; set; }
        public double PitchDeg       { get; set; }
        public double HeaveCm        { get; set; }
        public double HeavePeriodSec { get; set; }
        public double GpsSpeedKnot   { get; set; }
        public string GpsLat         { get; set; } = "NO FIX";
        public string GpsLon         { get; set; } = "NO FIX";
        public string GpsFixQuality  { get; set; } = "";
        public double GpsLatDeg      { get; set; } = double.NaN;
        public double GpsLonDeg      { get; set; } = double.NaN;
        public double TempCelsius    { get; set; }
        public double HumidityPct    { get; set; }
        public double PressureHPa    { get; set; }
        public List<SatelliteInfo> Satellites { get; set; } = new();
        public string GsaFixType     { get; set; } = "";
        public double Pdop           { get; set; }
        public double Hdop           { get; set; }
        public double Vdop           { get; set; }

        // PRS-GNSS-01 (GNSS health module)
        public int    SatsUsed       { get; set; }
        public double DgpsAgeSec     { get; set; } = double.NaN;
        public string DgpsStationId  { get; set; } = "";
        public GnssHealthStatus? GnssHealth { get; set; }

        // PRS-PQE-01 (position quality module) + combined LÕI DP-OA interpretation.
        public PqeStatus?  PqeStatus        { get; set; }
        public string      CombinedText     { get; set; } = "";
        public HealthState CombinedSeverity { get; set; } = HealthState.Unknown;
    }

    public class SatelliteInfo
    {
        public string Constellation { get; set; } = "";
        public int    CountInView   { get; set; }
        public int    AvgSnr        { get; set; }
        public double Age           { get; set; }
    }

    public class SnapshotRow
    {
        public string TaskName    { get; set; } = "";
        public string Value       { get; set; } = "";
        public string AlarmString { get; set; } = "Normal";
        public double Age         { get; set; }
        public bool   IsStale     { get; set; }
    }
}
