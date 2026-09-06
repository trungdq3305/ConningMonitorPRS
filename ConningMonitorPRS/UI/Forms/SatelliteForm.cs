using System;
using System.Drawing;
using System.Windows.Forms;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Models;
using ConningMonitorPRS.UI.Theme;

namespace ConningMonitorPRS.UI.Forms
{
    // GNSS satellite status — per-constellation count/SNR from $GSV, plus fix type and DOP
    // from $GSA. Standard NMEA-0183 only; does not track differential-correction broadcast
    // satellites (that needs proprietary receiver messages, out of scope here).
    public class SatelliteForm : Form
    {
        private Label _lblFix = null!;
        private DataGridView _dgv = null!;
        private System.Windows.Forms.Timer _timer = null!;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Palette.ApplyTitleBarTheme(Handle);
        }

        public SatelliteForm()
        {
            Text          = "SATELLITE STATUS";
            Size          = new Size(640, 380);
            MinimumSize   = new Size(480, 280);
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
            var header = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Palette.SectionHdrBg };
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

            Controls.Add(_dgv);
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

        private void RefreshData()
        {
            if (IsDisposed) return;
            try
            {
                var snap = ConningDataHub.Instance.GetSnapshot();

                string fixText = string.IsNullOrEmpty(snap.GsaFixType) ? "--" : snap.GsaFixType;
                _lblFix.Text = $"FIX: {fixText}   |   PDOP {snap.Pdop:0.0}   HDOP {snap.Hdop:0.0}   VDOP {snap.Vdop:0.0}";
                _lblFix.ForeColor = snap.GsaFixType == "3D" ? Palette.OkFg
                                  : snap.GsaFixType == "2D" ? Palette.WaitFg
                                  : Palette.TextValue;

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
    }
}
