using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Models;
using ConningMonitorPRS.UI.Theme;

namespace ConningMonitorPRS.UI.Forms
{
    // GNSS Health / Satellite Status — extended 2026-09-07 for PRS-GNSS-01 (DP-OA handover
    // doc): the original per-constellation count/SNR ($GSV) + fix type/DOP ($GSA) view is
    // kept as-is (bottom grid); the new top section (pnlHealth) shows the H1-H9 rule-engine
    // verdict (GnssHealthEvaluator, via ConningDataHub.Snapshot.GnssHealth) and the single
    // most severe active advisory in WHAT/WHY/IMPACT/ACTION form. Standard NMEA-0183 only;
    // does not track differential-correction broadcast satellites or receiver/antenna
    // diagnostics — those need proprietary receiver messages (H6 always shows UNKNOWN).
    public class SatelliteForm : Form
    {
        private Label _lblFix = null!;
        private Label _lblOverall = null!;
        private Label _lblCombined = null!;
        private DataGridView _dgvChannels = null!;
        private Label _lblAdvisory = null!;
        private DataGridView _dgv = null!;
        private System.Windows.Forms.Timer _timer = null!;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Palette.ApplyTitleBarTheme(Handle);
        }

        public SatelliteForm()
        {
            Text          = "GNSS HEALTH / SATELLITE STATUS";
            Size          = new Size(700, 620);
            MinimumSize   = new Size(560, 480);
            StartPosition = FormStartPosition.CenterParent;
            BackColor     = Palette.AppBg;

            BuildUI();

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
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
            var header = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Palette.SectionHdrBg };
            _lblFix = new Label
            {
                Text      = "FIX: -- | PDOP -- HDOP -- VDOP --",
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font      = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Palette.TextValue,
                BackColor = Color.Transparent,
                Padding   = new Padding(10, 0, 0, 0)
            };
            header.Controls.Add(_lblFix);

            var pnlHealth = new Panel { Dock = DockStyle.Top, Height = 274, BackColor = Palette.CardBg, Padding = new Padding(10, 6, 10, 6) };

            _lblOverall = new Label
            {
                Text = "OVERALL: —", Dock = DockStyle.Top, Height = 30,
                Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Palette.TextDim,
                BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft
            };
            // Doc section 8 combined GNSS×PQE interpretation — same text PositionQualityForm
            // shows, so either window alone gives the DPO the full picture (PRS-PQE-01,
            // 2026-09-07).
            _lblCombined = new Label
            {
                Text = "Combined: —", Dock = DockStyle.Top, Height = 22,
                Font = new Font("Segoe UI", 9, FontStyle.Italic), ForeColor = Palette.TextValue,
                BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft
            };

            _dgvChannels = new DataGridView
            {
                Dock                      = DockStyle.Top,
                Height                    = 150,
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
                ColumnHeadersHeight       = 28,
                RowTemplate               = { Height = 22 }
            };
            _dgvChannels.ColumnHeadersDefaultCellStyle.BackColor = Palette.SectionHdrBg;
            _dgvChannels.ColumnHeadersDefaultCellStyle.ForeColor = Palette.TextValue;
            _dgvChannels.ColumnHeadersDefaultCellStyle.Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _dgvChannels.DefaultCellStyle.BackColor              = Palette.CardBg;
            _dgvChannels.DefaultCellStyle.ForeColor              = Palette.TextValue;
            _dgvChannels.DefaultCellStyle.Font                   = new Font("Segoe UI", 9f);
            _dgvChannels.DefaultCellStyle.SelectionBackColor     = Palette.SurfaceHi;
            AddChannelCol("CHANNEL", 130, DataGridViewContentAlignment.MiddleLeft);
            AddChannelCol("STATE",   100, DataGridViewContentAlignment.MiddleCenter);
            AddChannelCol("DETAIL",  0,   DataGridViewContentAlignment.MiddleLeft);
            _dgvChannels.Columns["DETAIL"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

            _lblAdvisory = new Label
            {
                Dock = DockStyle.Fill, Text = "", Visible = false,
                Font = new Font("Segoe UI", 9), ForeColor = Palette.TextValue,
                BackColor = Color.Transparent, TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(0, 6, 0, 0)
            };

            // Added in reverse of visual order (Dock=Top stacks newest-added at the outer
            // edge) so the final layout reads top-to-bottom: overall badge, combined line,
            // channel grid, advisory text.
            pnlHealth.Controls.Add(_lblAdvisory);
            pnlHealth.Controls.Add(_dgvChannels);
            pnlHealth.Controls.Add(_lblCombined);
            pnlHealth.Controls.Add(_lblOverall);

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

            AddCol("CONSTELLATION", 160, DataGridViewContentAlignment.MiddleLeft);
            AddCol("SATS IN VIEW",  130, DataGridViewContentAlignment.MiddleCenter);
            AddCol("AVG SNR (dB)",  0,   DataGridViewContentAlignment.MiddleCenter);
            _dgv.Columns["AVG SNR (dB)"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

            // Add order matters for docking (see comment above): Fill first, then Top panels
            // in reverse of desired visual order — header ends up above pnlHealth, which ends
            // up above the satellite grid, which fills the rest.
            Controls.Add(_dgv);
            Controls.Add(pnlHealth);
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

        private void AddChannelCol(string name, int width, DataGridViewContentAlignment align)
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
            _dgvChannels.Columns.Add(col);
        }

        private Color ColorForState(HealthState s) => s switch
        {
            HealthState.Healthy  => Palette.OkFg,
            HealthState.Degraded => Palette.WaitFg,
            HealthState.Warning  => Palette.WaitFg,
            HealthState.Invalid  => Palette.LostFg,
            _                    => Palette.TextDim,
        };

        private void RefreshData()
        {
            if (IsDisposed) return;
            try
            {
                var snap = ConningDataHub.Instance.GetSnapshot();

                string fixText = string.IsNullOrEmpty(snap.GsaFixType) ? "--" : snap.GsaFixType;
                _lblFix.Text = $"FIX: {fixText}   |   PDOP {snap.Pdop:0.0}   HDOP {snap.Hdop:0.0}   VDOP {snap.Vdop:0.0}   |   Sats used {snap.SatsUsed}";
                _lblFix.ForeColor = snap.GsaFixType == "3D" ? Palette.OkFg
                                  : snap.GsaFixType == "2D" ? Palette.WaitFg
                                  : Palette.TextValue;

                RefreshHealth(snap.GnssHealth);

                _lblCombined.Text = string.IsNullOrEmpty(snap.CombinedText) ? "Combined: —" : $"Combined: {snap.CombinedText}";
                _lblCombined.ForeColor = ColorForState(snap.CombinedSeverity);

                _dgv.Rows.Clear();
                foreach (var sat in snap.Satellites)
                {
                    bool stale = sat.Age > 5.0;
                    int idx = _dgv.Rows.Add(sat.Constellation, sat.CountInView, sat.AvgSnr);
                    _dgv.Rows[idx].DefaultCellStyle.ForeColor = stale ? Palette.WaitFg
                        : sat.AvgSnr >= 35 ? Palette.OkFg
                        : sat.AvgSnr >= 20 ? Palette.TextValue
                        : Palette.LostFg;
                }
                if (_dgv.Rows.Count == 0)
                    _dgv.Rows.Add("Waiting for $GSV sentences…", "", "");
            }
            catch { }
        }

        private void RefreshHealth(GnssHealthStatus? health)
        {
            if (health == null)
            {
                _lblOverall.Text = "OVERALL: —";
                _lblOverall.ForeColor = Palette.TextDim;
                _dgvChannels.Rows.Clear();
                _lblAdvisory.Visible = false;
                return;
            }

            _lblOverall.Text = $"OVERALL: {health.Overall.ToString().ToUpperInvariant()}";
            _lblOverall.ForeColor = ColorForState(health.Overall);

            var keys = new List<string>(health.Channels.Keys);
            keys.Sort(StringComparer.Ordinal); // "H1".."H9" sort correctly as plain strings
            _dgvChannels.Rows.Clear();
            foreach (var key in keys)
            {
                var r = health.Channels[key];
                int idx = _dgvChannels.Rows.Add(key, r.State.ToString(), r.Evidence);
                _dgvChannels.Rows[idx].DefaultCellStyle.ForeColor = ColorForState(r.State);
            }

            if (health.Advisories.Count > 0)
            {
                var a = health.Advisories[0];
                _lblAdvisory.Text = $"⚠ {a.What}\nWHY: {a.Why}\nIMPACT: {a.Impact}\nACTION: {a.Action}";
                _lblAdvisory.ForeColor = ColorForState(a.Severity);
                _lblAdvisory.Visible = true;
            }
            else
            {
                _lblAdvisory.Visible = false;
            }
        }
    }
}
