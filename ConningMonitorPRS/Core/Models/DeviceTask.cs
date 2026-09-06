namespace ConningMonitorPRS.Core.Models
{
    public class DeviceTask
    {
        public string TaskName     { get; set; } = "";
        public string PortName     { get; set; } = "COM1";
        public int    BaudRate     { get; set; } = 9600;
        public string SentenceType { get; set; } = "";
    }
}
