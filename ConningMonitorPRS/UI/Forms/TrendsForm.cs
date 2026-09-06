using System;
using System.Drawing;
using System.Windows.Forms;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Models;
using ConningMonitorPRS.UI.Controls;
using ConningMonitorPRS.UI.Theme;

namespace ConningMonitorPRS.UI.Forms
{
    // Roll/Pitch/Heave, Wind, and Nav trend charts. Self-contained like DataListForm/
    // TargetsForm: pulls straight from ConningDataHub on its own timer, no dependency on
    // MainForm's tick handlers.
    //
    // Used to also host an own-ship radar (heading/wind + 30-min track) via RadarControl in a
    // SplitContainer — removed 2026-08-28 per user request ("bỏ luôn radar trong trendform đi,
    // tích hợp hết vô radar ở mainform"): RadarControl's full role (RANGE control, 30-min
    // track, and the new target-shape plotting) was absorbed into ConningControl (the main
    // radar in MainForm), and RadarControl.cs itself was deleted as dead code. This window is
    // now chart-only.
    public class TrendsForm : Form
    {
        private TrendChartControl _trend = null!;
        private System.Windows.Forms.Timer _timer = null!;
        private ComboBox _cboWindow = null!;
        private TrendChartControl.TrendMode _currentMode = TrendChartControl.TrendMode.Motion;

        // Readout paint-churn guard: Name/Color only change when the mode switches, and each
        // Value string only changes when its rounded reading actually moves — re-assigning
        // Label.Text/.ForeColor unconditionally every 200ms tick (even to an identical value)
        // still queues a repaint, adding to the flicker from the constant redraw pressure.
        private TrendChartControl.TrendMode? _lastAppliedMode;
        private readonly string?[] _cVal = new string?[3];

        // Live numeric readout above the chart — up to 3 tiles, reused across modes (unused
        // ones hidden) so the exact current value of each series is easy to read at a glance
        // instead of having to eyeball it off the chart line.
        private readonly (Panel Tile, Label Name, Label Value)[] _readouts = new (Panel, Label, Label)[3];

        // Shortest first — the combo defaults to index 0 so the chart opens on the tightest,
        // most-responsive window rather than a slow-filling 30-minute one.
        private static readonly (string Label, double Minutes)[] TrendWindows =
        {
            ("Last 1 min",  1.0),
            ("Last 2 min",  2.0),
            ("Last 5 min",  5.0),
            ("Last 15 min", 15.0),
            ("Last 30 min", 30.0),
        };

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Palette.ApplyTitleBarTheme(Handle);
        }

        public TrendsForm()
        {
            Text          = "TRENDS";
            Size          = new Size(760, 560);
            MinimumSize   = new Size(560, 420);
            StartPosition = FormStartPosition.CenterParent;
            BackColor     = Palette.AppBg;

            BuildUI();

            _timer = new System.Windows.Forms.Timer { Interval = 200 };
            _timer.Tick += (s, e) => Poll();
            _timer.Start();

            SystemConfig.ThemeChanged += ApplySelfTheme;
            FormClosed += (s, e) =>
            {
                SystemConfig.ThemeChanged -= ApplySelfTheme;
                _timer.Stop();
                _timer.Dispose();
            };
        }

        private void ApplySelfTheme() => Palette.ApplyTitleBarTheme(Handle);

        private void BuildUI()
        {
            // AutoScroll so the toolbar grows a horizontal scrollbar instead of silently
            // clipping the rightmost controls (e.g. the time-window combo) when narrow.
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, BackColor = Palette.PanelBg, Padding = new Padding(6, 6, 0, 0), WrapContents = false, AutoScroll = true };

            void AddModeBtn(string text, TrendChartControl.TrendMode mode)
            {
                var b = new Button { Text = text, Width = 90, Height = 26, FlatStyle = FlatStyle.Flat, BackColor = Palette.PanelBg, ForeColor = Palette.TextLabel, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
                b.FlatAppearance.BorderColor = Palette.BorderCard;
                b.Click += (s, e) => { _currentMode = mode; _trend.SetMode(mode, false); };
                toolbar.Controls.Add(b);
            }
            AddModeBtn("MOTION", TrendChartControl.TrendMode.Motion);
            AddModeBtn("NAV",    TrendChartControl.TrendMode.Nav);
            AddModeBtn("WIND",   TrendChartControl.TrendMode.Wind);

            _cboWindow = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Font = new Font("Segoe UI", 9), BackColor = Palette.InputBg, ForeColor = Palette.TextValue, Margin = new Padding(10, 2, 0, 0) };
            foreach (var w in TrendWindows) _cboWindow.Items.Add(w.Label);
            _cboWindow.SelectedIndex = 0; // shortest window first
            _cboWindow.SelectedIndexChanged += (s, e) => _trend.SetViewWindow(TrendWindows[_cboWindow.SelectedIndex].Minutes);
            toolbar.Controls.Add(_cboWindow);

            // Readout row — exact current value of every series in the active mode, colored to
            // match its line in the chart legend below.
            var readoutRow = new BufferedFlowPanel { Dock = DockStyle.Fill, BackColor = Palette.PanelBg, WrapContents = false, Padding = new Padding(6, 4, 0, 4) };
            for (int i = 0; i < _readouts.Length; i++)
            {
                var tile = MakeReadoutTile(out var nameLbl, out var valueLbl);
                readoutRow.Controls.Add(tile);
                _readouts[i] = (tile, nameLbl, valueLbl);
            }

            // toolbar is the only Top-docked child here, readoutRow is Fill — no ambiguity
            // about which ends up "outermost" the way there would be with two Top siblings.
            var topArea = new BufferedPanel { Dock = DockStyle.Top, Height = 40 + 60, BackColor = Palette.PanelBg };
            topArea.Controls.Add(readoutRow);
            topArea.Controls.Add(toolbar);

            _trend = new TrendChartControl { Dock = DockStyle.Fill };
            _trend.SetViewWindow(TrendWindows[0].Minutes);

            Controls.Add(_trend);
            Controls.Add(topArea);
        }

        // Plain Label repaints via a separate erase (WM_ERASEBKGND) then draw (WM_PAINT) pass —
        // for a control whose Text is reassigned every Poll() tick (200ms), that two-step
        // repaint shows as a visible flicker. Same fix as MainForm.ClockLabel: double buffer
        // and paint the background ourselves in one pass instead of a Transparent BackColor
        // (which repaints against the parent in between, making the flicker worse).
        private sealed class ReadoutLabel : Label
        {
            public ReadoutLabel() { DoubleBuffered = true; }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using var brush = new SolidBrush(BackColor);
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }

        // Double-buffering only the leaf Label wasn't enough: Panel/FlowLayoutPanel do NOT
        // double-buffer by default, so every Poll() tick still erased-then-redrew the tile's
        // own background as a separate pass from its (now double-buffered) child Label —
        // visible as the two frames briefly overlapping/tearing into each other. All 3 levels
        // that host a readout value (tile → readoutRow → topArea) need to double-buffer.
        private sealed class BufferedPanel : Panel
        {
            public BufferedPanel() { DoubleBuffered = true; }
        }

        private sealed class BufferedFlowPanel : FlowLayoutPanel
        {
            public BufferedFlowPanel() { DoubleBuffered = true; }
        }

        private Panel MakeReadoutTile(out Label nameLbl, out Label valueLbl)
        {
            var tile = new BufferedPanel { Width = 160, Height = 48, Margin = new Padding(4, 2, 4, 2), BackColor = Palette.CardBg };
            nameLbl = new ReadoutLabel
            {
                Dock = DockStyle.Top, Height = 16,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Palette.TextDim, BackColor = Palette.CardBg
            };
            valueLbl = new ReadoutLabel
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                ForeColor = Palette.TextValue, BackColor = Palette.CardBg
            };
            tile.Controls.Add(valueLbl);
            tile.Controls.Add(nameLbl);
            return tile;
        }

        private void UpdateReadouts(Snapshot snap)
        {
            (string Name, string Value, Color Color)[] vals = _currentMode switch
            {
                TrendChartControl.TrendMode.Motion => new[]
                {
                    ("ROLL",  $"{snap.RollDeg:0.0}°",   Palette.SeriesRoll),
                    ("PITCH", $"{snap.PitchDeg:0.0}°",  Palette.SeriesPitch),
                    ("HEAVE", $"{snap.HeaveCm:0.0} cm", Palette.SeriesHeave),
                },
                TrendChartControl.TrendMode.Nav => new[]
                {
                    ("SPEED",   $"{snap.GpsSpeedKnot:0.0} kn", Palette.SeriesWSpeed),
                    ("HEADING", $"{snap.Heading:000.0}°",      Palette.SeriesWDir),
                },
                _ => new[]
                {
                    ("WIND SPD", $"{snap.WindSpeedMs:0.0} m/s", Palette.SeriesWSpeed),
                    ("WIND DIR", $"{snap.WindDirDeg:000}°",     Palette.SeriesWDir),
                },
            };

            bool modeChanged = _currentMode != _lastAppliedMode;

            for (int i = 0; i < _readouts.Length; i++)
            {
                var (tile, nameLbl, valueLbl) = _readouts[i];
                if (i < vals.Length)
                {
                    if (modeChanged)
                    {
                        tile.Visible       = true;
                        nameLbl.Text       = vals[i].Name;
                        valueLbl.ForeColor = vals[i].Color;
                        _cVal[i] = null; // mode just switched — force the value text below to reapply
                    }
                    if (_cVal[i] != vals[i].Value)
                    {
                        valueLbl.Text = vals[i].Value;
                        _cVal[i] = vals[i].Value;
                    }
                }
                else if (modeChanged)
                {
                    tile.Visible = false;
                }
            }

            _lastAppliedMode = _currentMode;
        }

        private void Poll()
        {
            if (IsDisposed) return;
            var snap = ConningDataHub.Instance.GetSnapshot();

            _trend.PushMotionData(snap.RollDeg, snap.PitchDeg, snap.HeaveCm);
            _trend.PushNavData(snap.GpsSpeedKnot, snap.Heading);
            _trend.PushWindData(snap.WindSpeedMs, snap.WindDirDeg);
            _trend.Render();

            UpdateReadouts(snap);
        }
    }
}
