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
        private readonly CheckBox _writePermission = new()
        {
            AutoCheck = false, AutoSize = true, ThreeState = false,
            Margin = new Padding(4, 4, 4, 4)
        };
        private readonly Dictionary<GcuQuickSetting, CheckBox> _quickSettings = new();
        private readonly ToolTip _tips = new();
        private readonly CheckBox _startup = new()
        {
            Text = "开机自启动", AutoSize = true, AutoCheck = false, ThreeState = true,
            Margin = new Padding(4, 4, 4, 4), AccessibleName = "开机自启动"
        };
        private bool _startupBusy;
        private bool? _startupEnabled;
        private Func<bool> _readStartup = () => new MechrevoStartup().IsScheduled();
        private Func<bool, bool> _setStartup = enabled => new MechrevoStartup().SetEnabled(enabled);
        private bool _brightnessDirty;
        private bool _rendering;

        public MoreSettingsForm(MechrevoForm main)
        {
            _main = main;
            Owner = main;
            Text = "YALM · 更多设置";
            Icon = MechrevoUi.AppIcon;
            Font = main.Font;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(500, 350);
            MinimumSize = new Size(460, 350);
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = MinimizeBox = false;
            Padding = new Padding(12);
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var backlight = Header("背光", GHelper.Properties.Resources.backlight);
            backlight.ColumnCount = 2;
            backlight.ColumnStyles[0].SizeType = SizeType.AutoSize;
            backlight.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _brightness.Height = (int)Math.Round(40F * DeviceDpi / 192F);
            backlight.Controls.Add(_brightness, 1, 0);
            AddRow(root, backlight);
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
            _writePermission.Click += (_, _) => _main.ToggleWritePermission();
            AddRow(root, _writePermission);
            foreach (GcuQuickSettingDefinition definition in GcuQuickSettings.Definitions.Where(item => item.Setting != GcuQuickSetting.WinLock))
            {
                var check = new QuickSettingCheckBox
                {
                    Text = definition.Title, AutoSize = true, AutoCheck = false, ThreeState = true,
                    Margin = new Padding(4, 4, 4, 4), AccessibleName = definition.Title
                };
                check.Click += async (_, _) => await main.ToggleQuickSettingAsync(definition.Setting);
                _quickSettings.Add(definition.Setting, check);
                AddRow(root, check);
            }
            _tips.SetToolTip(_quickSettings[GcuQuickSetting.OsdVisible], "厂商功能键图标提示，与本程序屏幕叠加分开控制");
            _tips.SetToolTip(_quickSettings[GcuQuickSetting.NumPadLock], "厂商数字键盘锁定，不等同于数字锁定键状态");
            _tips.SetToolTip(_quickSettings[GcuQuickSetting.TouchpadHotkey], "控制触摸板切换热键；不会直接开关触摸板设备");
            _tips.SetToolTip(_quickSettings[GcuQuickSetting.UsbCharging], "允许关机／休眠时为支持的接口供电");
            _tips.SetToolTip(_quickSettings[GcuQuickSetting.AcRecovery], "关机后由未接电变为接电时自动开机");
            var topmost = new CheckBox
            {
                Text = "窗口置顶", AutoSize = true, Checked = main.TopMost,
                Margin = new Padding(4, 4, 4, 4)
            };
            topmost.CheckedChanged += (_, _) =>
            {
                main.TopMost = topmost.Checked;
                if (!main._demo) AppConfig.Set("topmost", topmost.Checked ? 1 : 0);
            };
            AddRow(root, topmost);
            _startup.Enabled = !main._demo && !main._startupTest;
            _startup.CheckState = _startup.Enabled ? CheckState.Indeterminate : CheckState.Unchecked;
            _tips.SetToolTip(_startup, _startup.Enabled
                ? "当前用户登录后隐藏到托盘；不自动切换性能模式"
                : "演示和启动自检不修改登录自启动");
            _startup.Click += async (_, _) => await UpdateStartupAsync(change: true);
            AddRow(root, _startup);
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
            VisibleChanged += async (_, _) =>
            {
                if (Visible) await UpdateStartupAsync(change: false);
            };
            RefreshState();
        }

        private async Task UpdateStartupAsync(bool change)
        {
            if (_startupBusy || _main._demo || _main._startupTest || IsDisposed) return;
            _startupBusy = true;
            _startup.Enabled = false;
            bool writing = change && _startupEnabled.HasValue;
            bool target = _startupEnabled != true;
            try
            {
                _startupEnabled = await Task.Run(() => writing ? _setStartup(target) : _readStartup());
                if (IsDisposed) return;
                _tips.SetToolTip(_startup, "当前用户登录后隐藏到托盘；不自动切换性能模式");
            }
            catch (Exception error)
            {
                MechrevoStartup.LogFailure(error);
                _startupEnabled = null;
                if (writing)
                {
                    try { _startupEnabled = await Task.Run(_readStartup); }
                    catch (Exception readError) { MechrevoStartup.LogFailure(readError); }
                }
                if (IsDisposed) return;
                string message = writing ? "开机自启动未能修改，请检查系统任务计划程序或以管理员身份运行后重试。"
                    : "开机自启动状态暂时无法读取，点击重试。";
                _tips.SetToolTip(_startup, message);
                _main.ShowActionStatus(message);
            }
            finally
            {
                _startupBusy = false;
                if (!IsDisposed)
                {
                    _startup.CheckState = _startupEnabled is null ? CheckState.Indeterminate
                        : _startupEnabled == true ? CheckState.Checked : CheckState.Unchecked;
                    _startup.AccessibleName = "开机自启动：" + (_startupEnabled is null ? "未确认"
                        : _startupEnabled == true ? "已开启" : "已关闭");
                    _startup.Enabled = true;
                }
            }
        }

        public void RefreshState()
        {
            if (IsDisposed) return;
            GcuSnapshot snapshot = _main.KeyboardSnapshot;
            bool available = snapshot.IsKeyboardFresh() && snapshot.Keyboard is { SingleZone: true, Brightness: not null };
            bool idle = !_main._keyboardBusy && !_main._connecting && !_main._switching && !_main._startupTest;
            _rendering = true;
            _writePermission.Text = _main.WritePermissionTitle;
            _writePermission.Checked = _main.WritePermissionGranted;
            _writePermission.Enabled = _main.CanToggleWritePermission && idle;
            _writePermission.AccessibleName = _main.WritePermissionTitle + "：" +
                (_main.WritePermissionGranted ? "已开启" : _main.WritePermissionDescription);
            _tips.SetToolTip(_writePermission, _main.WritePermissionDescription);
            if (!_brightnessDirty && !_main._keyboardBusy) _brightness.Value = snapshot.Keyboard?.Brightness ?? 0;
            foreach (var (setting, check) in _quickSettings)
            {
                GcuQuickSettingState state = snapshot.QuickSetting(setting);
                bool fresh = snapshot.IsConnected && state.IsFresh();
                check.CheckState = !fresh ? CheckState.Indeterminate : state.Enabled == true ? CheckState.Checked : CheckState.Unchecked;
                check.Enabled = fresh && idle && !_main._demo && _main._machine.SupportsPeripheralControl &&
                    _main._machineBlockReason is null;
                check.AccessibleName = check.Text + "：" + (!fresh ? "未收到" : state.Enabled == true ? "开启" : "关闭");
            }
            _rendering = false;
            _brightness.AccessibleName = available ? $"背光亮度：{_brightness.Value} / 4" : "背光亮度：未收到";
            _brightness.Enabled = available && idle && snapshot.Keyboard?.Power != false &&
                (_main._demo || _main._machine.SupportsPeripheralControl && _main._machineBlockReason is null);
            if (!available) _brightnessDirty = false;
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
