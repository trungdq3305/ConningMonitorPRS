using System;
using System.Drawing;
using System.Windows.Forms;
using ConningMonitorPRS.Core.Models;
using ConningMonitorPRS.UI.Theme;

namespace ConningMonitorPRS.UI.Forms
{
    public class LoginForm : Form
    {
        private TextBox _txtPass  = null!;
        private Button  _btnLogin = null!;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Palette.ApplyTitleBarTheme(Handle);
        }

        public LoginForm()
        {
            Text            = "SYSTEM LOGIN";
            Size            = new Size(400, 220);
            StartPosition   = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            BackColor       = Palette.AppBg;

            var lbl = new Label
            {
                Text      = "ENTER ADMIN PASSWORD:",
                Location  = new Point(30, 28),
                AutoSize  = true,
                Font      = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Palette.TextLabel,
                BackColor = Color.Transparent
            };

            _txtPass = new TextBox
            {
                Location     = new Point(30, 58),
                Size         = new Size(320, 30),
                Font         = new Font("Segoe UI", 12),
                PasswordChar = '*',
                BackColor    = Palette.InputBg,
                ForeColor    = Palette.TextValue,
                BorderStyle  = BorderStyle.FixedSingle
            };

            _btnLogin = new Button
            {
                Text      = "LOGIN",
                Location  = new Point(30, 106),
                Size      = new Size(150, 40),
                BackColor = Palette.BtnPrimaryBg,
                ForeColor = Palette.BtnPrimaryFg,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 10, FontStyle.Bold)
            };
            _btnLogin.FlatAppearance.BorderColor = Palette.BorderCard;
            _btnLogin.FlatAppearance.BorderSize  = 1;
            _btnLogin.Click += BtnLogin_Click;

            var btnCancel = new Button
            {
                Text      = "CANCEL",
                Location  = new Point(200, 106),
                Size      = new Size(150, 40),
                BackColor = Palette.PanelBg,
                ForeColor = Palette.TextLabel,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 10)
            };
            btnCancel.FlatAppearance.BorderColor = Palette.BorderCard;
            btnCancel.FlatAppearance.BorderSize  = 1;
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            AcceptButton = _btnLogin;
            Controls.AddRange(new Control[] { lbl, _txtPass, _btnLogin, btnCancel });
        }

        private void BtnLogin_Click(object? sender, EventArgs e)
        {
            if (_txtPass.Text == SystemConfig.AdminPassword)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                MessageBox.Show("Incorrect password. Please try again.", "Login Failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtPass.SelectAll();
                _txtPass.Focus();
            }
        }
    }
}
