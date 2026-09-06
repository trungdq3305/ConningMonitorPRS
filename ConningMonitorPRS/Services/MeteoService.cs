using System;
using Timer = System.Timers.Timer;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Models;

namespace ConningMonitorPRS.Services
{
    public class MeteoService : IDisposable
    {
        private readonly string    _portName;
        private readonly int       _baudRate;
        private readonly ComEngine _comEngine;
        private readonly Timer     _pollTimer;
        private readonly Random    _rng = new();

        public MeteoService(string portName, int baudRate, ComEngine comEngine)
        {
            _portName  = portName;
            _baudRate  = baudRate;
            _comEngine = comEngine;
            _pollTimer = new Timer(2000);
            _pollTimer.Elapsed += (s, e) => Poll();
        }

        public void Start() => _pollTimer.Start();
        public void Stop()  => _pollTimer.Stop();

        private void Poll()
        {
            if (SystemConfig.IsSimulationMode)
            {
                double temp  = 18 + _rng.NextDouble() * 22;
                double hum   = 30 + _rng.NextDouble() * 65;
                double press = 995 + _rng.NextDouble() * 40;
                ConningDataHub.Instance.UpdateMeteoData(temp, hum, press);
                ConningDataHub.Instance.UpdateRawString("METEO",
                    $"T={temp:0.0}°C H={hum:0.0}% P={press:0.0}hPa");
                return;
            }

            var port = _comEngine.GetManagedPort(_portName);
            if (port == null) return;

            try
            {
                // Modbus RTU: FC03, Slave 1, Addr 0x0000, Count 3
                byte[] req = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x03 };
                ushort crc = CalcCrc(req);
                byte[] full = new byte[8];
                Array.Copy(req, full, 6);
                full[6] = (byte)(crc & 0xFF);
                full[7] = (byte)(crc >> 8);

                lock (port)
                {
                    port.DiscardInBuffer();
                    port.Write(full, 0, full.Length);
                    byte[] resp = new byte[9];
                    int read = 0;
                    DateTime timeout = DateTime.Now.AddMilliseconds(600);
                    while (read < 9 && DateTime.Now < timeout)
                        read += port.Read(resp, read, 9 - read);
                    if (read < 9) return;

                    double temp  = ((resp[3] << 8) | resp[4]) / 100.0;
                    double hum   = ((resp[5] << 8) | resp[6]) / 100.0;
                    double press = ((resp[7] << 8) | resp[8]) / 10.0;
                    ConningDataHub.Instance.UpdateMeteoData(temp, hum, press);
                    ConningDataHub.Instance.UpdateRawString("METEO",
                        $"T={temp:0.0}°C H={hum:0.0}% P={press:0.0}hPa");
                }
            }
            catch (Exception ex) { SystemLogger.LogError("MeteoService.Poll", ex); }
        }

        private static ushort CalcCrc(byte[] data)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
            return crc;
        }

        public void Dispose()
        {
            _pollTimer.Stop();
            _pollTimer.Dispose();
        }
    }
}
