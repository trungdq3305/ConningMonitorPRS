using System.Collections.Generic;

namespace ConningMonitorPRS.Core.Models
{
    public class TargetPoint
    {
        public string Name    { get; set; } = "";
        public double Lat     { get; set; }   // primary point / vertex 1
        public double Lon     { get; set; }
        public bool   Enabled { get; set; }

        // Extra vertices (up to 3 more) turning this target into a shape instead of a point:
        // 0 = single point (drawn as a marker dot), 1 = a 2-vertex line, 2+ = a closed polygon
        // (with Lat/Lon above as vertex 1). Empty by default so old config.json targets
        // (point-only) load unchanged.
        public List<LatLon> ExtraPoints { get; set; } = new();
    }
}
