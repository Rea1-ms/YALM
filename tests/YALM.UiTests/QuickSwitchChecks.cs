using System.Text.Json;
using YALM.Mechrevo;

namespace YALM.UiTests;

internal static partial class Program
{
    private static void CheckQuickSwitches(bool render)
    {
        using var main = CreateForm(); // Never shown: only the fake transport supplies observations.
        var transport = new QuickTransport();
        var controller = new GcuController(transport, confirmationTimeout: TimeSpan.FromSeconds(1),
            confirmationPollInterval: TimeSpan.FromMilliseconds(5));
        controller.ConnectAsync(new GcuConnectionOptions("offline", "offline", "offline")).GetAwaiter().GetResult();
        Set(main, "_controller", controller);
        Type moreType = MainForm.GetNestedType("MoreSettingsForm", System.Reflection.BindingFlags.NonPublic)!;
        using var more = (Form)Activator.CreateInstance(moreType, main)!;
        Set(main, "_settingsForm", more);
        try
        {
            Call(main, "RenderSnapshot");
            foreach (Control control in Descendants(main).Concat(Descendants(more))) _ = control.Handle;
            main.PerformLayout();
            more.PerformLayout();
            Require((ReadWindowStyle(main.Handle, -16) & 0x00040000) == 0,
                "main native window has no user-resizable border");
            var headerButtons = new[] { "_backlightButton", "_overlayButton", "_fnLock", "_winLock" }
                .Select(name => Get<Button>(main, name)).ToArray();
            for (int index = 0; index < headerButtons.Length; index++)
            {
                Button button = headerButtons[index];
                Require(button.Top == headerButtons[0].Top && button.Bottom == headerButtons[0].Bottom,
                    "keyboard header controls align");
                Require(button.Width >= button.GetPreferredSize(Size.Empty).Width,
                    $"keyboard header includes native button text insets: {button.Text}, actual={button.Width}, preferred={button.GetPreferredSize(Size.Empty).Width}");
                if (index > 0) Require(headerButtons[index - 1].Right <= button.Left, "backlight, overlay, FN and Win appear in order");
            }
            Label keyboardTitle = Descendants(main).OfType<Label>().Single(label => label.Text == "笔记本键盘");
            Require(keyboardTitle.Right <= keyboardTitle.Parent!.ClientSize.Width &&
                keyboardTitle.Parent.Right <= headerButtons[0].Left,
                "keyboard title remains complete beside the content-sized switches");
            var checks = Get<Dictionary<GcuQuickSetting, CheckBox>>(more, "_quickSettings");
            Require(checks.Count == 5 && Descendants(more).OfType<CheckBox>().Count() == 8 &&
                Descendants(more).OfType<CheckBox>().Any(check => check.Text.StartsWith("允许写入", StringComparison.Ordinal)),
                "other contains five vendor switches, write permission, topmost and startup");
            CheckBox[] otherChecks = Descendants(more).OfType<CheckBox>().ToArray();
            for (int index = 0; index < otherChecks.Length; index++)
            {
                CheckBox check = otherChecks[index];
                Require(check.Width >= check.PreferredSize.Width && check.Height >= check.PreferredSize.Height &&
                    check.Left >= 0 && check.Right <= check.Parent!.ClientSize.Width,
                    "other checkbox fits its row: " + check.Text);
                if (index > 0) Require(otherChecks[index - 1].Bottom <= check.Top, "other checkbox rows do not overlap");
            }
            Require(!Descendants(more).Any(control => control.AccessibleName?.StartsWith("键盘背光总开关") == true),
                "backlight power has one entry on the main window");
            Require(checks.Values.All(check => !check.AutoCheck && check.Enabled), "settings display observed state and start available");

            Button win = Get<Button>(main, "_winLock");
            Require(!Active(win) && win.Enabled, "reported unlocked Windows key is available");
            transport.HoldStatus = true;
            Click(win);
            Require(Get<bool>(main, "_keyboardBusy") && !win.Enabled && !Active(win) && checks.Values.All(check => !check.Enabled),
                "pending lock retains observed state and disables related writes");
            Click(win);
            Require(transport.Writes.Count == 1, "pending Win clicks publish only once");
            transport.HoldStatus = false;
            PumpUntil(() => !Get<bool>(main, "_keyboardBusy"));
            Require(Active(win) && win.Enabled, "fresh Win lock report activates the button");
            Click(win);
            PumpUntil(() => !Get<bool>(main, "_keyboardBusy"));
            Require(!Active(win) && transport.Writes.Count == 2, "unlock restores the observed state");

            CheckBox osd = checks[GcuQuickSetting.OsdVisible];
            Require(!osd.Checked, "hidden vendor OSD starts as show-prompt unchecked");
            Click(osd);
            PumpUntil(() => !Get<bool>(main, "_keyboardBusy"));
            Require(osd.Checked && transport.Writes.Last().Contains("OSD_HIDDEN_OFF"), "show-prompt checkbox sends the inverse hidden command");

            Button power = Get<Button>(main, "_backlightButton");
            Control brightness = Get<Control>(more, "_brightness");
            Require(Active(power) && brightness.Enabled, "reported backlight starts on");
            transport.HoldStatus = true;
            Click(power);
            Require(Active(power) && !power.Enabled, "pending backlight write keeps the old observed state");
            Click(power);
            Require(transport.Writes.Count(payload => payload.Contains("SetPower")) == 1, "repeated backlight click sends one command");
            transport.HoldStatus = false;
            PumpUntil(() => !Get<bool>(main, "_keyboardBusy"));
            Require(!Active(power) && !brightness.Enabled && ReadBrightness(brightness) == 3, "power off disables brightness but preserves its value");
            Click(power);
            PumpUntil(() => !Get<bool>(main, "_keyboardBusy"));
            Require(Active(power) && brightness.Enabled && ReadBrightness(brightness) == 3, "power on restores access to retained brightness");
            foreach (string payload in transport.Writes.Where(payload => payload.Contains("SetPower")))
            {
                using var command = JsonDocument.Parse(payload);
                Require(command.RootElement.EnumerateObject().Count() == 2, "backlight toggle does not carry lighting settings");
            }

            transport.Settings["WinKey"] = "unsupported";
            transport.Settings["UsbCharger"] = "unsupported";
            transport.EmitSettings();
            Call(main, "RenderSnapshot");
            Require(!win.Enabled && win.AccessibleName?.Contains("未收到") == true &&
                checks[GcuQuickSetting.UsbCharging].CheckState == CheckState.Indeterminate && !checks[GcuQuickSetting.UsbCharging].Enabled,
                "unknown state disables the individual switch without displaying off");
            Require(checks[GcuQuickSetting.NumPadLock].Enabled, "missing state does not disable another independent switch");

            if (render)
            {
                transport.Settings["WinKey"] = "WINKEY_STATUS_UNLOCK";
                transport.Settings["FnKey"] = "FNKEY_LOCK";
                transport.Settings["UsbCharger"] = "USB_CHARGER_STATUS_OFF";
                transport.EmitSettings();
                Call(main, "RenderSnapshot");
                string directory = Path.GetFullPath(".cache/window-size-20261026/ui");
                Directory.CreateDirectory(directory);
                foreach (var (form, name) in new[] { (main, "main"), (more, "more") })
                {
                    using var preview = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(preview, new Rectangle(Point.Empty, form.Size));
                    // Native checkboxes omit WM_PRINT output when their parent form is hidden.
                    // Request each native client directly; do not change the production drawing.
                    foreach (CheckBox check in Descendants(form).OfType<CheckBox>())
                    {
                        using var native = new Bitmap(check.Width, check.Height);
                        using (Graphics graphics = Graphics.FromImage(native))
                        {
                            graphics.Clear(check.BackColor);
                            nint dc = graphics.GetHdc();
                            try { PrintNativeControl(check.Handle, 0x318, dc, (nint)4); }
                            finally { graphics.ReleaseHdc(dc); }
                        }
                        Point origin = check.PointToScreen(Point.Empty) - (Size)form.Location;
                        using Graphics target = Graphics.FromImage(preview);
                        target.DrawImageUnscaled(native, origin);
                    }
                    preview.Save(Path.Combine(directory, $"{name}-{main.DeviceDpi}.png"));
                }
            }
        }
        finally
        {
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Set(main, "_controller", null);
            Set(main, "_settingsForm", null);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint PrintNativeControl(nint window, int message, nint wParam, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int ReadWindowStyle(nint window, int index);

    private static bool Active(Control control) => (bool)control.GetType().GetProperty("Activated")!.GetValue(control)!;
    private static int ReadBrightness(Control control) => (int)control.GetType().GetProperty("Value")!.GetValue(control)!;
    private static void Click(Control control)
    {
        // DoEvents runs a temporary message loop and can uninstall its context.
        // Match the context a user click has inside Application.Run.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        control.GetType().GetMethod("OnClick", Private)!.Invoke(control, [EventArgs.Empty]);
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class QuickTransport : IGcuTransport
    {
        public bool IsConnected { get; private set; }
        public bool HoldStatus;
        public readonly List<string> Writes = [];
        public readonly Dictionary<string, string> Settings = new()
        {
            ["FnKey"] = "FNKEY_UNLOCK", ["WinKey"] = "WINKEY_STATUS_UNLOCK", ["NumPad"] = "NUMPAD_UNLOCK",
            ["OSD"] = "OSD_HIDDEN_ON", ["TouchpadToggle"] = "TOUCHPAD_TOGGLE_ON",
            ["UsbCharger"] = "USB_CHARGER_STATUS_OFF", ["AcRecoverySwitch_Status"] = "ACRECOVERY_TOGGLE_ON"
        };
        private (string Field, string Value)? _pendingSetting;
        private bool? _pendingPower;
        private bool _power = true;
        public event Action<string, string>? MessageReceived;
        public event Action? Disconnected { add { } remove { } }
        public Task ConnectAsync(GcuConnectionOptions options, CancellationToken ct) { IsConnected = true; return Task.CompletedTask; }
        public Task SubscribeAsync(IReadOnlyCollection<string> topics, CancellationToken ct) => Task.CompletedTask;
        public void EmitSettings() => MessageReceived?.Invoke("Setting/Status", JsonSerializer.Serialize(Settings));
        public Task PublishAsync(string topic, string payload, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            bool query = root.TryGetProperty("Action", out var action) && action.GetString() == "GETSTATUS";
            if (query)
            {
                if (HoldStatus) return Task.CompletedTask;
                if (topic == "Setting/Control")
                {
                    if (_pendingSetting is { } setting) { Settings[setting.Field] = setting.Value; _pendingSetting = null; }
                    EmitSettings();
                }
                if (topic == "Keyboard/Ctrl")
                {
                    if (_pendingPower is bool power) { _power = power; _pendingPower = null; }
                    MessageReceived?.Invoke("Keyboard/Status", JsonSerializer.Serialize(new
                    {
                        type = "SingleZone", effect = "Rainbow", brightNess = "3", powerStatus = _power ? "On" : "Off",
                        MonochromeIndex = "17", ManualIndex1 = "1", ManualIndex2 = "5", ManualIndex3 = "9",
                        ManualIndex4 = "13", ManualIndex5 = "21", ManualIndex6 = "25", ManualInterval = "4", BreathingIndex = "1"
                    }));
                }
                return Task.CompletedTask;
            }
            if (topic == "Setting/Control")
            {
                Writes.Add(payload);
                _pendingSetting = action.GetString() switch
                {
                    "WINKEY_LOCK" => ("WinKey", "WINKEY_STATUS_LOCK"), "WINKEY_UNLOCK" => ("WinKey", "WINKEY_STATUS_UNLOCK"),
                    "OSD_HIDDEN_OFF" => ("OSD", "OSD_HIDDEN_OFF"), "OSD_HIDDEN_ON" => ("OSD", "OSD_HIDDEN_ON"),
                    _ => throw new InvalidOperationException("Unexpected offline command")
                };
            }
            if (topic == "Keyboard/Ctrl")
            {
                Writes.Add(payload);
                if (root.GetProperty("function").GetString() != "SetPower") throw new InvalidOperationException("Unexpected lighting write");
                _pendingPower = root.GetProperty("powerstatus").GetInt32() == 1;
            }
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken ct) { IsConnected = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
