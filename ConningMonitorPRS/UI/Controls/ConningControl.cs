using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using ConningMonitorPRS.Core.Geo;
using ConningMonitorPRS.Core.Models;
using ConningMonitorPRS.UI.Theme;

namespace ConningMonitorPRS.UI.Controls
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  SPEC: 360°/5° ring FIXED (N always at top). Ship + velocity arrows rotate
    //  with HDG. Wind arrow plotted at true bearing (ring is north-up). Value
    //  labels are plain numbers next to each arrow tip — no box, no unit, no
    //  direction text (direction is conveyed by the arrow itself). No HDG box.
    // ═══════════════════════════════════════════════════════════════════════════
    public class ConningControl : UserControl
    {
        // ── State ─────────────────────────────────────────────────────────────
        private double _hdg    = 0;
        private double _cog    = 0;
        private double _sog    = 0;
        private double _rot    = 0;
        private double _wDir   = 225;
        private double _wSpd   = 0;
        private double _heaveCm = 0;
        private double _loa    = 100;
        private double _lob    = 20;
        private double _gpsOff = 0;

        // Drift/residual corner (top-right) — ported from RadarControl.UpdateDrift after the
        // mini radar card was removed from MainForm's side panel and this control became the
        // sole "radar" on the main screen.
        private bool   _driftEnabled;
        private double _driftBearingDeg;
        private double _driftDistanceM;

        // Ship afterimage — fading ghost silhouettes of heading AND position "stamped" every
        // few seconds, so both a turn and straight-line travel leave a lasting trail of poses
        // (like the overlaid-poses reference sprite sheet the user sent), not just a brief
        // real-time shadow. Was a continuous per-tick sample over a short 2.5s rolling window
        // at first — per user feedback ("lưu hình lại trên control luôn, cường độ 1 lần vài
        // giây") this now stamps once every StampIntervalSeconds and keeps MaxStamps of them,
        // so the trail persists over a much longer span (StampIntervalSeconds×MaxStamps ≈ 30s)
        // instead of vanishing after ~2.5s. The ring itself is always screen-center-anchored on
        // the CURRENT fix (this is an ego-centric display, not a moving map), so historical
        // stamps are re-projected each frame as an offset *relative to the current GPS fix*
        // (see DrawShipTrail) rather than an absolute screen position.
        private const double StampIntervalSeconds = 3.0;
        private const int    MaxStamps            = 10;
        private DateTime _lastStampTime = DateTime.MinValue;
        private double _gpsLatDeg = double.NaN, _gpsLonDeg = double.NaN;
        private readonly List<(DateTime t, double hdg, double lat, double lon)> _trail = new();

        // Range control (bottom-left) — the SINGLE real-world distance scale for everything in
        // this control: own-ship track, target shapes/markers, the graph-paper grid, AND (as of
        // 2026-09-03) the ship silhouette + velocity arrows + afterimage trail themselves. Ported
        // from RadarControl (2026-08-28, per user request "bỏ luôn radar trong trendform đi, tích
        // hợp hết vô radar ở mainform"). Discrete presets, not a slider — changed via the +/-
        // buttons only (mouse-wheel zoom removed 2026-09-03, "bỏ tăng giảm bằng lăn chuột").
        // Presets changed 2026-09-03 from {50,100,250,500,1000,2000,5000,10000} to a tighter
        // close-in scale, {5,10,20,50,100,1000}m (see RangePresetsM below).
        //
        // MERGE (2026-09-03): there used to be a SEPARATE "ZOOM" slider that scaled only the ship
        // silhouette/arrows against a LOA-derived pixel-per-metre figure, independent of RANGE —
        // this meant the square graph-paper grid (LOA/ZOOM-based) and the round RANGE rings
        // (RANGE-based) could show mismatched metre values in the same picture (e.g. grid reading
        // "20m/cell" while a ring at R/4 read "125m"), which the user flagged as confusing
        // ("các đường kẻ ô ly và zoom và range nên đồng bộ"). Per user's chosen fix ("gộp hẳn ZOOM
        // vào RANGE"), ZOOM is removed entirely — the ship is now drawn to its TRUE size relative
        // to the selected RANGE (LOA/LOB in metres × the same pixels-per-metre the grid/rings/
        // track/targets all share), so every element on screen agrees on one scale. A small RANGE
        // (e.g. 50m) against a 100m LOA draws a ship larger than the ring (correct — the vessel
        // genuinely doesn't fit at that scale); a large RANGE (e.g. 10km) shrinks the ship near to
        // a single pixel, so ShipMinHalfLenPx/ShipMinHalfWidthPx floor it to a small but still-
        // visible icon (see OnPaint) rather than letting it disappear, matching how a radar/AIS
        // target stays a visible icon even far outside true-scale legibility.
        // Presets replaced 2026-09-03 (per user request "để theo thang 5 mét, 10 mét, 20 mét, 50
        // mét 100 mét, 1000 mét" — was {50,100,250,500,1000,2000,5000,10000}) — a tighter, more
        // close-in scale for zoomed-in work (5m-100m) plus a single coarse 1000m step, rather than
        // the old preset's even spacing all the way out to 10km.
        // 500m inserted between 100m and 1000m (2026-09-03, per user request "thêm mốc 300m" then
        // "thay 300m bằng 500m") — a 10× jump from 100 straight to 1000 left a gap; index 3
        // (default, 50m) is unaffected since the insertion is after it.
        private static readonly double[] RangePresetsM = { 5, 10, 20, 50, 100, 500, 1000 };
        private int _rangeIndex = 3; // default 50 m
        public double RangeMeters => RangePresetsM[_rangeIndex];
        private RectangleF _rangeInRect, _rangeOutRect;

        // Own-ship track (north-up, relative to current position) — up to 30 min. Ported from
        // RadarControl.PushTrackPoint/DrawTrack.
        private readonly Queue<(DateTime t, double lat, double lon)> _track = new();
        private const double TrackMaxAgeMin = 30.0;

        // ── Colors ────────────────────────────────────────────────────────────
        // Ring/ship colors are fixed brand colors — already legible on both themes.
        // Everything else (labels drawn on the open background, and the value labels)
        // follows Palette so it doesn't wash out when the theme flips.
        private static readonly Color ColShipF  = Color.FromArgb(255, 185,  60,  0);
        private static readonly Color ColShipB  = Color.FromArgb(255, 220, 110, 10);
        private static Color ColLat   => Palette.ConningLateral;
        private static Color ColAxial => Palette.ConningAxial;
        private static Color ColWind  => Palette.ConningWindArrow;
        private static Color ColHdgLn => Palette.ConningHdgLine;

        public ConningControl()
        {
            DoubleBuffered = true;
            ResizeRedraw   = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        public void Update(double hdg, double cog, double sog, double rot,
                           double wDir, double wSpd, double heaveCm,
                           double loa, double lob, double gpsOff)
        {
            _hdg = hdg; _cog = cog; _sog = sog; _rot = rot;
            _wDir = wDir; _wSpd = wSpd; _heaveCm = heaveCm;
            _loa = loa; _lob = lob; _gpsOff = gpsOff;

            var now = DateTime.UtcNow;
            if ((now - _lastStampTime).TotalSeconds >= StampIntervalSeconds)
            {
                _trail.Add((now, hdg, _gpsLatDeg, _gpsLonDeg));
                _lastStampTime = now;
                while (_trail.Count > MaxStamps) _trail.RemoveAt(0);
            }
        }

        // Call before Update() each tick — the trail sample Update() records uses whatever
        // lat/lon was last set here, so this must land before Update() in the same tick to
        // keep the two in sync (MainForm.UiTick calls them back-to-back).
        public void UpdatePosition(double latDeg, double lonDeg)
        {
            _gpsLatDeg = latDeg;
            _gpsLonDeg = lonDeg;
        }

        public void UpdateDrift(bool enabled, double bearingDeg, double distanceM)
        {
            _driftEnabled    = enabled;
            _driftBearingDeg = bearingDeg;
            _driftDistanceM  = distanceM;
        }

        // Ported from RadarControl.PushTrackPoint.
        public void PushTrackPoint(double latDeg, double lonDeg)
        {
            if (double.IsNaN(latDeg) || double.IsNaN(lonDeg)) return;
            var now = DateTime.Now;
            _track.Enqueue((now, latDeg, lonDeg));
            while (_track.Count > 0 && (now - _track.Peek().t).TotalMinutes > TrackMaxAgeMin)
                _track.Dequeue();
        }

        // RANGE's +/- buttons — a plain click.
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_rangeInRect.Contains(e.Location) && _rangeIndex > 0)
                { _rangeIndex--; Invalidate(); }
            else if (_rangeOutRect.Contains(e.Location) && _rangeIndex < RangePresetsM.Length - 1)
                { _rangeIndex++; Invalidate(); }
        }

        // Mouse-wheel RANGE zoom removed (2026-09-03, per user request "bỏ tăng giảm bằng lăn
        // chuột") — RANGE now changes ONLY via the +/- buttons in DrawRangeControl (OnMouseClick).

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Palette.RadarBg);

            // ── Layout (ring fills the control, no HDG box below) ─────────────
            const float MARGIN = 10f;

            float R = Math.Min(Width * 0.47f, (Height - MARGIN * 2) / 2f);
            if (R < 40) return;

            float cx = Width  / 2f;
            float cy = Height / 2f;

            // ── Ship geometry — LENGTH drawn to TRUE scale against the selected RANGE (2026-09-03,
            // merged from the old separate ZOOM slider — see the RangePresetsM field comment).
            // pxPerMeter is the single scale every element in this control shares: grid, RANGE
            // rings, track, targets, and the ship's length. Floored to a minimum on-screen size
            // so the ship never shrinks below a legible icon at large RANGE — same convention a
            // radar/AIS target uses when it's smaller than one true-scale pixel.
            //
            // WIDTH (2026-09-03, per user request "giảm chiều dài tăng chiều rộng tỉ lệ 2 3", then
            // adjusted to 4:2 the same day): no longer derived from the real LOB — a true-to-scale
            // beam (LOB is typically ~1/5 of LOA) drew as a needle-thin sliver that was hard to
            // read heading/turn off of, and didn't match the stubbier ship icon in the reference
            // conning-display photo the user sent. sw is now a fixed fraction of sl (length:width
            // = 4:2 = 2:1, i.e. sw = sl/2) — a stylized icon proportion, not a literal hull beam —
            // so the ship stays legible at any RANGE while its overall SIZE still scales with
            // LOA/RANGE as before.
            //
            // BUG FIX (2026-09-03, right after the RANGE presets grew a 5m/10m/20m close-in tier):
            // sl used to have only a MINIMUM floor, no ceiling — at these tiny RANGE values a
            // normal-size ship (LOA~100m) computes to many times the ring radius R. Every arrow's
            // attachment point (bowY/sternY) AND the heave-vector circle's radius derive from
            // sl/sw, so once sl blew past R those landed off-canvas or, for the filled heave
            // circle, ballooned into a solid blob that painted over the whole display (it's drawn
            // last among the ship-attached elements) — user report: "bắt đầu từ 20m là các mũi tên
            // không thể nhìn thấy nữa". Clamping sl's UPPER bound to ShipMaxHalfLenFactor×R keeps
            // the ship — and everything attached to it — within the visible ring at any RANGE,
            // trading a little "true to scale" purity at extreme close range for arrows that are
            // always on-screen (per user request: "vẫn có thể nhìn thấy tất cả mũi tên ở tất cả
            // khoảng cách").
            //
            // CEILING LOWERED 0.85→0.40 (2026-09-03, same day) — 0.85 stopped the ship from
            // clipping off-canvas, but the ship still visibly dominated the ring at 50m and below
            // (user report, with screenshot: "50m để nhỏ lại vừa đủ k quá to như hiện tại"). At
            // default LOA=100, the raw true-scale sl already exceeds even 0.40×R for every preset
            // from 5m through 100m — so lowering the ceiling to 0.40 shrinks the ship uniformly
            // across ALL of those presets in one change (exactly the "các kích thước kia đồng thời
            // cũng tinh chỉnh" the user asked for), not just the 50m case. 0.40 isn't arbitrary —
            // it's the same magnitude this control's ship size was fixed at before RANGE-driven
            // sizing existed, so it's already a proven, comfortable on-screen size.
            //
            // PER-TIER CEILING (2026-09-03, same day) — follow-up request "100m nhỏ bằng 1 nửa
            // hiện tại, 50m trở xuống thì giữ nguyên": 5/10/20/50m all share ONE ceiling (they were
            // already visually identical, all pinned at it — see note above), but 100m needed to
            // shrink independently of that group instead of moving together with it. Ceiling is
            // now RangeMeters-dependent instead of a single constant: 0.40 for RANGE≤50m
            // (unchanged), 0.20 (exactly half) from 100m up. 1000m still lands on its own via true
            // scale regardless (raw sl ≈0.047×R at default LOA — nowhere near either ceiling).
            const float ShipMinHalfLenPx = 10f, ShipMinHalfWidthPx = 4f;
            float shipMaxHalfLenFactor = RangeMeters <= 50 ? 0.40f : 0.20f;
            const float ShipLengthToWidthRatio = 4f / 2f; // sl:sw = 4:2
            float pxPerMeter = (float)(R * 0.94 / RangeMeters);
            float sl = Math.Clamp((float)(Math.Max(_loa, 1.0) / 2.0) * pxPerMeter, ShipMinHalfLenPx, R * shipMaxHalfLenFactor);
            float sw = Math.Max(sl / ShipLengthToWidthRatio, ShipMinHalfWidthPx);

            // Visual zoom for arrows/value labels/pen widths — tracks how much the ship itself
            // grew/shrank against the OLD fixed reference size (R×0.40, what "sl" always was
            // before the RANGE merge), clamped to a legible band. Per user request: "các mũi tên
            // và số cũng thu phóng theo luôn, đến 1 mức to hoặc nhỏ nhất định để dễ nhìn" — arrows
            // and value labels should scale WITH RANGE too (like the ship/grid now do), but capped
            // so an extreme RANGE doesn't make arrows comically huge or unreadably thin/tiny text.
            //
            // BUG FIX (2026-09-03, right after the RANGE presets grew a 5m/10m/20m close-in tier):
            // at these tiny RANGE values a normal-sized ship (LOA~100m) is enormously bigger than
            // the ring (sl can be 10-20× the R×0.40 reference), so visualZoom pins at its ceiling
            // for ALL THREE of the new close-in presets — user report "5 10 20 m khi zoom thì các
            // mũi tên hiển thị bị quá to". The old ceiling (1.8×) was tuned back when 50m was the
            // smallest preset, where sl rarely pushed the ratio much past it; it reads as too large
            // now that closer presets exist and hit the ceiling harder/more often. Lowered to 1.2×
            // — arrows/text still grow noticeably when zoomed in, just not aggressively so.
            const float VisualZoomMin = 0.6f, VisualZoomMax = 1.2f;
            float visualZoom = Math.Clamp(sl / (R * 0.40f), VisualZoomMin, VisualZoomMax);

            // ── Velocity calculations ────────────────────────────────────────
            // _sog (and therefore vLong/vLat/vBow/vStern) is in KNOTS, matching the SPEED card's
            // default unit. omega×r below is physically an m/s quantity (ω in rad/s, r in metres)
            // — BUG FIX: this used to be added straight into vBow/vStern as if it were already in
            // knots, silently understating the turn-rate contribution to the bow/stern arrows by
            // a factor of ~1.94 (1 knot = 0.514444 m/s). Converted to knots here (÷0.514444)
            // before combining, instead of converting _sog to m/s everywhere, so the already-
            // tuned arrow-length scale for straight-line motion (vLong/vLat) is undisturbed.
            const double MPerSecToKnot = 1.0 / 0.514444;
            double deltaRad = (_cog - _hdg) * Math.PI / 180.0;
            double vLong    = Math.Cos(deltaRad) * _sog;
            double vLat     = Math.Sin(deltaRad) * _sog;
            double omega    = _rot * Math.PI / 180.0 / 60.0;
            double loa2     = Math.Max(_loa, 1.0) / 2.0;
            double vBow     = vLat + omega * (loa2 - _gpsOff) * MPerSecToKnot;
            double vStern   = vLat - omega * (loa2 + _gpsOff) * MPerSecToKnot;

            float arrowScale = (float)(R * 0.36f / 5.0) * visualZoom;
            float arrowMax   = R * 0.38f * visualZoom;
            float bowY       = -sl * 0.72f;
            float sternY     = sl * 0.36f;

            // Grid cell size — a FIXED real-world metre value, nice-rounded from the ship's own
            // LOA, that does NOT get re-derived from the current pxPerMeter/R (BUG FIX 2026-09-03:
            // the old ComputeNiceGridMeters always solved backwards for a ~R/7 on-screen pixel
            // target regardless of pxPerMeter's magnitude, so the rendered grid spacing landed
            // back near the same pixel size at every RANGE — only the printed "Xm/sq" label
            // changed, the actual squares never visibly grew/shrank when zooming. User report:
            // "các ô ly không thu phóng" — the grid must resize WITH RANGE, matching how the ship
            // silhouette now does. Keeping the metre value fixed and letting gridSpacingPx =
            // metres × pxPerMeter fall out naturally is what makes that happen: a smaller RANGE
            // (larger pxPerMeter) draws bigger squares, a larger RANGE draws smaller ones.
            // AutoCoarsenGridMeters only ever steps the value UP the 1-2-5-10 ladder, as a
            // decluttering floor for when RANGE is so large the LOA-sized cell would render as an
            // illegible sub-pixel mesh — it never pulls the base value back down, so zooming in
            // always visibly enlarges the squares.
            const float MinCellPx = 18f;
            double gridMeters = AutoCoarsenGridMeters(NiceRound(Math.Max(_loa, 1.0)), pxPerMeter, MinCellPx);
            float  gridSpacingPx = (float)(gridMeters * pxPerMeter);

            // ── Draw ─────────────────────────────────────────────────────────
            DrawCompassRing(g, cx, cy, R, Width, Height, gridSpacingPx, RangeMeters);
            DrawTrack(g, cx, cy, R);
            DrawTargets(g, cx, cy, R);
            DrawHeadingLine(g, cx, cy, R, sl, _hdg);
            DrawShipTrail(g, cx, cy, sl, sw, pxPerMeter);
            DrawShip(g, cx, cy, sl, sw, _hdg);
            DrawLateralArrow(g, cx, cy, bowY,   vBow,   sw, arrowScale, arrowMax, R, _hdg, visualZoom);
            DrawLateralArrow(g, cx, cy, sternY, vStern, sw, arrowScale, arrowMax, R, _hdg, visualZoom);
            DrawAxialArrow(g, cx, cy, sl, vLong, arrowScale, arrowMax, R, _hdg, visualZoom);
            DrawHeaveVector(g, cx, cy, sw, R, _hdg, visualZoom);
            DrawWindArrow(g, cx, cy, R);
            DrawAxisLegend(g, R);

            // Corner readouts — RANGE (bottom-left, now also drives ship/grid scale — see
            // DrawRangeControl), DRIFT/residual (top-right). ZOOM corner was merged into RANGE
            // (2026-09-03). WIND corner was removed per earlier user request — WIND SPD/WIND DIR
            // cards on the left already cover it, and the wind arrow drawn on the ring itself
            // (above) still shows direction/speed live.
            DrawRangeControl(g, gridMeters);
            DrawDriftCorner(g);
        }

        // "Nice" 1-2-5-10× rounding (classic axis-tick algorithm) so a real-world metre value
        // reads as a believable round number (e.g. "20 m") instead of something like "17.3 m".
        private static double NiceRound(double v)
        {
            if (v <= 0) return 1;
            double exp    = Math.Floor(Math.Log10(v));
            double base10 = Math.Pow(10, exp);
            double frac   = v / base10;
            double nice   = frac < 1.5 ? 1 : frac < 3.5 ? 2 : frac < 7.5 ? 5 : 10;
            return nice * base10;
        }

        // Steps a "nice" 1-2-5-10× value UP the ladder (never down) until it would render at
        // least minCellPx apart on screen at the given pxPerMeter — a decluttering floor for
        // large RANGE, not the primary driver of the grid's size (see the OnPaint call site).
        private static double AutoCoarsenGridMeters(double niceMeters, float pxPerMeter, float minCellPx)
        {
            if (pxPerMeter <= 0.0001f) return niceMeters;
            double exp  = Math.Floor(Math.Log10(niceMeters) + 1e-9);
            double mant = Math.Round(niceMeters / Math.Pow(10, exp));
            int guard = 0;
            while (niceMeters * pxPerMeter < minCellPx && guard++ < 30)
            {
                if (mant == 1)      mant = 2;
                else if (mant == 2) mant = 5;
                else                { mant = 1; exp += 1; }
                niceMeters = mant * Math.Pow(10, exp);
            }
            return niceMeters;
        }

        // Bottom-left — the single RANGE control for this whole display (own-ship track, target
        // shapes/markers, graph-paper grid, AND the ship silhouette itself — see the field
        // comment on RangePresetsM). Ported near-verbatim from RadarControl, then absorbed the
        // old separate ZOOM box's grid-cell-size readout (2026-09-03 merge) as a 3rd line, since
        // that number is now driven by RANGE too instead of an independent ZOOM value.
        // Box doubled in size (2026-09-03, per user request "làm to ra gấp đôi") — every dimension
        // below (box/button size, font, text offsets) is exactly 2× the original so the whole
        // control scales up together instead of just the box outline growing around unchanged text.
        private void DrawRangeControl(Graphics g, double gridMeters)
        {
            using var font = new Font("Segoe UI", 15f, FontStyle.Bold);
            using var text = new SolidBrush(Palette.RadarText);
            using var bg    = new SolidBrush(Palette.RadarFace);
            using var bdr   = new Pen(Palette.RadarBorder, 1f);

            const float boxW = 152f, boxH = 104f, pad = 8f, btnSz = 32f;
            float bx = pad, by = Height - boxH - pad;
            g.FillRectangle(bg, bx, by, boxW, boxH);
            g.DrawRectangle(bdr, bx, by, boxW, boxH);

            string rangeText = RangeMeters >= 1000 ? $"{RangeMeters / 1000.0:0.#} km" : $"{RangeMeters:0} m";
            g.DrawString("RANGE", font, text, bx + 8, by + 4);
            g.DrawString(rangeText, font, text, bx + 8, by + 34);
            // Grid-cell size at the current RANGE — a distance reference for the graph-paper grid.
            string gridLbl = gridMeters >= 1000 ? $"{gridMeters / 1000.0:0.#}km/sq" : $"{gridMeters:0.#}m/sq";
            g.DrawString(gridLbl, font, text, bx + 8, by + 66);

            _rangeInRect  = new RectangleF(bx + boxW - btnSz - 6, by + 4,  btnSz, btnSz);
            _rangeOutRect = new RectangleF(bx + boxW - btnSz - 6, by + boxH - btnSz - 4, btnSz, btnSz);

            using var btnBg = new SolidBrush(Palette.BtnPrimaryBg);
            using var btnFg = new SolidBrush(Palette.BtnPrimaryFg);
            using var fmtC  = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.FillRectangle(btnBg, _rangeInRect);
            g.DrawString("+", font, btnFg, _rangeInRect, fmtC);
            g.FillRectangle(btnBg, _rangeOutRect);
            g.DrawString("−", font, btnFg, _rangeOutRect, fmtC);
        }

        // North-up relative plot: most recent point is own-ship (kept at center); older points
        // are projected via GeoMath.OffsetMeters and scaled by RangeMeters (the RANGE corner
        // control above) — points beyond the selected range simply draw past the ring edge
        // instead of auto-fitting to whatever's in the 30-min buffer. Ported from RadarControl.
        // DrawTrack.
        private void DrawTrack(Graphics g, float cx, float cy, float r)
        {
            if (_track.Count < 2) return;
            var pts = _track.ToArray();
            (double lat, double lon) reference = (pts[^1].lat, pts[^1].lon);
            double scale = (r * 0.94) / RangeMeters;

            using var trackPen = new Pen(Palette.RadarTrail, 2f);
            PointF prev = default;
            bool hasPrev = false;
            for (int i = 0; i < pts.Length; i++)
            {
                var (dx, dy) = GeoMath.OffsetMeters(reference.lat, reference.lon, pts[i].lat, pts[i].lon);
                var cur = new PointF(cx + (float)(dx * scale), cy - (float)(dy * scale));
                if (hasPrev) g.DrawLine(trackPen, prev, cur);
                prev = cur;
                hasPrev = true;
            }
        }

        // Target markers/shapes — reads SystemConfig.Targets directly (same pattern MainForm.
        // RefreshMiniTargets / TargetsForm.RefreshData already use, rather than a separate
        // Update-style push), plotted using the same RangeMeters scale as the own-ship track.
        // A target with only its primary Lat/Lon (no ExtraPoints) draws as a small marker dot;
        // 2 vertices draw as a line; 3+ draw as a closed polygon. Off-range targets simply draw
        // past the ring edge (not clipped), same as the track.
        private void DrawTargets(Graphics g, float cx, float cy, float r)
        {
            if (double.IsNaN(_gpsLatDeg) || double.IsNaN(_gpsLonDeg)) return;
            double scale = (r * 0.94) / RangeMeters;

            using var font = new Font("Segoe UI", 8f, FontStyle.Bold);
            using var textBrush = new SolidBrush(Palette.RadarTargetShape);
            using var pen = new Pen(Palette.RadarTargetShape, 2f);
            using var fill = new SolidBrush(Palette.RadarTargetShape);

            // In-range targets — unchanged shape/marker drawing (only the primary Lat/Lon vertex
            // decides in-range vs. off-range, same "keep it simple, ignore ExtraPoints for
            // readouts" rule TargetsForm/mini card already follow for bearing/distance).
            foreach (var t in SystemConfig.Targets)
            {
                if (!t.Enabled) continue;
                var (dx0, dy0) = GeoMath.OffsetMeters(_gpsLatDeg, _gpsLonDeg, t.Lat, t.Lon);
                if (Math.Sqrt(dx0 * dx0 + dy0 * dy0) > RangeMeters) continue;

                var verts = new List<PointF>(1 + t.ExtraPoints.Count)
                {
                    new PointF(cx + (float)(dx0 * scale), cy - (float)(dy0 * scale))
                };
                foreach (var p in t.ExtraPoints)
                {
                    var (dx, dy) = GeoMath.OffsetMeters(_gpsLatDeg, _gpsLonDeg, p.Lat, p.Lon);
                    verts.Add(new PointF(cx + (float)(dx * scale), cy - (float)(dy * scale)));
                }

                if (verts.Count == 1)
                {
                    const float mr = 4f;
                    g.FillEllipse(fill, verts[0].X - mr, verts[0].Y - mr, mr * 2, mr * 2);
                }
                else if (verts.Count == 2)
                {
                    g.DrawLine(pen, verts[0], verts[1]);
                }
                else
                {
                    g.DrawPolygon(pen, verts.ToArray());
                }
                g.DrawString(t.Name, font, textBrush, verts[0].X + 6, verts[0].Y - 14);
            }

            DrawOffRangeTargetIndicators(g, cx, cy, r);
        }

        // Off-range edge indicators — a target further than the current RANGE simply projects to
        // a point off the ring (possibly off the whole control), leaving no clue it exists or
        // which way to look. Per user request ("khi target bị zoom khỏi tầm thì hãy thêm 1 báo
        // hiệu ở viền map để người dùng biết target ở hướng nào"), draw a short outward-pointing
        // chevron right at the ring edge, at the target's true bearing, plus name+distance — the
        // same "off-screen indicator" convention games/nav software use for entities beyond the
        // visible viewport.
        //
        // STACKING (2026-09-03): with 2+ off-range targets in roughly the same direction, drawing
        // every label at the same fixed inset from the edge made them overlap into an unreadable
        // smear. Per follow-up feedback ("chữ báo target đó đừng để trùng nhau, mũi tên có thể
        // trùng, nếu cùng 1 hướng thì để chữ 1 cái trên 1 cái dưới") the CHEVRONS are still allowed
        // to sit on top of each other (each drawn at its own exact bearing — overlapping arrows
        // pointing the same way just reads as "more than one target that way"), but each LABEL is
        // pushed further inward along its own bearing ray for every earlier target within
        // GroupThresholdDeg of it, so same-direction labels stack outward-to-inward instead of
        // colliding. The O(n²) grouping check stays cheap even at the app's current cap of 300
        // targets (2026-09-03, was 4) — worst case ~90k simple double comparisons per repaint,
        // trivial next to the GDI+ drawing work already happening every frame.
        private void DrawOffRangeTargetIndicators(Graphics g, float cx, float cy, float r)
        {
            using var edgePen  = new Pen(Palette.RadarTargetShape, 2.6f) { CustomEndCap = new AdjustableArrowCap(5, 7) };
            using var edgeFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            using var fmtEdge  = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };

            var offRange = new List<(TargetPoint t, double bearingDeg, double distM)>();
            foreach (var t in SystemConfig.Targets)
            {
                if (!t.Enabled) continue;
                var (dx, dy) = GeoMath.OffsetMeters(_gpsLatDeg, _gpsLonDeg, t.Lat, t.Lon);
                double distM = Math.Sqrt(dx * dx + dy * dy);
                if (distM > RangeMeters)
                    offRange.Add((t, (Math.Atan2(dx, dy) * 180.0 / Math.PI + 360.0) % 360.0, distM));
            }
            if (offRange.Count == 0) return;
            offRange.Sort((a, b) => a.bearingDeg.CompareTo(b.bearingDeg));

            const double GroupThresholdDeg = 12.0;
            const float  LabelStackPx      = 14f;
            float edgeD = r * 0.94f;

            for (int i = 0; i < offRange.Count; i++)
            {
                var (t, bearingDeg, distM) = offRange[i];
                double bearingRad = bearingDeg * Math.PI / 180.0;
                float  sinB = (float)Math.Sin(bearingRad), cosB = (float)Math.Cos(bearingRad);

                PointF innerPt = new PointF(cx + sinB * (edgeD - 12f), cy - cosB * (edgeD - 12f));
                PointF outerPt = new PointF(cx + sinB * edgeD,         cy - cosB * edgeD);
                g.DrawLine(edgePen, innerPt, outerPt);

                int stack = 0;
                for (int k = 0; k < i; k++)
                {
                    double diff = Math.Abs(bearingDeg - offRange[k].bearingDeg);
                    if (diff > 180) diff = 360 - diff;
                    if (diff < GroupThresholdDeg) stack++;
                }

                string distLbl = distM >= 1000 ? $"{distM / 1000.0:0.#}km" : $"{distM:0}m";
                float  labelD  = edgeD - 26f - stack * LabelStackPx;
                float  lx = cx + sinB * labelD;
                float  ly = cy - cosB * labelD;
                DrawOutlinedString(g, $"{t.Name} {distLbl}", edgeFont, Palette.RadarTargetShape,
                                   Palette.RadarFace, 2.4f, lx, ly, fmtEdge);
            }
        }

        // Top-right corner.
        private void DrawDriftCorner(Graphics g)
        {
            if (!_driftEnabled) return;
            using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            using var text = new SolidBrush(Palette.LostFg);
            using var bg   = new SolidBrush(Palette.RadarFace);
            using var bdr  = new Pen(Palette.RadarBorder, 1f);

            const float boxW = 78f, boxH = 34f, pad = 4f;
            float bx = Width - boxW - pad, by = pad;
            g.FillRectangle(bg, bx, by, boxW, boxH);
            g.DrawRectangle(bdr, bx, by, boxW, boxH);

            using var fmtC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
            g.DrawString("DRIFT", font, text, new RectangleF(bx, by + 3, boxW, 14), fmtC);
            g.DrawString($"{_driftBearingDeg:000}°  {_driftDistanceM:0.0}m", font, text, new RectangleF(bx, by + 17, boxW, 14), fmtC);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  360° radar-style ring — filled face + concentric range rings + 30°
        //  ticks, matching the look of the mini RadarControl (side panel) —
        //  user asked to unify the visual style across both displays (2026-08-28);
        //  velocity arrows/heave vector/legend below are unchanged. Fixed: N is
        //  always at the top, the ring never rotates.
        // ─────────────────────────────────────────────────────────────────────
        private static void DrawCompassRing(Graphics g, float cx, float cy, float R, float width, float height, float gridSpacingPx, double rangeMeters)
        {
            using var faceBrush = new SolidBrush(Palette.RadarFace);
            g.FillEllipse(faceBrush, cx - R, cy - R, R * 2, R * 2);

            // Graph-paper grid — reference photo (2026-08-28) showed a fine square grid across
            // the whole panel, drawn UNDER the ring border/ticks/ship but ON TOP of the face
            // fill so it reads through the disc too, not just in the corners outside it. Spacing
            // is precomputed by the caller (ComputeNiceGridMeters × pixels-per-metre) so each
            // cell approximates a round real-world distance. As of 2026-09-03 this shares the
            // SAME pixels-per-metre as the grid-circle RANGE labels just below (both derived from
            // RangeMeters) — square lines vs. round rings keeps the two visually distinct while
            // agreeing on the same real-world scale (previously the grid used a separate LOA/ZOOM-
            // based scale that could show a different metre value than the RANGE rings).
            DrawGraphPaperGrid(g, width, height, cx, cy, gridSpacingPx);

            // Grid circles — labelled with the RANGE they represent (rangeMeters × i/4), placed
            // at bearing ~12° (just right of the N tick, the one clear spot not already used by
            // N/E/S/W or the 30° degree labels). Ported from RadarControl.
            //
            // BUG FIX (2026-09-03) — ring radius now matches the track/target/ship scale exactly:
            // these circles used to be drawn at a plain `R×i/4` radius, but DrawTrack/DrawTargets/
            // the ship geometry all convert metres→pixels via `(R×0.94)/RangeMeters` (the 0.94
            // leaves a small margin so the ship/arrows never touch the outer border). That 6%
            // mismatch meant a target sitting EXACTLY at, say, 3/4 of the selected RANGE would
            // plot at 0.705R on screen while the ring labelled "75%" sat at 0.75R — the ring and
            // the thing it's meant to measure didn't actually line up. Multiplying by the same
            // 0.94 here makes the labelled rings and the track/target plotting share one true
            // scale — a target at exactly RangeMeters now lands exactly on the outer ring border.
            using var gridPen = new Pen(Palette.RadarGrid, 1f);
            // Bigger/bolder + higher-contrast colour (RadarText, same as the N/E/S/W and 30° tick
            // labels — RadarGrid was the dim grid-line colour, easy to lose against the graph-
            // paper grid) with a background halo so the numbers stay legible over grid/track
            // lines crossing behind them, per user request "cho các số range trên radar rõ hơn".
            using var rangeLblFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            double rangeLblRad = 12.0 * Math.PI / 180.0;
            using var fmtNear = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };
            for (int i = 1; i <= 3; i++)
            {
                float ri = R * 0.94f * i / 4f;
                g.DrawEllipse(gridPen, cx - ri, cy - ri, ri * 2, ri * 2);
                double distM = rangeMeters * i / 4.0;
                string dlbl = distM >= 1000 ? $"{distM / 1000.0:0.#}k" : $"{distM:0}";
                float dx = cx + (float)Math.Sin(rangeLblRad) * ri;
                float dy = cy - (float)Math.Cos(rangeLblRad) * ri;
                DrawOutlinedString(g, dlbl, rangeLblFont, Palette.RadarText, Palette.RadarFace, 2.6f, dx, dy, fmtNear);
            }

            using var tickPen    = new Pen(Palette.RadarTick,  1f);
            using var northPen   = new Pen(Palette.RadarNorth, 2f);
            using var textBrush  = new SolidBrush(Palette.RadarText);
            using var northBrush = new SolidBrush(Palette.RadarNorth);

            float fs = Math.Max(9.5f, Math.Min(R * 0.088f, 15.5f));
            using var font   = new Font("Segoe UI", fs, FontStyle.Bold);
            using var fmtCtr = new StringFormat
            {
                Alignment     = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            for (int deg = 0; deg < 360; deg += 30)
            {
                double rad  = (deg - 90.0) * Math.PI / 180.0;
                float  cosA = (float)Math.Cos(rad);
                float  sinA = (float)Math.Sin(rad);
                bool   isN  = deg == 0;

                var pen = isN ? northPen : tickPen;
                float ox = cx + cosA * R;
                float oy = cy + sinA * R;
                g.DrawLine(pen, ox, oy, cx + cosA * (R - 12f), cy + sinA * (R - 12f));

                string lbl = deg switch
                {
                    0   => "N",  90  => "E",
                    180 => "S",  270 => "W",
                    _   => deg.ToString()
                };
                float lblR = R * 0.80f;
                float lx   = cx + cosA * lblR;
                float ly   = cy + sinA * lblR;
                g.DrawString(lbl, font, isN ? northBrush : textBrush, lx, ly, fmtCtr);
            }

            using var borderPen = new Pen(Palette.RadarBorder, 2f);
            g.DrawEllipse(borderPen, cx - R, cy - R, R * 2, R * 2);
        }

        // Fine square grid across the whole control (not just inside the ring) — spacing is
        // precomputed by the caller (see ComputeNiceGridMeters) to approximate a round
        // real-world distance per cell, scaled by the current RANGE. Lines are built outward from
        // the ship's own center (cx,cy), not the control's top-left corner — BUG FIX: anchoring
        // to (0,0) meant the grid pattern slid around under the ship every time spacing changed
        // (RANGE step), instead of the ship staying visually "pinned" to a grid intersection the
        // way zooming into a fixed map point should look. Anchoring at (cx,cy) guarantees a line
        // always passes exactly through the ship's own center.
        private static void DrawGraphPaperGrid(Graphics g, float width, float height, float cx, float cy, float spacing)
        {
            if (spacing < 1f) return;
            using var gridPen = new Pen(Color.FromArgb(100, Palette.RadarGrid), 1.3f);
            for (float x = cx; x <= width; x += spacing)
                g.DrawLine(gridPen, x, 0, x, height);
            for (float x = cx - spacing; x >= 0; x -= spacing)
                g.DrawLine(gridPen, x, 0, x, height);
            for (float y = cy; y <= height; y += spacing)
                g.DrawLine(gridPen, 0, y, width, y);
            for (float y = cy - spacing; y >= 0; y -= spacing)
                g.DrawLine(gridPen, 0, y, width, y);
        }

        // Rotates a point given relative to the ship's local frame (origin at the ship center,
        // -Y = bow) by the current heading, clockwise on screen to match the ring's own N(top)
        // → E(right) → S(bottom) → W(left) layout.
        //
        // BUG FIX: the previous formula here (rx = px·cos + py·sin, ry = -px·sin + py·cos) is
        // the *transpose* of the standard rotation matrix, not its correct form — it rotates
        // correctly at 0°/180° (N/S, where the mistake happens to cancel out) but mirrors E and
        // W: heading 90° (should point at the E tick, screen-right) was rotating the bow to
        // screen-*left* instead, i.e. toward where the W tick sits. Verified against
        // DrawCompassRing's own tick math (E tick is unambiguously at cx+R, right of center) and
        // against RadarControl's heading-line arrow, which uses a *different*, already-correct
        // direct polar formula (Sin/-Cos) — so the ship silhouette/arrows here were pointing at
        // a mirrored heading while the ring's own tick labels (and RadarControl's heading line)
        // were correct, which is why the ship in this control and the ship in the mini
        // RadarControl could visibly point in different directions for the identical heading
        // value. The correct rotation matrix is rx = px·cos − py·sin, ry = px·sin + py·cos.
        private static PointF RotatePoint(float px, float py, double hdgDeg, float cx, float cy)
        {
            double rad = hdgDeg * Math.PI / 180.0;
            float  sin = (float)Math.Sin(rad);
            float  cos = (float)Math.Cos(rad);
            float  rx  = px * cos - py * sin;
            float  ry  = px * sin + py * cos;
            return new PointF(cx + rx, cy + ry);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void DrawHeadingLine(Graphics g, float cx, float cy, float R, float sl, double hdg)
        {
            using var pen = new Pen(ColHdgLn, 1f) { DashStyle = DashStyle.Dot };
            PointF p1 = RotatePoint(0, -sl,          hdg, cx, cy);
            PointF p2 = RotatePoint(0, -R * 0.79f,   hdg, cx, cy);
            g.DrawLine(pen, p1, p2);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Ship afterimage — faded ghost silhouettes stamped every StampIntervalSeconds over
        //  BOTH heading and GPS position, drawn UNDER the live (solid) ship so a turn *and*
        //  straight-line travel both leave a lasting trail of poses, the way the reference
        //  running-sprite sheet reads as motion. This display is ego-centric (ship always
        //  screen-center on the CURRENT fix, ring doesn't pan like a moving map) — a stamp's
        //  ghost position is the CURRENT ship's screen center offset by how far away that
        //  historical fix now is from the current one (equirectangular approx, scaled by the
        //  same pxPerMeter the grid uses), not an absolute screen coordinate. Skips any stamp
        //  that's within ~1.5° heading AND ~2px position of the current state — without this
        //  guard, a fully stationary ship (holding heading, holding position) would stack many
        //  near-identical translucent fills exactly on top of each other, visibly darkening
        //  the ship's own fill/outline for no reason even though nothing moved.
        // ─────────────────────────────────────────────────────────────────────
        private void DrawShipTrail(Graphics g, float cx, float cy, float sl, float sw, float pxPerMeter)
        {
            if (_trail.Count == 0) return;
            var now = DateTime.UtcNow;
            bool hasFix = !double.IsNaN(_gpsLatDeg) && !double.IsNaN(_gpsLonDeg);
            double maxAgeSeconds = StampIntervalSeconds * MaxStamps;

            PointF[] local = ShipLocalPoints(sl, sw);

            foreach (var (t, hdg, lat, lon) in _trail)
            {
                double age = (now - t).TotalSeconds;
                if (age < 0) continue;

                float offX = 0f, offY = 0f;
                if (hasFix && !double.IsNaN(lat) && !double.IsNaN(lon))
                {
                    var (dxM, dyM) = GeoMath.OffsetMeters(_gpsLatDeg, _gpsLonDeg, lat, lon);
                    offX = (float)(dxM * pxPerMeter);
                    offY = (float)(-dyM * pxPerMeter);
                }

                double headingDelta = Math.Abs(AngleDiffDeg(hdg, _hdg));
                double offsetPx = Math.Sqrt(offX * offX + offY * offY);
                if (headingDelta < 1.5 && offsetPx < 2.0) continue;

                int alpha = (int)Math.Clamp(55.0 * (1.0 - age / maxAgeSeconds), 0, 55);
                if (alpha < 3) continue;

                float ghostCx = cx + offX, ghostCy = cy + offY;
                var pts = new PointF[local.Length];
                for (int k = 0; k < local.Length; k++)
                    pts[k] = RotatePoint(local[k].X, local[k].Y, hdg, ghostCx, ghostCy);

                using var path = BuildRoundedShipPath(pts, sw * ShipCornerRadiusFactor);
                using var ghostFill = new SolidBrush(Color.FromArgb(alpha, ColShipF));
                g.FillPath(ghostFill, path);
            }
        }

        private static double AngleDiffDeg(double a, double b)
        {
            double d = (a - b) % 360.0;
            if (d > 180) d -= 360;
            if (d < -180) d += 360;
            return d;
        }

        // Shared by DrawShip and DrawShipTrail so the live ship and its ghosts never drift out
        // of sync with each other. -Y = bow (pointed tip), +Y = stern (flat edge).
        private static PointF[] ShipLocalPoints(float sl, float sw) => new[]
        {
            new PointF(0,    -sl),
            new PointF(sw,   -sl * 0.52f),
            new PointF(sw,   sl),
            new PointF(-sw,  sl),
            new PointF(-sw,  -sl * 0.52f),
        };

        // Rounds only the CORNERS of the hull polygon — small fillets at each vertex, straight
        // edges preserved between them (2026-09-03, per user reference screenshot of a rounded
        // conning-display ship silhouette: "vẽ hình tàu bo lại như thế này"). First attempt used
        // a Catmull-Rom spline through all 5 points (AddClosedCurve) — that pulled the WHOLE
        // outline into a smooth loose curve, not just the corners, and with the shoulder points
        // already near full beam just below the bow tip, the spline overshot into a symmetric
        // rounded-rectangle/capsule silhouette that lost the tapered-bow read entirely (user
        // report: "sao sửa thành hình con nhộng vậy" — that's not the ship shape in the reference
        // photo). Fixed with the standard "rounded polygon" technique instead: each vertex Pi is
        // replaced by 2 points inset `radius` along its adjacent edges (clamped to half the
        // shorter edge so short edges — e.g. the narrow stern — never overlap), connected across
        // the corner by a quadratic Bézier curved through the ORIGINAL sharp vertex (GraphicsPath
        // has no native quadratic Add — converted to the equivalent cubic via the exact
        // quadratic→cubic control-point formula, C1/C2 = endpoint + 2/3×(quadControl−endpoint)).
        // Straight edges stay straight; only the 5 corners (bow tip, 2 shoulders, 2 stern
        // corners) round off — the hull keeps its tapered-bow/parallel-side/stern silhouette.
        //
        // Radius factor (2026-09-03, per user follow-up "cho các góc bo nhọn lại" — after seeing
        // it at 0.7×sw, sharpen the corners back down): callers pass `sw × ShipCornerRadiusFactor`
        // as the fillet radius. Lowered 0.7→0.35 — corners still visibly soften instead of being
        // knife-sharp, but nowhere near as rounded/blobby as the first pass.
        private const float ShipCornerRadiusFactor = 0.35f;
        private static GraphicsPath BuildRoundedShipPath(PointF[] pts, float radius)
        {
            int n = pts.Length;
            var afterVertex  = new PointF[n]; // point on the OUTGOING edge, `radius` past P[i]
            var beforeVertex = new PointF[n]; // point on the INCOMING edge, `radius` before P[i]
            for (int i = 0; i < n; i++)
            {
                int prev = (i - 1 + n) % n, next = (i + 1) % n;
                beforeVertex[i] = InsetToward(pts[i], pts[prev], radius);
                afterVertex[i]  = InsetToward(pts[i], pts[next], radius);
            }

            var path = new GraphicsPath();
            path.StartFigure();
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                path.AddLine(afterVertex[i], beforeVertex[next]); // straight edge segment
                PointF a = beforeVertex[next], c = pts[next], b = afterVertex[next]; // rounded corner AT P[next]
                var c1 = new PointF(a.X + 2f / 3f * (c.X - a.X), a.Y + 2f / 3f * (c.Y - a.Y));
                var c2 = new PointF(b.X + 2f / 3f * (c.X - b.X), b.Y + 2f / 3f * (c.Y - b.Y));
                path.AddBezier(a, c1, c2, b);
            }
            path.CloseFigure();
            return path;
        }

        private static PointF InsetToward(PointF from, PointF to, float dist)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.0001f) return from;
            float t = Math.Min(dist, len * 0.5f) / len; // never eat past the edge's midpoint
            return new PointF(from.X + dx * t, from.Y + dy * t);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Ship silhouette — pointed bow, flat stern — rotates with heading.
        //
        //  BUG FIX (2026-08-28): this used to be the opposite — flat bow / pointed stern,
        //  the original spec'd shape. That shape was geometrically self-consistent (the flat
        //  edge really was "the bow" per -Y-is-bow convention everywhere else in this file:
        //  RotatePoint, DrawHeadingLine, bowY/sternY arrow attachment points), but it fights a
        //  near-universal visual instinct — a pointed end reads as "the front" the way an
        //  arrowhead or a vehicle nose does. That instinct stayed harmless while the ship just
        //  sat there rotating in place, but became actively misleading once DrawShipTrail
        //  added a movement wake behind the ship: the wake is geometrically correct (trails
        //  the real, flat-edge bow) but a viewer reading the pointed stern as "the bow" sees
        //  the wake as sitting in *front* of what they think is the bow — confirmed by user
        //  report ("vệt dư ảnh ở phía đầu NHỌN"). Flipping the hull to pointed-bow/flat-stern
        //  makes the drawn shape agree with the instinctive reading, so the wake now reads as
        //  trailing behind the (visually obvious) front. -Y is still "the bow" by convention —
        //  only which end looks pointed vs. flat changed, so RotatePoint/DrawHeadingLine/
        //  bowY/sternY below are all untouched.
        // ─────────────────────────────────────────────────────────────────────
        // BUG FIX (2026-09-03, user report "hình tàu để dạng khung line thôi, để dạng sharp khó
        // theo dõi"): a solid-filled hull was hard to visually track against everything drawn
        // around/behind it (graph-paper grid, track trail, target shapes, off-range indicators —
        // all sharing this same small area near the ship). Outline-only reads as a clear, light
        // silhouette without occluding whatever's underneath, so FillPolygon is dropped entirely
        // — just the DrawPolygon stroke remains, thickened (1.5f→2.2f) since there's no longer a
        // fill to help the shape read at a glance.
        private static void DrawShip(Graphics g, float cx, float cy, float sl, float sw, double hdg)
        {
            PointF[] local = ShipLocalPoints(sl, sw);
            var pts = new PointF[local.Length];
            for (int i = 0; i < local.Length; i++)
                pts[i] = RotatePoint(local[i].X, local[i].Y, hdg, cx, cy);

            using var path = BuildRoundedShipPath(pts, sw * ShipCornerRadiusFactor);
            // Thickened 2.2→2.8 (2026-09-03, per user request "viền tàu hiện tại đậm hơn 1 chút").
            using var bpen = new Pen(ColShipB, 2.8f) { LineJoin = LineJoin.Round };
            g.DrawPath(bpen, path);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Lateral arrows — attached to the ship, rotate with it. Value shown
        //  as a plain number next to the tip, no box/unit/direction text.
        //  stbd (v>0): arrow to the right of centerline   port (v<0): left
        // ─────────────────────────────────────────────────────────────────────
        private static void DrawLateralArrow(Graphics g, float cx, float cy, float localY,
                                              double v, float sw,
                                              float scale, float maxL, float R, double hdg, float zoom)
        {
            if (Math.Abs(v) < 0.05) return;
            float len  = Math.Min(Math.Abs((float)(v * scale)), maxL);
            bool  stbd = v > 0;
            float localOx = stbd ? sw : -sw;
            float localEx = stbd ? localOx + len : localOx - len;

            PointF p1 = RotatePoint(localOx, localY, hdg, cx, cy);
            PointF p2 = RotatePoint(localEx, localY, hdg, cx, cy);

            using var pen = new Pen(ColLat, 5f * zoom)
                { CustomEndCap = new AdjustableArrowCap(8, 9) };
            g.DrawLine(pen, p1, p2);

            float  gap      = R * 0.06f;
            float  tagLocal = stbd ? localEx + gap : localEx - gap;
            PointF tagPos   = RotatePoint(tagLocal, localY, hdg, cx, cy);
            DrawValueLabel(g, tagPos.X, tagPos.Y, $"{Math.Abs(v):0.00}", ColLat, R, zoom);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Axial arrow — attached to the ship, rotates with it.
        //  FWD (v>0): toward bow    AFT (v<0): toward stern
        // ─────────────────────────────────────────────────────────────────────
        private static void DrawAxialArrow(Graphics g, float cx, float cy,
                                            float sl, double vLong,
                                            float scale, float maxL, float R, double hdg, float zoom)
        {
            if (Math.Abs(vLong) < 0.05) return;
            float len  = Math.Min(Math.Abs((float)(vLong * scale)), Math.Min(maxL, sl * 0.85f));
            if (len < 4) return;
            bool  ahead    = vLong > 0;
            float localEy  = ahead ? -len : len;

            PointF p1 = RotatePoint(0, 0,        hdg, cx, cy);
            PointF p2 = RotatePoint(0, localEy,  hdg, cx, cy);

            using var pen = new Pen(ColAxial, 6f * zoom)
                { CustomEndCap = new AdjustableArrowCap(9, 11) };
            g.DrawLine(pen, p1, p2);

            float  gap      = R * 0.05f;
            float  tagLocal = ahead ? localEy - gap : localEy + gap;
            PointF tagPos   = RotatePoint(0, tagLocal, hdg, cx, cy);
            DrawValueLabel(g, tagPos.X, tagPos.Y, $"{Math.Abs(vLong):0.0}", ColAxial, R, zoom);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Heave (Z) vector — drawn at the ship's own origin, the same point the axial (X)
        //  arrow starts from and the lateral (Y) arrows are offset from, so all 3 axes read
        //  as one 3-axis vector at a shared reference point. Heave is perpendicular to this
        //  top-down view, so it can't be a screen-plane arrow like X/Y — uses the standard
        //  vector-diagram convention for an out-of-plane axis instead: a dot inside a circle
        //  = pointing up/out of the page (heave > 0), a cross inside a circle = pointing
        //  down/into the page (heave < 0).
        // ─────────────────────────────────────────────────────────────────────
        private void DrawHeaveVector(Graphics g, float cx, float cy, float sw, float R, double hdg, float zoom)
        {
            if (Math.Abs(_heaveCm) < 0.3) return;
            PointF center = RotatePoint(0, 0, hdg, cx, cy);
            float  radius = sw * 0.55f;

            using var pen = new Pen(Palette.SeriesHeave, 2f * zoom);
            g.DrawEllipse(pen, center.X - radius, center.Y - radius, radius * 2, radius * 2);

            if (_heaveCm > 0)
            {
                float dotR = radius * 0.42f;
                using var brush = new SolidBrush(Palette.SeriesHeave);
                g.FillEllipse(brush, center.X - dotR, center.Y - dotR, dotR * 2, dotR * 2);
            }
            else
            {
                float k = radius * 0.62f;
                g.DrawLine(pen, center.X - k, center.Y - k, center.X + k, center.Y + k);
                g.DrawLine(pen, center.X - k, center.Y + k, center.X + k, center.Y - k);
            }

            float  gap      = radius + sw * 0.5f;
            PointF labelPos = RotatePoint(gap, 0, hdg, cx, cy);
            DrawValueLabel(g, labelPos.X, labelPos.Y, $"{Math.Abs(_heaveCm):0.0}", Palette.SeriesHeave, R, zoom);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Axis legend — boxed key at the top-left corner explaining what X/Y/Z mean on
        //  this display. Deliberately just 3 text rows with a color swatch each — an earlier
        //  version tried a hand-drawn "3D gizmo" icon and it looked cluttered/overlapped at
        //  small sizes, so this sticks to the same plain-text-plus-color-cue language already
        //  used everywhere else in this control. Colors match the actual arrows/vector
        //  elsewhere on the ship (ColAxial/ColLat/SeriesHeave) so a user can connect "orange
        //  arrow on the ship" → "X in the legend" by color alone.
        // ─────────────────────────────────────────────────────────────────────
        private static void DrawAxisLegend(Graphics g, float R)
        {
            if (R < 80f) return;

            const float pad  = 10f;
            const float rowH = 24f;
            float boxW = 180f, boxH = pad * 2 + rowH * 3;
            float bx0 = 10f, by0 = 10f;

            using var boxBg  = new SolidBrush(Palette.ConningTagBg);
            using var boxBdr = new Pen(Palette.BorderCard, 1.4f);
            g.FillRectangle(boxBg, bx0, by0, boxW, boxH);
            g.DrawRectangle(boxBdr, bx0, by0, boxW, boxH);

            using var font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            using var fmt  = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };

            void Row(int i, Color color, string text)
            {
                float rowCy = by0 + pad + rowH * i + rowH / 2f;
                const float swatchR = 5f;
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, bx0 + pad, rowCy - swatchR, swatchR * 2, swatchR * 2);
                g.DrawString(text, font, brush, bx0 + pad + swatchR * 2 + 8f, rowCy, fmt);
            }

            Row(0, ColAxial,            "X = FWD / AFT");
            Row(1, ColLat,              "Y = PORT / STBD");
            Row(2, Palette.SeriesHeave, "Z = UP / DOWN");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Wind arrow — plotted at true bearing (the ring is fixed/north-up),
        //  pointing from the ring edge toward the center.
        // ─────────────────────────────────────────────────────────────────────
        private void DrawWindArrow(Graphics g, float cx, float cy, float R)
        {
            if (_wSpd < 0.1) return;
            double rad = _wDir * Math.PI / 180.0;
            float  dx  = (float)Math.Sin(rad);
            float  dy  = -(float)Math.Cos(rad);
            float  startD = R * 0.94f;
            float  arrowL = Math.Min((float)(_wSpd / 15.0 * R * 0.30f), R * 0.36f);
            float  ox  = cx + dx * startD;
            float  oy  = cy + dy * startD;
            float  tx  = ox - dx * arrowL;
            float  ty  = oy - dy * arrowL;

            using var pen = new Pen(ColWind, 4f)
                { CustomEndCap = new AdjustableArrowCap(7, 8) };
            g.DrawLine(pen, ox, oy, tx, ty);

            // Offset label perpendicular to arrow toward the nearer screen edge
            float perpX = -dy;
            float perpY =  dx;
            float sign  = (perpX * cx + perpY * cy > perpX * tx + perpY * ty) ? 1f : -1f;
            float lx    = tx + perpX * sign * (R * 0.06f);
            float ly    = ty + perpY * sign * (R * 0.06f);
            DrawValueLabel(g, lx, ly, $"{_wSpd:0.0}", ColWind, R);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Plain numeric value label — no box, no unit, no direction text.
        //  Direction is conveyed by the arrow itself.
        // ─────────────────────────────────────────────────────────────────────
        // zoom (the RANGE-derived visualZoom from OnPaint, already clamped to [0.6, 1.8] there)
        // scales the clamped base size so text stays legible instead of pinned to a single size
        // regardless of RANGE. Ship-attached callers (lateral/axial/heave) pass the live value;
        // the wind arrow (ring-anchored, not ship-attached) always passes the default 1f.
        private static void DrawValueLabel(Graphics g, float x, float y, string text, Color color, float R, float zoom = 1f)
        {
            float fs = Math.Clamp(R * 0.13f, 11f, 22f) * zoom;
            using var font = new Font("Segoe UI", fs, FontStyle.Bold);
            using var fmt  = new StringFormat
            {
                Alignment     = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            DrawOutlinedString(g, text, font, color, Palette.RadarFace, 3.2f, x, y, fmt);
        }

        // Text drawn straight on the compass background (ring/ticks/ship can cross behind it)
        // needs its own contrast independent of whatever it overlaps. Halo the glyphs with
        // haloColor (matches the app background so it "cuts a hole" through whatever's under
        // it) then fill on top — reads clean in both themes without depending on exactly what's
        // underneath.
        private static void DrawOutlinedString(Graphics g, string text, Font font, Color fillColor,
                                                Color haloColor, float haloWidth, float x, float y,
                                                StringFormat fmt)
        {
            if (string.IsNullOrEmpty(text)) return;
            float emPx = font.Size * g.DpiY / 72f;
            using var path = new GraphicsPath();
            path.AddString(text, font.FontFamily, (int)font.Style, emPx, new PointF(x, y), fmt);
            using var haloPen  = new Pen(haloColor, haloWidth) { LineJoin = LineJoin.Round };
            using var fillBrush = new SolidBrush(fillColor);
            g.DrawPath(haloPen, path);
            g.FillPath(fillBrush, path);
        }
    }
}
