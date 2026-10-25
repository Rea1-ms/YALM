using GHelper.UI;
using System.Diagnostics;
using System.Text.Json;

namespace YALM.Mechrevo;

internal sealed partial class MechrevoForm
{
    private sealed class MoreSettingsForm : RForm
    {
        private readonly MechrevoForm _main;
        private readonly Slider _brightness = new()
        {
            Min = 0, Max = 4, Step = 1, Value = 0,
            Dock = DockStyle.Fill, Margin = Padding.Empty, TabStop = true
        };
        private readonly RCheckBox _backlightPower = new()
        {
            Text = "开启", AutoSize = true, AutoCheck = false, ThreeState = true,
            Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 12, 0),
            AccessibleName = "键盘背光总开关"
        };
        private readonly ToolTip _tips = new();
        private bool _brightnessDirty;
        private bool _rendering;

        public MoreSettingsForm(MechrevoForm main)
        {
            _main = main;
            Owner = main;
            Text = "YALM · 更多设置";
            Font = main.Font;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(500, 235);
            MinimumSize = new Size(460, 235);
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = MinimizeBox = false;
            Padding = new Padding(12);
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var backlight = Header("背光", GHelper.Properties.Resources.backlight);
            backlight.ColumnCount = 3;
            backlight.ColumnStyles[0].SizeType = SizeType.AutoSize;
            backlight.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            backlight.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            backlight.Controls.Add(_backlightPower, 1, 0);
            _brightness.Height = (int)Math.Round(40F * DeviceDpi / 192F);
            backlight.Controls.Add(_brightness, 2, 0);
            AddRow(root, backlight);
            _tips.SetToolTip(_backlightPower, "背光总开关，保留亮度、灯效和颜色配置");
            _backlightPower.Click += async (_, _) => await ToggleBacklightPowerAsync();
            _tips.SetToolTip(_brightness, "背光亮度 0～4 档，松开后应用");
            _brightness.ValueChanged += (_, _) =>
            {
                if (_rendering) return;
                _brightnessDirty = true;
                _brightness.AccessibleName = $"背光亮度：{_brightness.Value} / 4";
            };
            _brightness.MouseUp += async (_, _) => await ApplyBrightnessAsync();
            _brightness.KeyUp += async (_, e) =>
            {
                if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down) await ApplyBrightnessAsync();
            };
            _brightness.Leave += async (_, _) => await ApplyBrightnessAsync();

            var other = Header("其他", GHelper.Properties.Resources.icons8_settings_32);
            other.ColumnCount = 3;
            int iconSize = (int)Math.Round(32F * DeviceDpi / 192F);
            int cellSize = iconSize + 12;
            other.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, cellSize));
            other.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, cellSize));
            other.Controls.Add(IconButton(GHelper.Properties.Resources.icons8_search_32, "读取并打开诊断信息", ExportDiagnostics), 1, 0);
            other.Controls.Add(IconButton(GHelper.Properties.Resources.icons8_log_32, "打开日志", OpenLog), 2, 0);
            AddRow(root, other);
            var topmost = new CheckBox
            {
                Text = "窗口置顶", AutoSize = true, Checked = main.TopMost,
                Margin = new Padding(4, 8, 4, 6)
            };
            topmost.CheckedChanged += (_, _) =>
            {
                main.TopMost = topmost.Checked;
                if (!main._demo) AppConfig.Set("topmost", topmost.Checked ? 1 : 0);
            };
            AddRow(root, topmost);
            var overlay = new RButton
            {
                Text = "屏幕叠加设置", Margin = new Padding(2), BorderColor = Color.Transparent,
                BorderRadius = 2, Secondary = true, Width = 150, Anchor = AnchorStyles.Left,
                Enabled = !main._startupTest
            };
            MechrevoUi.SizeCompactButtons(this, overlay);
            overlay.Click += (_, _) => main.OpenOverlayConfig();
            AddRow(root, overlay);
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            scroll.Controls.Add(root);
            Controls.Add(scroll);
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
            GcuSnapshot snapshot = _main.KeyboardSnapshot;
            bool available = snapshot.IsKeyboardFresh() && snapshot.Keyboard is { SingleZone: true, Brightness: not null };
            bool powerAvailable = snapshot.IsKeyboardFresh() && snapshot.Keyboard is { SingleZone: true, Power: not null };
            bool idle = !_main._keyboardBusy && !_main._connecting && !_main._switching && !_main._startupTest;
            _rendering = true;
            if (!_brightnessDirty && !_main._keyboardBusy) _brightness.Value = snapshot.Keyboard?.Brightness ?? 0;
            _backlightPower.CheckState = !powerAvailable ? CheckState.Indeterminate
                : snapshot.Keyboard!.Power == true ? CheckState.Checked : CheckState.Unchecked;
            _backlightPower.Text = !powerAvailable ? "未收到" : snapshot.Keyboard!.Power == true ? "开启" : "关闭";
            _rendering = false;
            _backlightPower.AccessibleName = $"键盘背光总开关：{_backlightPower.Text}";
            _backlightPower.Enabled = powerAvailable && idle && !_main._demo;
            _brightness.AccessibleName = available ? $"背光亮度：{_brightness.Value} / 4" : "背光亮度：未收到";
            _brightness.Enabled = available && idle && snapshot.Keyboard?.Power != false;
            if (!available) _brightnessDirty = false;
        }

        private async Task ToggleBacklightPowerAsync()
        {
            if (!_backlightPower.Enabled || _main._demo || _main._keyboardBusy ||
                _main._controller is not { } controller || _main.KeyboardSnapshot.Keyboard?.Power is not bool on) return;
            await _main.RunKeyboardAsync(() => controller.SetKeyboardPowerAsync(!on, _main._lifetime.Token));
        }

        private async Task ApplyBrightnessAsync()
        {
            if (!_brightnessDirty || _main._demo || _main._keyboardBusy || !_brightness.Enabled ||
                _main._controller is not { } controller) return;
            int value = _brightness.Value;
            _brightnessDirty = false;
            await _main.RunKeyboardAsync(() => controller.SetKeyboardBrightnessAsync(value, _main._lifetime.Token));
        }

        private PictureBox IconButton(Image image, string text, Action click)
        {
            int size = (int)Math.Round(32F * DeviceDpi / 192F);
            var button = new PictureBox
            {
                BackgroundImage = image, BackgroundImageLayout = ImageLayout.Zoom, Size = new Size(size, size),
                Anchor = AnchorStyles.Right, Margin = new Padding(6, 0, 0, 0),
                Cursor = Cursors.Hand, AccessibleName = text, AccessibleRole = AccessibleRole.PushButton
            };
            _tips.SetToolTip(button, text);
            button.Click += (_, _) => click();
            return button;
        }

        private async void ExportDiagnostics()
        {
            if (_main._demo || _main._startupTest) return;
            await _main.RefreshAsync();
            if (IsDisposed) return;
            try
            {
                GcuSnapshot snapshot = _main._controller?.Snapshot ?? new();
                // Whitelist observations; never export connection options or raw errors.
                var report = new
                {
                    CapturedAt = DateTimeOffset.Now, _main._machine.DisplayName, _main._machine.BiosVersion,
                    snapshot.IsConnected, snapshot.Mode, snapshot.ModeUpdatedAt,
                    snapshot.Keyboard, snapshot.KeyboardUpdatedAt, SavedKeyboard = _main.ReadSavedKeyboard(snapshot),
                    _main._ecSnapshot
                };
                Directory.CreateDirectory(Logger.appPath);
                string path = Path.Combine(Logger.appPath, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmssfff}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { MessageBox.Show(this, "诊断信息无法保存或打开。", "诊断信息"); }
        }

        private void OpenLog()
        {
            if (_main._demo || _main._startupTest) return;
            try
            {
                if (!File.Exists(Logger.logFile)) Logger.WriteLine("打开助手日志。");
                Process.Start(new ProcessStartInfo(Logger.logFile) { UseShellExecute = true });
            }
            catch { MessageBox.Show(this, "日志无法打开。", "日志"); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tips.Dispose();
            base.Dispose(disposing);
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
    }
}
