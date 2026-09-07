namespace ConningMonitorPRS.Core.Models
{
    // One channel's verdict from the PRS-GNSS-01 rule engine — always carries enough to
    // explain itself (DP-OA handover doc rule #10: every state transition must be
    // traceable to a RuleID, metric, threshold, time, and evidence).
    public class RuleResult
    {
        public string      RuleId    { get; set; } = "";
        public string      ChannelId { get; set; } = ""; // "H1".."H9"
        public HealthState State     { get; set; } = HealthState.Unknown;
        public string      Metric    { get; set; } = "";
        public double      Value     { get; set; }
        public double?     Threshold { get; set; }
        public string      Evidence  { get; set; } = "";
    }
}
