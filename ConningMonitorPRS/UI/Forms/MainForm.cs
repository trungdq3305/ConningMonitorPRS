using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Geo;
using ConningMonitorPRS.Core.Models;
using ConningMonitorPRS.Services;
using ConningMonitorPRS.Services.Parsing;
using ConningMonitorPRS.UI.Controls;
using ConningMonitorPRS.UI.Theme;

namespace ConningMonitorPRS.UI.Forms
{
    public class MainForm : Form
    {
        // ── Services ──────────────────────────────────────────────────────────
        private ComEngine?         _comEngine;
        private NmeaParserService? _nmeaParser;
        private MeteoService?      _meteoService;
        private MruService?        _mruService;
        private AlarmEngine        _alarmEngine = null!;
        private DataLogger         _logger      = null!;

        // ── Reliability ───────────────────────────────────────────────────────
        private long   _lastUiTickTicks;
        private Thread? _watchdogThread;
        private volatile bool _watchdogRunning;

        // ── Main display ──────────────────────────────────────────────────────
        private ConningControl _conning     = null!;
        private DataListForm?  _dataListForm;
        private TargetsForm?   _targetsForm;
        private TrendsForm?    _trendsForm;
        private readonly ToolTip _cardTip = new();

        // ── Mini side panel (condensed radar + targets, always visible — see
        // BuildMiniSidePanel) ──────────────────────────────────────────────────
        // Growable row pool (2026-09-03, was a fixed 4-element array — SystemConfig.Targets can
        // now hold up to 300) — rows are built lazily, one per target actually needed, and reused
        // across refreshes exactly like the old fixed pool; see EnsureMiniTargetRow.
        private readonly List<(Panel Row, Label Name, Label Brg, Label Dist)> _miniTargetRows = new();
        private Panel          _miniTargetsListPanel = null!;
        private Label         _miniDriftLbl    = null!;
        private int           _miniTargetsTickCounter;
        private Label _miniRollVal = null!, _miniPitchVal = null!, _miniHeaveVal = null!;
        private string _cMiniRoll = "", _cMiniPitch = "", _cMiniHeave = "";
        private TrendChartControl _miniTrend = null!;

        // ── Data labels ───────────────────────────────────────────────────────
        private Label _lblPosition  = null!;
        private Label _lblSpeed     = null!;
        private Label _lblSpeedUnit = null!;
        private Label _lblHeading   = null!;
        private Label _lblWindSpd   = null!;
        private Label _lblWindDir   = null!;
        private Label _lblClock     = null!;

        // POSITION card toggles lat/lon ⇄ UTM on click; SPEED card toggles kn ⇄ m/s.
        private bool _showUtm;
        private bool _showSpeedMs;

        // ── Top bar badges ────────────────────────────────────────────────────
        private Label _badgeAlarm  = null!;
        private Label _badgeGps    = null!;
        private Label _badgeWind   = null!;
        private Label _badgeMotion = null!;
        private Label _badgeHdg    = null!;

        // ── Alarm tags ────────────────────────────────────────────────────────
        private Tag _windTag = null!, _rollTag = null!, _pitchTag = null!, _heaveTag = null!;
        private Tag _driftTag = null!;
        private Tag _gpsDuoTag = null!;

        // ── Timers ────────────────────────────────────────────────────────────
        private System.Windows.Forms.Timer _uiTimer     = null!;
        private System.Windows.Forms.Timer _healthTimer = null!;
        private System.Windows.Forms.Timer _logTimer    = null!;

        // ── State ─────────────────────────────────────────────────────────────
        private double    _rawHeaveCm;
        private double    _lastHeaveVal;
        private DateTime? _lastZeroCross;
        private const double HeaveArm = 10.0;

        // ── DUO GPS (redundant second receiver) ───────────────────────────────
        // Tracks each configured GPS port's last known fix independently; MainForm picks
        // which one to publish to ConningDataHub. See SelectActiveGpsSource().
        private sealed class GpsSource
        {
            public string LatStr = "NO FIX", LonStr = "NO FIX";
            public double LatDeg = double.NaN, LonDeg = double.NaN;
            public double SpeedKnot;
            public int    Quality = -1;
            public string QualityText = "";
            public DateTime LastUpdate = DateTime.MinValue;
            public bool HasFix => !double.IsNaN(LatDeg) && !double.IsNaN(LonDeg)
                                && (DateTime.Now - LastUpdate).TotalSeconds < 5.0;
        }
        private readonly GpsSource _gps1 = new(), _gps2 = new();
        private int _activeGpsSource = 1; // 1 or 2 — sticky, only changes on a clear quality win

        // Once the app has run this long without crashing, the crash-loop guard in
        // Program.cs is reset — a transient crash shouldn't slow down the *next* one.
        private static readonly TimeSpan StableUptime = TimeSpan.FromSeconds(60);
        private readonly DateTime _startedAtUtc = DateTime.UtcNow;
        private bool _crashGuardCleared;

        // ── Value cache (skip repaint when text unchanged) ────────────────────
        private string _cPos = "", _cSpd = "", _cHdg = "", _cWSpd = "", _cWDir = "";

        // ── CardPanel: fills background from Palette at paint-time ───────────
        private sealed class CardPanel : Panel
        {
            // ResizeRedraw = true: without it, this control (custom Paint-drawn border sized
            // off its own current Width/Height) isn't guaranteed to repaint after a resize —
            // any custom-painted content computed from Size can be left stale at whatever size
            // it last painted at. Cards that only get sized once via a Percent-based
            // TableLayoutPanel row rarely surface this; the ROLL/PITCH/HEAVE mini tiles do
            // (nested 2 TableLayoutPanels deep, cascading multiple resize passes during initial
            // layout) — their bottom border was being drawn short because the border Paint ran
            // before the card's final settled height, with no repaint forced afterward.
            public CardPanel() { DoubleBuffered = true; ResizeRedraw = true; }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using var brush = new SolidBrush(Palette.CardFace);
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }

        // Plain Panel does NOT double-buffer by default. A panel that hosts a child whose Text
        // gets reassigned on a timer (e.g. the mini Targets card's row/header panels — see
        // BuildMiniTargetsCard) still shows the child's repaint as two visibly separate erase-
        // then-draw passes unless the PARENT panel is also double-buffered, not just the child
        // Label — same root cause TrendsForm's BufferedPanel/BufferedFlowPanel were added for
        // ("Double-buffering only the leaf Label wasn't enough... all 3 levels that host a
        // readout value need to double-buffer"). No custom OnPaintBackground override needed
        // here (unlike CardPanel/ClockLabel below) — Panel's own default background painting is
        // fine once double-buffered; it's the two-pass erase/draw split that caused the flicker.
        private sealed class BufferedPanel : Panel
        {
            public BufferedPanel() { DoubleBuffered = true; }
        }

        // Plain Label repaints via a separate erase (WM_ERASEBKGND) then draw (WM_PAINT) pass —
        // for a control whose Text changes on its own every second (the clock), that two-step
        // repaint can show as a visible flash. Same fix as CardPanel: double buffer + paint the
        // background ourselves in one pass instead of letting the default erase run first.
        private sealed class ClockLabel : Label
        {
            public ClockLabel() { DoubleBuffered = true; }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using var brush = new SolidBrush(BackColor);
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Palette.ApplyTitleBarTheme(Handle);
        }

        // ═════════════════════════════════════════════════════════════════════
        public MainForm()
        {
            Text           = "CONNING MONITOR";
            WindowState    = FormWindowState.Maximized;
            MinimumSize    = new Size(900, 550);
            BackColor      = Palette.AppBg;
            DoubleBuffered = true;

            var cfg = ConfigService.Load();
            SystemConfig.Apply(cfg);
            Palette.IsLight = SystemConfig.IsLightTheme;

            if (cfg.Tasks != null)
                foreach (var saved in cfg.Tasks)
                {
                    var t = ConfigForm.Tasks.Find(x => x.TaskName == saved.TaskName);
                    if (t != null)
                    {
                        t.PortName     = saved.PortName;
                        if (saved.BaudRate > 0) t.BaudRate = saved.BaudRate;
                        t.SentenceType = saved.SentenceType;
                    }
                }

            SystemConfig.ThemeChanged += ApplyTheme;
            BuildLayout();
            InitServices();
            StartWatchdog();
            Task.Run(CheckNtpSync);
        }

        // ── Reliability: watchdog + NTP ──────────────────────────────────────
        private void StartWatchdog()
        {
            _lastUiTickTicks = DateTime.UtcNow.Ticks;
            _watchdogRunning = true;
            _watchdogThread  = new Thread(WatchdogLoop) { IsBackground = true, Name = "AppWatchdog" };
            _watchdogThread.Start();
        }

        private void WatchdogLoop()
        {
            while (_watchdogRunning)
            {
                Thread.Sleep(5000);
                long last = Interlocked.Read(ref _lastUiTickTicks);
                var  age  = DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc);
                if (age.TotalSeconds > 15)
                {
                    SystemLogger.LogInfo($"[Watchdog] UI frozen for {age.TotalSeconds:0}s, restarting");
                    Program.RestartApp();
                }
            }
        }

        private static void CheckNtpSync()
        {
            try
            {
                var psi = new ProcessStartInfo("w32tm", "/query /status")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow          = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                SystemLogger.LogInfo($"[NTP] {output.Replace("\r\n", " | ").Trim()}");
            }
            catch (Exception ex) { SystemLogger.LogError("CheckNtpSync", ex); }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  LAYOUT
        // ═════════════════════════════════════════════════════════════════════
        private void BuildLayout()
        {
            var root = new Panel { Dock = DockStyle.Fill, BackColor = Palette.AppBg };

            // Main split: 27% left | 73% right
            var mainTlp = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 2,
                RowCount    = 1,
                BackColor   = Palette.AppBg
            };
            mainTlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27F));
            mainTlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 73F));
            mainTlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainTlp.Controls.Add(BuildLeftPanel(),  0, 0);
            mainTlp.Controls.Add(BuildRightPanel(), 1, 0);

            root.Controls.Add(mainTlp);
            root.Controls.Add(BuildBottomBar());
            root.Controls.Add(BuildTopBar());
            Controls.Add(root);
        }

        // Panel doesn't expose DoubleBuffered publicly (it's protected on Control) — the clock
        // label inside repaints every second, so its container should also be double buffered
        // to avoid contributing to the same erase-then-draw flash (see ClockLabel above).
        private static readonly System.Reflection.PropertyInfo _dblBufProp =
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        private static void SetDoubleBuffered(Control c) => _dblBufProp.SetValue(c, true);

        // ── Top bar: alarm badge + connection badges + clock ──────────────────
        private Panel BuildTopBar()
        {
            var bar = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 44,
                BackColor = Palette.PanelBg
            };
            SetDoubleBuffered(bar);

            _lblClock = new ClockLabel
            {
                AutoSize  = false,
                Width     = 130,
                Dock      = DockStyle.Right,
                TextAlign = ContentAlignment.MiddleCenter,
                Font      = new Font("Segoe UI", 12, FontStyle.Bold),
                BackColor = Palette.PanelBg,
                ForeColor = Palette.TextValue,
                Padding   = new Padding(0, 0, 10, 0)
            };

            var flow = new FlowLayoutPanel
            {
                Dock         = DockStyle.Fill,
                BackColor    = Palette.PanelBg,
                Padding      = new Padding(8, 7, 0, 7),
                WrapContents = false
            };

            _badgeAlarm  = MakeTopBadge("✔ NORMAL",   Palette.AlarmNormalBg, Palette.AlarmNormalFg, 140);
            _badgeGps    = MakeTopBadge("GPS: WAIT",   Palette.WaitBg,        Palette.WaitFg,        150);
            _badgeWind   = MakeTopBadge("WIND: WAIT",  Palette.WaitBg,        Palette.WaitFg,        120);
            _badgeMotion = MakeTopBadge("R/P/H: WAIT", Palette.WaitBg,        Palette.WaitFg,        125);
            _badgeHdg    = MakeTopBadge("HDG: WAIT",   Palette.WaitBg,        Palette.WaitFg,        105);

            flow.Controls.AddRange(new Control[] { _badgeAlarm, _badgeGps, _badgeWind, _badgeMotion, _badgeHdg });

            bar.Controls.Add(_lblClock);
            bar.Controls.Add(flow);
            return bar;
        }

        private static Label MakeTopBadge(string text, Color bg, Color fg, int width)
        {
            var lbl = new Label
            {
                Text      = text,
                AutoSize  = false,
                Size      = new Size(width, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = bg,
                ForeColor = fg,
                Margin    = new Padding(0, 0, 4, 0)
            };
            lbl.Paint += (s, e) =>
            {
                using var pen = new Pen(Palette.BorderCard, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, lbl.Width - 1, lbl.Height - 1);
            };
            return lbl;
        }

        // ── Bottom bar: SETTINGS + DATA LIST ──────────────────────────────────
        // TARGETS and RADAR/TRENDS no longer have buttons here — they're opened by clicking
        // the mini Targets/Radar cards in the right-side strip instead (see BuildMiniSidePanel),
        // since those cards already show a live condensed view of the same windows.
        private Panel BuildBottomBar()
        {
            var bar = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 52,
                BackColor = Palette.PanelBg,
                Padding   = new Padding(8, 7, 8, 7)
            };

            var btnSettings = MakeButton("⚙  SETTINGS", Palette.BtnSettingsBg, Palette.BtnSettingsFg);
            btnSettings.Location = new Point(10, 7);
            btnSettings.Width    = 150;
            btnSettings.Click   += BtnSettings_Click;

            var btnScan = MakeButton("📋  DATA LIST", Palette.BtnPrimaryBg, Palette.BtnPrimaryFg);
            btnScan.Location = new Point(170, 7);
            btnScan.Width    = 160;
            btnScan.Click   += (s, e) =>
            {
                if (_dataListForm == null || _dataListForm.IsDisposed)
                {
                    _dataListForm = new DataListForm();
                    _dataListForm.Show(this);
                }
                else _dataListForm.BringToFront();
            };

            bar.Controls.Add(btnSettings);
            bar.Controls.Add(btnScan);
            return bar;
        }

        private void OpenTargetsForm()
        {
            if (_targetsForm == null || _targetsForm.IsDisposed)
            {
                _targetsForm = new TargetsForm();
                _targetsForm.Show(this);
            }
            else _targetsForm.BringToFront();
        }

        private void OpenTrendsForm()
        {
            if (_trendsForm == null || _trendsForm.IsDisposed)
            {
                _trendsForm = new TrendsForm();
                _trendsForm.Show(this);
            }
            else _trendsForm.BringToFront();
        }

        // ── Left panel: ship name + 2-column card grid ────────────────────────
        private Panel BuildLeftPanel()
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Palette.CardBg };

            string ship = SystemConfig.ShipName ?? "CONNING MONITOR";
            var lblShip = new Label
            {
                Text      = ship,
                Dock      = DockStyle.Top,
                Height    = 38,
                TextAlign = ContentAlignment.MiddleCenter,
                Font      = new Font("Segoe UI", 13, FontStyle.Bold),
                BackColor = Palette.SectionHdrBg,
                ForeColor = Palette.TextValue
            };

            var grid = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 2,
                RowCount    = 3,
                BackColor   = Palette.CardBg,
                Padding     = new Padding(6, 6, 6, 4)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 34F)); // POSITION
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33F)); // SPEED / HEADING
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33F)); // WIND SPD / WIND DIR

            // POSITION — full width. Click anywhere on the card to toggle lat/lon ⇄ UTM
            // (MERAN2-style dual display). Accent-colored "⇄" hint + tooltip signal it's
            // clickable without recoloring the whole title.
            var cardPos = BuildDataCard("POSITION", "", isMultiLine: true, out _lblPosition, out _, out var titlePos, clickable: true);
            grid.Controls.Add(cardPos, 0, 0);
            grid.SetColumnSpan(cardPos, 2);
            MakeCardClickable(cardPos, titlePos, _lblPosition, null,
                () => { _showUtm = !_showUtm; _cPos = ""; },
                "Click to toggle Lat/Lon ⇄ UTM");

            // SPEED | HEADING — click anywhere on SPEED to toggle knots ⇄ m/s.
            var cardSpeed = BuildDataCard("SPEED", "kn", isMultiLine: false, out _lblSpeed, out _lblSpeedUnit, out var titleSpeed, clickable: true);
            grid.Controls.Add(cardSpeed, 0, 1);
            grid.Controls.Add(BuildDataCard("HEADING", "°", isMultiLine: false, out _lblHeading, out _, out _), 1, 1);
            MakeCardClickable(cardSpeed, titleSpeed, _lblSpeed, _lblSpeedUnit,
                () => { _showSpeedMs = !_showSpeedMs; _lblSpeedUnit.Text = _showSpeedMs ? "m/s" : "kn"; _cSpd = ""; },
                "Click to toggle kn ⇄ m/s");

            // WIND SPD | WIND DIR
            grid.Controls.Add(BuildDataCard("WIND SPD", "m/s", isMultiLine: false, out _lblWindSpd, out _, out _), 0, 2);
            grid.Controls.Add(BuildDataCard("WIND DIR", "°",   isMultiLine: false, out _lblWindDir, out _, out _),  1, 2);

            outer.Controls.Add(grid);
            outer.Controls.Add(lblShip);
            return outer;
        }

        private Panel BuildDataCard(string title, string unit, bool isMultiLine, out Label valLabel, out Label unitLabel, out Label titleLabel, bool clickable = false)
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(4) };

            var lblTitle = new Label
            {
                Text      = title,
                AutoSize  = false,
                Dock      = DockStyle.Top,
                Height    = 28,
                TextAlign = ContentAlignment.MiddleCenter,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Palette.TextLabel,
                BackColor = Color.Transparent
            };

            var lblVal = new Label
            {
                Text      = "---",
                AutoSize  = false,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font      = new Font("Segoe UI", isMultiLine ? 12f : 22f, FontStyle.Bold),
                ForeColor = Palette.OkFg,
                BackColor = Color.Transparent
            };

            bool hasUnit = !string.IsNullOrEmpty(unit);
            var lblUnit = new Label
            {
                Text      = unit,
                AutoSize  = false,
                Dock      = DockStyle.Bottom,
                Height    = hasUnit ? 24 : 0,
                TextAlign = ContentAlignment.TopCenter,
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Palette.TextLabel,
                BackColor = Color.Transparent
            };

            card.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
                using var path = RoundedRect(rect, 8);
                using var bpen = new Pen(Palette.BorderCard, 1.5f);
                g.DrawPath(bpen, path);
                using var sep = new Pen(Palette.BorderPanel, 1);
                g.DrawLine(sep, 8, lblTitle.Height, card.Width - 8, lblTitle.Height);

                // Click hint: a small pill badge anchored to the card's own top-right corner
                // (not the title/unit text) — same spot on every clickable card regardless of
                // whether that card even has a unit line, and reads as a mini button rather
                // than a stray glyph in the text. lblTitle's BackColor is Transparent so this
                // shows through underneath it.
                if (clickable)
                {
                    int badgeSize = (int)Math.Max(18f, Math.Min(card.Height * 0.16f, 26f));
                    const int margin = 6;
                    var badgeRect = new Rectangle(card.Width - badgeSize - margin, margin, badgeSize, badgeSize);
                    using var badgePath   = RoundedRect(badgeRect, badgeSize / 3);
                    using var badgeFill   = new SolidBrush(Color.FromArgb(45, Palette.ClickHint));
                    using var badgeBorder = new Pen(Palette.ClickHint, 1.3f);
                    g.FillPath(badgeFill, badgePath);
                    g.DrawPath(badgeBorder, badgePath);

                    using var iconFont = new Font("Segoe UI", badgeSize * 0.5f, FontStyle.Bold);
                    using var iconBrush = new SolidBrush(Palette.ClickHint);
                    using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("⇄", iconFont, iconBrush, badgeRect, fmt);
                }
            };

            card.Resize += (s, e) =>
            {
                if (card.Height < 10 || card.Width < 10) return;
                int h = card.Height, w = card.Width;

                int titleH = Math.Max(26, Math.Min((int)(h * 0.24f), 40));
                lblTitle.Height = titleH;
                SafeFont(lblTitle, Math.Max(9f, Math.Min(h * 0.095f, 15f)));

                if (hasUnit)
                {
                    int unitH = Math.Max(22, Math.Min((int)(h * 0.18f), 30));
                    lblUnit.Height = unitH;
                    SafeFont(lblUnit, Math.Max(10f, Math.Min(h * 0.100f, 16f)));
                }

                float vf = isMultiLine
                    ? Math.Max(10f, Math.Min(Math.Min(h * 0.15f, w * 0.068f), 22f))
                    : Math.Max(14f, Math.Min(Math.Min(h * 0.30f, w * 0.20f),  46f));
                SafeFont(lblVal, vf);
            };

            // Add in order: Fill last (added first = lowest dock priority)
            card.Controls.Add(lblVal);
            card.Controls.Add(lblUnit);
            card.Controls.Add(lblTitle);

            valLabel   = lblVal;
            unitLabel  = lblUnit;
            titleLabel = lblTitle;
            return card;
        }

        // Cursor + tooltip on every visible part of the card (background, title, value, unit)
        // so the whole card reads as clickable, not just whichever label happens to catch the
        // click — a card with only a hand cursor on its center number is easy to miss.
        private void MakeCardClickable(Panel? card, Label title, Label value, Label? unit, Action onClick, string tooltip)
        {
            void Wire(Control? c)
            {
                if (c == null) return;
                c.Cursor = Cursors.Hand;
                c.Click += (s, e) => onClick();
                _cardTip.SetToolTip(c, tooltip);
            }
            Wire(card);
            Wire(title);
            Wire(value);
            Wire(unit);
        }

        // ── Right panel: ConningControl ───────────────────────────────────────
        private Panel BuildRightPanel()
        {
            _conning = new ConningControl { Dock = DockStyle.Fill };
            var conningHost = new Panel { Dock = DockStyle.Fill, BackColor = Palette.AppBg };
            conningHost.Controls.Add(_conning);

            // Condensed motion tiles + targets strip beside the compass ring. A mini
            // RadarControl preview used to sit here too, but was removed 2026-08-28 — it
            // duplicated the RANGE/WIND/DRIFT corner readouts now drawn directly on
            // ConningControl and looked visually inconsistent next to it. A mini
            // TrendChartControl (R/P/H) was added just below the Radar/Trends button instead
            // — see BuildMiniTrendChart() — reversing an earlier "don't embed the chart here"
            // decision; that control still carries the DataVisualization gotchas documented
            // above (SqlClient workaround, manual ChartArea %), and running a second instance
            // alongside TrendsForm's own does cost extra render/CPU, but the user asked for it
            // directly and that tradeoff is now accepted.
            var split = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 2,
                RowCount    = 1,
                BackColor   = Palette.AppBg
            };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 76F));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24F));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            split.Controls.Add(conningHost,       0, 0);
            split.Controls.Add(BuildMiniSidePanel(), 1, 0);

            var pnl = new Panel { Dock = DockStyle.Fill, BackColor = Palette.AppBg };
            pnl.Controls.Add(split);
            return pnl;
        }

        // Tiles row (ROLL/PITCH/HEAVE) + a slim Radar/Trends button (mini radar picture
        // removed, see BuildRadarTrendsButtonRow) + mini Roll/Pitch/Heave trend chart + mini
        // targets list. Same live data the full TrendsForm/TargetsForm windows show, just
        // condensed; clicking the targets card opens the full window.
        private Panel BuildMiniSidePanel()
        {
            // BackColor = CardBg (not AppBg) so this whole strip reads as one unified panel,
            // same visual language as the left column (BuildLeftPanel's outer.BackColor is
            // CardBg too) instead of cards floating loose on the app background.
            var side = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 1,
                RowCount    = 4,
                BackColor   = Palette.CardBg,
                Padding     = new Padding(2)
            };
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F)); // ROLL/PITCH/HEAVE tiles
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F)); // Radar/Trends button (mini radar picture removed)
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));  // mini R/P/H trend chart
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));  // mini targets
            side.Controls.Add(BuildMiniMotionTiles(),        0, 0);
            side.Controls.Add(BuildRadarTrendsButtonRow(),   0, 1);
            side.Controls.Add(BuildMiniTrendChart(),         0, 2);
            side.Controls.Add(BuildMiniTargetsCard(),        0, 3);
            return side;
        }

        // Roll/Pitch/Heave motion trend, locked to Motion mode (no mode-switch/time-window
        // toolbar — that lives in the full TrendsForm window; this is a glance-only preview).
        // User explicitly asked for this despite the earlier "don't embed TrendChartControl
        // here" decision (see BuildRightPanel's comment) — the double render/CPU cost of a
        // second Chart instance alongside TrendsForm's own is now an accepted tradeoff.
        private Panel BuildMiniTrendChart()
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(3) };
            _miniTrend = new TrendChartControl { Dock = DockStyle.Fill };
            // Shorter than TrendsForm's own 1-minute default — this panel is much narrower, so
            // 1 minute of points read as a dense, hard-to-follow squiggle; 30s spreads the same
            // ~100ms-cadence data across fewer on-screen points, making the lines easier to read.
            _miniTrend.SetViewWindow(0.5);
            card.Controls.Add(_miniTrend);
            return card;
        }

        // Two custom from-scratch attempts at these 3 tiles (a ClockLabel-based value label,
        // then a plain Label copied by hand from BuildDataCard's recipe) both rendered blank —
        // rather than guess at a third hand-written variant, this now calls BuildDataCard()
        // itself, the exact method already proven live for POSITION/SPEED/HEADING/WIND (same
        // CardPanel, same Resize-driven font scaling, same Paint-drawn border) instead of a
        // parallel reimplementation of it.
        private Panel BuildMiniMotionTiles()
        {
            var row = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                ColumnCount = 3,
                RowCount    = 1,
                BackColor   = Palette.CardBg
            };
            for (int i = 0; i < 3; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));

            var rollCard = BuildDataCard("ROLL", "", isMultiLine: false, out _miniRollVal, out _, out _);
            rollCard.Margin = new Padding(2);
            row.Controls.Add(rollCard, 0, 0);

            var pitchCard = BuildDataCard("PITCH", "", isMultiLine: false, out _miniPitchVal, out _, out _);
            pitchCard.Margin = new Padding(2);
            row.Controls.Add(pitchCard, 1, 0);

            var heaveCard = BuildDataCard("HEAVE", "", isMultiLine: false, out _miniHeaveVal, out _, out _);
            heaveCard.Margin = new Padding(2);
            row.Controls.Add(heaveCard, 2, 0);

            // Color-code to match the corresponding series on the trend chart, same idea as
            // the axis-legend colors on ConningControl — BuildDataCard defaults to Palette.OkFg.
            _miniRollVal.ForeColor  = Palette.SeriesRoll;
            _miniPitchVal.ForeColor = Palette.SeriesPitch;
            _miniHeaveVal.ForeColor = Palette.SeriesHeave;

            return row;
        }

        // Slim button-only row — the mini RadarControl preview that used to live here was
        // removed 2026-08-28 (user found it redundant/confusing next to ConningControl, which
        // now carries the RANGE/DRIFT corner readouts that used to be this control's reason for
        // existing — ZOOM was merged into RANGE 2026-09-03, WIND corner removed earlier). Text/
        // tooltip changed from "RADAR / TRENDS" to "TRENDS" the same day RadarControl was deleted
        // and its role fully absorbed into ConningControl — this button now only opens a chart
        // window, no radar in it anymore (see TrendsForm.cs).
        private Button BuildRadarTrendsButtonRow()
        {
            var btn = MakeButton("TRENDS  ⤢", Palette.BtnPrimaryBg, Palette.BtnPrimaryFg);
            btn.Dock = DockStyle.Fill;
            btn.Margin = new Padding(3);
            btn.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            btn.Click += (s, e) => OpenTrendsForm();
            _cardTip.SetToolTip(btn, "Open Trends");
            return btn;
        }

        private Panel BuildMiniTargetsCard()
        {
            var card = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(3) };

            var btn = MakeButton("TARGETS  ⤢", Palette.BtnPrimaryBg, Palette.BtnPrimaryFg);
            btn.Dock   = DockStyle.Top;
            btn.Height = 28;
            btn.Font   = new Font("Segoe UI", 8f, FontStyle.Bold);
            btn.Click += (s, e) => OpenTargetsForm();
            _cardTip.SetToolTip(btn, "Open Targets");

            // BUG FIX (2026-09-03, user report "phần target ở góc phải hiển thị bị nhấp nháy"):
            // this whole card's Text is reassigned every ~500ms by RefreshMiniTargets (bearing/
            // distance genuinely change tick-to-tick as the ship moves — no identical-value no-op
            // to fall back on), but every Label/Panel in it was a plain, non-double-buffered
            // control with Transparent BackColor — the exact flicker root cause already fixed
            // elsewhere in this file (ClockLabel) and in TrendsForm (ReadoutLabel/BufferedPanel/
            // BufferedFlowPanel: "Panel/FlowLayoutPanel do NOT double-buffer by default... all
            // levels that host a reassigned value need to double-buffer"). ClockLabel here reuses
            // that same double-buffer-plus-self-painted-background recipe; BackColor is set to
            // this label's actual visual background (CardFace, since it sits directly on `card`)
            // instead of Transparent, since ClockLabel's OnPaintBackground fills a literal color.
            _miniDriftLbl = new ClockLabel
            {
                Text        = "",
                Dock        = DockStyle.Top,
                Height      = 16,
                TextAlign   = ContentAlignment.MiddleLeft,
                Font        = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor   = Palette.TextDim,
                BackColor   = Palette.CardFace,
                Padding     = new Padding(6, 0, 0, 0),
                AutoEllipsis = true
            };

            // Growable row pool (2026-09-03, was a fixed 4-row loop — SystemConfig.Targets can now
            // hold up to 300) — see EnsureMiniTargetRow for how new rows get appended below the
            // existing ones as they're needed. AutoScroll lets the card show more rows than fit
            // its visible height (up to 300 targets, a percent-height side panel) instead of
            // clipping them — per user request "list có scroll down ở trong ... mainform luôn".
            var listPanel = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Palette.CardBg, Padding = new Padding(6, 2, 6, 0), AutoScroll = true };
            _miniTargetsListPanel = listPanel;
            for (int i = 0; i < 4; i++) EnsureMiniTargetRow(i);

            // Column header — same Left/Fill/Right widths as the data rows above so NAME/BRG/DIST
            // line up exactly with the values underneath. Added last (after the row loop) so it
            // ends up the topmost Dock=Top sibling, above row index 0.
            var colHeader = new BufferedPanel { Dock = DockStyle.Top, Height = 18, BackColor = Palette.CardBg };
            colHeader.Paint += (s, e) =>
            {
                using var pen = new Pen(Palette.BorderCard, 1f);
                e.Graphics.DrawLine(pen, 0, colHeader.Height - 1, colHeader.Width, colHeader.Height - 1);
            };
            var hName = new Label { Dock = DockStyle.Left, Width = 76, AutoSize = false, Text = "NAME", TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 7f, FontStyle.Bold), ForeColor = Palette.TextDim, BackColor = Color.Transparent };
            var hDist = new Label { Dock = DockStyle.Right, Width = 62, AutoSize = false, Text = "DIST", TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI", 7f, FontStyle.Bold), ForeColor = Palette.TextDim, BackColor = Color.Transparent };
            var hBrg  = new Label { Dock = DockStyle.Fill, AutoSize = false, Text = "BRG", TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 7f, FontStyle.Bold), ForeColor = Palette.TextDim, BackColor = Color.Transparent };
            colHeader.Controls.Add(hBrg);
            colHeader.Controls.Add(hDist);
            colHeader.Controls.Add(hName);
            listPanel.Controls.Add(colHeader);

            card.Controls.Add(listPanel);
            card.Controls.Add(_miniDriftLbl);
            card.Controls.Add(btn);
            // Body still opens the same window on click — the button above is the explicit,
            // unmistakable affordance; this is just a bonus shortcut, not a replacement for it.
            WireOpenClick(card, "Click to open Targets", OpenTargetsForm, _miniDriftLbl, listPanel);
            return card;
        }

        // Builds row `index` (Panel + 3 labels) into _miniTargetRows/_miniTargetsListPanel if it
        // doesn't already exist — the pool only ever grows to however many rows have actually
        // been needed so far (RefreshMiniTargets calls this for each enabled target it's about to
        // show), never pre-builds all the way to MaxTargets up front.
        //
        // Z-ORDER: appending via Controls.Add() alone would dock the new row ABOVE every existing
        // one (WinForms docks Dock=Top siblings in reverse Controls order — the newest-appended
        // control gets the highest index, and the highest index docks FIRST/topmost). Calling
        // BringToFront() right after Add() moves the new row to Controls index 0 instead, which
        // makes it dock LAST — i.e. at the BOTTOM of whatever's already stacked — exactly where a
        // newly-needed row (the next target down the list) belongs. colHeader, added once before
        // any dynamic growth and never touched again, keeps the highest index (and thus stays
        // visually topmost) throughout.
        private void EnsureMiniTargetRow(int index)
        {
            while (_miniTargetRows.Count <= index)
            {
                // BUG FIX (2026-09-03, right after the flicker fix above): nameLbl/brgLbl/distLbl
                // used to be Transparent, so this row's own Paint-drawn bottom border line showed
                // straight through them. Switching those Labels to ClockLabel (opaque — it fills
                // a literal BackColor in OnPaintBackground, no transparency support) means they
                // now paint solid pixels over their FULL docked bounds, which for Dock=Left/Right/
                // Fill children is the row's entire height — completely hiding the border line
                // underneath (user report: "sao mất luôn các đường kẻ của bảng"). Padding(0,0,0,1)
                // insets every docked child by 1px at the bottom, leaving that pixel row as the
                // row Panel's own (unpainted-over) background, where the border line lives.
                var row = new BufferedPanel { Dock = DockStyle.Top, Height = 36, BackColor = Palette.CardBg, Visible = false, Padding = new Padding(0, 0, 0, 1) };
                row.Paint += (s, e) =>
                {
                    using var pen = new Pen(Palette.BorderPanel, 1f);
                    e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1);
                };

                // ClockLabel (not plain Label) — see the flicker-fix comment on _miniDriftLbl
                // above; BackColor matches this label's actual parent (row/listPanel = CardBg).
                var nameLbl = new ClockLabel
                {
                    Dock = DockStyle.Left, Width = 76, AutoSize = false,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    ForeColor = Palette.TextValue, BackColor = Palette.CardBg
                };
                var distLbl = new ClockLabel
                {
                    Dock = DockStyle.Right, Width = 62, AutoSize = false,
                    TextAlign = ContentAlignment.MiddleRight,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    ForeColor = Palette.TextDim, BackColor = Palette.CardBg
                };
                var brgLbl = new ClockLabel
                {
                    Dock = DockStyle.Fill, AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    ForeColor = Palette.ClickHint, BackColor = Palette.CardBg
                };
                row.Controls.Add(brgLbl);
                row.Controls.Add(distLbl);
                row.Controls.Add(nameLbl);

                _miniTargetRows.Add((row, nameLbl, brgLbl, distLbl));
                _miniTargetsListPanel.Controls.Add(row);
                row.BringToFront();
            }
        }

        // Wires Click (+ hand cursor + tooltip) onto a mini card and its meaningful children —
        // DataGridView/RadarControl aren't Labels so this can't reuse MakeCardClickable (which
        // is typed to the title/value/unit Label triplet the POSITION/SPEED cards use).
        private void WireOpenClick(Control card, string tooltip, Action onClick, params Control[] extra)
        {
            void Wire(Control c)
            {
                c.Cursor = Cursors.Hand;
                c.Click += (s, e) => onClick();
                _cardTip.SetToolTip(c, tooltip);
            }
            Wire(card);
            foreach (var c in extra) Wire(c);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SERVICES
        // ═════════════════════════════════════════════════════════════════════
        private void InitServices()
        {
            _logger      = new DataLogger();
            _alarmEngine = new AlarmEngine();

            _windTag  = new Tag("WindSpeed");
            _rollTag  = new Tag("Roll");
            _pitchTag = new Tag("Pitch");
            _heaveTag = new Tag("Heave");
            _driftTag  = new Tag("DriftDistance");
            _gpsDuoTag = new Tag("GpsDuoDivergence");
            _alarmEngine.Register(new Alarm("AL_WIND",   _windTag,   () => SystemConfig.WindMax,  () => SystemConfig.WindConfirmSeconds));
            _alarmEngine.Register(new Alarm("AL_ROLL",   _rollTag,   () => SystemConfig.RMax,     () => SystemConfig.MotionConfirmSeconds));
            _alarmEngine.Register(new Alarm("AL_PITCH",  _pitchTag,  () => SystemConfig.PMax,     () => SystemConfig.MotionConfirmSeconds));
            _alarmEngine.Register(new Alarm("AL_HEAVE",  _heaveTag,  () => SystemConfig.HMax,     () => SystemConfig.MotionConfirmSeconds));
            _alarmEngine.Register(new Alarm("AL_DRIFT",  _driftTag,  () => SystemConfig.DriftRadiusM));
            _alarmEngine.Register(new Alarm("AL_GPSDUO", _gpsDuoTag, () => SystemConfig.GpsDuoDivergenceM));
            _alarmEngine.AlarmRaised  += OnAlarmRaised;
            _alarmEngine.AlarmCleared += OnAlarmCleared;
            _alarmEngine.AlarmAcked   += OnAlarmAcked;

            _nmeaParser = new NmeaParserService { HeaveArm = HeaveArm };
            _nmeaParser.SetPortTasks(ConfigForm.Tasks);
            _nmeaParser.OnHeadingParsed  += v      => ConningDataHub.Instance.UpdateNumericData("HEADING", v);
            _nmeaParser.OnWindParsed     += (s, d) => { ConningDataHub.Instance.UpdateNumericData("WIND", s, d); _windTag.Update(s); };
            // Roll/Pitch/Heave sole source is MruService (Xsens XBus binary) — see below.
            // NmeaParserService still parses $CNTB/$PRDID/$PASHR/$PHTRO for backward compat
            // if a legacy NMEA AHRS ever needs to be reconnected, but OnMotionParsed is not
            // subscribed here.
            _nmeaParser.OnPositionParsed += (port, lat, lon) =>
            {
                var src = GpsSourceForPort(port);
                if (src == null) return;
                src.LatStr = lat; src.LonStr = lon; src.LastUpdate = DateTime.Now;
                PublishActiveGps();
            };
            _nmeaParser.OnPositionRawParsed += (port, latDeg, lonDeg) =>
            {
                var src = GpsSourceForPort(port);
                if (src == null) return;
                src.LatDeg = latDeg; src.LonDeg = lonDeg; src.LastUpdate = DateTime.Now;
                PublishActiveGps();
            };
            _nmeaParser.OnSpeedParsed += (port, k) =>
            {
                var src = GpsSourceForPort(port);
                if (src == null) return;
                src.SpeedKnot = k; src.LastUpdate = DateTime.Now;
                PublishActiveGps();
            };
            _nmeaParser.OnCogParsed += (port, cog) =>
            {
                if (GpsSourceForPort(port) == ActiveGpsSource()) ConningDataHub.Instance.UpdateCog(cog);
            };
            _nmeaParser.OnGpsQualityParsed += (port, q) =>
            {
                var src = GpsSourceForPort(port);
                if (src == null) return;
                src.Quality = q; src.QualityText = GpsQualityText(q); src.LastUpdate = DateTime.Now;
                SelectActiveGpsSource();
                PublishActiveGps();
            };
            _nmeaParser.OnSatellitesParsed += (constellation, count, avgSnr) => ConningDataHub.Instance.UpdateSatellites(constellation, count, avgSnr);
            _nmeaParser.OnGsaParsed += (fixType, pdop, hdop, vdop) => ConningDataHub.Instance.UpdateGsa(fixType, pdop, hdop, vdop);

            _comEngine = new ComEngine();
            _comEngine.OnDataReceived += OnComData;

            if (SystemConfig.IsSimulationMode)
            {
                Text += "  [SIMULATION]";
                new SimulationEngine().Start(OnComData);
            }
            else
            {
                var taskList = new List<DeviceTask>();
                foreach (var t in ConfigForm.Tasks)
                    taskList.Add(new DeviceTask { TaskName = t.TaskName, PortName = t.PortName, BaudRate = t.BaudRate, SentenceType = t.SentenceType });
                // SerialPort.Open() is a blocking OS call — opening 5 ports sequentially here
                // (in the MainForm constructor, before the window is even shown) can stall
                // startup for seconds if a configured port is missing/slow to enumerate.
                // ComEngine is already thread-safe (internal locks), so hand this off.
                Task.Run(() => _comEngine.Initialize(taskList));
            }

            var meteoTask = ConfigForm.Tasks.Find(t => t.TaskName == "METEO");
            if (meteoTask != null)
            {
                _meteoService = new MeteoService(meteoTask.PortName, meteoTask.BaudRate, _comEngine);
                _meteoService.Start();
            }

            var mruTask = ConfigForm.Tasks.Find(t => t.TaskName == "MRU");
            if (mruTask != null)
            {
                _mruService = new MruService(mruTask.PortName, mruTask.BaudRate, _comEngine);
                _mruService.OnMotionParsed += (r, p, h) =>
                {
                    double ar = Math.Abs(r), ap = Math.Abs(p);
                    _rawHeaveCm = h;
                    _rollTag.Update(ar); _pitchTag.Update(ap);
                    // Washout filter overshoots several times the true amplitude for ~15-30s
                    // after each MRU (re)connect — don't feed AL_HEAVE until it's settled.
                    // Displayed heave (snap.HeaveCm) is unaffected: it comes from ConningDataHub,
                    // not this Tag.
                    if (_mruService.Processor.IsHeaveSettled) _heaveTag.Update(Math.Abs(h));
                };
                _mruService.Start();
            }

            FormClosed += (s, e) =>
            {
                _watchdogRunning = false;
                SystemConfig.ThemeChanged -= ApplyTheme;
                _uiTimer.Stop();     _uiTimer.Dispose();
                _healthTimer.Stop(); _healthTimer.Dispose();
                _logTimer.Stop();    _logTimer.Dispose();
                _mruService?.SendGoToConfig();
                _mruService?.Dispose();
                _meteoService?.Stop();
                _meteoService?.Dispose();
                _comEngine?.Dispose();
                _logger?.Dispose();
            };

            _uiTimer     = new System.Windows.Forms.Timer { Interval = 100  };
            _healthTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _logTimer    = new System.Windows.Forms.Timer { Interval = 1000 };
            _uiTimer.Tick     += UiTick;
            _healthTimer.Tick += HealthTick;
            _logTimer.Tick    += LogTick;
            _uiTimer.Start(); _healthTimer.Start(); _logTimer.Start();
        }

        private void OnComData(string port, string raw)
        {
            var task = ConfigForm.Tasks.Find(t => t.PortName == port);
            if (task != null) ConningDataHub.Instance.UpdateRawString(task.TaskName, raw.Trim());
            _nmeaParser?.Parse(port, raw);
        }

        // ── DUO GPS: source lookup, selection, publish ────────────────────────
        private GpsSource? GpsSourceForPort(string portName)
        {
            var t = ConfigForm.Tasks.Find(x => x.PortName == portName);
            if (t == null) return null;
            if (t.TaskName == "GPS")  return _gps1;
            if (t.TaskName == "GPS2") return _gps2;
            return null;
        }

        private GpsSource ActiveGpsSource() => _activeGpsSource == 2 ? _gps2 : _gps1;

        // RTK fixed > RTK float > DGPS > GPS/PPS; estimated/manual/simulation (6/7/8) and
        // invalid (0) are never preferred over a live fix from the other receiver.
        private static int QualityRank(int q) => q switch
        {
            4 => 4,
            5 => 3,
            2 => 2,
            1 or 3 => 1,
            _ => -1,
        };

        private void SelectActiveGpsSource()
        {
            if (!SystemConfig.DuoGpsEnabled) { _activeGpsSource = 1; return; }

            bool s1Ok = _gps1.HasFix, s2Ok = _gps2.HasFix;
            if (!s1Ok && !s2Ok) return; // neither usable — keep last selection
            if (!s1Ok) { _activeGpsSource = 2; return; }
            if (!s2Ok) { _activeGpsSource = 1; return; }

            int r1 = QualityRank(_gps1.Quality), r2 = QualityRank(_gps2.Quality);
            if (r1 != r2) _activeGpsSource = r1 > r2 ? 1 : 2;
            // else: tie — keep current source (sticky, avoids flip-flopping on equal quality)
        }

        private void PublishActiveGps()
        {
            var src = ActiveGpsSource();
            ConningDataHub.Instance.UpdateGpsData(src.SpeedKnot, src.LatStr, src.LonStr);
            if (!double.IsNaN(src.LatDeg) && !double.IsNaN(src.LonDeg))
                ConningDataHub.Instance.UpdateGpsRaw(src.LatDeg, src.LonDeg);
            ConningDataHub.Instance.UpdateGpsFixQuality(
                SystemConfig.DuoGpsEnabled ? $"{src.QualityText}·{_activeGpsSource}" : src.QualityText);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  TICK HANDLERS
        // ═════════════════════════════════════════════════════════════════════
        private const double KnotToMs = 0.514444;

        private void UiTick(object? sender, EventArgs e)
        {
            Interlocked.Exchange(ref _lastUiTickTicks, DateTime.UtcNow.Ticks);
            var snap = ConningDataHub.Instance.GetSnapshot();
            bool hasFix = !double.IsNaN(snap.GpsLatDeg) && !double.IsNaN(snap.GpsLonDeg);

            _driftTag.Update(SystemConfig.DriftWatchEnabled && hasFix
                ? GeoMath.DistanceMeters(SystemConfig.DriftRefLat, SystemConfig.DriftRefLon, snap.GpsLatDeg, snap.GpsLonDeg)
                : 0);
            _gpsDuoTag.Update(SystemConfig.DuoGpsEnabled && _gps1.HasFix && _gps2.HasFix
                ? GeoMath.DistanceMeters(_gps1.LatDeg, _gps1.LonDeg, _gps2.LatDeg, _gps2.LonDeg)
                : 0);
            _alarmEngine.Evaluate();

            // Clock
            string clk = DateTime.Now.ToString("HH:mm:ss");
            if (_lblClock.Text != clk) _lblClock.Text = clk;

            // Position — lat/lon or UTM (toggled by clicking the POSITION card)
            string pos;
            if (_showUtm && hasFix)
            {
                var utm = UtmConverter.ToUtm(snap.GpsLatDeg, snap.GpsLonDeg);
                pos = $"{utm.Zone}{utm.Band}\n{utm.Easting:0}mE  {utm.Northing:0}mN";
            }
            else
            {
                pos = snap.GpsLat == "NO FIX" ? "NO FIX" : $"{snap.GpsLat}\n{snap.GpsLon}";
            }
            if (pos != _cPos) { _lblPosition.Text = pos; _cPos = pos; }

            // Speed — knots or m/s (toggled by clicking the SPEED card)
            string spd = _showSpeedMs ? $"{snap.GpsSpeedKnot * KnotToMs:0.00}" : $"{snap.GpsSpeedKnot:0.0}";
            if (spd != _cSpd) { _lblSpeed.Text = spd; _cSpd = spd; }

            // Heading
            string hdg = $"{snap.Heading:000.0}";
            if (hdg != _cHdg) { _lblHeading.Text = hdg; _cHdg = hdg; }

            // Wind
            string wspd = $"{snap.WindSpeedMs:0.0}";
            string wdir = $"{snap.WindDirDeg:000}";
            if (wspd != _cWSpd) { _lblWindSpd.Text = wspd; _cWSpd = wspd; }
            if (wdir != _cWDir) { _lblWindDir.Text = wdir; _cWDir = wdir; }

            // Mini ROLL/PITCH/HEAVE tiles (side panel)
            string mr = $"{snap.RollDeg:0.0}";
            string mp = $"{snap.PitchDeg:0.0}";
            string mh = $"{snap.HeaveCm:0.0}";
            if (mr != _cMiniRoll)  { _miniRollVal.Text  = mr; _cMiniRoll  = mr; }
            if (mp != _cMiniPitch) { _miniPitchVal.Text = mp; _cMiniPitch = mp; }
            if (mh != _cMiniHeave) { _miniHeaveVal.Text = mh; _cMiniHeave = mh; }
            _miniTrend.PushMotionData(snap.RollDeg, snap.PitchDeg, snap.HeaveCm);
            _miniTrend.Render();

            // ConningControl — carries the RANGE/DRIFT corner readouts (ZOOM merged into RANGE
            // 2026-09-03, WIND corner removed earlier), own-ship track, and target shapes that
            // used to live in the mini radar card / RadarControl (both removed 2026-08-28 — see
            // CLAUDE.md "Radar/Trends window").
            // UpdatePosition() must land before Update() — Update() stamps the trail sample it
            // records with whatever lat/lon UpdatePosition last set, for the movement-afterimage.
            _conning.UpdatePosition(snap.GpsLatDeg, snap.GpsLonDeg);
            _conning.Update(snap.Heading, snap.CogDeg, snap.GpsSpeedKnot, snap.RotDegMin,
                            snap.WindDirDeg, snap.WindSpeedMs, snap.HeaveCm,
                            SystemConfig.Loa, SystemConfig.Lob, SystemConfig.GpsOffset);
            if (hasFix) _conning.PushTrackPoint(snap.GpsLatDeg, snap.GpsLonDeg);
            // Drift/residual corner — reuses _driftTag.Value (already computed above this tick)
            // for distance, adds a bearing from the reference point to the current fix so the
            // corner readout shows a direction too, not just a magnitude.
            bool driftShown = SystemConfig.DriftWatchEnabled && hasFix;
            double driftBrg = driftShown
                ? GeoMath.BearingDeg(SystemConfig.DriftRefLat, SystemConfig.DriftRefLon, snap.GpsLatDeg, snap.GpsLonDeg)
                : 0;
            _conning.UpdateDrift(driftShown, driftBrg, _driftTag.Value);
            _conning.Invalidate();

            // Mini targets — throttled to ~500ms (same cadence as TargetsForm's own timer);
            // DataGridView isn't double-buffered, so a full Rows.Clear()/Add() churn every
            // 100ms tick like the rest of this method would risk the same flicker fixed in
            // TrendsForm's readout tiles.
            if (++_miniTargetsTickCounter >= 5)
            {
                _miniTargetsTickCounter = 0;
                RefreshMiniTargets(snap, hasFix);
            }

            // Heave zero-crossing
            var now = DateTime.Now;
            if (_lastHeaveVal < 0 && _rawHeaveCm >= 0)
            {
                if (_lastZeroCross.HasValue)
                {
                    double sec = (now - _lastZeroCross.Value).TotalSeconds;
                    if (sec >= 2.0 && sec <= 30.0)
                        ConningDataHub.Instance.UpdateHeavePeriod(sec);
                }
                _lastZeroCross = now;
            }
            _lastHeaveVal = _rawHeaveCm;
        }

        // Mirrors TargetsForm.RefreshData() — same bearing/distance calc, condensed to
        // NAME/BRG/NM columns and a one-line drift status instead of the full 4-column grid.
        private void RefreshMiniTargets(Snapshot snap, bool hasFix)
        {
            if (!SystemConfig.DriftWatchEnabled)
            {
                _miniDriftLbl.Text = "";
            }
            else if (!hasFix)
            {
                _miniDriftLbl.Text      = "Watch: no GPS fix";
                _miniDriftLbl.ForeColor = Palette.WaitFg;
            }
            else
            {
                double dist     = GeoMath.DistanceMeters(SystemConfig.DriftRefLat, SystemConfig.DriftRefLon, snap.GpsLatDeg, snap.GpsLonDeg);
                bool   exceeded = dist > SystemConfig.DriftRadiusM;
                _miniDriftLbl.Text      = exceeded ? $"⚠ Drift {dist:0}m OUTSIDE" : $"Drift {dist:0}m OK";
                _miniDriftLbl.ForeColor = exceeded ? Palette.AlarmActiveFg : Palette.TextDim;
            }

            // Grows the row pool on demand (up to however many targets are actually Enabled — no
            // fixed cap here, SystemConfig.Targets can hold up to TargetsForm.MaxTargets) instead
            // of the old fixed-4 cutoff.
            int shown = 0;
            foreach (var t in SystemConfig.Targets)
            {
                if (!t.Enabled) continue;
                EnsureMiniTargetRow(shown);
                var (row, nameLbl, brgLbl, distLbl) = _miniTargetRows[shown];
                nameLbl.Text = t.Name;
                if (hasFix)
                {
                    double distM = GeoMath.DistanceMeters(snap.GpsLatDeg, snap.GpsLonDeg, t.Lat, t.Lon);
                    double brg   = GeoMath.BearingDeg(snap.GpsLatDeg, snap.GpsLonDeg, t.Lat, t.Lon);
                    brgLbl.Text  = $"{brg:000}°";
                    distLbl.Text = $"{distM / 1852.0:0.0} nm";
                }
                else
                {
                    brgLbl.Text  = "--";
                    distLbl.Text = "--";
                }
                row.Visible = true;
                shown++;
            }
            for (int i = shown; i < _miniTargetRows.Count; i++) _miniTargetRows[i].Row.Visible = false;
        }

        private void HealthTick(object? sender, EventArgs e)
        {
            if (!_crashGuardCleared && DateTime.UtcNow - _startedAtUtc > StableUptime)
            {
                _crashGuardCleared = true;
                Program.ClearCrashGuard();
            }

            var snap = ConningDataHub.Instance.GetSnapshot();
            foreach (var row in snap.TaskRows)
            {
                switch (row.TaskName)
                {
                    case "GPS":     UpdateBadge(_badgeGps,    "GPS",   row, string.IsNullOrEmpty(snap.GpsFixQuality) ? "OK" : snap.GpsFixQuality); break;
                    case "WIND":    UpdateBadge(_badgeWind,   "WIND",  row); break;
                    case "R/P/H":   UpdateBadge(_badgeMotion, "R/P/H", row); break;
                    case "HEADING": UpdateBadge(_badgeHdg,    "HDG",   row); break;
                }
            }
        }

        private void LogTick(object? sender, EventArgs e)
        {
            var snap = ConningDataHub.Instance.GetSnapshot();
            _logger.LogSnapshot(snap.GpsSpeedKnot, snap.Heading, snap.RollDeg, snap.PitchDeg,
                snap.HeaveCm, snap.HeavePeriodSec, snap.WindSpeedMs, snap.WindDirDeg, snap.GpsLat, snap.GpsLon);
        }

        // ── Alarm handlers ─────────────────────────────────────────────────────
        private void OnAlarmRaised(Alarm a)  { RefreshAlarmBadge(); _logger.LogAlarmEvent("RAISED",  a.Id, a.State.ToString(), a.Tag.Value, a.HighLimitProvider()); }
        private void OnAlarmCleared(Alarm a) { RefreshAlarmBadge(); _logger.LogAlarmEvent("CLEARED", a.Id, a.State.ToString(), a.Tag.Value, a.HighLimitProvider()); }
        private void OnAlarmAcked(Alarm a)   { RefreshAlarmBadge(); _logger.LogAlarmEvent("ACKED",   a.Id, a.State.ToString(), a.Tag.Value, a.HighLimitProvider()); }

        private void RefreshAlarmBadge()
        {
            var all = _alarmEngine.GetAll();
            Alarm? unacked = null, acked = null;
            foreach (var a in all)
            {
                if (a.IsActive && !a.IsAcked) { unacked = a; break; }
                if (a.IsActive &&  a.IsAcked)   acked   = a;
            }

            if (unacked != null)
            {
                _badgeAlarm.Text      = $"⚠ {unacked.Id}";
                _badgeAlarm.BackColor = Palette.AlarmActiveBg;
                _badgeAlarm.ForeColor = Palette.AlarmActiveFg;
            }
            else if (acked != null)
            {
                _badgeAlarm.Text      = $"ACK {acked.Id}";
                _badgeAlarm.BackColor = Palette.AlarmAckBg;
                _badgeAlarm.ForeColor = Palette.AlarmAckFg;
            }
            else
            {
                _badgeAlarm.Text      = "✔ NORMAL";
                _badgeAlarm.BackColor = Palette.AlarmNormalBg;
                _badgeAlarm.ForeColor = Palette.AlarmNormalFg;
            }
        }

        // ── Theme ──────────────────────────────────────────────────────────────
        // All Palette colors are computed properties (IsLight ? light : dark), but most controls
        // had their BackColor/ForeColor assigned ONCE at construction — Refresh() alone repaints
        // with the same frozen colors. Snapshot every Palette color before/after the switch, build
        // an old→new map, and walk the control tree swapping any color that matches.
        private static readonly System.Reflection.PropertyInfo[] _paletteColorProps =
            typeof(Palette).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(Color))
                .ToArray();

        private static Color[] SnapshotPalette() =>
            _paletteColorProps.Select(p => (Color)p.GetValue(null)!).ToArray();

        private void ApplyTheme()
        {
            if (InvokeRequired) { Invoke(new Action(ApplyTheme)); return; }

            Color[] oldColors = SnapshotPalette();
            Palette.IsLight = SystemConfig.IsLightTheme;
            Color[] newColors = SnapshotPalette();

            var map = new Dictionary<Color, Color>();
            for (int i = 0; i < oldColors.Length; i++)
                map.TryAdd(oldColors[i], newColors[i]);

            Palette.CurrentSwapMap = map;
            Palette.ApplyToForm(this);
            Palette.ApplyTitleBarTheme(Handle);

            _conning?.Invalidate();
            Refresh();
        }

        // GGA fix quality field (0-based index 6): 0=invalid,1=GPS,2=DGPS,3=PPS,4=RTK fixed,
        // 5=RTK float,6=estimated,7=manual,8=simulation. Shown on the GPS badge in place of the
        // generic "OK" so the DPO can see correction status at a glance (as MERAN2 does).
        private static string GpsQualityText(int quality) => quality switch
        {
            1 => "GPS",
            2 => "DGPS",
            3 => "PPS",
            4 => "RTK FIX",
            5 => "RTK FLT",
            6 => "EST",
            7 => "MANUAL",
            8 => "SIM",
            _ => "OK",
        };

        // ── Helpers ────────────────────────────────────────────────────────────
        private static void UpdateBadge(Label badge, string name, SnapshotRow row, string okText = "OK")
        {
            if (row.Age > 900)
            {
                badge.Text = $"{name}: WAIT"; badge.BackColor = Palette.WaitBg; badge.ForeColor = Palette.WaitFg;
            }
            else if (row.IsStale)
            {
                badge.Text = $"{name}: LOST"; badge.BackColor = Palette.LostBg; badge.ForeColor = Palette.LostFg;
            }
            else
            {
                badge.Text = $"{name}: {okText}"; badge.BackColor = Palette.OkBg; badge.ForeColor = Palette.OkFg;
            }
        }

        private void BtnSettings_Click(object? sender, EventArgs e)
        {
            if (new LoginForm().ShowDialog() == DialogResult.OK)
            {
                new ConfigForm().ShowDialog();
                SystemConfig.Apply(ConfigService.Load());
            }
        }

        private static void SafeFont(Label lbl, float size)
        {
            if (lbl.Font != null && Math.Abs(lbl.Font.Size - size) < 0.5f) return;
            var old = lbl.Font;
            lbl.Font = new Font("Segoe UI", size, FontStyle.Bold);
            old?.Dispose();
        }

        private static Button MakeButton(string text, Color bg, Color fg)
        {
            var btn = new Button
            {
                Text      = text,
                Height    = 38,
                BackColor = bg,
                ForeColor = fg,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            btn.FlatAppearance.BorderColor = Palette.BorderCard;
            btn.FlatAppearance.BorderSize  = 1;
            return btn;
        }

        private static GraphicsPath RoundedRect(Rectangle r, int rad)
        {
            int d    = rad * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X,            r.Y,            d, d, 180, 90);
            path.AddArc(r.Right - d,    r.Y,            d, d, 270, 90);
            path.AddArc(r.Right - d,    r.Bottom - d,   d, d, 0,   90);
            path.AddArc(r.X,            r.Bottom - d,   d, d, 90,  90);
            path.CloseFigure();
            return path;
        }
    }
}
