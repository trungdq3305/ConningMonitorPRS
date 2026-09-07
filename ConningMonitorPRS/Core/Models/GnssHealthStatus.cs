using System;
using System.Collections.Generic;

namespace ConningMonitorPRS.Core.Models
{
    // WHAT/WHY/IMPACT/ACTION advisory shape mandated by the DP-OA handover doc (section 9)
    // for anything shown to the DPO.
    public class Advisory
    {
        public string      RuleId   { get; set; } = "";
        public string      What     { get; set; } = "";
        public string      Why      { get; set; } = "";
        public string      Impact   { get; set; } = "";
        public string      Action   { get; set; } = "";
        public HealthState Severity { get; set; } = HealthState.Unknown;
    }

    // Aggregate output of PRS-GNSS-01 (GnssHealthEvaluator) for one evaluation cycle.
    public class GnssHealthStatus
    {
        public HealthState                     Overall     { get; set; } = HealthState.Unknown;
        public Dictionary<string, RuleResult>  Channels    { get; set; } = new(); // "H1".."H9"
        public List<Advisory>                  Advisories  { get; set; } = new(); // worst-first
        public DateTime                        TimestampUtc { get; set; } = DateTime.UtcNow;
    }
}
