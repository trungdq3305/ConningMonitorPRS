using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Timers;
using Timer = System.Timers.Timer;
using ConningMonitorPRS.Core.Models;

namespace ConningMonitorPRS.Services
{
    public class ComEngine : IDisposable
    {
        public event Action<string, string>? OnDataReceived;

        private readonly Dictionary<string, SerialPort>   _ports      = new();
        private readonly Dictionary<string, StringBuilder> _buffers    = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastRx = new();
        private readonly object _portLock   = new();
        private readonly object _bufferLock = new();
        private List<DeviceTask> _tasks     = new();
        private Timer? _watchdog;
        private volatile bool _disposed;

        public void Initialize(List<DeviceTask> tasks)
        {
            _tasks = tasks;
            foreach (var t in tasks)
            {
                if (_disposed) return;
                TryOpenPort(t);
            }

            if (_disposed) return;
            _watchdog = new Timer(5000);
            _watchdog.Elapsed += WatchdogCheck;
            _watchdog.Start();
        }

        private void TryOpenPort(DeviceTask task)
        {
            if (_disposed) return;

            // Initialize() runs SerialPort.Open() (a blocking OS call) on a background task;
            // if the app is closing while an Open() is in flight, don't make Dispose() block
            // on this same lock waiting for it — just skip this attempt this cycle.
            if (!Monitor.TryEnter(_portLock, 100)) return;
            try
            {
                if (_disposed) return;
                if (_ports.TryGetValue(task.PortName, out var existing) && existing.IsOpen) return;
                try
                {
                    bool isMeteo = task.TaskName == "METEO";
                    bool isMru   = task.TaskName == "MRU";

                    var sp = new SerialPort(task.PortName, task.BaudRate, Parity.None, 8, StopBits.One)
                    {
                        ReadTimeout  = isMeteo ? 600 : isMru ? 5000 : SerialPort.InfiniteTimeout,
                        WriteTimeout = isMeteo ? 300 : isMru ? 500  : SerialPort.InfiniteTimeout,
                        Handshake    = Handshake.None
                    };

                    if (isMru)
                    {
                        sp.DtrEnable      = false;
                        sp.RtsEnable      = false;
                        sp.ReadBufferSize = 8192;
                    }

                    if (!isMeteo && !isMru)
                        sp.DataReceived += (s, e) => ReadPort(task.PortName, sp);

                    sp.Open();

                    if (_disposed) { try { sp.Close(); sp.Dispose(); } catch { } return; }

                    _ports[task.PortName] = sp;
                    lock (_bufferLock) _buffers[task.PortName] = new StringBuilder();
                    _lastRx[task.PortName] = DateTime.Now;
                    SystemLogger.LogInfo($"[COM] {task.PortName} opened at {task.BaudRate}");
                }
                catch (Exception ex)
                {
                    SystemLogger.LogError($"COM {task.PortName}", ex);
                }
            }
            finally { Monitor.Exit(_portLock); }
        }

        private void ReadPort(string portName, SerialPort sp)
        {
            try
            {
                string data;
                lock (_portLock) data = sp.ReadExisting();
                if (string.IsNullOrEmpty(data)) return;

                lock (_bufferLock)
                {
                    if (!_buffers.ContainsKey(portName)) _buffers[portName] = new StringBuilder();
                    _buffers[portName].Append(data);
                    string buf = _buffers[portName].ToString();
                    int nl;
                    while ((nl = buf.IndexOf('\n')) >= 0)
                    {
                        string line = buf[..(nl + 1)];
                        buf = buf[(nl + 1)..];
                        _lastRx[portName] = DateTime.Now;
                        OnDataReceived?.Invoke(portName, line.Trim());
                    }
                    _buffers[portName].Clear();
                    _buffers[portName].Append(buf);
                }
            }
            catch (Exception ex) { SystemLogger.LogError($"ReadPort {portName}", ex); }
        }

        private void WatchdogCheck(object? sender, ElapsedEventArgs e)
        {
            foreach (var task in _tasks)
            {
                bool isOpen;
                lock (_portLock)
                    isOpen = _ports.TryGetValue(task.PortName, out var sp) && sp.IsOpen;

                if (!isOpen)
                {
                    SystemLogger.LogInfo($"[COM] {task.PortName} offline, retrying...");
                    TryOpenPort(task);
                }
            }
        }

        public SerialPort? GetManagedPort(string portName)
        {
            lock (_portLock)
                return _ports.TryGetValue(portName, out var sp) && sp.IsOpen ? sp : null;
        }

        public void Dispose()
        {
            _disposed = true;
            _watchdog?.Stop();
            _watchdog?.Dispose();

            // If a background TryOpenPort() is mid-flight inside the blocking SerialPort.Open()
            // call, it's holding _portLock for however long the OS takes — don't let Dispose()
            // (called on the UI thread from FormClosed) hang the app-closing UI waiting for it.
            // TryOpenPort itself checks _disposed and will close/abandon the port it just opened.
            if (Monitor.TryEnter(_portLock, 300))
            {
                try
                {
                    foreach (var sp in _ports.Values)
                        try { sp.Close(); sp.Dispose(); } catch { }
                }
                finally { Monitor.Exit(_portLock); }
            }
        }
    }
}
