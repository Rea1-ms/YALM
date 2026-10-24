using GHelper.UI;

namespace YALM.Mechrevo;

internal sealed partial class CustomModeForm
{
    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 10, 0) };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var nav = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 10) };
        for (int i = 0; i < 3; i++) nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3));
        AddPage(nav, "CPU", BuildCpuPage());
        AddPage(nav, "GPU", BuildGpuPage());
        AddPage(nav, "更多", BuildAdvancedPage());
        left.Controls.Add(nav, 0, 0);
        left.Controls.Add(_pages, 0, 1);
        root.Controls.Add(left, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        heading.Controls.Add(SectionHeading("风扇曲线", GHelper.Properties.Resources.icons8_fan_32), 0, 0);
        for (int i = 0; i < 5; i++) _profile.Items.Add($"自定义 {i + 1}");
        _profile.SelectedIndex = 0;
        _profile.AccessibleName = "待切换的自定义档位";
        _profile.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _activateProfile.Text = "切换";
        _activateProfile.Dock = DockStyle.Fill;
        _activateProfile.Height = 32;
        _activateProfile.Click += async (_, _) => await RunAsync("正在切换自定义档位…",
            () => _controller.SetCustomProfileAsync(_profile.SelectedIndex, _lifetime.Token), requireCustomMode: false);
        heading.Controls.Add(_profile, 1, 0);
        heading.Controls.Add(_activateProfile, 2, 0);
        right.Controls.Add(heading, 0, 0);

        var snap = new RCheckBox { Text = "对齐网格", Checked = true, AutoSize = true, Margin = new Padding(4, 2, 4, 0) };
        snap.CheckedChanged += (_, _) => _cpuCurve.SnapToGrid = _gpuCurve.SnapToGrid = snap.Checked;
        right.Controls.Add(snap, 0, 1);
        right.Controls.Add(_cpuCurve, 0, 2);
        right.Controls.Add(_gpuCurve, 0, 3);
        var actions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 8, 0, 0) };
        for (int i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3));
        _refresh.Text = "重新读取";
        _refresh.Click += async (_, _) =>
        {
            if (_demo) RenderSnapshot(loadEditors: true);
            else
            {
                await RefreshCustomAsync(true);
                await RefreshGpuPowerAsync();
            }
        };
        _applyCpuCurve.Click += async (_, _) => await ApplyCurveAsync(FanCurveTarget.Cpu, _cpuCurve);
        _applyGpuCurve.Click += async (_, _) => await ApplyCurveAsync(FanCurveTarget.Gpu, _gpuCurve);
        _cpuCurve.DraftChanged += () => RenderSnapshot(loadEditors: false);
        _gpuCurve.DraftChanged += () => RenderSnapshot(loadEditors: false);
        foreach (RButton button in new[] { _refresh, _applyCpuCurve, _applyGpuCurve }) button.Dock = DockStyle.Fill;
        actions.Controls.Add(_refresh, 0, 0);
        actions.Controls.Add(_applyCpuCurve, 1, 0);
        actions.Controls.Add(_applyGpuCurve, 2, 0);
        right.Controls.Add(actions, 0, 4);
        root.Controls.Add(right, 1, 0);
        _status.MaximumSize = new Size(800, 0);
        root.Controls.Add(_status, 0, 1);
        root.SetColumnSpan(_status, 2);
        Controls.Add(root);
        SelectPage("CPU");
    }

    private Control BuildCpuPage()
    {
        var layout = PageLayout();
        AddRow(layout, SectionHeading("功率限制", GHelper.Properties.Resources.icons8_processor_32));
        AddSetting(layout, GcuCustomWriteField.Pl1, "长时功耗", "瓦");
        AddSetting(layout, GcuCustomWriteField.Pl2, "短时功耗", "瓦");
        AddSetting(layout, GcuCustomWriteField.Pl4, "瞬时功耗", "瓦");
        AddSetting(layout, GcuCustomWriteField.TccTarget, "温度墙", "℃");
        _tccEnabled.Dock = DockStyle.Top;
        _tccEnabled.Click += async (_, _) => await RunAsync("正在修改温度墙开关…",
            () => _controller.SetCustomFieldAsync(GcuCustomWriteField.TccEnabled, Snapshot.CustomSettings?.TccEnabled == true ? 0 : 1, _lifetime.Token));
        AddRow(layout, _tccEnabled);
        AddRow(layout, InfoLabel("调整后点击对应的应用按钮。功耗范围以本机服务回报为准。"));
        return ScrollPage(layout);
    }

    private Control BuildGpuPage()
    {
        var layout = PageLayout();
        AddRow(layout, SectionHeading("GPU 功耗与频率", GHelper.Properties.Resources.icons8_video_card_32));
        _readonlyValues.MaximumSize = new Size(265, 0);
        AddRow(layout, _readonlyValues);
        AddRow(layout, InfoLabel("当前仅查看 GPU 参数。"));
        return ScrollPage(layout);
    }

    private Control BuildAdvancedPage()
    {
        var layout = PageLayout();
        AddRow(layout, SectionHeading("风扇设置", GHelper.Properties.Resources.icons8_fan_32));
        _fanBoost.Dock = DockStyle.Top;
        _fanBoost.Click += async (_, _) => await RunAsync("正在修改风扇增强…",
            () => _controller.SetFanBoostAsync(Snapshot.CustomSettings?.FanBoostEnabled != true, _lifetime.Token));
        AddRow(layout, _fanBoost);
        AddSetting(layout, GcuCustomWriteField.FanSwitchSpeed, "切换灵敏度", "毫秒");
        _fanSwitchEnabled.Dock = DockStyle.Top;
        _fanSwitchEnabled.Click += async (_, _) => await RunAsync("正在修改风扇切换开关…",
            () => _controller.SetCustomFieldAsync(GcuCustomWriteField.FanSwitchSpeedEnabled, Snapshot.CustomSettings?.FanSwitchSpeedEnabled == true ? 0 : 1, _lifetime.Token));
        AddRow(layout, _fanSwitchEnabled);
        _summary.MaximumSize = _curveTable.MaximumSize = new Size(265, 0);
        AddRow(layout, _summary);
        AddRow(layout, _curveTable);
        AddRow(layout, InfoLabel("实线为升温，虚线为降温。温度点由固件固定，可拖动节点或输入占空比；首点为 0%，末点同步五个尾值。独立输出只读。"));
        return ScrollPage(layout);
    }

    private void AddPage(TableLayoutPanel nav, string title, Control page)
    {
        int index = _navigation.Count;
        var button = ActionButton(title, RForm.colorStandard);
        button.Height = 34;
        button.Dock = DockStyle.Fill;
        button.Click += (_, _) => SelectPage(title);
        nav.Controls.Add(button, index, 0);
        page.Dock = DockStyle.Fill;
        _pages.Controls.Add(page);
        _navigation.Add(title, (button, page));
    }

    private Control SectionHeading(string text, Image icon) =>
        MechrevoUi.Heading(this, TextLabel(text, 10, FontStyle.Bold), icon);

    private void SelectPage(string title)
    {
        foreach (var (name, entry) in _navigation)
        {
            entry.Button.Activated = name == title;
            entry.Page.Visible = name == title;
            if (name == title) entry.Page.BringToFront();
        }
    }

    private void AddSetting(TableLayoutPanel layout, GcuCustomWriteField field, string title, string unit)
    {
        var input = new RNumericUpDown
        {
            Minimum = 0, Maximum = 1000, Width = 72, TextAlign = HorizontalAlignment.Right,
            AccessibleName = title, Anchor = AnchorStyles.Right
        };
        var apply = ActionButton("应用", RForm.colorStandard);
        apply.Width = 56;
        apply.Height = 30;
        apply.AccessibleName = "应用" + title;
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 8, 0, 0) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        heading.Controls.Add(TextLabel($"{title}（{unit}）", 9F), 0, 0);
        heading.Controls.Add(input, 1, 0);
        heading.Controls.Add(apply, 2, 0);
        var slider = new RTrackBar
        {
            Minimum = 0, Maximum = 1000, Dock = DockStyle.Top, Height = 34,
            AutoSize = false, TickStyle = TickStyle.None, AccessibleName = title,
            Margin = new Padding(0, 0, 0, 10)
        };
        input.ValueChanged += (_, _) => slider.Value = Math.Clamp((int)input.Value, slider.Minimum, slider.Maximum);
        slider.ValueChanged += (_, _) =>
        {
            decimal step = input.Increment;
            decimal value = input.Minimum + Math.Round((slider.Value - input.Minimum) / step) * step;
            input.Value = Math.Clamp(value, input.Minimum, input.Maximum);
        };
        apply.Click += async (_, _) => await RunAsync($"正在写入{title}…",
            () => _controller.SetCustomFieldAsync(field, (int)input.Value, _lifetime.Token));
        AddRow(layout, heading);
        AddRow(layout, slider);
        _settings.Add(new SettingEditor(field, title, unit, input, slider, apply));
    }

    private static TableLayoutPanel PageLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    private static Panel ScrollPage(Control content)
    {
        var panel = new Panel { AutoScroll = true, Padding = new Padding(0, 0, 6, 0) };
        panel.Controls.Add(content);
        return panel;
    }

    private static void AddRow(TableLayoutPanel layout, Control control)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(control, 0, row);
    }

    private static GcuSnapshot DemoSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        int[] up = [0, 54, 59, 63, 66, 69, 72, 75, 78, 81, 84];
        int[] down = [48, 50, 64, 67, 70, 73, 76, 79, 82, 85, 255];
        var cpu = Enumerable.Range(0, 16).Select(i => new FanCurvePoint(i,
            i > 10 ? 255 : up[i], i > 10 ? 255 : down[i], i == 0 ? 0 : Math.Min(i, 10) * 10)).ToArray();
        var gpu = cpu.Select(p => p with { Duty = p.Id == 0 ? 0 : Math.Min(100, p.Duty + 5) }).ToArray();
        return new GcuSnapshot
        {
            IsConnected = true, Mode = 3, ModeUpdatedAt = now,
            CustomSettingsUpdatedAt = now, FanTableUpdatedAt = now,
            CustomSettings = new GcuCustomSettings
            {
                ProfileIndex = 0, ProfileName = "演示档位",
                Pl1 = 55, Pl1Minimum = 10, Pl1Maximum = 120,
                Pl2 = 80, Pl2Minimum = 10, Pl2Maximum = 150,
                Pl4 = 120, Pl4Minimum = 10, Pl4Maximum = 180,
                TjMax = 100, TccRawOffset = 10, TccEnabled = true,
                FanSwitchSpeed = 300, FanSwitchSpeedEnabled = false,
                FanBoostEnabled = false, GpuTgp = 115, GpuDynamicBoost = 25,
                GpuCoreClockOffset = 0, GpuMemoryClockOffset = 0
            },
            FanTable = new FanCurveTable(true, "演示曲线", false, null, null, cpu, gpu)
        };
    }
}
