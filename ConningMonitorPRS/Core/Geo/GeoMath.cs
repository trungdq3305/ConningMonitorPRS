using System;

namespace ConningMonitorPRS.Core.Geo
{
    public static class GeoMath
    {
        private const double EarthRadiusM = 6371000.0;

        public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180.0;
            double p2 = lat2 * Math.PI / 180.0;
            double dPhi = (lat2 - lat1) * Math.PI / 180.0;
            double dLambda = (lon2 - lon1) * Math.PI / 180.0;

            double a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
                     + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return EarthRadiusM * c;
        }

        public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180.0;
            double p2 = lat2 * Math.PI / 180.0;
            double dLambda = (lon2 - lon1) * Math.PI / 180.0;

            double y = Math.Sin(dLambda) * Math.Cos(p2);
            double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dLambda);
            double theta = Math.Atan2(y, x);
            return (theta * 180.0 / Math.PI + 360.0) % 360.0;
        }

        // Flat-earth (equirectangular) approximation of how far (toLat,toLon) is from
        // (fromLat,fromLon), in metres east/north — fine at the few-km scale this is used for
        // (own-ship track/target plotting relative to the current GPS fix). Was previously
        // hand-duplicated with the same 111320.0 constant in ConningControl's wake-trail and
        // RadarControl's track drawing; consolidated here so both (and the radar/track/target
        // plotting this now feeds) share one implementation.
        public static (double dxEastM, double dyNorthM) OffsetMeters(double fromLat, double fromLon, double toLat, double toLon)
        {
            const double mPerDegLat = 111320.0;
            double mPerDegLon = 111320.0 * Math.Cos(fromLat * Math.PI / 180.0);
            return ((toLon - fromLon) * mPerDegLon, (toLat - fromLat) * mPerDegLat);
        }
    }
}
