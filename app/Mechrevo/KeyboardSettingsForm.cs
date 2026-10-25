using GHelper.UI;

namespace YALM.Mechrevo;

// Draft editor only. Opening, choosing a color, refreshing and cancelling do not write to hardware.
internal sealed class KeyboardSettingsForm : RForm
{
    private readonly Func<Task<GcuKeyboardDraft?>> _reload;
    private readonly RColorButton[] _colors = new RColorButton[6];
    private readonly Slider _interval = new()
    {
        Min = 1, Max = 10, Step = 1, Value = 1, Dock = DockStyle.Fill,
        Margin = new Padding(8, 0, 8, 0), TabStop = true
    };
    private readonly Label _intervalValue = new() { AutoSize = true, Anchor = AnchorStyles.Right, MinimumSize = new Size(46, 0) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(430, 0), Margin = new Padding(4, 12, 4, 8) };
    private readonly RButton _apply = ActionButton("应用");
    public GcuKeyboardDraft Draft { get; private set; }

    public KeyboardSettingsForm(GcuKeyboardDraft draft, Func<Task<GcuKeyboardDraft?>> reload, bool demo)
    {
        Draft = draft;
        _reload = reload;
        Text = "自定义六色轮换";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(460, 0);
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(12);
        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(root, new Label { Text = "按 1 → 6 顺序循环，点击色块选择颜色。", AutoSize = true, Margin = new Padding(4, 8, 4, 12) });
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, Margin = Padding.Empty };
        for (int i = 0; i < 6; i++)
        {
            int slot = i + 1;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 6));
            var button = new RColorButton
            {
                Text = slot.ToString(), AccessibleName = $"第 {slot} 个颜色", Secondary = true, BorderRadius = 2,
                Dock = DockStyle.Fill, Margin = new Padding(2), TextAlign = ContentAlignment.MiddleLeft
            };
            MechrevoUi.SizeCompactButtons(this, button);
            _colors[i] = button;
            button.Click += (_, _) =>
            {
                GcuRgb rgb = Draft.ColorAt(slot);
                using var picker = new RColorPicker(Color.FromArgb(rgb.R, rgb.G, rgb.B));
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                Draft = Draft.WithColor(slot, new(picker.Color.R, picker.Color.G, picker.Color.B));
                RenderColors();
                _status.Text = "颜色尚未应用。";
            };
            row.Controls.Add(button, i, 0);
        }
        AddRow(root, row);
        var timing = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(4, 14, 4, 4)
        };
        timing.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        timing.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timing.Controls.Add(new Label { Text = "每色显示", AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty }, 0, 0);
        _interval.Height = (int)Math.Round(40F * DeviceDpi / 192F);
        timing.Controls.Add(_interval, 1, 0);
        timing.Controls.Add(_intervalValue, 2, 0);
        _interval.Value = Draft.ManualInterval;
        RenderInterval();
        _interval.ValueChanged += (_, _) =>
        {
            Draft = Draft with { ManualInterval = _interval.Value };
            RenderInterval();
            _status.Text = "间隔尚未应用。";
        };
        AddRow(root, timing);
        AddRow(root, _status);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancel = ActionButton("取消");
        var refresh = ActionButton("重新读取");
        MechrevoUi.SizeCompactButtons(this, _apply, cancel, refresh);
        cancel.DialogResult = DialogResult.Cancel;
        _apply.Enabled = !demo;
        _apply.Click += (_, _) => { if (Draft.IsValid) DialogResult = DialogResult.OK; };
        refresh.Click += async (_, _) =>
        {
            refresh.Enabled = _apply.Enabled = false;
            GcuKeyboardDraft? current;
            try { current = await _reload(); }
            catch { current = null; }
            if (IsDisposed) return;
            refresh.Enabled = true;
            if (current is null)
            {
                _apply.Enabled = false;
                _status.Text = "未读到完整配置，请等待服务状态更新后重新读取。";
                return;
            }
            Draft = current;
            _interval.Value = Draft.ManualInterval;
            _apply.Enabled = !demo;
            RenderColors();
            _status.Text = "已读取服务颜色序号与厂商保存的自定义颜色，尚未应用。";
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(_apply);
        actions.Controls.Add(refresh);
        AddRow(root, actions);
        Controls.Add(root);
        AcceptButton = _apply;
        CancelButton = cancel;
        _status.Text = demo ? "演示 · 仅预览。" : "颜色来自服务序号与厂商保存配置；点击应用后生效。";
        InitTheme(true);
        RenderColors();
    }

    private void RenderColors()
    {
        for (int i = 0; i < _colors.Length; i++)
        {
            GcuRgb rgb = Draft.ColorAt(i + 1);
            _colors[i].SwatchColor = Color.FromArgb(rgb.R, rgb.G, rgb.B);
            _colors[i].AccessibleDescription = $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";
        }
    }

    private void RenderInterval()
    {
        _intervalValue.Text = $"{_interval.Value} 秒";
        _interval.AccessibleName = $"每色显示：{_interval.Value} 秒";
    }

    private static RButton ActionButton(string text) => new()
    {
        Text = text, Width = 96, Margin = new Padding(2), BorderColor = Color.Transparent, BorderRadius = 2, Secondary = true
    };

    private static void AddRow(TableLayoutPanel root, Control control)
    {
        int row = root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(control, 0, row);
    }
}
