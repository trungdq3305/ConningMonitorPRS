using System;
using System.Collections.Generic;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Geo;
using ConningMonitorPRS.Core.Models;

namespace ConningMonitorPRS.Services
{
    // PRS-GNSS-01 — GNSS/DGPS health rule engine (9 channels, H1-H9) from the DP-OA handover
    // doc, section 5's baseline rule matrix. Pure calculation: takes a Snapshot, returns a
    // GnssHealthStatus. Does NOT write to ConningDataHub, does NOT log, does NOT touch alarm
    // Tags — that is DpOaCore's job (the "LÕI DP-OA" layer), matching the doc's pipeline
    // diagram (section 1): Máy thu → PRS-GNSS-01 → LÕI DP-OA → HMI/Log.
    //
    // Known limitations (deliberate, documented rather than worked around):
    //  - H6 (receiver/antenna) is always UNKNOWN — proprietary receiver diagnostic messages
    //    are out of scope for standard NMEA-0183 (matches the doc's own FAT T11 expectation).
    //  - H4 (constellation) approximates "how many constellations are contributing" from GSV
    //    in-view counts, not a true per-constellation used-in-fix breakdown (GSA doesn't carry
    //    a constellation tag in this app's parser).
    //  - H1 and H7 currently derive from the same underlying "last update to the GPS task"
    //    timestamp in ConningDataHub (there's no separate raw-line-vs-valid-fix timestamp) —
    //    they differ only by threshold (H1 short/H7 3x longer), not by an independent signal,
    //    until the hub tracks the two ages separately.
    public class GnssHealthEvaluator
    {
        private readonly PersistenceTimer _h1Timer = new();
        private readonly PersistenceTimer _h7Timer = new();

        private double? _prevLatDeg, _prevLonDeg;
        private DateTime? _prevPosUtc;

        private readonly Queue<double> _hdopHistory = new();
        private readonly Queue<int>    _satsHistory = new();
        private const int TrendSamples = 10;

        public GnssHealthStatus Evaluate(Snapshot snap)
        {
            var channels = new Dictionary<string, RuleResult>
            {
                ["H1"] = EvalH1DataAvailability(snap),
                ["H2"] = EvalH2PositionSolution(snap),
                ["H3"] = EvalH3SatelliteGeometry(snap),
                ["H4"] = EvalH4Constellation(snap),
                ["H5"] = EvalH5DifferentialCorrection(snap),
                ["H6"] = EvalH6ReceiverAntenna(),
                ["H7"] = EvalH7DataLink(snap),
                ["H8"] = EvalH8PositionIntegrity(snap),
                ["H9"] = EvalH9Trend(snap),
            };

            var overall = HealthState.Unknown;
            bool sawKnown = false;
            foreach (var c in channels.Values)
            {
                if (c.State == HealthState.Unknown) continue;
                if (!sawKnown || c.State.Severity() > overall.Severity()) overall = c.State;
                sawKnown = true;
            }

            var advisories = new List<Advisory>();
            foreach (var c in channels.Values)
                if (c.State != HealthState.Healthy && c.State != HealthState.Unknown)
                    advisories.Add(BuildAdvisory(c));
            advisories.Sort((a, b) => b.Severity.Severity().CompareTo(a.Severity.Severity()));

            return new GnssHealthStatus
            {
                Overall      = overall,
                Channels     = channels,
                Advisories   = advisories,
                TimestampUtc = DateTime.UtcNow,
            };
        }

        // H1 — data availability: is the GPS task still producing anything at all recently.
        private RuleResult EvalH1DataAvailability(Snapshot snap)
        {
            double age = GpsTaskAge(snap);
            bool timedOut = age > SystemConfig.GnssDataTimeoutSeconds;
            bool confirmed = _h1Timer.Confirm(timedOut, SystemConfig.GnssConfirmSeconds);
            var state = confirmed ? HealthState.Invalid : HealthState.Healthy;
            return new RuleResult
            {
                RuleId = "GNSS_H1_TIMEOUT", ChannelId = "H1", State = state,
                Metric = "DataAgeSeconds", Value = age, Threshold = SystemConfig.GnssDataTimeoutSeconds,
                Evidence = $"GPS task age {age:0.0}s vs timeout {SystemConfig.GnssDataTimeoutSeconds:0.0}s"
            };
        }

        // H2 — position solution: 3D fix vs 2D vs no fix.
        private RuleResult EvalH2PositionSolution(Snapshot snap)
        {
            string fix = snap.GsaFixType;
            HealthState state = fix switch
            {
                "3D" => HealthState.Healthy,
                "2D" => HealthState.Degraded,
                "NO FIX" => HealthState.Invalid,
                _ => HealthState.Unknown,
            };
            return new RuleResult
            {
                RuleId = "GNSS_H2_FIXTYPE", ChannelId = "H2", State = state,
                Metric = "FixType", Value = 0, Threshold = null,
                Evidence = $"GSA fix type = '{(string.IsNullOrEmpty(fix) ? "n/a" : fix)}'"
            };
        }

        // H3 — satellite geometry: worst-of HDOP/PDOP tier.
        private RuleResult EvalH3SatelliteGeometry(Snapshot snap)
        {
            HealthState hdopState = TierAscending(snap.Hdop,
                SystemConfig.GnssHdopDegraded, SystemConfig.GnssHdopWarning, SystemConfig.GnssHdopInvalid);
            HealthState pdopState = TierAscending(snap.Pdop,
                SystemConfig.GnssPdopDegraded, double.PositiveInfinity, SystemConfig.GnssPdopInvalid);
            var worst = hdopState.Severity() >= pdopState.Severity() ? hdopState : pdopState;
            return new RuleResult
            {
                RuleId = "GNSS_H3_DOP", ChannelId = "H3", State = worst,
                Metric = "Hdop/Pdop", Value = snap.Hdop, Threshold = SystemConfig.GnssHdopWarning,
                Evidence = $"HDOP={snap.Hdop:0.00} PDOP={snap.Pdop:0.00} " +
                           $"(Degraded>{SystemConfig.GnssHdopDegraded:0.0}, Warning>{SystemConfig.GnssHdopWarning:0.0}, Invalid>{SystemConfig.GnssHdopInvalid:0.0})"
            };
        }

        // H4 — constellation status: how many constellations are actively contributing
        // (approximated from GSV in-view counts — see class-level "known limitations").
        private RuleResult EvalH4Constellation(Snapshot snap)
        {
            int active = 0;
            foreach (var s in snap.Satellites)
                if (s.CountInView > 0 && s.Age < 5.0) active++;

            HealthState state = active >= 2 ? HealthState.Healthy
                               : active == 1 ? HealthState.Degraded
                               : HealthState.Unknown;
            return new RuleResult
            {
                RuleId = "GNSS_H4_CONSTELLATION", ChannelId = "H4", State = state,
                Metric = "ActiveConstellations", Value = active, Threshold = 2,
                Evidence = $"{active} constellation(s) reporting satellites in view (GSV, <5s old)"
            };
        }

        // H5 — differential correction age. Only meaningful in DGPS/RTK mode; standalone GPS
        // is explicitly still valid per the doc ("GNSS standalone vẫn có thể còn hợp lệ").
        private RuleResult EvalH5DifferentialCorrection(Snapshot snap)
        {
            bool isDifferential = snap.GpsFixQuality is "DGPS" or "RTK FIX" or "RTK FLT";
            if (!isDifferential)
            {
                return new RuleResult
                {
                    RuleId = "GNSS_H5_CORRAGE", ChannelId = "H5", State = HealthState.Unknown,
                    Metric = "DgpsAgeSeconds", Value = double.NaN, Threshold = null,
                    Evidence = $"Fix quality '{snap.GpsFixQuality}' is not a differential mode — correction age N/A"
                };
            }

            double age = snap.DgpsAgeSec;
            HealthState state = double.IsNaN(age) ? HealthState.Unknown
                : TierAscending(age, SystemConfig.GnssCorrectionAgeDegraded,
                    SystemConfig.GnssCorrectionAgeWarning, SystemConfig.GnssCorrectionAgeInvalid);
            return new RuleResult
            {
                RuleId = "GNSS_H5_CORRAGE", ChannelId = "H5", State = state,
                Metric = "DgpsAgeSeconds", Value = age, Threshold = SystemConfig.GnssCorrectionAgeWarning,
                Evidence = double.IsNaN(age)
                    ? $"Fix quality '{snap.GpsFixQuality}' but no correction-age field in GGA"
                    : $"Correction age {age:0.0}s, station {(string.IsNullOrEmpty(snap.DgpsStationId) ? "?" : snap.DgpsStationId)}"
            };
        }

        // H6 — receiver/antenna diagnostics: needs proprietary receiver messages, always
        // Unknown over standard NMEA-0183 (doc FAT T11: "not HEALTHY").
        private static RuleResult EvalH6ReceiverAntenna() => new()
        {
            RuleId = "GNSS_H6_RECEIVER", ChannelId = "H6", State = HealthState.Unknown,
            Metric = "n/a", Value = 0, Threshold = null,
            Evidence = "Receiver/antenna diagnostics require proprietary receiver messages — out of scope for standard NMEA-0183"
        };

        // H7 — data link: is anything at all arriving on the GPS port (coarser/longer-fuse
        // version of H1 — see class-level "known limitations" about the shared timestamp).
        private RuleResult EvalH7DataLink(Snapshot snap)
        {
            double age = GpsTaskAge(snap);
            double timeout = SystemConfig.GnssDataTimeoutSeconds * 3.0;
            bool confirmed = _h7Timer.Confirm(age > timeout, SystemConfig.GnssConfirmSeconds);
            var state = confirmed ? HealthState.Invalid : HealthState.Healthy;
            return new RuleResult
            {
                RuleId = "GNSS_H7_DATALINK", ChannelId = "H7", State = state,
                Metric = "DataAgeSeconds", Value = age, Threshold = timeout,
                Evidence = $"GPS port age {age:0.0}s vs link-down threshold {timeout:0.0}s"
            };
        }

        // H8 — position integrity: does the implied displacement between ticks roughly match
        // the vessel's own reported SOG (a real jump/glitch won't).
        private RuleResult EvalH8PositionIntegrity(Snapshot snap)
        {
            bool hasFix = !double.IsNaN(snap.GpsLatDeg) && !double.IsNaN(snap.GpsLonDeg);
            var now = DateTime.UtcNow;

            if (!hasFix || _prevLatDeg is null || _prevLonDeg is null || _prevPosUtc is null)
            {
                if (hasFix) { _prevLatDeg = snap.GpsLatDeg; _prevLonDeg = snap.GpsLonDeg; _prevPosUtc = now; }
                return new RuleResult
                {
                    RuleId = "GNSS_H8_JUMP", ChannelId = "H8", State = HealthState.Unknown,
                    Metric = "JumpResidualM", Value = 0, Threshold = SystemConfig.GnssPositionJumpWarningM,
                    Evidence = "Not enough position history yet to evaluate"
                };
            }

            double dt = (now - _prevPosUtc.Value).TotalSeconds;
            double actualM = GeoMath.DistanceMeters(_prevLatDeg.Value, _prevLonDeg.Value, snap.GpsLatDeg, snap.GpsLonDeg);
            double expectedM = snap.GpsSpeedKnot * 0.514444 * Math.Max(dt, 0.001);
            double residual = Math.Abs(actualM - expectedM);

            _prevLatDeg = snap.GpsLatDeg; _prevLonDeg = snap.GpsLonDeg; _prevPosUtc = now;

            var state = (dt > 0.05 && residual > SystemConfig.GnssPositionJumpWarningM)
                ? HealthState.Warning : HealthState.Healthy;
            return new RuleResult
            {
                RuleId = "GNSS_H8_JUMP", ChannelId = "H8", State = state,
                Metric = "JumpResidualM", Value = residual, Threshold = SystemConfig.GnssPositionJumpWarningM,
                Evidence = $"Moved {actualM:0.0}m in {dt:0.0}s, SOG implies {expectedM:0.0}m (residual {residual:0.0}m)"
            };
        }

        // H9 — trend: has HDOP or satellites-used been getting steadily worse over the last
        // ~TrendSamples evaluator ticks (~1 sample/second, driven by MainForm.HealthTick).
        private RuleResult EvalH9Trend(Snapshot snap)
        {
            _hdopHistory.Enqueue(snap.Hdop);
            _satsHistory.Enqueue(snap.SatsUsed);
            while (_hdopHistory.Count > TrendSamples) _hdopHistory.Dequeue();
            while (_satsHistory.Count > TrendSamples) _satsHistory.Dequeue();

            if (_hdopHistory.Count < 5)
                return new RuleResult
                {
                    RuleId = "GNSS_H9_TREND", ChannelId = "H9", State = HealthState.Unknown,
                    Metric = "SampleCount", Value = _hdopHistory.Count, Threshold = 5,
                    Evidence = "Warming up — not enough samples for a trend yet"
                };

            var hdopArr = _hdopHistory.ToArray();
            var satsArr = _satsHistory.ToArray();
            bool hdopWorsening = IsMonotonic(hdopArr, ascending: true) && (hdopArr[^1] - hdopArr[0]) > 0.5;
            bool satsWorsening = IsMonotonic(Array.ConvertAll(satsArr, x => (double)x), ascending: false)
                                  && (satsArr[0] - satsArr[^1]) >= 2;

            var state = (hdopWorsening || satsWorsening) ? HealthState.Degraded : HealthState.Healthy;
            return new RuleResult
            {
                RuleId = "GNSS_H9_TREND", ChannelId = "H9", State = state,
                Metric = "HdopDelta", Value = hdopArr[^1] - hdopArr[0], Threshold = 0.5,
                Evidence = hdopWorsening ? $"HDOP trending up: {hdopArr[0]:0.00} → {hdopArr[^1]:0.00}"
                         : satsWorsening ? $"Satellites used trending down: {satsArr[0]} → {satsArr[^1]}"
                         : "No sustained degradation trend"
            };
        }

        private static double GpsTaskAge(Snapshot snap)
        {
            foreach (var row in snap.TaskRows)
                if (row.TaskName == "GPS") return row.Age;
            return 9999;
        }

        // Ascending tier: value <= degraded -> Healthy; <= warning -> Degraded; <= invalid ->
        // Warning; > invalid -> Invalid. NaN (field absent) -> Unknown.
        private static HealthState TierAscending(double value, double degraded, double warning, double invalid)
        {
            if (double.IsNaN(value)) return HealthState.Unknown;
            if (value <= degraded) return HealthState.Healthy;
            if (value <= warning) return HealthState.Degraded;
            if (value <= invalid) return HealthState.Warning;
            return HealthState.Invalid;
        }

        private static bool IsMonotonic(double[] values, bool ascending)
        {
            for (int i = 1; i < values.Length; i++)
            {
                if (ascending && values[i] < values[i - 1] - 0.05) return false;
                if (!ascending && values[i] > values[i - 1] + 0.05) return false;
            }
            return true;
        }

        private static Advisory BuildAdvisory(RuleResult r)
        {
            var (what, why, impact, action) = r.ChannelId switch
            {
                "H1" => ("Mất dữ liệu vị trí GPS", r.Evidence, "Không còn cập nhật vị trí — mất tham chiếu vị trí.", "Kiểm tra kết nối cổng COM/thiết bị GPS."),
                "H2" => ("Chất lượng giải vị trí suy giảm", r.Evidence, "Độ chính xác vị trí có thể không đạt yêu cầu DP.", "Theo dõi số vệ tinh/hình học, chuẩn bị chuyển PRS khác nếu tiếp tục xấu."),
                "H3" => ("Hình học vệ tinh suy giảm", r.Evidence, "Độ chính xác vị trí có thể giảm.", "Theo dõi HDOP/PDOP, đối chiếu PRS khác nếu tiếp tục xấu đi."),
                "H4" => ("Chòm sao vệ tinh đóng góp bị thu hẹp", r.Evidence, "Giảm dự phòng hình học — nhạy hơn với che khuất/nhiễu.", "Theo dõi số chòm sao đang đóng góp."),
                "H5" => ("Hiệu chỉnh vi sai (DGPS/RTK) đã cũ", r.Evidence, "Độ chính xác vị trí giảm dần theo tuổi hiệu chỉnh.", "Kiểm tra đường truyền hiệu chỉnh/trạm tham chiếu."),
                "H7" => ("Mất liên lạc với máy thu GPS", r.Evidence, "Không còn dữ liệu nào từ cổng GPS.", "Kiểm tra cáp/cổng COM/nguồn máy thu."),
                "H8" => ("Vị trí nhảy vọt bất thường", r.Evidence, "Có thể là nhiễu/đa đường hoặc lỗi máy thu.", "Đối chiếu với PRS khác trước khi tin vào vị trí hiện tại."),
                "H9" => ("Xu hướng GNSS đang xấu dần", r.Evidence, "Cảnh báo sớm trước khi chạm ngưỡng cứng.", "Theo dõi sát HDOP/số vệ tinh trong vài phút tới."),
                _ => (r.ChannelId, r.Evidence, "", ""),
            };
            return new Advisory { RuleId = r.RuleId, What = what, Why = why, Impact = impact, Action = action, Severity = r.State };
        }
    }
}
