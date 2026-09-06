using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Ports;
using System.Threading;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Models;
using ConningMonitorPRS.Services.Mru;

namespace ConningMonitorPRS.Services
{
    /// <summary>
    /// Đọc dữ liệu nhị phân XBus MTData2 từ Xsens MTi qua COM port.
    /// Frame: FA FF 36 LEN [data items...] CS
    /// Trích xuất: Euler 0x2030, FreeAcceleration 0x4030, RateOfTurn 0x8020.
    /// Tính Roll/Pitch/Heading/Heave qua XsensMruProcessor, ghi vào ConningDataHub ("R/P/H").
    /// Port lifecycle (open/retry/watchdog) do ComEngine quản lý.
    /// </summary>
    public sealed class MruService : IDisposable
    {
        // ── XBus constants ──────────────────────────────────────────────────
        private const byte Preamble = 0xFA;
        private const byte Bid = 0xFF;
        private const byte MidMTData2 = 0x36;

        // MTData2 DataID (big-endian 2-byte)
        private const ushort IdEuler = 0x2030;  // float32 roll, pitch, yaw (deg)
        private const ushort IdFreeAcc = 0x4030;  // float32 ax, ay, az (m/s²)
        private const ushort IdRoT = 0x8020;  // float32 wx, wy, wz

        // ── Fields ───────────────────────────────────────────────────────────
        private readonly string _portName;
        private readonly ComEngine _comEngine;
        private readonly XsensMruProcessor _processor = new XsensMruProcessor();
        public XsensMruProcessor Processor => _processor;

        // Roll°, Pitch°, HeaveCm — signed, fired on every valid frame (sim or hardware).
        public event Action<double, double, double>? OnMotionParsed;

        private Thread? _readThread;
        private volatile bool _running;
        private DateTime _lastFrameTime = DateTime.MinValue;
        private bool _firstFrameLogged = false;

        // Throttle counters — log on 1st, 5th, then every 30 occurrences
        private int _checksumFailCount;
        private int _timeoutFailCount;

        // Pre-allocated frame buffer: avoids allocs per MTData2 frame.
        // Max XBus MTData2 payload is 512 bytes (guarded in ProcessPort before ReadExact is called).
        private readonly byte[] _frameBuffer = new byte[512];

        // Debug telemetry — available for future UI/diagnostics
        public double LastAccZ         { get; private set; }
        public double LastDt           { get; private set; }
        public double LastDtSmoothed   { get; private set; }
        public double LastHeaveVelocity{ get; private set; }
        public bool   UsingSampleFine  { get; private set; }
        public long   FrameCount       { get; private set; }
        private double _pendingDt;
        private bool   _pendingUsingSampleFine;

        // Simulation
        private readonly Random _rng = new Random();
        private System.Timers.Timer? _simTimer;
        private double _simRoll, _simPitch, _simYaw;
        private double _simHeavePhase = 0;

        public MruService(string portName, int baudRate, ComEngine comEngine)
        {
            _portName = portName;
            _comEngine = comEngine;
        }

        // ── START / STOP ────────────────────────────────────────────────────

        public void Start()
        {
            _running = true;
            if (SystemConfig.IsSimulationMode)
            {
                SystemLogger.LogInfo($"[MRU] Starting in SIMULATION mode.");
                _simTimer = new System.Timers.Timer(100) { AutoReset = true };
                _simTimer.Elapsed += (s, e) => SimulateTick();
                _simTimer.Start();
            }
            else
            {
                SystemLogger.LogInfo($"[MRU] Starting on port={_portName}.");
                _readThread = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "MruService.ReadLoop"
                };
                _readThread.Start();
            }
        }

        public void Stop()
        {
            _running = false;
            _simTimer?.Stop();
            _simTimer?.Dispose();
            _simTimer = null;
        }

        /// <summary>
        /// Stops the ReadLoop then sends GoToConfig so the device returns to Config mode.
        /// Must be called synchronously (UI thread) in FormClosed before async cleanup starts.
        /// ReadLoop is stopped first to prevent it from re-sending GoToMeasurement after GoToConfig.
        /// </summary>
        public void SendGoToConfig()
        {
            if (SystemConfig.IsSimulationMode) return;

            _running = false;
            _readThread?.Join(1500);

            try
            {
                SerialPort? port = _comEngine?.GetManagedPort(_portName);
                if (port != null && port.IsOpen)
                {
                    port.Write([0xFA, 0xFF, 0x30, 0x00, 0xD1], 0, 5); // GoToConfig
                    Thread.Sleep(100);
                }
            }
            catch { }
        }

        public void Dispose() => Stop();

        // ── SIMULATION ───────────────────────────────────────────────────────

        private void SimulateTick()
        {
            double dt = 0.1;
            _simRoll += (_rng.NextDouble() - 0.5) * 0.4 * dt;
            _simPitch += (_rng.NextDouble() - 0.5) * 0.4 * dt;
            _simYaw += (_rng.NextDouble() - 0.5) * 0.5 * dt;
            _simHeavePhase += dt * 2.0 * Math.PI / 6.0;  // chu kỳ 6 giây

            _simRoll = Math.Clamp(_simRoll, -5.0, 5.0);
            _simPitch = Math.Clamp(_simPitch, -5.0, 5.0);
            _simYaw = XsensMruProcessor.Normalize360(_simYaw);

            double heaveAcc = 0.22 * Math.Sin(_simHeavePhase) + (_rng.NextDouble() - 0.5) * 0.02;
            var acc = new Vec3(0, 0, heaveAcc);
            var rot = new Vec3(0, 0, 0);
            var out_ = _processor.UpdateEuler(_simRoll, _simPitch, _simYaw, acc, rot, dt);
            if (!out_.Valid) return;

            _pendingDt              = dt;
            _pendingUsingSampleFine = false;
            PublishOutput(out_);
            ConningDataHub.Instance.UpdateRawString("R/P/H",
                $"SIM MRU R={out_.RollDeg:0.00}° P={out_.PitchDeg:0.00}° H={out_.HeadingDeg:0.0}° Heave={out_.HeaveInstantCg * 100:0.0}cm");
        }

        // ── READ LOOP ────────────────────────────────────────────────────────

        private void ReadLoop()
        {
            bool portLoggedOnce = false;
            while (_running)
            {
                SerialPort? port = null;
                try
                {
                    port = _comEngine?.GetManagedPort(_portName);
                    if (port == null || !port.IsOpen)
                    {
                        portLoggedOnce    = false;
                        _firstFrameLogged = false;
                        Thread.Sleep(500);
                        continue;
                    }

                    if (!portLoggedOnce)
                    {
                        SystemLogger.LogInfo($"[MRU] Port {_portName} open — entering read loop.");
                        portLoggedOnce = true;
                    }

                    ProcessPort(port);
                }
                catch (TimeoutException)
                {
                    _timeoutFailCount++;
                    if (ShouldLogThrottled(_timeoutFailCount))
                        SystemLogger.LogInfo($"[MRU] {_portName}: read timeout count={_timeoutFailCount}.");

                    if (_timeoutFailCount % 5 == 0)
                    {
                        SystemLogger.LogInfo($"[MRU] {_portName}: {_timeoutFailCount} consecutive timeouts — closing port for RS-232 line reset.");
                        try { port?.Close(); } catch { }
                        Thread.Sleep(1000);
                    }
                    else
                    {
                        Thread.Sleep(200);
                    }
                }
                catch (IOException ex)
                {
                    try { port?.Close(); } catch { }
                    _timeoutFailCount = 0;
                    SystemLogger.LogInfo($"[MRU] {_portName}: hardware error ({ex.GetType().Name}) — port closed, waiting for watchdog retry.");
                    Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    _timeoutFailCount = 0;
                    SystemLogger.LogInfo($"[MRU] ReadLoop unexpected exception on {_portName}: {ex.GetType().Name} — {ex.Message}. Resuming after 500ms.");
                    Thread.Sleep(500);
                }
            }
            SystemLogger.LogInfo($"[MRU] ReadLoop exited.");
        }

        private void TrySend(SerialPort port, byte[] frame)
        {
            try
            {
                port.Write(frame, 0, frame.Length);
            }
            catch (Exception ex)
            {
                SystemLogger.LogInfo($"[MRU] TrySend failed on {_portName}: {ex.GetType().Name} — {ex.Message}");
            }
        }

        private void InitDevice(SerialPort port)
        {
            bool ackReceived = false;
            for (int attempt = 1; attempt <= 3 && _running; attempt++)
            {
                try { port.DiscardInBuffer(); } catch { }
                SystemLogger.LogInfo($"[MRU] GoToConfig attempt {attempt}/3 on {_portName} (baud={port.BaudRate}).");
                TrySend(port, [0xFA, 0xFF, 0x30, 0x00, 0xD1]);

                if (TryReadGoToConfigAck(port, timeoutMs: 500))
                {
                    SystemLogger.LogInfo($"[MRU] GoToConfig ACK received (attempt {attempt}/3) — device in Config mode.");
                    ackReceived = true;
                    break;
                }

                SystemLogger.LogInfo($"[MRU] No GoToConfig ACK (attempt {attempt}/3). " +
                    (attempt < 3 ? "Device may still be booting. Retrying in 400ms..." : ""));
                if (attempt < 3) Thread.Sleep(400);
            }

            if (!ackReceived)
                SystemLogger.LogInfo($"[MRU] WARNING: no GoToConfig ACK after 3 attempts on {_portName}. " +
                    $"Possible causes: device not powered | wrong baud rate ({port.BaudRate}) | " +
                    "RS-232 cable disconnected | device frozen after power cycle. " +
                    "Sending GoToMeasurement anyway — check Logs for checksum failures.");

            try { port.DiscardInBuffer(); } catch { }
            SystemLogger.LogInfo($"[MRU] Sending GoToMeasurement on {_portName}.");
            TrySend(port, [0xFA, 0xFF, 0x10, 0x00, 0xF1]);
            Thread.Sleep(800);
            try { port.DiscardInBuffer(); } catch { }
        }

        /// <summary>
        /// Scans incoming bytes for a GoToConfig ACK frame (FA FF 31 xx xx).
        /// Returns true when ACK found within timeoutMs, false on timeout or if device returns Error.
        /// </summary>
        private bool TryReadGoToConfigAck(SerialPort port, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            int savedTimeout = port.ReadTimeout;
            port.ReadTimeout = 100;
            try
            {
                while (DateTime.UtcNow < deadline && _running)
                {
                    int b;
                    try { b = port.ReadByte(); } catch (TimeoutException) { continue; }
                    if (b != 0xFA) continue;
                    try
                    {
                        if (port.ReadByte() != 0xFF) continue;   // BID
                        int mid     = port.ReadByte();
                        int lenByte = port.ReadByte();
                        int len = lenByte == 0xFF
                            ? Math.Min((port.ReadByte() << 8) | port.ReadByte(), 512)
                            : lenByte;
                        for (int i = 0; i < len; i++) port.ReadByte();
                        port.ReadByte();  // checksum
                        if (mid == 0x31) return true;  // GoToConfig ACK
                        if (mid == 0x42) SystemLogger.LogInfo($"[MRU] Device returned Error frame during GoToConfig on {_portName}.");
                    }
                    catch (TimeoutException) { /* incomplete frame — keep scanning */ }
                    catch { break; }
                }
            }
            finally { try { port.ReadTimeout = savedTimeout; } catch { } }
            return false;
        }

        private void ProcessPort(SerialPort port)
        {
            InitDevice(port);
            _processor.Reset();

            while (_running)
            {
                int b = port.ReadByte();
                if (b != Preamble) continue;

                int bid = port.ReadByte();
                if (bid != Bid) continue;

                int mid = port.ReadByte();
                if (mid != MidMTData2) continue;

                int lenByte = port.ReadByte();
                int len;
                int lenSum;
                if (lenByte == 0xFF)
                {
                    int hi = port.ReadByte();
                    int lo = port.ReadByte();
                    len = (hi << 8) | lo;
                    lenSum = 0xFF + hi + lo;
                }
                else
                {
                    len = lenByte;
                    lenSum = lenByte;
                }

                if (len > 512) continue;

                ReadExact(port, len);

                int cs = port.ReadByte();

                int sum = Bid + MidMTData2 + lenSum;
                for (int i = 0; i < len; i++) sum += _frameBuffer[i];
                sum += cs;
                if ((sum & 0xFF) != 0)
                {
                    _checksumFailCount++;
                    if (ShouldLogThrottled(_checksumFailCount))
                        SystemLogger.LogInfo($"[MRU] {_portName}: checksum fail count={_checksumFailCount}. Check baud rate / cable / device output config.");
                    continue;
                }

                _checksumFailCount = 0;
                _timeoutFailCount  = 0;

                ParseAndUpdate(len);
            }
        }

        // ── MTData2 PARSER ───────────────────────────────────────────────────

        private float _roll, _pitch, _yaw;
        private float _ax, _ay, _az;
        private float _wx, _wy, _wz;
        private bool _hasEuler, _hasFreeAcc, _hasRoT, _hasSampleFine;
        private uint _curSampleFine, _prevSampleFine;

        private const double SampleFineHz = 10000.0;

        private void ParseAndUpdate(int dataLen)
        {
            int pos = 0;
            byte[] data = _frameBuffer;
            _hasEuler = _hasFreeAcc = _hasRoT = _hasSampleFine = false;

            while (pos + 3 <= dataLen)
            {
                ushort dataId = (ushort)((data[pos] << 8) | data[pos + 1]);
                byte itemLen = data[pos + 2];
                pos += 3;

                if (pos + itemLen > dataLen) break;

                switch (dataId)
                {
                    case IdEuler when itemLen >= 12:
                        _roll = ParseFloat(data, pos);
                        _pitch = ParseFloat(data, pos + 4);
                        _yaw = ParseFloat(data, pos + 8);
                        _hasEuler = true;
                        break;

                    case IdFreeAcc when itemLen >= 12:
                        _ax = ParseFloat(data, pos);
                        _ay = ParseFloat(data, pos + 4);
                        _az = ParseFloat(data, pos + 8);
                        _hasFreeAcc = true;
                        break;

                    case ushort rotId when (rotId & 0xFFF0) == (IdRoT & 0xFFF0) && itemLen >= 12:
                        _wx = ParseFloat(data, pos);
                        _wy = ParseFloat(data, pos + 4);
                        _wz = ParseFloat(data, pos + 8);
                        _hasRoT = true;
                        break;

                    case 0x1060 when itemLen >= 4:  // SampleTimeFine (10kHz hardware clock)
                        _curSampleFine = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
                        _hasSampleFine = true;
                        break;
                }

                pos += itemLen;
            }

            if (!_hasEuler) return;

            double dt;
            if (_hasSampleFine && _lastFrameTime != DateTime.MinValue)
            {
                uint ticks = _curSampleFine - _prevSampleFine;
                dt = Math.Clamp(ticks / SampleFineHz, 0.001, 0.2);
            }
            else
            {
                DateTime now = DateTime.UtcNow;
                dt = _lastFrameTime == DateTime.MinValue
                    ? 0.01
                    : Math.Clamp((now - _lastFrameTime).TotalSeconds, 0.001, 0.2);
            }
            _prevSampleFine          = _curSampleFine;
            _lastFrameTime           = DateTime.UtcNow;
            _pendingDt               = dt;
            _pendingUsingSampleFine  = _hasSampleFine && _lastFrameTime != DateTime.MinValue;

            var freeAcc = _hasFreeAcc ? new Vec3(_ax, _ay, _az) : new Vec3(0, 0, 0);
            var rotBody = _hasRoT ? new Vec3(_wx, _wy, _wz) : new Vec3(0, 0, 0);

            var out_ = _processor.UpdateEuler(
                _roll, _pitch, _yaw,
                freeAcc, rotBody,
                dt);

            if (!out_.Valid) return;

            if (!_firstFrameLogged)
            {
                SystemLogger.LogInfo($"[MRU] First valid frame received on {_portName} — R={out_.RollDeg:0.00}° P={out_.PitchDeg:0.00}°");
                _firstFrameLogged = true;
            }

            PublishOutput(out_);
            ConningDataHub.Instance.UpdateRawString("R/P/H",
                $"MRU R={out_.RollDeg:0.00}° P={out_.PitchDeg:0.00}° H={out_.HeadingDeg:0.0}° Heave={out_.HeaveInstantCg * 100:0.0}cm");
        }

        private void PublishOutput(MruOutput o)
        {
            LastAccZ          = o.VerticalAcceleration;
            LastDt            = _pendingDt;
            LastDtSmoothed    = LastDtSmoothed < 0.0001
                                ? _pendingDt
                                : 0.9 * LastDtSmoothed + 0.1 * _pendingDt;
            LastHeaveVelocity = o.HeaveVelocity;
            UsingSampleFine   = _pendingUsingSampleFine;
            FrameCount++;

            double heaveCm = o.HeaveInstantCg * 100.0;

            // ConningDataHub/Alarm.Evaluate() compare Tag.Value > limit directly (no internal
            // abs) — Roll/Pitch/Heave must be stored as magnitude here, same convention as the
            // legacy $CNTB NMEA path it replaces.
            ConningDataHub.Instance.UpdateNumericData("R/P/H",
                Math.Abs(o.RollDeg), Math.Abs(o.PitchDeg), Math.Abs(heaveCm));

            // MainForm needs the signed heave for zero-crossing period detection.
            OnMotionParsed?.Invoke(o.RollDeg, o.PitchDeg, heaveCm);
        }

        private static bool ShouldLogThrottled(int count) =>
            count == 1 || count == 5 || count % 30 == 0;

        private void ReadExact(SerialPort port, int count)
        {
            int read      = 0;
            int zeroReads = 0;
            while (read < count && _running)
            {
                int n = port.Read(_frameBuffer, read, count - read);
                if (n > 0)
                {
                    read     += n;
                    zeroReads = 0;
                }
                else
                {
                    if (++zeroReads > 50)
                        throw new TimeoutException($"[MRU] ReadExact: {zeroReads} consecutive zero-byte reads on {_portName} — hardware stall.");
                    Thread.Sleep(1);
                }
            }
        }

        private static float ParseFloat(byte[] buf, int offset)
        {
            uint u = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(offset, 4));
            return BitConverter.Int32BitsToSingle((int)u);
        }
    }
}
