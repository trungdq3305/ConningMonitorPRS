using System.Collections.Generic;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Models;

namespace ConningMonitorPRS.Services
{
    // LÕI DP-OA — the DP-OA handover doc's pipeline (section 1) draws a distinct core layer
    // between each PRS module and the HMI/logger: Máy thu → PRS-GNSS-01 → LÕI DP-OA →
    // Khuyến cáo cho DPO/HMI → Log Sự kiện + Dữ liệu Thô. This class is that layer.
    //
    // It owns two responsibilities that must not leak back into MainForm or either evaluator:
    // (1) detecting per-channel state transitions and logging them with full evidence, and
    // (2) being the single point that publishes module status to the HMI (ConningDataHub).
    // Now that both PRS-GNSS-01 and PRS-PQE-01 (module 2) feed in, it also does the job the
    // pipeline diagram actually draws a "core" for: combining the two module verdicts per the
    // interpretation table in doc section 8 (HEALTHY+HEALTHY, DEGRADED+HEALTHY, HEALTHY+
    // WARNING = "critical: check for multipath/bias", WARNING+WARNING, ...) into one HMI-
    // facing message, instead of each module reaching the HMI independently.
    public class DpOaCore
    {
        private readonly DataLogger _logger;
        private GnssHealthStatus? _lastGnss;
        private PqeStatus?        _lastPqe;

        public GnssHealthStatus? CurrentGnssStatus => _lastGnss;
        public PqeStatus?        CurrentPqeStatus  => _lastPqe;

        public DpOaCore(DataLogger logger) => _logger = logger;

        public void IngestGnssStatus(GnssHealthStatus status)
        {
            LogTransitions(_lastGnss?.Channels, status.Channels);
            _lastGnss = status;
            ConningDataHub.Instance.UpdateGnssHealth(status);
            RecomputeCombined();
        }

        public void IngestPqeStatus(PqeStatus status)
        {
            LogTransitions(_lastPqe?.Channels, status.Channels);
            _lastPqe = status;
            ConningDataHub.Instance.UpdatePqeStatus(status);
            RecomputeCombined();
        }

        private void LogTransitions(Dictionary<string, RuleResult>? prevChannels, Dictionary<string, RuleResult> newChannels)
        {
            foreach (var (channelId, result) in newChannels)
            {
                RuleResult? prev = null;
                prevChannels?.TryGetValue(channelId, out prev);
                if (prev == null || prev.State != result.State)
                    _logger.LogRuleEvent(result.RuleId, channelId,
                        prev?.State.ToString() ?? "n/a", result.State.ToString(),
                        result.Value, result.Threshold, result.Evidence);
            }
        }

        // Doc section 8's interpretation table — only meaningful once both modules have
        // reported at least once; until then the HMI shows "waiting for data" (Unknown).
        private void RecomputeCombined()
        {
            if (_lastGnss == null || _lastPqe == null) return;
            var (text, severity) = BuildCombinedText(_lastGnss.Overall, _lastPqe.Overall);
            ConningDataHub.Instance.UpdateCombinedStatus(text, severity);
        }

        private static (string text, HealthState severity) BuildCombinedText(HealthState gnss, HealthState pqe)
        {
            if (gnss == HealthState.Healthy && pqe == HealthState.Healthy)
                return ("Độ tin cậy cao: trạng thái máy thu và hành vi vị trí quan sát được đồng nhất với nhau.", HealthState.Healthy);
            if (gnss == HealthState.Degraded && pqe == HealthState.Healthy)
                return ("Chất lượng máy thu/hiệu chỉnh bị giảm, nhưng vị trí thực tế vẫn ổn định. Tiếp tục giám sát.", HealthState.Degraded);
            if (gnss == HealthState.Healthy && (pqe == HealthState.Warning || pqe == HealthState.Invalid))
                return ("Tình huống quan trọng: máy thu báo cáo bình thường nhưng hành vi vị trí thực tế lại bất thường. Cần kiểm tra đa đường, sai lệch hệ thống, nhiễu và PRS độc lập.", HealthState.Warning);
            if ((gnss == HealthState.Warning || gnss == HealthState.Invalid) && (pqe == HealthState.Warning || pqe == HealthState.Invalid))
                return ("Độ tin cậy thấp: cả chỉ số máy thu/hệ thống lẫn hành vi vị trí thực tế đều kém.", HealthState.Invalid);
            if (gnss == HealthState.Unknown && (pqe == HealthState.Healthy || pqe == HealthState.Degraded))
                return ("Một số chẩn đoán GNSS không khả dụng; không kết luận tình trạng máy thu chỉ dựa trên chất lượng vị trí.", HealthState.Unknown);
            if (gnss == HealthState.Healthy && pqe == HealthState.Unknown)
                return ("Máy thu có vẻ hoạt động tốt, nhưng chưa đủ dữ liệu để đánh giá chất lượng vị trí thực tế (đang khởi động/warm-up).", HealthState.Unknown);

            // Any combination not explicitly listed in doc section 8 — fall back to worst-of
            // severity with a generic message rather than leaving it unhandled.
            var worst = gnss.Severity() >= pqe.Severity() ? gnss : pqe;
            return ($"GNSS: {gnss} · Chất lượng vị trí: {pqe}. Xem chi tiết ở từng cửa sổ tương ứng.", worst);
        }
    }
}
