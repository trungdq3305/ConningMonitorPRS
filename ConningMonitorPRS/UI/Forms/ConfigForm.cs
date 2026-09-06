using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Models;
using ConningMonitorPRS.Services;
using ConningMonitorPRS.UI.Theme;

namespace ConningMonitorPRS.UI.Forms
{
    public class ConfigForm : Form
    {
        // Shared task list (loaded from config at startup)
        public static List<DeviceTask> Tasks = new()
        {
            new DeviceTask { TaskName = "GPS",     PortName = "COM1", BaudRate = 9600 },
            new DeviceTask { TaskName = "WIND",    PortName = "COM2", BaudRate = 4800 },
            new DeviceTask { TaskName = "MRU",     PortName = "COM3", BaudRate = 115200 },
            new DeviceTask { TaskName = "HEADING", PortName = "COM4", BaudRate = 4800 },
            new DeviceTask { TaskName = "aux data", PortName = "COM5", BaudRate = 4800 },
            // Second GPS receiver for DUO mode — only used when SystemConfig.DuoGpsEnabled.
            new DeviceTask { TaskName = "GPS2",    PortName = "COM6", BaudRate = 9600 },
        };

        private NumericUpDown _numWind = null!, _numRoll = null!, _numPitch = null!, _numHeave = null!;
        private NumericUpDown _numMotionConfirm = null!, _numWindConfirm = null!;
        private NumericUpDown _numLoa = null!, _numLob = null!, _numGpsOffset = null!;
        private DataGridView  _dgvCom  = null!;
        private string?       _comPortBeforeEdit;
        private bool          _isSwappingComPort;
        private Button        _btnDay = null!, _btnNight = null!;
        private DataGridView  _dgvHistory = null!;
        private ComboBox      _cboRange   = null!;
        private Panel         _tabHistory = null!;
        private bool          _historyLoaded;
        private CancellationTokenSource? _historyCts;

        private CheckBox      _chkDriftEnabled = null!;
        private TextBox       _txtRefLat = null!, _txtRefLon = null!;
        private NumericUpDown _numDriftRadius = null!;

        private CheckBox      _chkDuoGps = null!;
        private NumericUpDown _numGpsDuoDivergence = null!;

        private bool _originalTheme;
        private bool _pendingIsLight;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Palette.ApplyTitleBarTheme(Handle);
        }

        public ConfigForm()
        {
            Text            = "SYSTEM CONFIGURATION";
            ClientSize      = new Size(640, 500);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = Palette.AppBg;

            var cfg = ConfigService.Load();
            if (cfg.Tasks != null)
                foreach (var saved in cfg.Tasks)
                {
                    var t = Tasks.Find(x => x.TaskName == saved.TaskName);
                    if (t != null) { t.PortName = saved.PortName; if (saved.BaudRate > 0) t.BaudRate = saved.BaudRate; t.SentenceType = saved.SentenceType; }
                }

            BuildUI();
            LoadData();
        }

        private void BuildUI()
        {
            var pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Palette.PanelBg, Padding = new Padding(8) };

            _btnDay = MakeBtn("☀  DAY",  95,  DockStyle.Left);
            _btnNight = MakeBtn("🌙  NIGHT", 105, DockStyle.Left);
            _btnDay.Click   += (s, e) => { _pendingIsLight = true;  SystemConfig.IsLightTheme = true;  ApplySelfTheme(); UpdateThemeButtons(); };
            _btnNight.Click += (s, e) => { _pendingIsLight = false; SystemConfig.IsLightTheme = false; ApplySelfTheme(); UpdateThemeButtons(); };

            var btnSave = new Button { Text = "SAVE / EXIT", Width = 140, Dock = DockStyle.Right, BackColor = Palette.BtnPrimaryBg, ForeColor = Palette.BtnPrimaryFg, FlatStyle = FlatStyle.Flat };
            btnSave.FlatAppearance.BorderColor = Palette.BorderCard;
            btnSave.FlatAppearance.BorderSize  = 1;
            btnSave.Click += BtnSave_Click;

            this.FormClosing += (s, e) => { if (DialogResult != DialogResult.OK) SystemConfig.IsLightTheme = _originalTheme; };

            pnlBottom.Controls.AddRange(new Control[] { _btnDay, _btnNight, btnSave });

            var tabBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, BackColor = Palette.PanelBg, Padding = new Padding(4, 4, 0, 0), WrapContents = false };
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Palette.AppBg };

            var pnlAlarm    = new Panel { Dock = DockStyle.Fill, BackColor = Palette.CardBg, Visible = true };
            var pnlCom      = new Panel { Dock = DockStyle.Fill, BackColor = Palette.CardBg, Visible = false };
            var pnlPosWatch = new Panel { Dock = DockStyle.Fill, BackColor = Palette.CardBg, Visible = false };
            _tabHistory     = new Panel { Dock = DockStyle.Fill, BackColor = Palette.CardBg, Visible = false };

            SetupAlarmTab(pnlAlarm);
            SetupComTab(pnlCom);
            SetupPositionWatchTab(pnlPosWatch);
            SetupHistoryTab(_tabHistory);

            content.Controls.Add(_tabHistory);
            content.Controls.Add(pnlPosWatch);
            content.Controls.Add(pnlCom);
            content.Controls.Add(pnlAlarm);

            // "Targets & Watch" renamed to "Position Watch" (2026-09-03) — target editing moved
            // entirely to TargetsForm (see SetupPositionWatchTab), this tab is now just the
            // Position Watch (drift alarm) reference point/radius.
            string[] names = { "Alarm Limits", "COM Config", "Alarm History", "Position Watch" };
            Panel[]  pages = { pnlAlarm, pnlCom, _tabHistory, pnlPosWatch };
            var btns = new Button[4];

            void SelectTab(int idx)
            {
                for (int i = 0; i < 4; i++)
                {
                    pages[i].Visible = i == idx;
                    btns[i].BackColor = i == idx ? Palette.BtnActiveBg : Palette.PanelBg;
                    btns[i].ForeColor = i == idx ? Palette.BtnActiveFg : Palette.TextLabel;
                    btns[i].FlatAppearance.BorderColor = i == idx ? Palette.BtnActiveFg : Palette.BorderCard;
                }
                pages[idx].BringToFront();
                if (idx == 2 && !_historyLoaded) { _historyLoaded = true; _ = LoadHistoryAsync(); }
            }

            for (int i = 0; i < 4; i++)
            {
                int ci = i;
                btns[i] = new Button
                {
                    Text = names[i], BackColor = i == 0 ? Palette.BtnActiveBg : Palette.PanelBg,
                    ForeColor = i == 0 ? Palette.BtnActiveFg : Palette.TextLabel,
                    FlatStyle = FlatStyle.Flat, Height = 28, AutoSize = true,
                    Padding = new Padding(10, 0, 10, 0), Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    Margin = new Padding(0, 0, 2, 0)
                };
                btns[i].FlatAppearance.BorderColor = i == 0 ? Palette.BtnActiveFg : Palette.BorderCard;
                btns[i].FlatAppearance.BorderSize  = 1;
                btns[i].Click += (s, e) => SelectTab(ci);
                tabBar.Controls.Add(btns[i]);
            }

            Controls.Add(content);
            Controls.Add(tabBar);
            Controls.Add(pnlBottom);
        }

        private Button MakeBtn(string text, int width, DockStyle dock)
        {
            var btn = new Button { Text = text, Width = width, Dock = dock, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
            btn.FlatAppearance.BorderSize = 1;
            return btn;
        }

        private void SetupAlarmTab(Panel tab)
        {
            tab.Controls.Add(new Label { Text = "ALARM LIMITS", Top = 12, Left = 30, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Palette.TextDim, BackColor = Color.Transparent });
            int y = 44;
            _numWind  = AddParamRow(tab, "Wind Max (m/s):", 30, ref y);
            _numRoll  = AddParamRow(tab, "Roll Max (°):",   30, ref y);
            _numPitch = AddParamRow(tab, "Pitch Max (°):",  30, ref y);
            _numHeave = AddParamRow(tab, "Heave Max (cm):", 30, ref y);
            _numMotionConfirm = AddParamRow(tab, "Motion Confirm (s):", 30, ref y, min: 0, max: 30);
            _numWindConfirm   = AddParamRow(tab, "Wind Confirm (s):",   30, ref y, min: 0, max: 30);

            tab.Controls.Add(new Label { Text = "VESSEL / GPS", Top = 12, Left = 340, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Palette.TextDim, BackColor = Color.Transparent });
            int y2 = 44;
            _numLoa       = AddParamRow(tab, "LOA (m):",       340, ref y2, min: 1,    max: 500);
            _numLob       = AddParamRow(tab, "LOB (m):",       340, ref y2, min: 1,    max: 100);
            _numGpsOffset = AddParamRow(tab, "GPS Offset (m):", 340, ref y2, min: -100, max: 100);
        }

        private NumericUpDown AddParamRow(Panel p, string text, int left, ref int top, decimal min = 0, decimal max = 9999)
        {
            p.Controls.Add(new Label { Text = text, Top = top, Left = left, AutoSize = true, Font = new Font("Segoe UI", 10), ForeColor = Palette.TextLabel, BackColor = Color.Transparent });
            var num = new NumericUpDown { Top = top - 3, Left = left + 140, Width = 110, DecimalPlaces = 1, Minimum = min, Maximum = max, Font = new Font("Segoe UI", 10), BackColor = Palette.InputBg, ForeColor = Palette.TextValue };
            p.Controls.Add(num);
            top += 55;
            return num;
        }

        private void SetupComTab(Panel tab)
        {
            var pnlDuo = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Palette.CardBg };
            _chkDuoGps = new CheckBox { Text = "Enable DUO GPS mode (redundant GPS2 — see row below)", Top = 6, Left = 4, AutoSize = true, ForeColor = Palette.TextLabel, BackColor = Color.Transparent, Font = new Font("Segoe UI", 9) };
            pnlDuo.Controls.Add(new Label { Text = "Divergence alarm (m):", Top = 8, Left = 400, AutoSize = true, Font = new Font("Segoe UI", 9), ForeColor = Palette.TextLabel, BackColor = Color.Transparent });
            _numGpsDuoDivergence = new NumericUpDown { Top = 5, Left = 530, Width = 70, Minimum = 5, Maximum = 5000, Font = new Font("Segoe UI", 9), BackColor = Palette.InputBg, ForeColor = Palette.TextValue };
            pnlDuo.Controls.Add(_chkDuoGps);
            pnlDuo.Controls.Add(_numGpsDuoDivergence);

            tab.Controls.Add(new Label { Text = "Edit COM port assignments:", Dock = DockStyle.Top, Height = 26, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 8.5f), ForeColor = Palette.TextLabel, BackColor = Color.Transparent, Padding = new Padding(4, 0, 0, 0) });
            _dgvCom = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, BackgroundColor = Palette.CardBg, GridColor = Palette.BorderCard, BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false };
            _dgvCom.ColumnHeadersDefaultCellStyle.BackColor = Palette.PanelBg;
            _dgvCom.ColumnHeadersDefaultCellStyle.ForeColor = Palette.TextLabel;
            _dgvCom.DefaultCellStyle.BackColor = Palette.CardBg;
            _dgvCom.DefaultCellStyle.ForeColor = Palette.TextValue;
            _dgvCom.DefaultCellStyle.SelectionBackColor = Palette.SurfaceHi;
            _dgvCom.Columns.Add(new DataGridViewTextBoxColumn { Name = "Task", HeaderText = "Task", ReadOnly = true, FillWeight = 80 });
            _dgvCom.Columns.Add(new DataGridViewComboBoxColumn { Name = "Port", HeaderText = "COM Port",   FillWeight = 90,  FlatStyle = FlatStyle.Flat });
            _dgvCom.Columns.Add(new DataGridViewComboBoxColumn { Name = "Baud", HeaderText = "Baud Rate",  FillWeight = 100, FlatStyle = FlatStyle.Flat });

            // Baud is a combo box but still editable by hand (common rates in the dropdown,
            // custom values typed directly) — Port stays a strict picklist of detected ports.
            _dgvCom.EditingControlShowing += (s, e) =>
            {
                if (_dgvCom.CurrentCell?.OwningColumn?.Name == "Baud" && e.Control is ComboBox cb)
                    cb.DropDownStyle = ComboBoxStyle.DropDown;
            };

            // Picking a COM port already assigned to another task swaps the two tasks' ports
            // instead of leaving 2 rows pointing at the same physical port (which would only
            // ever let one of them actually open at runtime) — per user request: no save-time
            // "port already in use" error, just auto-swap so both rows stay valid/distinct.
            //
            // BUG FIX: this originally hooked CellEndEdit, which for a ComboBoxColumn does NOT
            // fire the instant an item is picked from the dropdown — DataGridView leaves the
            // cell "dirty but still editing" until the user tabs/clicks away, so the swap only
            // ever happened after leaving the cell, not "ngay khi bị trùng" (immediately) like
            // asked. Standard WinForms fix: force the dirty edit to commit immediately via
            // CurrentCellDirtyStateChanged → CommitEdit(...Commit), which pushes the combo's
            // new selection into the cell (firing CellValueChanged) right away without waiting
            // for focus to leave. The swap itself now lives in CellValueChanged instead of
            // CellEndEdit so it reacts to that immediate commit. _isSwappingComPort guards
            // against the swap's own programmatic Value assignment re-entering this handler.
            _dgvCom.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_dgvCom.IsCurrentCellDirty && _dgvCom.CurrentCell?.OwningColumn?.Name == "Port")
                    _dgvCom.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _dgvCom.CellBeginEdit += (s, e) =>
            {
                if (_dgvCom.Columns[e.ColumnIndex].Name == "Port")
                    _comPortBeforeEdit = _dgvCom.Rows[e.RowIndex].Cells["Port"].Value?.ToString();
            };
            _dgvCom.CellValueChanged += (s, e) =>
            {
                if (_isSwappingComPort) return;
                if (e.RowIndex < 0 || _dgvCom.Columns[e.ColumnIndex].Name != "Port") return;
                string? newVal = _dgvCom.Rows[e.RowIndex].Cells["Port"].Value?.ToString();
                if (string.IsNullOrEmpty(newVal) || newVal == _comPortBeforeEdit) return;

                foreach (DataGridViewRow row in _dgvCom.Rows)
                {
                    if (row.Index == e.RowIndex) continue;
                    if (row.Cells["Port"].Value?.ToString() == newVal)
                    {
                        _isSwappingComPort = true;
                        row.Cells["Port"].Value = _comPortBeforeEdit;
                        _isSwappingComPort = false;
                        break;
                    }
                }
            };

            tab.Controls.Add(_dgvCom);
            tab.Controls.Add(pnlDuo);
        }

        private static readonly int[] CommonBauds = { 4800, 9600, 19200, 38400, 57600, 115200 };

        private static List<string> BuildPortItems()
        {
            var ports = new List<string>(SerialPort.GetPortNames());
            foreach (var t in Tasks)
                if (!ports.Contains(t.PortName)) ports.Add(t.PortName);
            ports.Sort((a, b) =>
            {
                int na = ExtractComNumber(a), nb = ExtractComNumber(b);
                return na >= 0 && nb >= 0 ? na.CompareTo(nb) : string.CompareOrdinal(a, b);
            });
            return ports;
        }

        private static int ExtractComNumber(string port)
        {
            var digits = new string(port.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out int n) ? n : -1;
        }

        private static List<int> BuildBaudItems()
        {
            var bauds = new List<int>(CommonBauds);
            foreach (var t in Tasks)
                if (!bauds.Contains(t.BaudRate)) bauds.Add(t.BaudRate);
            bauds.Sort();
            return bauds;
        }

        // Target editing (up to 4, now up to 300 coords/rows) lived here until 2026-09-03, when
        // the user asked to remove it from Settings entirely — TargetsForm is now the SOLE place
        // to add/edit targets (its own "EDIT TARGETS" grid + ADD TARGET button), so an operator
        // no longer needs an admin login just to add a target. This tab is Position Watch only.
        private void SetupPositionWatchTab(Panel tab)
        {
            var pnlWatch = new Panel { Dock = DockStyle.Fill, BackColor = Palette.CardBg };
            pnlWatch.Controls.Add(new Label { Text = "POSITION WATCH (Drift Alarm)", Top = 10, Left = 20, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Palette.TextDim, BackColor = Color.Transparent });

            _chkDriftEnabled = new CheckBox { Text = "Enabled", Top = 38, Left = 20, AutoSize = true, ForeColor = Palette.TextLabel, BackColor = Color.Transparent, Font = new Font("Segoe UI", 9.5f) };

            pnlWatch.Controls.Add(new Label { Text = "Ref Lat (°):", Top = 72, Left = 20, AutoSize = true, Font = new Font("Segoe UI", 9.5f), ForeColor = Palette.TextLabel, BackColor = Color.Transparent });
            _txtRefLat = new TextBox { Top = 69, Left = 110, Width = 100, Font = new Font("Segoe UI", 9.5f), BackColor = Palette.InputBg, ForeColor = Palette.TextValue };

            pnlWatch.Controls.Add(new Label { Text = "Ref Lon (°):", Top = 72, Left = 225, AutoSize = true, Font = new Font("Segoe UI", 9.5f), ForeColor = Palette.TextLabel, BackColor = Color.Transparent });
            _txtRefLon = new TextBox { Top = 69, Left = 315, Width = 100, Font = new Font("Segoe UI", 9.5f), BackColor = Palette.InputBg, ForeColor = Palette.TextValue };

            pnlWatch.Controls.Add(new Label { Text = "Radius (m):", Top = 72, Left = 430, AutoSize = true, Font = new Font("Segoe UI", 9.5f), ForeColor = Palette.TextLabel, BackColor = Color.Transparent });
            _numDriftRadius = new NumericUpDown { Top = 69, Left = 520, Width = 80, Minimum = 10, Maximum = 50000, DecimalPlaces = 0, Font = new Font("Segoe UI", 9.5f), BackColor = Palette.InputBg, ForeColor = Palette.TextValue };

            var btnUseCurrent = new Button { Text = "Use Current GPS Position", Top = 105, Left = 20, Width = 220, Height = 26, FlatStyle = FlatStyle.Flat, BackColor = Palette.BtnPrimaryBg, ForeColor = Palette.BtnPrimaryFg, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            btnUseCurrent.FlatAppearance.BorderColor = Palette.BorderCard;
            btnUseCurrent.Click += (s, e) =>
            {
                var snap = ConningDataHub.Instance.GetSnapshot();
                if (!double.IsNaN(snap.GpsLatDeg) && !double.IsNaN(snap.GpsLonDeg))
                {
                    _txtRefLat.Text = snap.GpsLatDeg.ToString("0.000000", CultureInfo.InvariantCulture);
                    _txtRefLon.Text = snap.GpsLonDeg.ToString("0.000000", CultureInfo.InvariantCulture);
                }
                else
                {
                    MessageBox.Show("No GPS fix yet.", "Position Watch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            pnlWatch.Controls.AddRange(new Control[] { _chkDriftEnabled, _txtRefLat, _txtRefLon, _numDriftRadius, btnUseCurrent });

            tab.Controls.Add(pnlWatch);
        }

        private void SetupHistoryTab(Panel tab)
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 44, FixedPanel = FixedPanel.Panel1, IsSplitterFixed = true, SplitterWidth = 1 };
            split.Panel1.BackColor = Palette.PanelBg;
            split.Panel2.BackColor = Palette.CardBg;

            _cboRange = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Left = 8, Top = 9, Font = new Font("Segoe UI", 9), BackColor = Palette.InputBg, ForeColor = Palette.TextValue };
            _cboRange.Items.AddRange(new object[] { "Last 1 hour", "Last 6 hours", "Last 24 hours", "Last 7 days" });
            _cboRange.SelectedIndex = 2;

            var btnRefresh = MakeToolBtn("⟳  Refresh", Palette.BtnPrimaryBg, Palette.BtnPrimaryFg, 130, 9, 100);
            btnRefresh.Click += (s, e) => _ = LoadHistoryAsync();
            var btnOpen = MakeToolBtn("📁  Open Logs", Palette.PanelBg, Palette.TextLabel, 240, 9, 130);
            btnOpen.Click += (s, e) => { string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs"); Directory.CreateDirectory(p); Process.Start("explorer.exe", p); };
            split.Panel1.Controls.AddRange(new Control[] { _cboRange, btnRefresh, btnOpen });

            _dgvHistory = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, BackgroundColor = Palette.CardBg, GridColor = Palette.BorderCard, BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, Font = new Font("Segoe UI", 9) };
            _dgvHistory.ColumnHeadersDefaultCellStyle.BackColor = Palette.PanelBg;
            _dgvHistory.ColumnHeadersDefaultCellStyle.ForeColor = Palette.TextLabel;
            _dgvHistory.DefaultCellStyle.BackColor = Palette.CardBg;
            _dgvHistory.DefaultCellStyle.ForeColor = Palette.TextValue;
            _dgvHistory.DefaultCellStyle.SelectionBackColor = Palette.SurfaceHi;
            _dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time",     FillWeight = 100 });
            _dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Alarm ID", FillWeight = 70 });
            _dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Event",    FillWeight = 60 });
            _dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "State",    FillWeight = 70 });
            _dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value",    FillWeight = 50 });
            _dgvHistory.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Limit",    FillWeight = 50 });
            split.Panel2.Controls.Add(_dgvHistory);
            tab.Controls.Add(split);
        }

        private Button MakeToolBtn(string text, Color bg, Color fg, int left, int top, int width)
        {
            var btn = new Button { Text = text, Left = left, Top = top, Width = width, Height = 26, BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            btn.FlatAppearance.BorderColor = Palette.BorderCard;
            btn.FlatAppearance.BorderSize  = 1;
            return btn;
        }

        private async Task LoadHistoryAsync()
        {
            _historyCts?.Cancel();
            _historyCts = new CancellationTokenSource();
            var token = _historyCts.Token;
            double hoursBack = _cboRange.SelectedIndex switch { 0 => 1.0, 1 => 6.0, 2 => 24.0, 3 => 168.0, _ => 24.0 };
            DateTime cutoff = DateTime.Now.AddHours(-hoursBack);
            string baseFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

            _dgvHistory.Rows.Clear();
            _dgvHistory.Rows.Add("Loading…", "", "", "", "", "");

            List<string[]> rows;
            try { rows = await Task.Run(() => ReadAlarmRows(cutoff, baseFolder, token), token); }
            catch (OperationCanceledException) { return; }
            if (token.IsCancellationRequested || IsDisposed) return;

            _dgvHistory.Rows.Clear();
            foreach (var r in rows)
            {
                int idx = _dgvHistory.Rows.Add(r);
                var rowStyle = _dgvHistory.Rows[idx].DefaultCellStyle;
                rowStyle.BackColor = r[2] switch { "RAISED" => Palette.AlarmActiveBg, "CLEARED" => Palette.AlarmNormalBg, "ACKED" => Palette.AlarmAckBg, _ => Palette.CardBg };
                rowStyle.ForeColor = r[2] switch { "RAISED" => Palette.AlarmActiveFg, "CLEARED" => Palette.AlarmNormalFg, "ACKED" => Palette.AlarmAckFg, _ => Palette.TextValue };
            }
        }

        private static List<string[]> ReadAlarmRows(DateTime cutoff, string baseFolder, CancellationToken token)
        {
            var rows = new List<string[]>();
            if (!Directory.Exists(baseFolder)) return rows;
            foreach (var dir in Directory.GetDirectories(baseFolder))
            {
                token.ThrowIfCancellationRequested();
                if (!DateTime.TryParseExact(Path.GetFileName(dir), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dd)) continue;
                if (dd.Date < cutoff.Date) continue;
                foreach (var file in Directory.GetFiles(dir, "*.csv"))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var line in File.ReadLines(file))
                        {
                            var p = line.Split(',');
                            if (p.Length < 15 || p[1] != "ALARM") continue;
                            if (!TimeSpan.TryParse(p[0], out TimeSpan ts)) continue;
                            if (dd.Date.Add(ts) < cutoff) continue;
                            string evt = (p.Length > 14 ? p[14] : "").Split('|')[0];
                            rows.Add(new[] { dd.Date.Add(ts).ToString("yyyy-MM-dd HH:mm:ss"), p[10], evt, p[11], p[12], p[13] });
                        }
                    }
                    catch { }
                }
            }
            rows.Sort((a, b) => string.Compare(b[0], a[0], StringComparison.Ordinal));
            return rows;
        }

        // SystemConfig.IsLightTheme setter fires ThemeChanged synchronously (same UI thread),
        // which runs MainForm.ApplyTheme() and rebuilds Palette.CurrentSwapMap before this
        // returns — reuse that map so ConfigForm's own controls (frozen at construction time)
        // pick up the same colors instead of staying on the old theme.
        private void ApplySelfTheme()
        {
            Palette.ApplyToForm(this);
            Palette.ApplyTitleBarTheme(Handle);
        }

        private void UpdateThemeButtons()
        {
            if (_pendingIsLight)
            {
                _btnDay.Enabled = false; _btnDay.BackColor = Color.FromArgb(0xFF, 0xE0, 0x60); _btnDay.ForeColor = Color.FromArgb(0x3C, 0x22, 0x00); _btnDay.FlatAppearance.BorderColor = Color.FromArgb(0xCC, 0xAA, 0x00);
                _btnNight.Enabled = true; _btnNight.BackColor = Palette.BtnPrimaryBg; _btnNight.ForeColor = Palette.BtnPrimaryFg; _btnNight.FlatAppearance.BorderColor = Palette.BorderCard;
            }
            else
            {
                _btnNight.Enabled = false; _btnNight.BackColor = Color.FromArgb(0x10, 0x22, 0x50); _btnNight.ForeColor = Color.FromArgb(0x90, 0xB8, 0xFF); _btnNight.FlatAppearance.BorderColor = Color.FromArgb(0x3A, 0x60, 0xB0);
                _btnDay.Enabled = true; _btnDay.BackColor = Palette.BtnPrimaryBg; _btnDay.ForeColor = Palette.BtnPrimaryFg; _btnDay.FlatAppearance.BorderColor = Palette.BorderCard;
            }
        }

        private void LoadData()
        {
            _numWind.Value  = (decimal)SystemConfig.WindMax;
            _numRoll.Value  = (decimal)SystemConfig.RMax;
            _numPitch.Value = (decimal)SystemConfig.PMax;
            _numHeave.Value = (decimal)SystemConfig.HMax;
            _numMotionConfirm.Value = Math.Clamp((decimal)SystemConfig.MotionConfirmSeconds, _numMotionConfirm.Minimum, _numMotionConfirm.Maximum);
            _numWindConfirm.Value   = Math.Clamp((decimal)SystemConfig.WindConfirmSeconds,   _numWindConfirm.Minimum,   _numWindConfirm.Maximum);
            _numLoa.Value       = Math.Clamp((decimal)SystemConfig.Loa,       _numLoa.Minimum,       _numLoa.Maximum);
            _numLob.Value       = Math.Clamp((decimal)SystemConfig.Lob,       _numLob.Minimum,       _numLob.Maximum);
            _numGpsOffset.Value = Math.Clamp((decimal)SystemConfig.GpsOffset, _numGpsOffset.Minimum, _numGpsOffset.Maximum);
            _originalTheme   = SystemConfig.IsLightTheme;
            _pendingIsLight  = SystemConfig.IsLightTheme;
            UpdateThemeButtons();

            var portCol = (DataGridViewComboBoxColumn)_dgvCom.Columns["Port"];
            var baudCol = (DataGridViewComboBoxColumn)_dgvCom.Columns["Baud"];
            portCol.Items.Clear();
            portCol.Items.AddRange(BuildPortItems().Cast<object>().ToArray());
            baudCol.Items.Clear();
            baudCol.Items.AddRange(BuildBaudItems().Cast<object>().ToArray());

            _dgvCom.Rows.Clear();
            foreach (var t in Tasks) _dgvCom.Rows.Add(t.TaskName, t.PortName, t.BaudRate);

            _chkDriftEnabled.Checked = SystemConfig.DriftWatchEnabled;
            _txtRefLat.Text = SystemConfig.DriftRefLat.ToString("0.000000", CultureInfo.InvariantCulture);
            _txtRefLon.Text = SystemConfig.DriftRefLon.ToString("0.000000", CultureInfo.InvariantCulture);
            _numDriftRadius.Value = Math.Clamp((decimal)SystemConfig.DriftRadiusM, _numDriftRadius.Minimum, _numDriftRadius.Maximum);

            _chkDuoGps.Checked = SystemConfig.DuoGpsEnabled;
            _numGpsDuoDivergence.Value = Math.Clamp((decimal)SystemConfig.GpsDuoDivergenceM, _numGpsDuoDivergence.Minimum, _numGpsDuoDivergence.Maximum);
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            SystemConfig.WindMax          = (double)_numWind.Value;
            SystemConfig.RMax             = (double)_numRoll.Value;
            SystemConfig.PMax             = (double)_numPitch.Value;
            SystemConfig.HMax             = (double)_numHeave.Value;
            SystemConfig.MotionConfirmSeconds = (double)_numMotionConfirm.Value;
            SystemConfig.WindConfirmSeconds   = (double)_numWindConfirm.Value;
            SystemConfig.Loa              = (double)_numLoa.Value;
            SystemConfig.Lob              = (double)_numLob.Value;
            SystemConfig.GpsOffset        = (double)_numGpsOffset.Value;
            SystemConfig.IsLightTheme     = _pendingIsLight;

            foreach (DataGridViewRow row in _dgvCom.Rows)
            {
                string? taskName = row.Cells["Task"].Value?.ToString();
                var t = Tasks.Find(x => x.TaskName == taskName);
                if (t != null)
                {
                    t.PortName = row.Cells["Port"].Value?.ToString() ?? t.PortName;
                    if (int.TryParse(row.Cells["Baud"].Value?.ToString(), out int baud) && baud > 0)
                        t.BaudRate = baud;
                }
            }

            SystemConfig.DriftWatchEnabled = _chkDriftEnabled.Checked;
            SystemConfig.DriftRefLat       = double.TryParse(_txtRefLat.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double refLat) ? refLat : 0.0;
            SystemConfig.DriftRefLon       = double.TryParse(_txtRefLon.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double refLon) ? refLon : 0.0;
            SystemConfig.DriftRadiusM      = (double)_numDriftRadius.Value;

            // Targets are no longer edited here (see SetupPositionWatchTab) — SystemConfig.Targets
            // is left untouched, so Export() below persists whatever TargetsForm's own Save last
            // set it to.
            SystemConfig.DuoGpsEnabled     = _chkDuoGps.Checked;
            SystemConfig.GpsDuoDivergenceM = (double)_numGpsDuoDivergence.Value;

            var saveCfg = SystemConfig.Export();
            saveCfg.Tasks = Tasks;
            ConfigService.Save(saveCfg);
            MessageBox.Show("Configuration saved!\n\nRestart to apply COM port changes.", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
