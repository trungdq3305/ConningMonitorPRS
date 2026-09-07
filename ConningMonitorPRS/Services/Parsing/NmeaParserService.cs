using System;
using System.Collections.Generic;
using ConningMonitorPRS.Core.Models;

namespace ConningMonitorPRS.Services.Parsing
{
    public class NmeaParserService
    {
        public double HeaveArm { get; set; } = 10.0;

        public event Action<double>?                 OnHeadingParsed;
        public event Action<double, double>?         OnWindParsed;
        public event Action<double, double, double>? OnMotionParsed;
        // GPS-derived events carry the source port name so a second GPS receiver (DUO mode)
        // can be told apart from the first — MainForm picks which source's data to publish.
        public event Action<string, string, string>? OnPositionParsed;     // port, lat, lon
        public event Action<string, double, double>? OnPositionRawParsed;  // port, latDeg, lonDeg
        public event Action<string, double>?         OnSpeedParsed;       // port, knots
        public event Action<string, double>?         OnCogParsed;         // port, cogDeg
        public event Action<string, int>?            OnGpsQualityParsed;  // port, quality
        public event Action<string, int, int>?        OnSatellitesParsed; // constellation, countInView, avgSnr
        public event Action<string, double, double, double>? OnGsaParsed;  // fixType, PDOP, HDOP, VDOP
        // GGA fields beyond lat/lon/quality — satellites used (field 7), HDOP (field 8),
        // DGPS correction age in seconds (field 13), reference station ID (field 14). Added
        // for PRS-GNSS-01 (GNSS health module). Correction age/station ID are commonly blank
        // outside DGPS/RTK mode — TryParse leaves them at NaN/"" rather than failing the whole
        // sentence, since most of a GGA line is still useful without those two trailing fields.
        public event Action<string, int, double, double, string>? OnGgaExtendedParsed; // port, satsUsed, hdop, dgpsAgeSec, stationId

        private readonly Dictionary<string, int> _errorCount = new();
        private readonly Dictionary<string, (int totalInView, int snrSum, int snrCount)> _gsvAccum = new();
        private List<DeviceTask> _portTasks = new();

        public void SetPortTasks(IEnumerable<DeviceTask> tasks) => _portTasks = new(tasks);

        public void Parse(string portName, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            string line = raw.Trim();
            if (!line.StartsWith("$")) return;
            if (!ValidateChecksum(line)) { CountError(portName); return; }
            _errorCount[portName] = 0;

            string[] p = line.Split(',');
            if (p.Length == 0) return;

            string type = p[0].TrimStart('$').ToUpperInvariant();

            // Filter by SentenceType if configured
            var task = _portTasks.Find(t => t.PortName == portName);
            if (task != null && !string.IsNullOrEmpty(task.SentenceType))
            {
                bool match = false;
                foreach (var allowed in task.SentenceType.Split(','))
                    if (type.EndsWith(allowed.Trim(), StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                if (!match) return;
            }

            if (type.EndsWith("HDT") && p.Length >= 2)
            {
                if (double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double hdg))
                    OnHeadingParsed?.Invoke(hdg);
                return;
            }

            // HCR: Yokogawa gyro compass proprietary sentence, e.g. "$HEHCR,056.0,A,N,00.0*hh" —
            // same field position (p[1]) as HDT. The same device also emits "$xxHRC" (no comma
            // between sentence ID and value) at a much faster rate — NOT handled here, since
            // there's no safe way to guess field boundaries on a no-delimiter format.
            if (type.EndsWith("HCR") && p.Length >= 2)
            {
                if (double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double hdgHcr))
                    OnHeadingParsed?.Invoke(hdgHcr);
                return;
            }

            if (type.EndsWith("MWV") && p.Length >= 5)
            {
                if (double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double dir) &&
                    double.TryParse(p[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double spd))
                    OnWindParsed?.Invoke(spd, dir);
                return;
            }

            if (p[0].Equals("$CNTB", StringComparison.OrdinalIgnoreCase) && p.Length >= 4)
            {
                if (double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double r) &&
                    double.TryParse(p[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double pt) &&
                    double.TryParse(p[3].Split('*')[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double h))
                    OnMotionParsed?.Invoke(r, pt, h);
                return;
            }

            if (p[0].Equals("$PRDID", StringComparison.OrdinalIgnoreCase) && p.Length >= 4)
            {
                if (double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double pt) &&
                    double.TryParse(p[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double r))
                {
                    double h = HeaveArm * Math.Sin(pt * Math.PI / 180.0) * 100;
                    OnMotionParsed?.Invoke(r, pt, h);
                }
                return;
            }

            if (p[0].Equals("$PASHR", StringComparison.OrdinalIgnoreCase) && p.Length >= 9)
            {
                // $PASHR,[ts,]Hdg,T,Roll,Pitch,Heave,...
                int offset = (p.Length >= 12) ? 1 : 0;
                if (double.TryParse(p[3 + offset], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double r) &&
                    double.TryParse(p[4 + offset], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double pt))
                {
                    double h = 0;
                    if (p.Length > 5 + offset && double.TryParse(p[5 + offset],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double hv))
                        h = hv * 100;
                    else
                        h = HeaveArm * Math.Sin(pt * Math.PI / 180.0) * 100;
                    OnMotionParsed?.Invoke(r, pt, h);
                }
                return;
            }

            if (type.EndsWith("GGA") && p.Length >= 6)
            {
                string lat = FormatLatLon(p[2], p[3]);
                string lon = FormatLatLon(p[4], p[5]);
                if (lat != "NO FIX")
                {
                    OnPositionParsed?.Invoke(portName, lat, lon);
                    double latDeg = ParseDecimalDegrees(p[2], p[3]);
                    double lonDeg = ParseDecimalDegrees(p[4], p[5]);
                    if (!double.IsNaN(latDeg) && !double.IsNaN(lonDeg))
                        OnPositionRawParsed?.Invoke(portName, latDeg, lonDeg);
                }
                if (p.Length >= 7 && int.TryParse(p[6], out int quality))
                    OnGpsQualityParsed?.Invoke(portName, quality);

                // Extended fields — satellites used (7), HDOP (8), DGPS age (13), station ID
                // (14). p.Length >= 6 already guaranteed by the branch guard above; each field
                // beyond that is read independently so a short/truncated sentence still yields
                // whatever trailing fields it does have instead of dropping all of them.
                int    satsUsed   = (p.Length >= 8 && int.TryParse(p[7], out int su)) ? su : 0;
                double hdopGga    = (p.Length >= 9 && double.TryParse(p[8], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double hd)) ? hd : double.NaN;
                double dgpsAgeSec = (p.Length >= 14 && double.TryParse(p[13], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double age)) ? age : double.NaN;
                string stationId  = (p.Length >= 15) ? p[14].Split('*')[0] : "";
                OnGgaExtendedParsed?.Invoke(portName, satsUsed, hdopGga, dgpsAgeSec, stationId);
                return;
            }

            // GNS: GNSS Fix Data — same lat/lon field positions as GGA (p[2..5]). Some
            // multi-constellation receivers send only GNS, never GGA. Mode indicator (p[6],
            // e.g. "ANN") is a per-constellation letter code, not the single fix-quality digit
            // GGA uses — no safe mapping to OnGpsQualityParsed, so only position is fired here.
            if (type.EndsWith("GNS") && p.Length >= 6)
            {
                string latGns = FormatLatLon(p[2], p[3]);
                string lonGns = FormatLatLon(p[4], p[5]);
                if (latGns != "NO FIX")
                {
                    OnPositionParsed?.Invoke(portName, latGns, lonGns);
                    double latDegGns = ParseDecimalDegrees(p[2], p[3]);
                    double lonDegGns = ParseDecimalDegrees(p[4], p[5]);
                    if (!double.IsNaN(latDegGns) && !double.IsNaN(lonDegGns))
                        OnPositionRawParsed?.Invoke(portName, latDegGns, lonDegGns);
                }
                return;
            }

            if (type.EndsWith("VTG") && p.Length >= 6)
            {
                // VTG: $--VTG,COG_T,T,,M,SOG_N,N,SOG_K,K,mode
                if (double.TryParse(p[5], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double kn))
                    OnSpeedParsed?.Invoke(portName, kn);
                if (p.Length >= 2 && double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double cog))
                    OnCogParsed?.Invoke(portName, cog);
                return;
            }

            if (type.EndsWith("GSV") && p.Length >= 4)
            {
                // $--GSV,totalMsgs,msgNum,totalSatsInView,[svid,elev,azim,snr]*N *CS
                // Accumulate across the (possibly multi-sentence) cycle for this constellation;
                // fire once when the last sentence of the cycle (msgNum == totalMsgs) arrives.
                if (int.TryParse(p[1], out int totalMsgs) &&
                    int.TryParse(p[2], out int msgNum) &&
                    int.TryParse(p[3], out int totalInView))
                {
                    string constellation = TalkerToConstellation(type);
                    if (msgNum == 1 || !_gsvAccum.ContainsKey(constellation))
                        _gsvAccum[constellation] = (totalInView, 0, 0);

                    var acc = _gsvAccum[constellation];
                    for (int i = 4; i + 3 < p.Length; i += 4)
                    {
                        string snrField = p[i + 3].Split('*')[0];
                        if (int.TryParse(snrField, out int snr))
                            acc = (acc.totalInView, acc.snrSum + snr, acc.snrCount + 1);
                    }
                    _gsvAccum[constellation] = acc;

                    if (msgNum >= totalMsgs)
                    {
                        int avgSnr = acc.snrCount > 0 ? acc.snrSum / acc.snrCount : 0;
                        OnSatellitesParsed?.Invoke(constellation, acc.totalInView, avgSnr);
                        _gsvAccum.Remove(constellation);
                    }
                }
                return;
            }

            if (type.EndsWith("GSA") && p.Length >= 18)
            {
                // $--GSA,mode1,mode2,sv1..sv12,PDOP,HDOP,VDOP*CS
                string fixType = p[2] switch { "1" => "NO FIX", "2" => "2D", "3" => "3D", _ => "?" };
                if (double.TryParse(p[15], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pdop) &&
                    double.TryParse(p[16], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hdop) &&
                    double.TryParse(p[17].Split('*')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vdop))
                    OnGsaParsed?.Invoke(fixType, pdop, hdop, vdop);
                return;
            }
        }

        private static string TalkerToConstellation(string type)
        {
            string talker = type.Length >= 2 ? type[..2] : "";
            return talker switch
            {
                "GP" => "GPS",
                "GL" => "GLONASS",
                "GA" => "GALILEO",
                "GB" or "BD" => "BEIDOU",
                "GQ" => "QZSS",
                "GI" => "NAVIC",
                "GN" => "GNSS",
                _ => talker,
            };
        }

        private void CountError(string port)
        {
            _errorCount.TryGetValue(port, out int c);
            _errorCount[port] = c + 1;
        }

        private static bool ValidateChecksum(string sentence)
        {
            int star = sentence.LastIndexOf('*');
            if (star < 1 || star + 2 >= sentence.Length) return false;
            string hexPart = sentence.Substring(star + 1, 2);
            if (!byte.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber,
                null, out byte expected)) return false;
            byte xor = 0;
            for (int i = 1; i < star; i++) xor ^= (byte)sentence[i];
            return xor == expected;
        }

        private static string FormatLatLon(string value, string dir)
        {
            if (string.IsNullOrEmpty(value)) return "NO FIX";
            try
            {
                bool isLat = dir == "N" || dir == "S";
                int  dLen  = isLat ? 2 : 3;
                double deg = double.Parse(value[..dLen], System.Globalization.CultureInfo.InvariantCulture);
                double min = double.Parse(value[dLen..], System.Globalization.CultureInfo.InvariantCulture);
                return $"{deg:00}°{min:00.000}'{dir}";
            }
            catch { return "NO FIX"; }
        }

        private static double ParseDecimalDegrees(string value, string dir)
        {
            if (string.IsNullOrEmpty(value)) return double.NaN;
            try
            {
                bool isLat = dir == "N" || dir == "S";
                int  dLen  = isLat ? 2 : 3;
                double deg = double.Parse(value[..dLen], System.Globalization.CultureInfo.InvariantCulture);
                double min = double.Parse(value[dLen..], System.Globalization.CultureInfo.InvariantCulture);
                double dd  = deg + min / 60.0;
                return (dir == "S" || dir == "W") ? -dd : dd;
            }
            catch { return double.NaN; }
        }
    }
}
