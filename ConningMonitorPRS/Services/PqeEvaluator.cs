using System;
using System.Collections.Generic;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Geo;
using ConningMonitorPRS.Core.Models;

namespace ConningMonitorPRS.Services
{
    // PRS-PQE-01 — Position Quality Evaluation (8 channels, Q1-Q8) from the DP-OA handover
    // doc, section 7's baseline rule matrix. Pure calculation, same contract as
    // GnssHealthEvaluator: takes a Snapshot, returns a PqeStatus. Does NOT write to
    // ConningDataHub, does NOT log — that is DpOaCore's job.
    //
    // Consumes the already-normalized position stream (Snapshot.GpsLatDeg/GpsLonDeg/
    // GpsSpeedKnot) — no NMEA parsing needed here, unlike PRS-GNSS-01.
    //
    // Known limitations (deliberate, documented rather than worked around):
    //  - Q7 (cross-PRS consistency) is always UNKNOWN — this app has exactly one active
    //    position source (DUO GPS is disabled, see CLAUDE.md "DUO GPS mode"), so there is
    //    nothing to compare against, matching the doc's own rule ("only compare once time-
    //    aligned against another validated PRS").
    //  - Q6 (dynamic consistency) compares inferred velocity (from the filtered position
    //    stream) against Snapshot.GpsSpeedKnot, which comes from the SAME GPS receiver's
    //    $VTG — not an independent motion reference (gyro log / EM log) as the doc
    //    envisions. Still useful for catching internal inconsistency in the position stream,
    //    just not a true independent cross-check.
    public class PqeEvaluator
    {
        private const double FastTauSeconds = 5.0;
        private const double SlowTauSeconds = 60.0;
        private const double FreezeEpsilonM = 0.05; // position considered "unchanged" below this
        private const int    MedianWindow   = 5;

        private double? _originLat, _originLon;
        private readonly Queue<double> _medianEBuf = new();
        private readonly Queue<double> _medianNBuf = new();

        private bool   _filtersInit;
        private double _fastE, _fastN, _slowE, _slowN;
        private DateTime _lastFilterUtc;

        private readonly Queue<(DateTime t, double e, double n)> _residualBuf = new();
        private readonly Queue<(DateTime t, double e, double n)> _slowBuf     = new();

        private double? _prevE, _prevN;
        private DateTime? _prevUtc;

        private double? _freezeAnchorE, _freezeAnchorN;
        private double  _freezeImpliedAccumM;

        private int _sampleCount;

        public PqeStatus Evaluate(Snapshot snap)
        {
            var now = DateTime.UtcNow;
            bool hasFix = !double.IsNaN(snap.GpsLatDeg) && !double.IsNaN(snap.GpsLonDeg);

            var channels = new Dictionary<string, RuleResult> { ["Q1"] = EvalQ1(snap, hasFix) };

            if (!hasFix)
            {
                foreach (var id in new[] { "Q2", "Q3", "Q4", "Q5", "Q6", "Q8" })
                    channels[id] = Unavailable(id);
                channels["Q7"] = EvalQ7();
                return Aggregate(channels);
            }

            if (_originLat is null || _originLon is null) { _originLat = snap.GpsLatDeg; _originLon = snap.GpsLonDeg; }
            var (e, n) = GeoMath.OffsetMeters(_originLat.Value, _originLon.Value, snap.GpsLatDeg, snap.GpsLonDeg);

            _medianEBuf.Enqueue(e); _medianNBuf.Enqueue(n);
            while (_medianEBuf.Count > MedianWindow) _medianEBuf.Dequeue();
            while (_medianNBuf.Count > MedianWindow) _medianNBuf.Dequeue();
            double eMed = Median(_medianEBuf);
            double nMed = Median(_medianNBuf);

            if (!_filtersInit)
            {
                _fastE = eMed; _fastN = nMed; _slowE = eMed; _slowN = nMed; _filtersInit = true;
            }
            else
            {
                double dtF = Math.Max((now - _lastFilterUtc).TotalSeconds, 0.001);
                double alphaFast = dtF / (FastTauSeconds + dtF);
                double alphaSlow = dtF / (SlowTauSeconds + dtF);
                _fastE += alphaFast * (eMed - _fastE);
                _fastN += alphaFast * (nMed - _fastN);
                _slowE += alphaSlow * (eMed - _slowE);
                _slowN += alphaSlow * (nMed - _slowN);
            }
            _lastFilterUtc = now;

            double resE = eMed - _fastE, resN = nMed - _fastN;
            _residualBuf.Enqueue((now, resE, resN));
            TrimOlderThan(_residualBuf, now, 60.0);
            _slowBuf.Enqueue((now, _slowE, _slowN));
            TrimOlderThan(_slowBuf, now, 65.0);

            _sampleCount++;
            bool warm = _sampleCount >= SystemConfig.PqeWarmupSamples;

            // Jump + dynamic-consistency both need the previous tick's (median-filtered) point.
            double jumpResidualM = double.NaN, velMismatchKn = double.NaN;
            if (_prevUtc is not null && _prevE is not null && _prevN is not null)
            {
                double dt = Math.Max((now - _prevUtc.Value).TotalSeconds, 0.001);
                double actualM = Math.Sqrt(Math.Pow(eMed - _prevE.Value, 2) + Math.Pow(nMed - _prevN.Value, 2));
                double expectedM = snap.GpsSpeedKnot * 0.514444 * dt;
                jumpResidualM = Math.Abs(actualM - expectedM);
                velMismatchKn = Math.Abs(actualM / dt / 0.514444 - snap.GpsSpeedKnot);
            }

            // Freeze: accumulate SOG-implied displacement while the raw position hasn't moved
            // beyond FreezeEpsilonM; any real movement resets the anchor.
            double movedSinceAnchor = (_freezeAnchorE is null || _freezeAnchorN is null)
                ? double.PositiveInfinity
                : Math.Sqrt(Math.Pow(eMed - _freezeAnchorE.Value, 2) + Math.Pow(nMed - _freezeAnchorN.Value, 2));
            if (movedSinceAnchor > FreezeEpsilonM)
            {
                _freezeAnchorE = eMed; _freezeAnchorN = nMed; _freezeImpliedAccumM = 0;
            }
            else if (_prevUtc is not null)
            {
                double dt = Math.Max((now - _prevUtc.Value).TotalSeconds, 0.0);
                _freezeImpliedAccumM += snap.GpsSpeedKnot * 0.514444 * dt;
            }
            bool frozen = _freezeImpliedAccumM > SystemConfig.PqeFreezeThresholdM;

            _prevE = eMed; _prevN = nMed; _prevUtc = now;

            channels["Q2"] = EvalQ2(warm, now);
            channels["Q3"] = EvalQ3(warm, now);
            channels["Q4"] = EvalQ4(warm, now);
            channels["Q5"] = EvalQ5(warm, jumpResidualM, frozen);
            channels["Q6"] = EvalQ6(warm, velMismatchKn);
            channels["Q7"] = EvalQ7();
            channels["Q8"] = EvalQ8(warm, channels);

            return Aggregate(channels);
        }

        private RuleResult EvalQ1(Snapshot snap, bool hasFix)
        {
            double age = GpsTaskAge(snap);
            var state = !hasFix ? HealthState.Unknown
                       : age <= SystemConfig.GnssDataTimeoutSeconds ? HealthState.Healthy : HealthState.Invalid;
            return new RuleResult
            {
                RuleId = "PQE_Q1_INPUT", ChannelId = "Q1", State = state,
                Metric = "DataAgeSeconds", Value = age, Threshold = SystemConfig.GnssDataTimeoutSeconds,
                Evidence = hasFix ? $"Position data age {age:0.0}s" : "No position fix received yet"
            };
        }

        private RuleResult EvalQ2(bool warm, DateTime now)
        {
            double rms30 = ComputeRms(now, 30.0);
            var state = !warm || double.IsNaN(rms30) ? HealthState.Unknown
                : TierAscending(rms30, SystemConfig.PqeRms30Degraded, SystemConfig.PqeRms30Warning, SystemConfig.PqeRms30Invalid);
            return new RuleResult
            {
                RuleId = "PQE_Q2_NOISE", ChannelId = "Q2", State = state,
                Metric = "Rms30M", Value = double.IsNaN(rms30) ? 0 : rms30, Threshold = SystemConfig.PqeRms30Warning,
                Evidence = !warm ? "Warming up" :
                    $"RMS(30s)={rms30:0.00}m (Degraded>{SystemConfig.PqeRms30Degraded:0.00}, Warning>{SystemConfig.PqeRms30Warning:0.00}, Invalid>{SystemConfig.PqeRms30Invalid:0.00})"
            };
        }

        private RuleResult EvalQ3(bool warm, DateTime now)
        {
            double r95 = ComputeR95(now, 60.0);
            var state = !warm || double.IsNaN(r95) ? HealthState.Unknown
                : TierAscending(r95, SystemConfig.PqeR95Degraded, SystemConfig.PqeR95Warning, SystemConfig.PqeR95Invalid);
            return new RuleResult
            {
                RuleId = "PQE_Q3_STABILITY", ChannelId = "Q3", State = state,
                Metric = "R95_60sM", Value = double.IsNaN(r95) ? 0 : r95, Threshold = SystemConfig.PqeR95Warning,
                Evidence = !warm ? "Warming up" :
                    $"R95(60s)={r95:0.00}m (Degraded>{SystemConfig.PqeR95Degraded:0.00}, Warning>{SystemConfig.PqeR95Warning:0.00}, Invalid>{SystemConfig.PqeR95Invalid:0.00})"
            };
        }

        private RuleResult EvalQ4(bool warm, DateTime now)
        {
            var (rate, bearing) = ComputeDrift(now);
            var state = !warm ? HealthState.Unknown
                : TierAscending(rate, SystemConfig.PqeDriftDegraded, SystemConfig.PqeDriftWarning, SystemConfig.PqeDriftInvalid);
            return new RuleResult
            {
                RuleId = "PQE_Q4_DRIFT", ChannelId = "Q4", State = state,
                Metric = "DriftRateMPerMin", Value = rate, Threshold = SystemConfig.PqeDriftWarning,
                Evidence = !warm ? "Warming up" : $"Drift {rate:0.000} m/min, bearing {bearing:0}°"
            };
        }

        private static RuleResult EvalQ5(bool warm, double jumpResidualM, bool frozen)
        {
            var jumpState = double.IsNaN(jumpResidualM) ? HealthState.Unknown
                : TierAscending(jumpResidualM, SystemConfig.PqeJumpDegraded, SystemConfig.PqeJumpWarning, SystemConfig.PqeJumpInvalid);
            var freezeState = frozen ? HealthState.Warning : HealthState.Healthy;
            var state = !warm ? HealthState.Unknown
                : (jumpState.Severity() >= freezeState.Severity() ? jumpState : freezeState);
            string evidence = !warm ? "Warming up"
                : frozen ? $"Freeze suspected — SOG implies ~{SystemConfig.PqeFreezeThresholdM:0.00}m+ movement with no position change"
                : double.IsNaN(jumpResidualM) ? "Not enough position history yet"
                : $"Jump residual {jumpResidualM:0.00}m (Degraded>{SystemConfig.PqeJumpDegraded:0.0}, Warning>{SystemConfig.PqeJumpWarning:0.0}, Invalid>{SystemConfig.PqeJumpInvalid:0.0})";
            return new RuleResult
            {
                RuleId = "PQE_Q5_JUMPFREEZE", ChannelId = "Q5", State = state,
                Metric = "JumpResidualM", Value = double.IsNaN(jumpResidualM) ? 0 : jumpResidualM,
                Threshold = SystemConfig.PqeJumpWarning, Evidence = evidence
            };
        }

        private static RuleResult EvalQ6(bool warm, double velMismatchKn)
        {
            HealthState state;
            if (!warm || double.IsNaN(velMismatchKn)) state = HealthState.Unknown;
            else if (velMismatchKn <= SystemConfig.PqeVelMismatchDegradedKn) state = HealthState.Healthy;
            else if (velMismatchKn <= SystemConfig.PqeVelMismatchWarningKn) state = HealthState.Degraded;
            else state = HealthState.Warning;
            return new RuleResult
            {
                RuleId = "PQE_Q6_DYNAMICS", ChannelId = "Q6", State = state,
                Metric = "VelMismatchKn", Value = double.IsNaN(velMismatchKn) ? 0 : velMismatchKn,
                Threshold = SystemConfig.PqeVelMismatchWarningKn,
                Evidence = !warm ? "Warming up" : double.IsNaN(velMismatchKn) ? "Not enough position history yet"
                    : $"|inferred velocity - SOG| = {velMismatchKn:0.00}kn (Degraded>{SystemConfig.PqeVelMismatchDegradedKn:0.0}, Warning>{SystemConfig.PqeVelMismatchWarningKn:0.0})"
            };
        }

        private static RuleResult EvalQ7() => new()
        {
            RuleId = "PQE_Q7_CROSSPRS", ChannelId = "Q7", State = HealthState.Unknown,
            Metric = "n/a", Value = 0, Threshold = null,
            Evidence = "Only one position source configured (DUO GPS disabled) — cross-PRS comparison not possible"
        };

        private static RuleResult EvalQ8(bool warm, Dictionary<string, RuleResult> channels)
        {
            int degraded = 0;
            foreach (var id in new[] { "Q2", "Q3", "Q4", "Q5", "Q6" })
                if (channels.TryGetValue(id, out var r) && r.State != HealthState.Healthy && r.State != HealthState.Unknown)
                    degraded++;
            var state = !warm ? HealthState.Unknown : degraded >= 3 ? HealthState.Warning : HealthState.Healthy;
            return new RuleResult
            {
                RuleId = "PQE_Q8_TREND", ChannelId = "Q8", State = state,
                Metric = "DegradedChannelCount", Value = degraded, Threshold = 3,
                Evidence = !warm ? "Warming up" : $"{degraded} of 5 channels (Q2-Q6) below Healthy"
            };
        }

        private static RuleResult Unavailable(string channelId) => new()
        {
            RuleId = $"PQE_{channelId}_NODATA", ChannelId = channelId, State = HealthState.Unknown,
            Metric = "n/a", Value = 0, Threshold = null, Evidence = "No position fix received yet"
        };

        private double ComputeRms(DateTime now, double windowSeconds)
        {
            double sumSq = 0; int count = 0;
            foreach (var (t, e, n) in _residualBuf)
            {
                if ((now - t).TotalSeconds > windowSeconds) continue;
                sumSq += e * e + n * n; count++;
            }
            return count > 0 ? Math.Sqrt(sumSq / count) : double.NaN;
        }

        private double ComputeR95(DateTime now, double windowSeconds)
        {
            var mags = new List<double>();
            foreach (var (t, e, n) in _residualBuf)
            {
                if ((now - t).TotalSeconds > windowSeconds) continue;
                mags.Add(Math.Sqrt(e * e + n * n));
            }
            if (mags.Count == 0) return double.NaN;
            mags.Sort();
            int idx = Math.Clamp((int)Math.Ceiling(0.95 * mags.Count) - 1, 0, mags.Count - 1);
            return mags[idx];
        }

        // (rateMPerMin, bearingDeg) — bearing via atan2(dxEast, dyNorth), 0=N clockwise, same
        // convention as ConningControl's off-range target indicator (already local ENU meters
        // here, not lat/lon, so GeoMath.BearingDeg — which takes lat/lon — doesn't apply).
        private (double rateMPerMin, double bearingDeg) ComputeDrift(DateTime now)
        {
            if (_slowBuf.Count == 0) return (0, 0);
            var oldest = _slowBuf.Peek();
            double dtMin = (now - oldest.t).TotalMinutes;
            if (dtMin < 0.1) return (0, 0);
            double de = _slowE - oldest.e, dn = _slowN - oldest.n;
            double dist = Math.Sqrt(de * de + dn * dn);
            double bearing = Math.Atan2(de, dn) * 180.0 / Math.PI;
            if (bearing < 0) bearing += 360;
            return (dist / dtMin, bearing);
        }

        private static void TrimOlderThan(Queue<(DateTime t, double e, double n)> q, DateTime now, double maxAgeSeconds)
        {
            while (q.Count > 0 && (now - q.Peek().t).TotalSeconds > maxAgeSeconds) q.Dequeue();
        }

        private static double Median(Queue<double> buf)
        {
            var arr = buf.ToArray();
            Array.Sort(arr);
            int mid = arr.Length / 2;
            return arr.Length % 2 == 1 ? arr[mid] : (arr[mid - 1] + arr[mid]) / 2.0;
        }

        private static double GpsTaskAge(Snapshot snap)
        {
            foreach (var row in snap.TaskRows)
                if (row.TaskName == "GPS") return row.Age;
            return 9999;
        }

        private static HealthState TierAscending(double value, double degraded, double warning, double invalid)
        {
            if (double.IsNaN(value)) return HealthState.Unknown;
            if (value <= degraded) return HealthState.Healthy;
            if (value <= warning) return HealthState.Degraded;
            if (value <= invalid) return HealthState.Warning;
            return HealthState.Invalid;
        }

        private static PqeStatus Aggregate(Dictionary<string, RuleResult> channels)
        {
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

            return new PqeStatus { Overall = overall, Channels = channels, Advisories = advisories, TimestampUtc = DateTime.UtcNow };
        }

        private static Advisory BuildAdvisory(RuleResult r)
        {
            var (what, why, impact, action) = r.ChannelId switch
            {
                "Q1" => ("Mất dữ liệu vị trí đầu vào", r.Evidence, "Không thể đánh giá chất lượng vị trí.", "Kiểm tra nguồn GPS (xem PRS-GNSS-01)."),
                "Q2" => ("Nhiễu vị trí ngắn hạn tăng cao", r.Evidence, "Vị trí dao động nhiều hơn bình thường quanh giá trị thật.", "So sánh RMS với PRS khác nếu có, theo dõi xu hướng."),
                "Q3" => ("Độ ổn định vị trí suy giảm", r.Evidence, "Đám mây vị trí (R95) rộng hơn ngưỡng bình thường.", "Theo dõi sát, chuẩn bị phương án PRS dự phòng."),
                "Q4" => ("Vị trí đang trôi dạt", r.Evidence, "Tâm vị trí dịch chuyển liên tục theo thời gian, không phải nhiễu ngẫu nhiên.", "Đối chiếu với PRS độc lập khác, kiểm tra sai lệch hệ thống."),
                "Q5" => ("Nhảy vọt hoặc đóng băng vị trí", r.Evidence, "Vị trí hiện tại có thể không phản ánh đúng chuyển động thật của tàu.", "Không dựa vào vị trí này một mình — đối chiếu PRS khác trước khi tin."),
                "Q6" => ("Vận tốc suy ra không khớp SOG", r.Evidence, "Luồng vị trí có thể không nhất quán nội tại.", "Theo dõi thêm; xem xét cả PRS-GNSS-01 (H8) để đối chiếu."),
                "Q8" => ("Xu hướng chất lượng vị trí đang xấu dần", r.Evidence, "Nhiều chỉ số cùng suy giảm — cảnh báo sớm trước khi chạm ngưỡng cứng.", "Theo dõi sát các kênh Q2-Q6 trong vài phút tới."),
                _ => (r.ChannelId, r.Evidence, "", ""),
            };
            return new Advisory { RuleId = r.RuleId, What = what, Why = why, Impact = impact, Action = action, Severity = r.State };
        }
    }
}
