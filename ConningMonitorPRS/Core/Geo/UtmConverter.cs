using System;

namespace ConningMonitorPRS.Core.Geo
{
    // WGS84 Transverse Mercator projection (Snyder's formulas) — sufficient precision for
    // bridge situational awareness, not survey-grade.
    public static class UtmConverter
    {
        private const double A  = 6378137.0;            // WGS84 semi-major axis (m)
        private const double F  = 1.0 / 298.257223563;  // WGS84 flattening
        private const double K0 = 0.9996;

        public readonly struct UtmCoord
        {
            public readonly int    Zone;
            public readonly char   Band;
            public readonly double Easting;
            public readonly double Northing;

            public UtmCoord(int zone, char band, double easting, double northing)
            {
                Zone = zone; Band = band; Easting = easting; Northing = northing;
            }

            public override string ToString() => $"{Zone}{Band} {Easting:0} E {Northing:0} N";
        }

        public static UtmCoord ToUtm(double latDeg, double lonDeg)
        {
            double e2  = F * (2 - F);
            double ep2 = e2 / (1 - e2);

            int    zone         = (int)Math.Floor((lonDeg + 180) / 6) + 1;
            double lonOriginDeg = (zone - 1) * 6 - 180 + 3;

            double latRad = latDeg * Math.PI / 180.0;
            double lonRad = lonDeg * Math.PI / 180.0;
            double lonOriginRad = lonOriginDeg * Math.PI / 180.0;

            double sinLat = Math.Sin(latRad);
            double cosLat = Math.Cos(latRad);
            double tanLat = Math.Tan(latRad);

            double n = A / Math.Sqrt(1 - e2 * sinLat * sinLat);
            double t = tanLat * tanLat;
            double c = ep2 * cosLat * cosLat;
            double ax = cosLat * (lonRad - lonOriginRad);

            double m = A * (
                (1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256) * latRad
                - (3 * e2 / 8 + 3 * e2 * e2 / 32 + 45 * e2 * e2 * e2 / 1024) * Math.Sin(2 * latRad)
                + (15 * e2 * e2 / 256 + 45 * e2 * e2 * e2 / 1024) * Math.Sin(4 * latRad)
                - (35 * e2 * e2 * e2 / 3072) * Math.Sin(6 * latRad));

            double easting = K0 * n * (ax + (1 - t + c) * Math.Pow(ax, 3) / 6
                + (5 - 18 * t + t * t + 72 * c - 58 * ep2) * Math.Pow(ax, 5) / 120) + 500000.0;

            double northing = K0 * (m + n * tanLat * (
                ax * ax / 2 + (5 - t + 9 * c + 4 * c * c) * Math.Pow(ax, 4) / 24
                + (61 - 58 * t + t * t + 600 * c - 330 * ep2) * Math.Pow(ax, 6) / 720));

            if (latDeg < 0) northing += 10_000_000.0;

            return new UtmCoord(zone, BandLetter(latDeg), easting, northing);
        }

        private static char BandLetter(double lat)
        {
            if (lat <= -72) return 'C';
            if (lat <= -64) return 'D';
            if (lat <= -56) return 'E';
            if (lat <= -48) return 'F';
            if (lat <= -40) return 'G';
            if (lat <= -32) return 'H';
            if (lat <= -24) return 'J';
            if (lat <= -16) return 'K';
            if (lat <=  -8) return 'L';
            if (lat <=   0) return 'M';
            if (lat <=   8) return 'N';
            if (lat <=  16) return 'P';
            if (lat <=  24) return 'Q';
            if (lat <=  32) return 'R';
            if (lat <=  40) return 'S';
            if (lat <=  48) return 'T';
            if (lat <=  56) return 'U';
            if (lat <=  64) return 'V';
            if (lat <=  72) return 'W';
            return 'X';
        }
    }
}
