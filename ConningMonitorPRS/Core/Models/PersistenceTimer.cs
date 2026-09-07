using System;

namespace ConningMonitorPRS.Core.Models
{
    // On-delay confirm timer, factored out of Alarm.Evaluate()'s violation-streak logic
    // (Core/Models/Alarm.cs) so PRS-GNSS-01's H1-H9 rule engine can reuse the same
    // "condition must hold continuously for N seconds" pattern without copy-pasting a
    // DateTime? field 9 times. Any dip back to false resets the streak to zero — a brief
    // glitch never counts toward the confirm delay, same guarantee Alarm gives today.
    public class PersistenceTimer
    {
        private DateTime? _startUtc;

        public bool Confirm(bool conditionTrue, double confirmSeconds)
        {
            if (!conditionTrue) { _startUtc = null; return false; }
            _startUtc ??= DateTime.UtcNow;
            return (DateTime.UtcNow - _startUtc.Value).TotalSeconds >= confirmSeconds;
        }
    }
}
