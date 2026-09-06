using System;

namespace ConningMonitorPRS.Core.Models
{
    public class Alarm
    {
        public string        Id                     { get; }
        public Tag            Tag                    { get; }
        public Func<double>  HighLimitProvider      { get; }
        // On-delay confirm timer — how long Tag.Value must stay continuously above the limit
        // before the alarm actually raises. Optional: alarms that don't pass one (AL_DRIFT,
        // AL_GPSDUO) default to 0s, i.e. the old instant-trip behavior, unchanged.
        public Func<double>  ConfirmSecondsProvider { get; }
        public AlarmState    State                  { get; private set; } = AlarmState.Normal;
        public bool          IsActive               => State != AlarmState.Normal;
        public bool          IsAcked                => State == AlarmState.Acknowledged;

        private DateTime? _violationStartUtc;

        public Alarm(string id, Tag tag, Func<double> highLimitProvider, Func<double>? confirmSecondsProvider = null)
        {
            Id                     = id;
            Tag                    = tag;
            HighLimitProvider      = highLimitProvider;
            ConfirmSecondsProvider = confirmSecondsProvider ?? (() => 0.0);
        }

        public bool Evaluate(out bool raised, out bool cleared)
        {
            raised  = false;
            cleared = false;
            double limit   = HighLimitProvider();
            bool   exceeds = Tag.Value > limit;

            // Any dip back under the limit — even for a single evaluation cycle — resets the
            // streak to zero, so a brief spike/glitch never accumulates toward the confirm delay.
            if (exceeds) _violationStartUtc ??= DateTime.UtcNow;
            else         _violationStartUtc = null;

            if (State == AlarmState.Normal)
            {
                if (exceeds && (DateTime.UtcNow - _violationStartUtc!.Value).TotalSeconds >= ConfirmSecondsProvider())
                {
                    State  = AlarmState.Active;
                    raised = true;
                }
            }
            // Clear side uses 5% hysteresis instead of a bare "!exceeds" check, so the alarm
            // doesn't chatter Active/Normal while the value hovers right at the limit.
            else if (Tag.Value <= limit * 0.95)
            {
                State   = AlarmState.Normal;
                cleared = true;
            }
            return IsActive;
        }

        public bool Ack()
        {
            if (State == AlarmState.Active) { State = AlarmState.Acknowledged; return true; }
            return false;
        }
    }
}
