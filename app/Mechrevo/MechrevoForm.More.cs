using GHelper.UI;

namespace YALM.Mechrevo;

internal sealed partial class MechrevoForm
{
    // Match upstream Extra: one window for backlight and the remaining settings.
    // Keep the hardware adapter independent of upstream ASUS initialization.
    private sealed class MoreSettingsForm : RForm
    {
        private readonly MechrevoForm _main;
        private readonly KeyboardSettingsPanel _keyboard;
        private readonly RButton _connectionToggle = MoreButton("展开");
        private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true };

        public MoreSettingsForm(MechrevoForm main)
        {
            _main = main;
            Owner = main;
            Text = "YALM · 更多设置";
            Font = main.Font;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(500, 610);
            MinimumSize = new Size(460, 430);
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            Padding = new Padding(12);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
                Margin = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(root, Header("背光", GHelper.Properties.Resources.icons8_brightness_32));
            _keyboard = new KeyboardSettingsPanel(() => _main._controller, main._demo);
            AddRow(root, _keyboard);

            AddRow(root, Header("其他", GHelper.Properties.Resources.icons8_settings_32));
            var topmost = new CheckBox
            {
                Text = "窗口置顶", AutoSize = true, Checked = main.TopMost,
                Margin = new Padding(4, 8, 4, 6)
            };
            topmost.CheckedChanged += (_, _) =>
            {
                _main.TopMost = topmost.Checked;
                if (!_main._demo) AppConfig.Set("topmost", topmost.Checked ? 1 : 0);
            };
            AddRow(root, topmost);
            var overlay = MoreButton("屏幕叠加设置");
            MechrevoUi.SizeCompactButtons(this, _connectionToggle, overlay);
            overlay.Width = 150;
            overlay.Dock = DockStyle.None;
            overlay.Anchor = AnchorStyles.Left;
            overlay.Enabled = !main._startupTest;
            overlay.Click += (_, _) => _main.OpenOverlayConfig();
            AddRow(root, overlay);

            var connectionHeader = Header("连接与状态", GHelper.Properties.Resources.icons8_settings_32);
            connectionHeader.ColumnCount = 2;
            connectionHeader.ColumnStyles[0].SizeType = SizeType.Percent;
            connectionHeader.ColumnStyles[0].Width = 100;
            connectionHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            connectionHeader.Controls.Add(_connectionToggle, 1, 0);
            AddRow(root, connectionHeader);
            main._details.Visible = false;
            main._connectionSettings.Visible = !main._demo;
            AddRow(root, main._details);
            _connectionToggle.Click += (_, _) => SetConnectionVisible(!main._details.Visible);

            _scroll.Controls.Add(root);
            Controls.Add(_scroll);
            InitTheme(true);
            FormClosing += (_, e) =>
            {
                if (e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                Hide();
            };
            Shown += (_, _) => PositionBesideMain();
            RefreshState();
        }

        public void RefreshState()
        {
            if (IsDisposed) return;
            _keyboard.RefreshState();
        }

        public void SelectColor()
        {
            _scroll.ScrollControlIntoView(_keyboard);
            _keyboard.SelectColor();
        }

        public void ShowConnection()
        {
            SetConnectionVisible(true);
            _scroll.ScrollControlIntoView(_main._connectionSettings);
            _main._clientId.Focus();
        }

        private void SetConnectionVisible(bool visible)
        {
            _main._details.Visible = visible;
            _connectionToggle.Text = visible ? "收起" : "展开";
        }

        private void PositionBesideMain()
        {
            Rectangle area = Screen.FromControl(_main).WorkingArea;
            Height = Math.Min(Height, area.Height - 20);
            int left = _main.Left - Width - 5;
            if (left < area.Left) left = Math.Min(_main.Right + 5, area.Right - Width);
            Location = new Point(Math.Max(area.Left, left),
                Math.Clamp(_main.Bottom - Height, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }

        private TableLayoutPanel Header(string text, Image icon)
        {
            var label = new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            var header = new TableLayoutPanel
            {
                Name = "SettingsHeader", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
                Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 10, 0, 4)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.Controls.Add(MechrevoUi.Heading(this, label, icon), 0, 0);
            return header;
        }

        private static RButton MoreButton(string text) => new()
        {
            Text = text, Dock = DockStyle.Top, Margin = new Padding(2),
            BorderColor = Color.Transparent, BorderRadius = 2, Secondary = true
        };
    }
}
