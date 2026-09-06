using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Geo;
using ConningMonitorPRS.Core.Models;
using ConningMonitorPRS.Services;
using ConningMonitorPRS.UI.Theme;

namespace ConningMonitorPRS.UI.Forms
{
    // Live distance/bearing to configured targets, plus drift-off status against the Position
    // Watch reference point. Targets are editable right here (own grid + Save button) — this is
    // now the SOLE place to add/edit targets (2026-09-03: removed from ConfigForm's Settings
    // entirely per user request, so an operator no longer needs an admin login just to manage
    // targets — see ConfigForm.SetupPositionWatchTab). Position Watch reference point/radius
    // stays ConfigForm-only (this window intentionally doesn't duplicate every admin-only
    // setting, just target management).
    //
    // Up to MaxTargets (300), 6 coordinates each (2026-09-03, was 4 targets/4 coords — per user
    // request "Số lượng target nên để khoảng 300 / mỗi target 6 tọa độ"). Starts with the same 4
    // blank rows as before; ADD TARGET appends 1 more row at a time up to the cap. Both grids
    // (_dgv live view, _dgvEdit) are plain DataGridView, which already scrolls natively once its
    // row/column count exceeds the visible area — no special scrolling code needed here.
    public class TargetsForm : Form
    {
        private const int MaxTargets = 300;
        private const int MaxCoords  = 6; // primary vertex + up to 5 ExtraPoints
        private const int InitialTargetRows = 4;

        private Label _lblDrift = null!;
        private DataGridView _dgv = null!;
        private DataGridView _dgvEdit = null!;
        private Button _btnSave = null!;
        private Button _btnAdd  = null!;
        private System.Windows.Forms.Timer _timer = null!;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Palette.ApplyTitleBarTheme(Handle);
        }

        public TargetsForm()
        {
            Text          = "TARGETS & POSITION WATCH";
            // Wide enough to show every edit-grid column (On/Name/Lat/Lon + 5×Lat/Lon = 14 cols,
            // ~1130px of fixed column width, see the _dgvEdit setup below) up to Lat6/Lon6 without
            // needing to scroll horizontally right after opening — per user report the old 760px
            // default only reached partway into Lat4. MinimumSize stays narrower; shrinking below
            // the full column width still works fine via the grid's native horizontal scrollbar.
            Size          = new Size(1180, 560);
            MinimumSize   = new Size(560, 440);
            StartPosition = FormStartPosition.CenterParent;
            BackColor     = Palette.AppBg;

            BuildUI();
            LoadEditGrid();

            _timer = new System.Windows.Forms.Timer { Interval = 500 };
            _timer.Tick += (s, e) => RefreshData();
            _timer.Start();
            RefreshData();

            SystemConfig.ThemeChanged += ApplySelfTheme;
            FormClosed += (s, e) =>
            {
                SystemConfig.ThemeChanged -= ApplySelfTheme;
                _timer.Stop();
                _timer.Dispose();
            };
        }

        private void ApplySelfTheme()
        {
            Palette.ApplyToForm(this);
            Palette.ApplyTitleBarTheme(Handle);
        }

        private void BuildUI()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Palette.SectionHdrBg };
            _lblDrift = new Label
            {
                Text      = "POSITION WATCH: disabled",
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font      = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Palette.TextValue,
                BackColor = Color.Transparent,
                Padding   = new Padding(10, 0, 0, 0)
            };
            header.Controls.Add(_lblDrift);

            _dgv = new DataGridView
            {
                Dock                      = DockStyle.Fill,
                AutoSizeColumnsMode       = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor           = Palette.ChartBg,
                GridColor                 = Palette.BorderCard,
                BorderStyle               = BorderStyle.None,
                CellBorderStyle           = DataGridViewCellBorderStyle.SingleHorizontal,
                EnableHeadersVisualStyles = false,
                AllowUserToAddRows        = false,
                ReadOnly                  = true,
                RowHeadersVisible         = false,
                SelectionMode             = DataGridViewSelectionMode.FullRowSelect,
                ColumnHeadersHeight       = 32,
                RowTemplate               = { Height = 34 }
            };
            _dgv.ColumnHeadersDefaultCellStyle.BackColor = Palette.SectionHdrBg;
            _dgv.ColumnHeadersDefaultCellStyle.ForeColor = Palette.TextValue;
            _dgv.ColumnHeadersDefaultCellStyle.Font      = new Font("Segoe UI", 9f, FontStyle.Bold);
            _dgv.DefaultCellStyle.BackColor              = Palette.CardBg;
            _dgv.DefaultCellStyle.ForeColor              = Palette.TextValue;
            _dgv.DefaultCellStyle.Font                   = new Font("Segoe UI", 10f);
            _dgv.DefaultCellStyle.SelectionBackColor     = Palette.SurfaceHi;
            _dgv.DefaultCellStyle.SelectionForeColor     = Palette.TextValue;

            AddCol("NAME",          160, DataGridViewContentAlignment.MiddleLeft);
            AddCol("BEARING",       120, DataGridViewContentAlignment.MiddleCenter);
            AddCol("DISTANCE (nm)", 130, DataGridViewContentAlignment.MiddleCenter);
            AddCol("DISTANCE (m)",  0,   DataGridViewContentAlignment.MiddleCenter);
            _dgv.Columns["DISTANCE (m)"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

            // ── Edit section (bottom, fixed height) — dynamic-length On/Name/Lat/Lon×6 grid,
            // starts at InitialTargetRows (4) blank rows, ADD TARGET appends more up to
            // MaxTargets (300). Deliberately its own grid, not the live one above: the live grid
            // clears+rebuilds its rows every 500ms tick and only lists *enabled* targets, both of
            // which would fight with in-progress edits and hide disabled rows.
            var editPanel = new Panel { Dock = DockStyle.Bottom, Height = 210, BackColor = Palette.AppBg, Padding = new Padding(0, 6, 0, 0) };

            var editTitle = new Label
            {
                Text      = $"EDIT TARGETS (up to {MaxTargets})",
                Dock      = DockStyle.Top,
                Height    = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Palette.TextDim,
                BackColor = Color.Transparent,
                Padding   = new Padding(10, 0, 0, 0)
            };

            var btnRow = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Palette.AppBg };
            _btnSave = new Button
            {
                Text      = "SAVE TARGETS",
                Width     = 160,
                Height    = 28,
                Location  = new Point(10, 4),
                FlatStyle = FlatStyle.Flat,
                BackColor = Palette.BtnPrimaryBg,
                ForeColor = Palette.BtnPrimaryFg,
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            _btnSave.FlatAppearance.BorderColor = Palette.BorderCard;
            _btnSave.Click += (s, e) => SaveEditGrid();

            // Appends 1 blank row at the very bottom of the edit grid, up to MaxTargets.
            _btnAdd = new Button
            {
                Text      = "+ ADD TARGET",
                Width     = 130,
                Height    = 28,
                Location  = new Point(180, 4),
                FlatStyle = FlatStyle.Flat,
                BackColor = Palette.PanelBg,
                ForeColor = Palette.TextLabel,
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            _btnAdd.FlatAppearance.BorderColor = Palette.BorderCard;
            // New target defaults to the CURRENT GPS position (2026-09-03, per user request "target
            // khi add mặc định sẽ lấy tọa độ hiện tại") — same source ConfigForm's "Use Current GPS
            // Position" button uses for Position Watch. Falls back to blank (AddTargetRow's default)
            // if there's no fix yet, same as this app's other "no fix" fallbacks — no popup, doesn't
            // block adding a row to fill in by hand.
            _btnAdd.Click += (s, e) =>
            {
                var snap = ConningDataHub.Instance.GetSnapshot();
                bool hasFix = !double.IsNaN(snap.GpsLatDeg) && !double.IsNaN(snap.GpsLonDeg);
                AddTargetRow(lat: hasFix ? snap.GpsLatDeg : (double?)null, lon: hasFix ? snap.GpsLonDeg : (double?)null);
            };
            btnRow.Controls.Add(_btnAdd);
            btnRow.Controls.Add(_btnSave);

            _dgvEdit = new DataGridView
            {
                Dock                      = DockStyle.Fill,
                // AutoSizeColumnsMode.Fill would squeeze all 4+2×(MaxCoords-1) = 14 columns into
                // whatever width the window happens to be, making the Lat/Lon fields illegibly
                // narrow — None + explicit Width (below) keeps every column a usable size and
                // lets the grid scroll horizontally too (native DataGridView behavior, no extra
                // code needed) when the window is narrower than the full column set.
                AutoSizeColumnsMode       = DataGridViewAutoSizeColumnsMode.None,
                AllowUserToAddRows        = false,
                AllowUserToDeleteRows     = false,
                RowHeadersVisible         = false,
                BackgroundColor           = Palette.CardBg,
                GridColor                 = Palette.BorderCard,
                BorderStyle               = BorderStyle.None,
                EnableHeadersVisualStyles = false
            };
            _dgvEdit.ColumnHeadersDefaultCellStyle.BackColor = Palette.PanelBg;
            _dgvEdit.ColumnHeadersDefaultCellStyle.ForeColor = Palette.TextLabel;
            _dgvEdit.DefaultCellStyle.BackColor              = Palette.CardBg;
            _dgvEdit.DefaultCellStyle.ForeColor              = Palette.TextValue;
            _dgvEdit.DefaultCellStyle.SelectionBackColor     = Palette.SurfaceHi;
            _dgvEdit.DefaultCellStyle.SelectionForeColor     = Palette.TextValue;
            // Per-row delete button (2026-09-03, per user request "có nút thêm nút xóa target ở mỗi
            // target") — leftmost column so it's always visible without scrolling right past the 14
            // data columns. UseColumnTextForButtonValue means every cell just shows Text ("✕")
            // regardless of its Value, so AddTargetRow only needs a placeholder in that slot.
            _dgvEdit.Columns.Add(new DataGridViewButtonColumn
            {
                Name = "Delete", HeaderText = "", Text = "✕", UseColumnTextForButtonValue = true,
                Width = 32, FlatStyle = FlatStyle.Flat,
                DefaultCellStyle = { ForeColor = Palette.AlarmActiveFg, Alignment = DataGridViewContentAlignment.MiddleCenter }
            });
            _dgvEdit.CellContentClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                if (_dgvEdit.Columns[e.ColumnIndex].Name == "Delete") _dgvEdit.Rows.RemoveAt(e.RowIndex);
            };
            _dgvEdit.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "On",      Width = 40 });
            _dgvEdit.Columns.Add(new DataGridViewTextBoxColumn  { Name = "Name",    HeaderText = "Name",    Width = 110 });
            _dgvEdit.Columns.Add(new DataGridViewTextBoxColumn  { Name = "Lat",     HeaderText = "Lat (°)", Width = 90 });
            _dgvEdit.Columns.Add(new DataGridViewTextBoxColumn  { Name = "Lon",     HeaderText = "Lon (°)", Width = 90 });
            // Vertices 2-6 — all optional (leave blank = target stays a single point). Filling
            // in ≥1 more vertex turns the target into a shape (line at 2 vertices total,
            // polygon at 3+) drawn on ConningControl's radar instead of just a marker dot.
            for (int v = 2; v <= MaxCoords; v++)
            {
                _dgvEdit.Columns.Add(new DataGridViewTextBoxColumn { Name = $"Lat{v}", HeaderText = $"Lat{v} (°)", Width = 80 });
                _dgvEdit.Columns.Add(new DataGridViewTextBoxColumn { Name = $"Lon{v}", HeaderText = $"Lon{v} (°)", Width = 80 });
            }
            for (int i = 0; i < InitialTargetRows; i++) AddTargetRow($"Target {i + 1}");

            editPanel.Controls.Add(_dgvEdit);
            editPanel.Controls.Add(btnRow);
            editPanel.Controls.Add(editTitle);

            Controls.Add(_dgv);
            Controls.Add(editPanel);
            Controls.Add(header);
        }

        private void AddCol(string name, int width, DataGridViewContentAlignment align)
        {
            var col = new DataGridViewTextBoxColumn
            {
                Name       = name,
                HeaderText = name,
                ReadOnly   = true,
                SortMode   = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = align }
            };
            if (width > 0) col.Width = width;
            _dgv.Columns.Add(col);
        }

        // Appends 1 target row at the bottom of the edit grid (up to MaxTargets) — used for the
        // initial InitialTargetRows seed (blank, no lat/lon), LoadEditGrid's grow-to-fit loop
        // (blank, populated right after by the caller), and the "+ ADD TARGET" button (lat/lon
        // pre-filled with the current GPS fix, if any — see its Click handler). Silently no-ops
        // at the cap rather than erroring — MaxTargets is generous (300) enough that hitting it
        // signals something's off, not a routine action worth interrupting the user over.
        private void AddTargetRow(string? name = null, double? lat = null, double? lon = null)
        {
            if (_dgvEdit.Rows.Count >= MaxTargets) return;
            string latStr = lat.HasValue ? lat.Value.ToString("0.000000", CultureInfo.InvariantCulture) : "";
            string lonStr = lon.HasValue ? lon.Value.ToString("0.000000", CultureInfo.InvariantCulture) : "";
            var vals = new List<object> { "", false, name ?? $"Target {_dgvEdit.Rows.Count + 1}", latStr, lonStr };
            for (int v = 2; v <= MaxCoords; v++) { vals.Add(""); vals.Add(""); }
            _dgvEdit.Rows.Add(vals.ToArray());
        }

        // Loads the current SystemConfig.Targets into the edit grid — called once at Load, not
        // on the 500ms refresh tick (that would wipe out whatever the user is mid-typing). Grows
        // the grid to fit however many targets were previously saved (never shrinks below
        // InitialTargetRows) so a config with more than the default 4 still loads in full.
        private void LoadEditGrid()
        {
            while (_dgvEdit.Rows.Count < SystemConfig.Targets.Count) AddTargetRow();

            for (int i = 0; i < _dgvEdit.Rows.Count; i++)
            {
                var t = i < SystemConfig.Targets.Count ? SystemConfig.Targets[i] : null;
                _dgvEdit.Rows[i].Cells["Enabled"].Value = t?.Enabled ?? false;
                _dgvEdit.Rows[i].Cells["Name"].Value    = t?.Name ?? $"Target {i + 1}";
                _dgvEdit.Rows[i].Cells["Lat"].Value     = t != null ? t.Lat.ToString("0.000000", CultureInfo.InvariantCulture) : "";
                _dgvEdit.Rows[i].Cells["Lon"].Value     = t != null ? t.Lon.ToString("0.000000", CultureInfo.InvariantCulture) : "";
                for (int v = 2; v <= MaxCoords; v++)
                {
                    var p = t != null && t.ExtraPoints.Count >= v - 1 ? t.ExtraPoints[v - 2] : null;
                    _dgvEdit.Rows[i].Cells[$"Lat{v}"].Value = p != null ? p.Lat.ToString("0.000000", CultureInfo.InvariantCulture) : "";
                    _dgvEdit.Rows[i].Cells[$"Lon{v}"].Value = p != null ? p.Lon.ToString("0.000000", CultureInfo.InvariantCulture) : "";
                }
            }
        }

        private void SaveEditGrid()
        {
            _dgvEdit.EndEdit();

            var targets = new List<TargetPoint>();
            foreach (DataGridViewRow row in _dgvEdit.Rows)
            {
                bool enabled = row.Cells["Enabled"].Value is bool b && b;
                bool okLat = double.TryParse(row.Cells["Lat"].Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat);
                bool okLon = double.TryParse(row.Cells["Lon"].Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon);
                var extraPoints = new List<LatLon>();
                for (int v = 2; v <= MaxCoords; v++)
                {
                    bool okLatV = double.TryParse(row.Cells[$"Lat{v}"].Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double latV);
                    bool okLonV = double.TryParse(row.Cells[$"Lon{v}"].Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lonV);
                    // Both fields must parse — a vertex with only 1 of 2 filled in is treated
                    // as not entered rather than erroring, so a half-typed row doesn't block Save.
                    if (okLatV && okLonV) extraPoints.Add(new LatLon { Lat = latV, Lon = lonV });
                }
                targets.Add(new TargetPoint
                {
                    Name        = row.Cells["Name"].Value?.ToString() ?? "",
                    Lat         = okLat ? lat : 0.0,
                    Lon         = okLon ? lon : 0.0,
                    Enabled     = enabled && okLat && okLon,
                    ExtraPoints = extraPoints,
                });
            }
            SystemConfig.Targets = targets;

            var cfg = SystemConfig.Export();
            cfg.Tasks = ConfigForm.Tasks;
            ConfigService.Save(cfg);

            RefreshData();
            FlashSaved();
        }

        // Lightweight non-modal confirmation — ConfigForm shows a MessageBox on save, but it
        // also closes right after; this window stays open for ongoing live monitoring, so a
        // modal popup here would interrupt that every time someone tweaks a target. Flash the
        // button itself instead — the user pressed Save, so their eyes are already on it.
        private void FlashSaved()
        {
            _btnSave.Text      = "SAVED  ✓";
            _btnSave.BackColor = Palette.OkBg;
            _btnSave.ForeColor = Palette.OkFg;

            var revert = new System.Windows.Forms.Timer { Interval = 1200 };
            revert.Tick += (s, e) =>
            {
                _btnSave.Text      = "SAVE TARGETS";
                _btnSave.BackColor = Palette.BtnPrimaryBg;
                _btnSave.ForeColor = Palette.BtnPrimaryFg;
                revert.Stop();
                revert.Dispose();
            };
            revert.Start();
        }

        private void RefreshData()
        {
            if (IsDisposed) return;
            try
            {
                var snap   = ConningDataHub.Instance.GetSnapshot();
                bool hasFix = !double.IsNaN(snap.GpsLatDeg) && !double.IsNaN(snap.GpsLonDeg);

                if (!SystemConfig.DriftWatchEnabled)
                {
                    _lblDrift.Text = "POSITION WATCH: disabled — enable it in Settings ▸ Position Watch";
                    _lblDrift.ForeColor = Palette.TextDim;
                }
                else if (!hasFix)
                {
                    _lblDrift.Text = "POSITION WATCH: no GPS fix";
                    _lblDrift.ForeColor = Palette.WaitFg;
                }
                else
                {
                    double dist = GeoMath.DistanceMeters(SystemConfig.DriftRefLat, SystemConfig.DriftRefLon, snap.GpsLatDeg, snap.GpsLonDeg);
                    bool exceeded = dist > SystemConfig.DriftRadiusM;
                    _lblDrift.Text = $"POSITION WATCH: {dist:0} m from reference (radius {SystemConfig.DriftRadiusM:0} m) — {(exceeded ? "⚠ OUTSIDE SAFE ZONE" : "OK")}";
                    _lblDrift.ForeColor = exceeded ? Palette.AlarmActiveFg : Palette.OkFg;
                }

                _dgv.Rows.Clear();
                foreach (var t in SystemConfig.Targets)
                {
                    if (!t.Enabled) continue;
                    if (!hasFix)
                    {
                        _dgv.Rows.Add(t.Name, "--", "--", "--");
                        continue;
                    }
                    double distM = GeoMath.DistanceMeters(snap.GpsLatDeg, snap.GpsLonDeg, t.Lat, t.Lon);
                    double brg   = GeoMath.BearingDeg(snap.GpsLatDeg, snap.GpsLonDeg, t.Lat, t.Lon);
                    _dgv.Rows.Add(t.Name, $"{brg:000}°", $"{distM / 1852.0:0.00}", $"{distM:0}");
                }
                if (_dgv.Rows.Count == 0)
                    _dgv.Rows.Add("No targets enabled — edit below to add one", "", "", "");
            }
            catch { }
        }
    }
}
