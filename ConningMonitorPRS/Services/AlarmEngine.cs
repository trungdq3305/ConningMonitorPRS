using System;
using System.Collections.Generic;
using ConningMonitorPRS.Core.Models;

namespace ConningMonitorPRS.Services
{
    public class AlarmEngine
    {
        private readonly List<Alarm> _alarms = new();

        public event Action<Alarm>? AlarmRaised;
        public event Action<Alarm>? AlarmCleared;
        public event Action<Alarm>? AlarmAcked;

        public void Register(Alarm alarm) => _alarms.Add(alarm);

        public void Evaluate()
        {
            foreach (var a in _alarms)
            {
                a.Evaluate(out bool raised, out bool cleared);
                if (raised)  AlarmRaised?.Invoke(a);
                if (cleared) AlarmCleared?.Invoke(a);
            }
        }

        public void Ack(string id)
        {
            foreach (var a in _alarms)
                if (a.Id == id && a.Ack())
                    AlarmAcked?.Invoke(a);
        }

        public IReadOnlyList<Alarm> GetAll() => _alarms;
    }
}
