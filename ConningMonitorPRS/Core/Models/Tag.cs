namespace ConningMonitorPRS.Core.Models
{
    public class Tag
    {
        public string Name  { get; }
        public double Value { get; private set; }

        public Tag(string name) => Name = name;
        public void Update(double v) => Value = v;
    }
}
