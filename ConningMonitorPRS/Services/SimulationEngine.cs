using System;
using System.Text;
using System.Timers;
using ConningMonitorPRS.UI.Forms;
using Timer = System.Timers.Timer;

namespace ConningMonitorPRS.Services
{
    public class SimulationEngine
    {
        private Timer? _timer;
        private Action<string, string>? _callback;
        private readonly Random _rng = new();

        private double _hdg    = 45.0;
        private double _wSpd   = 12.0;
        private double _wDir   = 135.0;
        private double _lat    = 10.7769;
        private double _lon    = 106.7009;
        private int    _tickCount;

        // Straight-leg / turn cycle — was a near-static ±0.2°/tick jitter before (never
        // produced a real turn), which gave nothing to test the heading-afterimage trail
        // (ConningControl.DrawShipTrail) or ROT-driven bow/stern arrows against. Alternates a
        // steady leg (afterimage should stay invisible, ROT≈0) with a smooth ±30-70° turn
        // (afterimage should sweep visibly, ROT clearly nonzero) — exercises both sides of the
        // "only show the trail while actually turning" guard, not just the turning side.
        private bool   _turning;
        private double _phaseTimeSec;
        private double _turnStartHdg, _turnDeltaDeg;
        private const double StraightLegSeconds = 8.0;
        private const double TurnSeconds        = 6.0;

        public void Start(Action<string, string> callback)
        {
            _callback = callback;
            _timer    = new Timer(100);
            _timer.Elapsed += OnTick;
            _timer.Start();
        }

        public void Stop() => _timer?.Stop();

        // BUG FIX: this used to hardcode "COM1"/"COM2"/"COM4"/"COM6" for every sentence below —
        // fine as long as ConfigForm.Tasks' saved port assignments happened to match those
        // defaults, but silently broke the moment they didn't (e.g. after testing the COM
        // auto-swap feature in Settings, or just reconfiguring real hardware ports and then
        // switching back to Simulation Mode): MainForm.GpsSourceForPort(port) looks up
        // ConfigForm.Tasks by PortName, finds no match for the hardcoded literal, and silently
        // drops the GPS event — POSITION shows "NO FIX" forever with no error anywhere. Looking
        // the port up by TaskName instead (falling back to the same literal if that task is
        // missing entirely) means Simulation Mode keeps working regardless of what COM ports
        // are currently saved, which is the whole point of a simulation mode.
        private static string PortFor(string taskName, string fallback) =>
            ConfigForm.Tasks.Find(t => t.TaskName == taskName)?.PortName ?? fallback;

        private void OnTick(object? sender, ElapsedEventArgs e)
        {
            const double dtSec = 0.1; // matches the 100ms timer interval below

            // Looked up once per tick (cheap — 6-item list) rather than per sentence below.
            string gpsPort     = PortFor("GPS",     "COM1");
            string windPort    = PortFor("WIND",    "COM2");
            string headingPort = PortFor("HEADING", "COM4");
            string gps2Port    = PortFor("GPS2",    "COM6");

            _phaseTimeSec += dtSec;
            if (!_turning)
            {
                // Steady leg — small jitter only, so ROT stays ~0. Note: this does NOT hide
                // DrawShipTrail's afterimage on its own anymore — dead-reckoning (below) keeps
                // the ship translating at ~8.5-9kt even on a "straight" leg, so the trail's
                // position-offset half of its guard stays open and a straight-line wake is
                // still drawn throughout this phase. Only a ship that's both holding heading
                // AND sitting still (SOG≈0, not exercised by this simulator) would fully hide
                // the afterimage — this phase specifically demonstrates the "turning" guard
                // relaxing (no heading sweep, wake only), the turn phase below demonstrates both
                // heading sweep and wake together.
                _hdg = (_hdg + _rng.NextDouble() * 0.2 - 0.1 + 360) % 360;
                if (_phaseTimeSec >= StraightLegSeconds)
                {
                    _turning       = true;
                    _phaseTimeSec  = 0;
                    _turnStartHdg  = _hdg;
                    _turnDeltaDeg  = (_rng.NextDouble() < 0.5 ? -1.0 : 1.0) * (30.0 + _rng.NextDouble() * 40.0);
                }
            }
            else
            {
                // Cosine ease-in/out over the turn — smooth acceleration/deceleration like a
                // real helm order, instead of snapping straight to a constant turn rate.
                double frac  = Math.Clamp(_phaseTimeSec / TurnSeconds, 0.0, 1.0);
                double eased = 0.5 - 0.5 * Math.Cos(frac * Math.PI);
                _hdg = (_turnStartHdg + _turnDeltaDeg * eased + 360) % 360;
                if (_phaseTimeSec >= TurnSeconds)
                {
                    _turning      = false;
                    _phaseTimeSec = 0;
                }
            }

            _wSpd  = Math.Max(0, _wSpd  + _rng.NextDouble() * 0.4 - 0.2);
            _wDir  = (_wDir  + _rng.NextDouble() * 1.0 - 0.5 + 360) % 360;

            _callback?.Invoke(headingPort, AppendChecksum($"$HEHDT,{_hdg:0.0},T"));
            // Yokogawa gyro compass HCR sentence — same heading, alongside HDT, so the new
            // NmeaParserService HCR branch is exercised in Simulation Mode.
            _callback?.Invoke(headingPort, AppendChecksum($"$HEHCR,{_hdg:0.0},A,N,00.0"));
            _callback?.Invoke(windPort, AppendChecksum($"$WIMWV,{_wDir:0.0},R,{_wSpd:0.00},M,A"));
            // COM3 (MRU) is Xsens XBus binary in real hardware — MruService generates its own
            // Roll/Pitch/Heave simulation internally, not routed through this NMEA engine.

            // Dead-reckon the position forward each tick from the current heading/speed — the
            // old code left _lat/_lon completely static (SOG randomized but the ship never
            // actually went anywhere), so POSITION never moved and RadarControl's track trail
            // had nothing to draw. Equirectangular approximation is fine at this speed/scale.
            double speed = 8.5 + _rng.NextDouble() * 0.5; // knots
            double distM = speed * 0.514444 * dtSec;
            double hdgRad = _hdg * Math.PI / 180.0;
            _lat += (distM * Math.Cos(hdgRad)) / 111320.0;
            _lon += (distM * Math.Sin(hdgRad)) / (111320.0 * Math.Cos(_lat * Math.PI / 180.0));

            string latStr = FormatDMM(_lat, true);
            string lonStr = FormatDMM(_lon, false);
            _callback?.Invoke(gpsPort, AppendChecksum($"$GPGGA,120000.00,{latStr},N,{lonStr},E,1,08,1.0,10.0,M,,,,"));
            // GNS sentence — same position, alongside GGA, so the new NmeaParserService GNS
            // branch is exercised in Simulation Mode (some real multi-constellation receivers
            // send only GNS, never GGA).
            _callback?.Invoke(gpsPort, AppendChecksum($"$GPGNS,120000.00,{latStr},N,{lonStr},E,AA,08,1.0,10.0,M,,,"));
            _callback?.Invoke(gpsPort, AppendChecksum($"$GPVTG,{_hdg:0.0},T,,M,{speed:0.00},N,,K,A"));

            // Second GPS receiver (DUO mode, default port COM6) — a few metres off GPS1 and a
            // notch lower fix quality, so enabling DuoGpsEnabled has something real to compare.
            string lat2Str = FormatDMM(_lat + 0.00012, true);
            _callback?.Invoke(gps2Port, AppendChecksum($"$GPGGA,120000.00,{lat2Str},N,{lonStr},E,1,07,1.3,10.0,M,,,,"));
            _callback?.Invoke(gps2Port, AppendChecksum($"$GPVTG,{_hdg:0.0},T,,M,{speed:0.00},N,,K,A"));

            // Simulate METEO via special tag
            double temp  = 28.0 + _rng.NextDouble() * 4;
            double hum   = 65.0 + _rng.NextDouble() * 20;
            double press = 1013.0 + _rng.NextDouble() * 10;
            ConningMonitorPRS.Core.Data.ConningDataHub.Instance.UpdateMeteoData(temp, hum, press);
            ConningMonitorPRS.Core.Data.ConningDataHub.Instance.UpdateRawString("METEO",
                $"T={temp:0.0}°C H={hum:0.0}% P={press:0.0}hPa");

            // GSV/GSA (satellite status) update at ~1 Hz like a real receiver, not every 100ms tick.
            if (++_tickCount % 10 == 0)
            {
                SendGsv("GP", 10, 42, gpsPort);
                SendGsv("GL", 8, 37, gpsPort);
                _callback?.Invoke(gpsPort, AppendChecksum("$GPGSA,A,3,01,02,03,04,05,06,07,08,,,,,1.4,0.9,1.1"));
            }
        }

        private void SendGsv(string talker, int totalInView, int baseSnr, string port)
        {
            var sb = new StringBuilder($"${talker}GSV,1,1,{totalInView}");
            int shown = Math.Min(4, totalInView);
            for (int i = 0; i < shown; i++)
            {
                int prn  = 1 + i;
                int elev = 20 + i * 15;
                int azim = 40 + i * 60;
                int snr  = Math.Max(0, baseSnr + _rng.Next(-4, 5));
                sb.Append($",{prn:00},{elev:00},{azim:000},{snr:00}");
            }
            _callback?.Invoke(port, AppendChecksum(sb.ToString()));
        }

        private static string FormatDMM(double deg, bool isLat)
        {
            double d   = Math.Floor(Math.Abs(deg));
            double min = (Math.Abs(deg) - d) * 60;
            return isLat ? $"{d:00}{min:00.0000}" : $"{d:000}{min:00.0000}";
        }

        private static string AppendChecksum(string sentence)
        {
            int start = sentence.StartsWith("$") ? 1 : 0;
            byte xor  = 0;
            for (int i = start; i < sentence.Length; i++)
                xor ^= (byte)sentence[i];
            return $"{sentence}*{xor:X2}\r\n";
        }
    }
}
