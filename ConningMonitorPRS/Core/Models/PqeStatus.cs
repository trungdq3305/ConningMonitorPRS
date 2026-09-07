using System;
using System.Collections.Generic;

namespace ConningMonitorPRS.Core.Models
{
    // PRS-PQE-01 (Position Quality Evaluation) aggregate output — mirrors GnssHealthStatus's
    // shape exactly (Overall + Channels + Advisories), reusing RuleResult/Advisory/HealthState
    // rather than a shared base type: two small classes, not worth an interface.
    public class PqeStatus
    {
        public HealthState                    Overall      { get; set; } = HealthState.Unknown;
        public Dictionary<string, RuleResult> Channels     { get; set; } = new(); // "Q1".."Q8"
        public List<Advisory>                 Advisories   { get; set; } = new(); // worst-first
        public DateTime                       TimestampUtc { get; set; } = DateTime.UtcNow;
    }
}
