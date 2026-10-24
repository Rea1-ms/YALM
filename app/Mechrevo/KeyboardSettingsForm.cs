using GHelper.UI;

namespace YALM.Mechrevo;

internal sealed class KeyboardSettingsPanel : UserControl
{
    private readonly Func<GcuController?> _controller;
    private readonly bool _demo;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RComboBox _effect = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly RNumericUpDown _brightness = new() { Minimum = 0, Maximum = 4, Dock = DockStyle.Fill };
    private readonly RComboBox _color = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly RButton _power = ActionButton("键盘灯开关");
    private readonly RButton _apply = ActionButton("应用灯效");
    private readonly RButton _refresh = ActionButton("重新读取");
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(330, 0), Margin = new Padding(4, 12, 4, 4) };
    private bool _busy;
    private bool _dirty;
    private bool _rendering;

    public KeyboardSettingsPanel(Func<GcuController?> controller, bool demo)
    {
        _controller = controller;
        _demo = demo;
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;
        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _effect.Items.AddRange(["恒亮（静态）", "彩虹"]);
        _effect.PlaceholderText = "选择灯效";
        for (int i = 0; i < GcuKeyboardProtocol.Palette.Length; i++)
        {
            var (r, g, b) = GcuKeyboardProtocol.Palette[i];
            _color.Items.Add($"{i + 1} · #{r:X2}{g:X2}{b:X2}");
        }
        AddField(root, "灯效", _effect);
        AddField(root, "亮度档位", _brightness);
        AddField(root, "颜色", _color);
        _effect.SelectionChangeCommitted += (_, _) => MarkDirty();
        _brightness.ValueChanged += (_, _) => MarkDirty();
        _color.SelectionChangeCommitted += (_, _) => MarkDirty();
        _color.DrawMode = DrawMode.OwnerDrawFixed;
        _color.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var (r, g, b) = GcuKeyboardProtocol.Palette[e.Index];
            using var brush = new SolidBrush(Color.FromArgb(r, g, b));
            e.Graphics.FillRectangle(brush, e.Bounds.Right - 26, e.Bounds.Top + 3, 20, Math.Max(2, e.Bounds.Height - 6));
        };
        var actions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        for (int i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3));
        actions.Controls.Add(_power, 0, 0);
        actions.Controls.Add(_apply, 1, 0);
        actions.Controls.Add(_refresh, 2, 0);
        root.Controls.Add(actions, 0, root.RowCount++);
        root.SetColumnSpan(actions, 2);
        root.Controls.Add(_status, 0, root.RowCount++);
        root.SetColumnSpan(_status, 2);
        var note = new Label
        {
            Text = "单区键盘服务不回报当前灯效，与灯光开关无关。\n亮度和颜色随所选灯效一起应用；闲置临时熄灯后的开关回报可能不变。",
            AutoSize = true, MaximumSize = new Size(330, 0), Margin = new Padding(4, 12, 4, 4)
        };
        root.Controls.Add(note, 0, root.RowCount++);
        root.SetColumnSpan(note, 2);
        Controls.Add(root);
        _power.Click += async (_, _) =>
        {
            if (_controller() is { } current && current.Snapshot.Keyboard?.Power is bool on)
                await RunAsync(() => current.SetKeyboardPowerAsync(!on, _lifetime.Token));
        };
        _apply.Click += async (_, _) =>
        {
            if (_controller() is not { } current || _effect.SelectedIndex < 0 || _color.SelectedIndex < 0) return;
            string effect = _effect.SelectedIndex == 0 ? "Single" : "Rainbow";
            int brightness = (int)_brightness.Value;
            int color = _color.SelectedIndex + 1;
            await RunAsync(() => current.SetKeyboardEffectAsync(effect, brightness, color, _lifetime.Token));
        };
        _refresh.Click += async (_, _) =>
        {
            if (_controller() is not { } current || _busy) return;
            _dirty = false;
            try { await current.RefreshAsync(_lifetime.Token); }
            catch (OperationCanceledException) { }
            catch { _status.Text = "键盘状态读取失败。"; }
            Render();
        };
        Render();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }

    private void MarkDirty()
    {
        if (_rendering) return;
        _dirty = true;
        _status.Text = "设置尚未应用。";
        Render();
    }

    public void SelectColor()
    {
        // Choose the effect explicitly when the service cannot identify it.
        if (_effect.SelectedIndex < 0) _effect.Focus();
        else _color.Focus();
    }

    private async Task RunAsync(Func<Task<GcuWriteResult>> operation)
    {
        if (_demo || _busy || IsDisposed) return;
        _busy = true;
        _status.Text = "正在等待服务回报…";
        Render();
        try
        {
            GcuWriteResult result = await operation();
            if (IsDisposed) return;
            _status.Text = result.Message;
            if (result.Confirmed) _dirty = false;
        }
        catch (OperationCanceledException) { }
        catch { if (!IsDisposed) _status.Text = "设置未完成，请检查厂商服务。"; }
        finally { _busy = false; if (!IsDisposed) Render(); }
    }

    public void RefreshState() => Render();

    private void Render()
    {
        if (IsDisposed) return;
        GcuSnapshot snapshot = _controller()?.Snapshot ?? new();
        GcuKeyboardSettings? keyboard = _demo
            ? new() { Type = "SingleZone", Effect = "Single", Brightness = 3, MonochromeIndex = 17, Power = true }
            : snapshot.Keyboard;
        bool fresh = _demo || snapshot.IsKeyboardFresh();
        bool supported = fresh && keyboard?.SingleZone == true;
        _rendering = true;
        if (!_dirty)
        {
            _effect.SelectedIndex = keyboard?.Effect switch { "Single" => 0, "Rainbow" => 1, _ => -1 };
            _brightness.Value = keyboard?.Brightness ?? 0;
            _color.SelectedIndex = keyboard?.MonochromeIndex is int color ? color - 1 : -1;
        }
        _rendering = false;
        _power.Text = "键盘灯：" + (!fresh || keyboard?.Power is null ? "未收到" : keyboard.Power == true ? "开" : "关");
        _power.Enabled = !_demo && !_busy && supported && keyboard?.Power is not null;
        _apply.Enabled = !_demo && !_busy && supported && keyboard?.Brightness is not null &&
            _dirty && _effect.SelectedIndex >= 0 && _color.SelectedIndex >= 0;
        _refresh.Enabled = !_demo && !_busy && snapshot.IsConnected;
        _effect.Enabled = supported && !_busy;
        _brightness.Enabled = supported && !_busy && keyboard?.Brightness is not null;
        _color.Enabled = supported && !_busy && _effect.SelectedIndex == 0;
        if (_demo) _status.Text = "演示 · 仅预览，不连接键盘。";
        else if (!fresh) _status.Text = "等待新鲜的键盘状态。";
        else if (keyboard?.Brightness is null) _status.Text = "亮度尚未回报，暂时不能应用灯效。";
    }

    private static RButton ActionButton(string text) => new()
    {
        Text = text, Dock = DockStyle.Top, Height = 26, Margin = new Padding(2, 6, 2, 2),
        BorderColor = Color.Transparent, BorderRadius = 2, Secondary = true
    };

    private static void AddField(TableLayoutPanel root, string title, Control input)
    {
        int row = root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 8, 4, 8) }, 0, row);
        input.Margin = new Padding(4, 6, 4, 6);
        root.Controls.Add(input, 1, row);
    }
}
