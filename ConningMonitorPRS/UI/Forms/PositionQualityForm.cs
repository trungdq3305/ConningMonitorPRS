using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Models;
using ConningMonitorPRS.UI.Theme;

namespace ConningMonitorPRS.UI.Forms
{
    // PRS-PQE-01 (Position Quality Evaluation) — DP-OA handover doc, module 2/2. Non-modal,
    // self-contained (own 1000ms Timer reading ConningDataHub.Instance.GetSnapshot()), same
    // pattern as SatelliteForm/DataListForm/TargetsForm/TrendsForm. Kept as a separate window
    // from "GNSS Health" per user request (2026-09-07) rather than merged into it — both
    // windows show the same "Combined" line (doc section 8 interpretation) so either one
    // alone still gives the DPO the full picture.
    public class PositionQualityForm : Form
    {
        private Label _lblOverall  = null!;
        private Label _lblCombined = null!;
        private DataGridView _dgvChannels = null!;
        private Label _lblAdvisory = null!;
        private System.Windows.Forms.Timer _timer = null!;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Palette.ApplyTitleBarTheme(Handle);
        }

        public PositionQualityForm()
        {
            Text          = "POSITION QUALITY";
            Size          = new Size(640, 560);
            MinimumSize   = new Size(520, 420);
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
            var header = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = Palette.SectionHdrBg, Padding = new Padding(10, 0, 10, 0) };
            _lblOverall = new Label
            {
                Text = "OVERALL: —", Dock = DockStyle.Top, Height = 34,
                Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Palette.TextDim,
                BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft
            };
            _lblCombined = new Label
            {
                Text = "Combined: —", Dock = DockStyle.Top, Height = 28,
                Font = new Font("Segoe UI", 9, FontStyle.Italic), ForeColor = Palette.TextValue,
                BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(_lblCombined);
            header.Controls.Add(_lblOverall);

            _dgvChannels = new DataGridView
            {
                Dock                      = DockStyle.Top,
                Height                    = 220,
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
                RowTemplate               = { Height = 24 }
            };
            _dgvChannels.ColumnHeadersDefaultCellStyle.BackColor = Palette.SectionHdrBg;
            _dgvChannels.ColumnHeadersDefaultCellStyle.ForeColor = Palette.TextValue;
            _dgvChannels.ColumnHeadersDefaultCellStyle.Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _dgvChannels.DefaultCellStyle.BackColor              = Palette.CardBg;
            _dgvChannels.DefaultCellStyle.ForeColor              = Palette.TextValue;
            _dgvChannels.DefaultCellStyle.Font                   = new Font("Segoe UI", 9f);
            _dgvChannels.DefaultCellStyle.SelectionBackColor     = Palette.SurfaceHi;
            AddCol("CHANNEL", 130, DataGridViewContentAlignment.MiddleLeft);
            AddCol("STATE",   100, DataGridViewContentAlignment.MiddleCenter);
            AddCol("DETAIL",  0,   DataGridViewContentAlignment.MiddleLeft);
            _dgvChannels.Columns["DETAIL"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

            _lblAdvisory = new Label
            {
                Dock = DockStyle.Fill, Text = "", Visible = false,
                Font = new Font("Segoe UI", 9), ForeColor = Palette.TextValue,
                BackColor = Color.Transparent, TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(10, 10, 10, 10)
            };

            // Fill first, then Top controls in reverse of desired visual order (Dock=Top
            // stacks newest-added at the outer edge) — same rule used in SatelliteForm.
            Controls.Add(_lblAdvisory);
            Controls.Add(_dgvChannels);
            Controls.Add(header);
        }

        private void AddCol(string name, int width, DataGridViewContentAlignment align)
        {
            var col = new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = name, ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
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
                var pqe  = snap.PqeStatus;

                if (pqe == null)
                {
                    _lblOverall.Text = "OVERALL: —";
                    _lblOverall.ForeColor = Palette.TextDim;
                    _dgvChannels.Rows.Clear();
                    _lblAdvisory.Visible = false;
                }
                else
                {
                    _lblOverall.Text = $"OVERALL: {pqe.Overall.ToString().ToUpperInvariant()}";
                    _lblOverall.ForeColor = ColorForState(pqe.Overall);

                    var keys = new List<string>(pqe.Channels.Keys);
                    keys.Sort(StringComparer.Ordinal); // "Q1".."Q8" sort correctly as plain strings
                    _dgvChannels.Rows.Clear();
                    foreach (var key in keys)
                    {
                        var r = pqe.Channels[key];
                        int idx = _dgvChannels.Rows.Add(key, r.State.ToString(), r.Evidence);
                        _dgvChannels.Rows[idx].DefaultCellStyle.ForeColor = ColorForState(r.State);
                    }

                    if (pqe.Advisories.Count > 0)
                    {
                        var a = pqe.Advisories[0];
                        _lblAdvisory.Text = $"⚠ {a.What}\nWHY: {a.Why}\nIMPACT: {a.Impact}\nACTION: {a.Action}";
                        _lblAdvisory.ForeColor = ColorForState(a.Severity);
                        _lblAdvisory.Visible = true;
                    }
                    else
                    {
                        _lblAdvisory.Visible = false;
                    }
                }

                _lblCombined.Text = string.IsNullOrEmpty(snap.CombinedText) ? "Combined: —" : $"Combined: {snap.CombinedText}";
                _lblCombined.ForeColor = ColorForState(snap.CombinedSeverity);
            }
            catch { }
        }
    }
}
