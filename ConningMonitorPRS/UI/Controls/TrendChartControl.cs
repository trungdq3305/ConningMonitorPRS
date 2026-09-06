using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using ConningMonitorPRS.Core.Models;
using ConningMonitorPRS.UI.Theme;

namespace ConningMonitorPRS.UI.Controls
{
    public class TrendChartControl : UserControl
    {
        public enum TrendMode { Motion, Wind, Nav }

        private readonly Chart _chart = new();
        private TrendMode _mode       = TrendMode.Motion;
        private bool      _separate   = false;
        private double    _viewMin    = 2.0;

        private const int MaxPoints = 12000; // 20 min × 10 Hz

        private readonly Queue<(DateTime t, double v1, double v2, double v3)> _motionBuf = new();
        private readonly Queue<(DateTime t, double v1, double v2)>            _windBuf   = new();
        private readonly Queue<(DateTime t, double v1, double v2)>            _navBuf    = new();

        private double _hoverX;
        private string _hoverText = "";

        public TrendChartControl()
        {
            _chart.Dock = DockStyle.Fill;
            _chart.MouseMove += OnChartMouseMove;
            _chart.PostPaint  += OnChartPostPaint;
            Controls.Add(_chart);
            SetMode(TrendMode.Motion, false);

            // BUG FIX: theme toggle left this chart showing stale colors (e.g. still the dark
            // ChartBg/series colors while the rest of the app switched to light). Palette.
            // ApplyToForm's swap-map walk only touches plain Control.BackColor/ForeColor (plus
            // a DataGridView special-case) — it has no idea how to reach into a Chart's
            // ChartArea/Series/Legend colors, which are only ever set once, inside BuildChart(),
            // from whatever Palette.* returned at that moment. Rebuilding on ThemeChanged re-
            // reads Palette.* fresh; the next periodic Render() (every ~100ms from the caller)
            // repopulates the series points BuildChart() just cleared, so nothing visibly blanks.
            SystemConfig.ThemeChanged += OnThemeChanged;
        }

        private void OnThemeChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { Invoke(new Action(OnThemeChanged)); return; }
            BuildChart();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) SystemConfig.ThemeChanged -= OnThemeChanged;
            base.Dispose(disposing);
        }

        public void PushMotionData(double roll, double pitch, double heave)
        {
            Enqueue(_motionBuf, (DateTime.Now, roll, pitch, heave));
        }

        public void PushWindData(double speed, double dir)
        {
            Enqueue(_windBuf, (DateTime.Now, speed, dir));
        }

        public void PushNavData(double speedKnot, double headingDeg)
        {
            Enqueue(_navBuf, (DateTime.Now, speedKnot, headingDeg));
        }

        private static void Enqueue<T>(Queue<T> q, T item)
        {
            q.Enqueue(item);
            if (q.Count > MaxPoints) q.Dequeue();
        }

        public void SetViewWindow(double minutes) => _viewMin = minutes;

        public void SetMode(TrendMode mode, bool separate)
        {
            _mode     = mode;
            _separate = separate;
            BuildChart();
        }

        private void BuildChart()
        {
            _chart.Series.Clear();
            _chart.ChartAreas.Clear();
            _chart.Legends.Clear();

            _chart.BackColor = Palette.ChartBg;

            if (_separate)
                BuildSeparate();
            else
                BuildCombined();

            // BUG FIX: Legend.Docking = Top normally makes the Chart auto-reserve a strip of
            // space above the plot area for it — but MakeArea() below pins ChartArea.Position
            // with Auto=false (a separate, deliberate fix for resize-margin behavior, see its
            // comment), which also disables that auto-reservation. Without it, the legend just
            // draws on top of wherever the area's Position already claims, so on a short panel
            // (little total height) the legend visibly overlaps/crowds the plotted lines right
            // under it. Pinning the legend's own Position to a fixed top strip, and shifting
            // MakeArea()'s Position.Y down to start right after it, reserves real space instead.
            var leg = new Legend
            {
                BackColor = Palette.CardBg,
                ForeColor = Palette.TextLabel,
                Font      = new Font("Segoe UI", 8f),
                Docking   = Docking.Top
            };
            leg.Position.Auto = false;
            leg.Position.X = 0; leg.Position.Y = 0; leg.Position.Width = 100; leg.Position.Height = 8;
            _chart.Legends.Add(leg);
        }

        private ChartArea MakeArea(string name, string yTitle, string? y2Title = null)
        {
            var area = new ChartArea(name)
            {
                BackColor = Palette.ChartBg,
                BorderColor = Palette.BorderPanel,
            };
            area.AxisX.LabelStyle.ForeColor = Palette.TextDim;
            area.AxisX.LineColor            = Palette.BorderPanel;
            area.AxisX.MajorGrid.LineColor  = Palette.GridLine;
            area.AxisX.LabelStyle.Format    = "HH:mm:ss";
            area.AxisX.IntervalType         = DateTimeIntervalType.Seconds;
            area.AxisX.MajorGrid.Enabled    = true;
            area.AxisX.IsMarginVisible      = false;

            area.AxisY.Title        = yTitle;
            area.AxisY.TitleForeColor = Palette.TextLabel;
            area.AxisY.LabelStyle.ForeColor = Palette.TextDim;
            area.AxisY.LineColor    = Palette.BorderPanel;
            area.AxisY.MajorGrid.LineColor = Palette.GridLine;

            if (y2Title != null)
            {
                area.AxisY2.Enabled   = AxisEnabled.True;
                area.AxisY2.Title     = y2Title;
                area.AxisY2.TitleForeColor = Palette.TextLabel;
                area.AxisY2.LabelStyle.ForeColor = Palette.TextDim;
                area.AxisY2.LineColor = Palette.BorderPanel;
                area.AxisY2.MajorGrid.Enabled = false;
            }

            foreach (var cursor in new[] { area.CursorX, area.CursorY })
                cursor.IsUserEnabled = false;

            // Left as "Auto", this package computes axis-label margins in pixels rather than
            // as a percentage of the control's size — when the host window is narrowed, the
            // left margin stays pinned at the same pixel width while the right side (and the
            // whole plot area) just gets clipped off instead of rescaling, so everything reads
            // as "sliding left" rather than shrinking. Pinning both the outer chart area and
            // the actual plot rectangle to fixed PERCENTAGES routes all of that through normal
            // Win32 proportional geometry instead, so it always scales with the control.
            area.Position.Auto = false;
            // Y starts at 9 (was 2) to sit below the legend's own reserved 0-8% strip instead
            // of overlapping it; bottom edge (Y+Height) stays at 98, same margin as before.
            area.Position.X = 2; area.Position.Y = 9; area.Position.Width = 96; area.Position.Height = 89;
            area.InnerPlotPosition.Auto = false;
            area.InnerPlotPosition.X = 9; area.InnerPlotPosition.Y = 4;
            area.InnerPlotPosition.Width = y2Title != null ? 82 : 88;
            area.InnerPlotPosition.Height = 84;

            return area;
        }

        private Series MakeSeries(string name, Color color, string areaName, bool useY2 = false)
        {
            return new Series(name)
            {
                ChartType     = SeriesChartType.Line,
                Color         = color,
                BorderWidth   = 2,
                ChartArea     = areaName,
                YAxisType     = useY2 ? AxisType.Secondary : AxisType.Primary,
                XValueType    = ChartValueType.DateTime,
            };
        }

        private void BuildCombined()
        {
            switch (_mode)
            {
                case TrendMode.Motion:
                    var a1 = MakeArea("A1", "° (Roll/Pitch)", "cm (Heave)");
                    _chart.ChartAreas.Add(a1);
                    _chart.Series.Add(MakeSeries("Roll",  Palette.SeriesRoll,  "A1"));
                    _chart.Series.Add(MakeSeries("Pitch", Palette.SeriesPitch, "A1"));
                    _chart.Series.Add(MakeSeries("Heave", Palette.SeriesHeave, "A1", useY2: true));
                    break;
                case TrendMode.Wind:
                    var a2 = MakeArea("A1", "m/s", "° Dir");
                    a2.AxisY2.Minimum = 0; a2.AxisY2.Maximum = 360; a2.AxisY2.Interval = 90;
                    _chart.ChartAreas.Add(a2);
                    _chart.Series.Add(MakeSeries("Wind Spd", Palette.SeriesWSpeed, "A1"));
                    _chart.Series.Add(MakeSeries("Wind Dir", Palette.SeriesWDir,   "A1", useY2: true));
                    break;
                case TrendMode.Nav:
                    var a4 = MakeArea("A1", "kn (Speed)", "° (HDG)");
                    a4.AxisY2.Minimum = 0; a4.AxisY2.Maximum = 360; a4.AxisY2.Interval = 90;
                    _chart.ChartAreas.Add(a4);
                    _chart.Series.Add(MakeSeries("Speed",   Palette.SeriesWSpeed, "A1"));
                    _chart.Series.Add(MakeSeries("Heading", Palette.SeriesWDir,   "A1", useY2: true));
                    break;
            }
        }

        private void BuildSeparate()
        {
            string[] names; Color[] colors; string[] yLabels; bool[] y2Flags;

            switch (_mode)
            {
                case TrendMode.Motion:
                    names   = new[] { "Roll", "Pitch", "Heave" };
                    colors  = new[] { Palette.SeriesRoll, Palette.SeriesPitch, Palette.SeriesHeave };
                    yLabels = new[] { "°", "°", "cm" };
                    y2Flags = new[] { false, false, false };
                    break;
                case TrendMode.Wind:
                    names   = new[] { "Wind Spd", "Wind Dir" };
                    colors  = new[] { Palette.SeriesWSpeed, Palette.SeriesWDir };
                    yLabels = new[] { "m/s", "°" };
                    y2Flags = new[] { false, false };
                    break;
                default: // TrendMode.Nav
                    names   = new[] { "Speed", "Heading" };
                    colors  = new[] { Palette.SeriesWSpeed, Palette.SeriesWDir };
                    yLabels = new[] { "kn", "°" };
                    y2Flags = new[] { false, false };
                    break;
            }

            for (int i = 0; i < names.Length; i++)
            {
                var area = MakeArea($"A{i}", yLabels[i]);
                _chart.ChartAreas.Add(area);
                _chart.Series.Add(MakeSeries(names[i], colors[i], $"A{i}"));
            }
        }

        public void Render()
        {
            if (InvokeRequired) { Invoke(new Action(Render)); return; }
            if (_chart.Series.Count == 0) return;

            DateTime now    = DateTime.Now;
            DateTime cutoff = now.AddMinutes(-_viewMin);

            switch (_mode)
            {
                case TrendMode.Motion: FillMotion(cutoff); break;
                case TrendMode.Wind:   FillWind(cutoff);   break;
                case TrendMode.Nav:    FillNav(cutoff);    break;
            }

            // Pin the X-axis to exactly [now-viewMin, now] instead of letting the chart
            // auto-fit to whatever data happens to exist — without this, the visible span
            // grows from "however little data has accumulated so far" up to the full window,
            // which reads as the chart itself shrinking/expanding rather than a fixed-width
            // window that data scrolls through (what "Last N min" is supposed to mean).
            double xMin = cutoff.ToOADate();
            double xMax = now.ToOADate();
            foreach (ChartArea area in _chart.ChartAreas)
            {
                area.AxisX.Minimum = xMin;
                area.AxisX.Maximum = xMax;
            }

            _chart.Invalidate();
        }

        private void FillMotion(DateTime cutoff)
        {
            if (_chart.Series.Count < 3) return;
            ClearSeries();
            foreach (var (t, r, p, h) in _motionBuf)
            {
                if (t < cutoff) continue;
                double xv = t.ToOADate();
                _chart.Series[0].Points.AddXY(xv, r);
                _chart.Series[1].Points.AddXY(xv, p);
                _chart.Series[2].Points.AddXY(xv, h);
            }
        }

        private void FillWind(DateTime cutoff)
        {
            if (_chart.Series.Count < 2) return;
            ClearSeries();
            foreach (var (t, s, d) in _windBuf)
            {
                if (t < cutoff) continue;
                double xv = t.ToOADate();
                _chart.Series[0].Points.AddXY(xv, s);
                _chart.Series[1].Points.AddXY(xv, d);
            }
        }

        private void FillNav(DateTime cutoff)
        {
            if (_chart.Series.Count < 2) return;
            ClearSeries();
            foreach (var (t, speed, hdg) in _navBuf)
            {
                if (t < cutoff) continue;
                double xv = t.ToOADate();
                _chart.Series[0].Points.AddXY(xv, speed);
                _chart.Series[1].Points.AddXY(xv, hdg);
            }
        }

        private void ClearSeries()
        {
            foreach (var s in _chart.Series) s.Points.Clear();
        }

        private void OnChartMouseMove(object? sender, MouseEventArgs e)
        {
            try
            {
                if (_chart.ChartAreas.Count == 0) return;
                var area = _chart.ChartAreas[0];
                double xv = area.AxisX.PixelPositionToValue(e.X);
                _hoverX    = xv;
                _hoverText = DateTime.FromOADate(xv).ToString("HH:mm:ss");
            }
            catch { }
        }

        private void OnChartPostPaint(object? sender, ChartPaintEventArgs e)
        {
            if (_hoverX == 0 || _chart.ChartAreas.Count == 0) return;
            try
            {
                var area = _chart.ChartAreas[0];
                float px = (float)area.AxisX.ValueToPixelPosition(_hoverX);
                using var pen = new Pen(Palette.TextDim, 1) { DashStyle = DashStyle.Dash };
                e.ChartGraphics.Graphics.DrawLine(pen,
                    px, (float)area.Position.Y * Height / 100,
                    px, (float)(area.Position.Y + area.Position.Height) * Height / 100);
                using var brush = new SolidBrush(Palette.TextLabel);
                using var font  = new Font("Segoe UI", 8f);
                e.ChartGraphics.Graphics.DrawString(_hoverText, font, brush, px + 4, 4);
            }
            catch { }
        }
    }
}
