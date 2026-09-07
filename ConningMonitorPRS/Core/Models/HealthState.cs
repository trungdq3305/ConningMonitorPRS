namespace ConningMonitorPRS.Core.Models
{
    // Graded health state for PRS-GNSS-01 (and future PRS modules) — distinct from the
    // binary Normal/Active/Acknowledged AlarmState used by the existing Alarm/AlarmEngine.
    // Unknown is deliberately not "healthy" (DP-OA handover doc rule #4): a channel that
    // has no data to evaluate must say so, not silently report Healthy.
    public enum HealthState { Unknown, Healthy, Degraded, Warning, Invalid }

    public static class HealthStateExtensions
    {
        // Ordinal severity for "worse-of" comparisons across channels. Unknown returns -1
        // and must be excluded from worse-of aggregation by the caller (it doesn't mean
        // "better than Healthy" — it means "no verdict").
        public static int Severity(this HealthState state) => state switch
        {
            HealthState.Healthy  => 0,
            HealthState.Degraded => 1,
            HealthState.Warning  => 2,
            HealthState.Invalid  => 3,
            _                    => -1,
        };
    }
}
