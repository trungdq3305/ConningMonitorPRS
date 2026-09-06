using System.Drawing;
using System.Runtime.InteropServices;

namespace ConningMonitorPRS.UI.Theme
{
    public static class Palette
    {
        public static bool IsLight { get; set; } = false;

        // Apply dark/light title bar chrome via DWM — Windows 10 2004+ / Windows 11.
        // Must be called after the window handle is created (OnHandleCreated or later),
        // and again whenever the theme changes for any form that stays open across a switch.
        public static void ApplyTitleBarTheme(System.IntPtr hwnd)
        {
            int useDark = IsLight ? 0 : 1;
            try { DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref useDark, 4); } catch { }
        }

        [DllImport("dwmapi.dll", PreserveSig = false)]
        private static extern void DwmSetWindowAttribute(System.IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // Built by MainForm.ApplyTheme() each time the theme is switched (old→new color pairs).
        // Other open forms (ConfigForm, DataListForm) read this to apply the same swap without
        // needing to re-snapshot the palette themselves.
        public static System.Collections.Generic.Dictionary<Color, Color>? CurrentSwapMap { get; set; }

        // Walk every control in root and swap BackColor / ForeColor / FlatAppearance border
        // using the map built by MainForm. Skips controls whose color is not in the map.
        public static void ApplyToForm(System.Windows.Forms.Control root)
        {
            if (CurrentSwapMap == null) return;
            root.SuspendLayout();
            ApplySwapMap(root, CurrentSwapMap);
            root.ResumeLayout(false);
        }

        private static void ApplySwapMap(System.Windows.Forms.Control root,
            System.Collections.Generic.Dictionary<Color, Color> map)
        {
            foreach (System.Windows.Forms.Control c in root.Controls)
            {
                if (map.TryGetValue(c.BackColor, out Color nb)) c.BackColor = nb;
                if (map.TryGetValue(c.ForeColor, out Color nf)) c.ForeColor = nf;
                if (c is System.Windows.Forms.Button btn &&
                    map.TryGetValue(btn.FlatAppearance.BorderColor, out Color nbdr))
                    btn.FlatAppearance.BorderColor = nbdr;
                if (c is System.Windows.Forms.DataGridView dgv)
                    ApplyDgvTheme(dgv, map);
                if (c.Controls.Count > 0)
                    ApplySwapMap(c, map);
            }
        }

        private static void ApplyDgvTheme(System.Windows.Forms.DataGridView dgv,
            System.Collections.Generic.Dictionary<Color, Color> map)
        {
            if (map.TryGetValue(dgv.BackgroundColor, out Color dbg)) dgv.BackgroundColor = dbg;
            if (map.TryGetValue(dgv.GridColor,       out Color dgc)) dgv.GridColor       = dgc;
            ApplyDgvCellStyle(dgv.ColumnHeadersDefaultCellStyle, map);
            ApplyDgvCellStyle(dgv.DefaultCellStyle, map);
            foreach (System.Windows.Forms.DataGridViewColumn col in dgv.Columns)
                ApplyDgvCellStyle(col.DefaultCellStyle, map);
            foreach (System.Windows.Forms.DataGridViewRow row in dgv.Rows)
                ApplyDgvCellStyle(row.DefaultCellStyle, map);
        }

        private static void ApplyDgvCellStyle(
            System.Windows.Forms.DataGridViewCellStyle style,
            System.Collections.Generic.Dictionary<Color, Color> map)
        {
            if (map.TryGetValue(style.BackColor,          out Color nb))  style.BackColor          = nb;
            if (map.TryGetValue(style.ForeColor,          out Color nf))  style.ForeColor          = nf;
            if (map.TryGetValue(style.SelectionBackColor, out Color nsb)) style.SelectionBackColor = nsb;
            if (map.TryGetValue(style.SelectionForeColor, out Color nsf)) style.SelectionForeColor = nsf;
        }

        // BACKGROUNDS
        public static Color AppBg        => IsLight ? Color.FromArgb(0xCC, 0xDD, 0xEE) : Color.FromArgb(0x08, 0x0C, 0x14);
        public static Color PanelBg      => IsLight ? Color.FromArgb(0xA4, 0xBE, 0xD8) : Color.FromArgb(0x0E, 0x16, 0x24);
        public static Color CardBg       => IsLight ? Color.FromArgb(0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0x22, 0x34);
        public static Color CardFace     => IsLight ? Color.FromArgb(0xEB, 0xF3, 0xFF) : Color.FromArgb(0x1E, 0x2E, 0x46);
        public static Color ChartBg      => IsLight ? Color.FromArgb(0xF4, 0xF9, 0xFF) : Color.FromArgb(0x0A, 0x12, 0x20);
        public static Color SurfaceHi    => IsLight ? Color.FromArgb(0xC0, 0xD4, 0xEC) : Color.FromArgb(0x1C, 0x2E, 0x46);
        public static Color SectionHdrBg => IsLight ? Color.FromArgb(0x3A, 0x68, 0x9C) : Color.FromArgb(0x1A, 0x38, 0x68);
        public static Color InputBg      => IsLight ? Color.FromArgb(0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0x22, 0x34);

        // BORDERS
        public static Color BorderCard   => IsLight ? Color.FromArgb(0x70, 0x9C, 0xC4) : Color.FromArgb(0x2E, 0x4C, 0x78);
        public static Color BorderPanel  => IsLight ? Color.FromArgb(0x7E, 0xA4, 0xC4) : Color.FromArgb(0x20, 0x36, 0x58);
        public static Color GridLine     => IsLight ? Color.FromArgb(0xA0, 0xBE, 0xD8) : Color.FromArgb(0x1C, 0x2E, 0x48);

        // TEXT
        public static Color TextLabel    => IsLight ? Color.FromArgb(0x1E, 0x44, 0x70) : Color.FromArgb(0x68, 0x8C, 0xB4);
        public static Color TextValue    => IsLight ? Color.FromArgb(0x04, 0x0C, 0x18) : Color.FromArgb(0xDD, 0xEE, 0xFF);
        public static Color TextGps      => IsLight ? Color.FromArgb(0x00, 0x50, 0xA0) : Color.FromArgb(0x50, 0xCC, 0xFF);
        public static Color TextDim      => IsLight ? Color.FromArgb(0x60, 0x80, 0x9C) : Color.FromArgb(0x38, 0x50, 0x68);

        // STATUS
        public static Color OkBg         => IsLight ? Color.FromArgb(0xB8, 0xEC, 0xCC) : Color.FromArgb(0x06, 0x22, 0x12);
        public static Color OkFg         => IsLight ? Color.FromArgb(0x00, 0x5E, 0x2C) : Color.FromArgb(0x00, 0xCC, 0x66);
        public static Color WaitBg       => IsLight ? Color.FromArgb(0xDC, 0xE8, 0xF6) : Color.FromArgb(0x10, 0x1A, 0x2C);
        public static Color WaitFg       => IsLight ? Color.FromArgb(0x38, 0x64, 0x90) : Color.FromArgb(0x70, 0x98, 0xC2);
        public static Color LostBg       => IsLight ? Color.FromArgb(0xF0, 0xD4, 0xD4) : Color.FromArgb(0x2C, 0x08, 0x08);
        public static Color LostFg       => IsLight ? Color.FromArgb(0xB8, 0x20, 0x00) : Color.FromArgb(0xFF, 0x44, 0x44);

        // ALARM (ISA-101)
        public static Color AlarmActiveBg => IsLight ? Color.FromArgb(0xFF, 0xD4, 0xD4) : Color.FromArgb(0x3A, 0x00, 0x00);
        public static Color AlarmActiveFg => IsLight ? Color.FromArgb(0xB8, 0x00, 0x00) : Color.FromArgb(0xFF, 0x24, 0x24);
        public static Color AlarmAckBg    => IsLight ? Color.FromArgb(0xFF, 0xEE, 0xC0) : Color.FromArgb(0x2A, 0x14, 0x00);
        public static Color AlarmAckFg    => IsLight ? Color.FromArgb(0x94, 0x5E, 0x00) : Color.FromArgb(0xFF, 0xB8, 0x00);
        public static Color AlarmNormalBg => IsLight ? Color.FromArgb(0xB8, 0xEC, 0xCC) : Color.FromArgb(0x06, 0x20, 0x12);
        public static Color AlarmNormalFg => IsLight ? Color.FromArgb(0x00, 0x5E, 0x2C) : Color.FromArgb(0x00, 0xCC, 0x66);
        public static Color ValueAlarm    => IsLight ? Color.FromArgb(0xB8, 0x00, 0x00) : Color.FromArgb(0xFF, 0x24, 0x24);
        public static Color ValueAck      => IsLight ? Color.FromArgb(0x94, 0x5E, 0x00) : Color.FromArgb(0xFF, 0xB8, 0x00);

        // CHART SERIES
        public static Color SeriesRoll   => IsLight ? Color.FromArgb(0x00, 0x78, 0xCC) : Color.FromArgb(0x00, 0xC0, 0xF0);
        public static Color SeriesPitch  => IsLight ? Color.FromArgb(0xCC, 0x88, 0x00) : Color.FromArgb(0xFF, 0xB8, 0x00);
        public static Color SeriesHeave  => IsLight ? Color.FromArgb(0x88, 0x28, 0xCC) : Color.FromArgb(0xB8, 0x60, 0xFF);
        public static Color SeriesWSpeed => IsLight ? Color.FromArgb(0x00, 0x8A, 0x50) : Color.FromArgb(0x44, 0xE0, 0x9C);
        public static Color SeriesWDir   => IsLight ? Color.FromArgb(0xBB, 0x99, 0x00) : Color.FromArgb(0xFF, 0xD8, 0x40);

        // RADAR
        public static Color RadarBg        => IsLight ? Color.FromArgb(0xEC, 0xF4, 0xFF) : Color.FromArgb(0x08, 0x10, 0x1E);
        public static Color RadarFace      => IsLight ? Color.FromArgb(0xF4, 0xF9, 0xFF) : Color.FromArgb(0x0B, 0x16, 0x2A);
        public static Color RadarBorder    => IsLight ? Color.FromArgb(0x5E, 0x84, 0xB0) : Color.FromArgb(0x22, 0x3C, 0x5C);
        public static Color RadarGrid      => IsLight ? Color.FromArgb(0xAE, 0xC8, 0xE2) : Color.FromArgb(0x16, 0x28, 0x44);
        public static Color RadarTick      => IsLight ? Color.FromArgb(0x7E, 0xA0, 0xC6) : Color.FromArgb(0x2A, 0x46, 0x6A);
        public static Color RadarText      => IsLight ? Color.FromArgb(0x3C, 0x60, 0x90) : Color.FromArgb(0x56, 0x78, 0xAA);
        public static Color RadarNorth     => IsLight ? Color.FromArgb(0x00, 0x6C, 0xCC) : Color.FromArgb(0x00, 0xC4, 0xFF);
        public static Color RadarTrail     => IsLight ? Color.FromArgb(0x00, 0x8A, 0x50) : Color.FromArgb(0x44, 0xE0, 0x9C);
        // Target markers/shapes on ConningControl's radar — magenta, the traditional chart
        // color for marks/danger areas, deliberately distinct from every other color already
        // in use on the ring (orange=ship, green=lateral/track, blue=axial, purple=heave,
        // gold=wind, red=drift).
        public static Color RadarTargetShape => IsLight ? Color.FromArgb(0xB8, 0x00, 0x8C) : Color.FromArgb(0xFF, 0x4D, 0xE0);

        // Accent used to mark a control as clickable (e.g. the "⇄" hint on POSITION/SPEED
        // cards) — deliberately distinct from OkFg (value text) and the button palette so it
        // reads as an affordance, not just more data.
        public static Color ClickHint      => IsLight ? Color.FromArgb(0x00, 0x78, 0xCC) : Color.FromArgb(0x00, 0xD4, 0xFF);

        // CONNING (ConningControl compass ring — labels/lines only; ring/ship/arrow fill
        // colors stay fixed brand colors, already legible on both themes)
        public static Color ConningCardText  => IsLight ? Color.FromArgb(0x1E, 0x44, 0x70) : Color.White;
        public static Color ConningTickLabel => IsLight ? Color.FromArgb(0x2E, 0x6A, 0x3E) : Color.FromArgb(140, 210, 140);
        public static Color ConningHdgLine   => IsLight ? Color.FromArgb(90, 0x1E, 0x44, 0x70) : Color.FromArgb(90, 255, 255, 255);
        public static Color ConningWindArrow => IsLight ? Color.FromArgb(0x00, 0x6E, 0xB0) : Color.FromArgb(100, 215, 255);
        // Value-tag box (LONG/BOW/STN/WIND): dark "readout" style at night; a light card at day
        // so it doesn't read as a stray black box against the light compass background.
        public static Color ConningTagBg     => IsLight ? Color.FromArgb(255, 0xF2, 0xF6, 0xFC) : Color.FromArgb(255, 5, 10, 22);
        public static Color ConningLateral   => IsLight ? Color.FromArgb(0x12, 0x8A, 0x30) : Color.FromArgb(40, 210, 70);
        public static Color ConningAxial     => IsLight ? Color.FromArgb(0x0E, 0x5A, 0xC8) : Color.FromArgb(60, 145, 255);

        // BUTTONS
        public static Color BtnPrimaryBg  => IsLight ? Color.FromArgb(0xAE, 0xCC, 0xEA) : Color.FromArgb(0x18, 0x2C, 0x4E);
        public static Color BtnPrimaryFg  => IsLight ? Color.FromArgb(0x0C, 0x34, 0x64) : Color.FromArgb(0x80, 0xB6, 0xE2);
        public static Color BtnSettingsBg => IsLight ? Color.FromArgb(0xFF, 0xEC, 0xBC) : Color.FromArgb(0x28, 0x16, 0x00);
        public static Color BtnSettingsFg => IsLight ? Color.FromArgb(0x76, 0x42, 0x00) : Color.FromArgb(0xFF, 0xB8, 0x00);
        public static Color BtnActiveBg   => IsLight ? Color.FromArgb(0x15, 0x65, 0xC0) : Color.FromArgb(0x1A, 0x6B, 0xB5);
        public static Color BtnActiveFg   => IsLight ? Color.FromArgb(0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0xFF, 0xFF);
        public static Color BtnActiveBorder => IsLight ? Color.FromArgb(0x64, 0xB5, 0xF6) : Color.FromArgb(0x64, 0xB5, 0xF6);
    }
}
