using System;
using System.Drawing;
using System.Windows.Forms;
using ConningMonitorPRS.Core.Data;
using ConningMonitorPRS.Core.Models;
using ConningMonitorPRS.UI.Theme;

namespace ConningMonitorPRS.UI.Forms
{
    public class DataListForm : Form
    {
        private DataGridView _dgv       = null!;
        private System.Windows.Forms.Timer _timer = null!;
        private readonly Font _statusFont = new Font("Segoe UI", 9f, FontStyle.Bold);

        // Keyed by TaskName (case-insensitive prefix match)
        // "--" = talker ID wildcard (standard NMEA-0183 notation) — parsing matches by sentence
        // suffix only (e.g. type.EndsWith("MWV")), so any talker (GP/WI/II/…) is accepted. Using
        // a real talker here (e.g. "$WIMWV") reads as a required exact match and has caused
        // confusion when a device sends the same sentence under a different talker (e.g. "$IIMWV").
        private static readonly (string Header, string Desc)[] _taskMeta =
        {
            ("$--GGA / $--GNS / $--VTG", "GPS Position & Speed Over Ground"),   // GPS
            ("$--MWV",                   "Wind Speed and Direction"),            // WIND
            ("XBus MTData2",             "Roll / Pitch / Heave (Xsens MRU)"),   // MRU
            ("$--HDT / $--HCR",          "Heading True"),                        // HEADING
            ("-",                        "Aux Data"),                            // aux / others
        };

        private static (string Header, string Desc) MetaForTask(string taskName)
        {
            string t = taskName.ToUpperInvariant();
            if (t.Contains("GPS"))     return _taskMeta[0];
            if (t.Contains("WIND"))    return _taskMeta[1];
            if (t.Contains("MRU") || t.Contains("R/P") || t.Contains("ROLL") || t.Contains("RPH")) return _taskMeta[2];
            if (t.Contains("HEAD"))    return _taskMeta[3];
            return _taskMeta[4];
        }

        // DataHub always stores Roll/Pitch/Heave under hub key "R/P/H", regardless of the
        // configured device task name (e.g. "MRU" for the Xsens binary sensor).
        private static string HubKeyForTask(string taskName) =>
            taskName == "MRU" ? "R/P/H" : taskName;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Palette.ApplyTitleBarTheme(Handle);
        }

        public DataListForm()
        {
            Text          = "RAW COM SCAN";
            Size          = new Size(1260, 340);
            MinimumSize   = new Size(900, 280);
            StartPosition = FormStartPosition.CenterParent;
            BackColor     = Palette.AppBg;

            BuildUI();

            _timer = new System.Windows.Forms.Timer { Interval = 500 };
            _timer.Tick += (s, e) => RefreshData();
            _timer.Start();
            RefreshData();

            // Non-modal window — can stay open while the user toggles Day/Night from Settings,
            // so it must react to ThemeChanged itself instead of relying on MainForm.
            SystemConfig.ThemeChanged += ApplySelfTheme;

            FormClosed += (s, e) =>
            {
                SystemConfig.ThemeChanged -= ApplySelfTheme;
                _timer.Stop();
                _timer.Dispose();
                _statusFont.Dispose();
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
            header.Controls.Add(new Label
            {
                Text      = "RAW DATA SCANNER  —  refreshes every 500ms  —  Age > 2s = LOST",
                Dock      = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Font      = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Palette.TextValue,
                BackColor = Color.Transparent,
                Padding   = new Padding(10, 0, 0, 0)
            });

            _dgv = new DataGridView
            {
                Dock                      = DockStyle.Fill,
                AutoSizeColumnsMode       = DataGridViewAutoSizeColumnsMode.None,
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
                RowTemplate               = { Height = 36 }
            };
            _dgv.ColumnHeadersDefaultCellStyle.BackColor = Palette.SectionHdrBg;
            _dgv.ColumnHeadersDefaultCellStyle.ForeColor = Palette.TextValue;
            _dgv.ColumnHeadersDefaultCellStyle.Font      = new Font("Segoe UI", 9f, FontStyle.Bold);
            _dgv.DefaultCellStyle.BackColor              = Palette.CardBg;
            _dgv.DefaultCellStyle.ForeColor              = Palette.TextValue;
            _dgv.DefaultCellStyle.Font                   = new Font("Consolas", 9.5f);
            _dgv.DefaultCellStyle.SelectionBackColor     = Palette.SurfaceHi;
            _dgv.DefaultCellStyle.SelectionForeColor     = Palette.TextValue;

            AddCol("#",                38,  DataGridViewContentAlignment.MiddleCenter);
            AddCol("PORT",             65,  DataGridViewContentAlignment.MiddleCenter);
            AddCol("TASK",             80,  DataGridViewContentAlignment.MiddleCenter);
            AddCol("DESCRIPTION",      200, DataGridViewContentAlignment.MiddleLeft);
            AddCol("BAUD",             70,  DataGridViewContentAlignment.MiddleCenter);
            AddCol("EXPECTED",         180, DataGridViewContentAlignment.MiddleCenter);
            AddCol("RAW DATA",         0,   DataGridViewContentAlignment.MiddleLeft);
            AddCol("AGE(s)",           72,  DataGridViewContentAlignment.MiddleCenter);
            AddCol("STATUS",           76,  DataGridViewContentAlignment.MiddleCenter);
            _dgv.Columns["RAW DATA"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

            var taskList = ConningMonitorPRS.UI.Forms.ConfigForm.Tasks;
            for (int i = 0; i < taskList.Count; i++)
            {
                var    t    = taskList[i];
                var    meta = MetaForTask(t.TaskName);
                _dgv.Rows.Add(i + 1, t.PortName, t.TaskName, meta.Desc,
                    t.BaudRate > 0 ? t.BaudRate.ToString() : "-", meta.Header, "", "", "WAIT");
            }

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
            if (_dgv == null || _dgv.IsDisposed || IsDisposed) return;
            try
            {
                var snap     = ConningDataHub.Instance.GetSnapshot();
                var taskList = ConningMonitorPRS.UI.Forms.ConfigForm.Tasks;

                for (int i = 0; i < _dgv.Rows.Count && i < taskList.Count; i++)
                {
                    string taskName = taskList[i].TaskName;
                    string portName = taskList[i].PortName;
                    int    baud     = taskList[i].BaudRate;

                    _dgv.Rows[i].Cells["PORT"].Value = portName;
                    _dgv.Rows[i].Cells["BAUD"].Value = baud > 0 ? baud.ToString() : "-";

                    var row = snap.TaskRows.Find(r => r.TaskName == HubKeyForTask(taskName));
                    if (row == null) continue;

                    string raw = row.Value ?? "";
                    _dgv.Rows[i].Cells["RAW DATA"].Value = raw.Length > 120 ? raw[..120] + "…" : raw;
                    _dgv.Rows[i].Cells["AGE(s)"].Value   = row.Age > 900 ? "" : row.Age.ToString("0.0");

                    Color fg, okBg, okFg; string status;
                    if (row.Age > 900)    { status = "WAIT"; fg = Palette.WaitFg; okBg = Palette.WaitBg; okFg = Palette.WaitFg; }
                    else if (row.IsStale) { status = "LOST"; fg = Palette.LostFg; okBg = Palette.LostBg; okFg = Palette.LostFg; }
                    else                  { status = "OK";   fg = Palette.OkFg;   okBg = Palette.OkBg;   okFg = Palette.OkFg; }

                    _dgv.Rows[i].Cells["STATUS"].Value            = status;
                    _dgv.Rows[i].Cells["STATUS"].Style.BackColor  = okBg;
                    _dgv.Rows[i].Cells["STATUS"].Style.ForeColor  = okFg;
                    _dgv.Rows[i].Cells["STATUS"].Style.Font       = _statusFont;
                    _dgv.Rows[i].DefaultCellStyle.ForeColor       = fg;
                    _dgv.Rows[i].DefaultCellStyle.BackColor       = Palette.CardBg;
                }
            }
            catch { }
        }
    }
}
