using GHelper.UI;
using System.Windows.Forms.DataVisualization.Charting;

namespace YALM.Mechrevo;

// The chart uses the upstream control; firmware temperatures and the 16-slot
// wire format stay in the Mechrevo adapter. Editing never sends a command.
internal sealed class FanCurveEditor : UserControl
{
    private readonly RChart _chart = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, TabStop = true };
    private readonly Label _point = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4) };
    private readonly RNumericUpDown _duty = new() { Minimum = 0, Maximum = 100, Width = 62, Anchor = AnchorStyles.Right };
    private readonly Series _rising;
    private readonly Series _falling;
    private IReadOnlyList<FanCurvePoint> _points = [];
    private int[] _duties = [];
    private int[] _baseline = [];
    private string? _identity;
    private int _selected;
    private bool _dragging;
    private bool _updating;
    private bool _editable;

    public event Action? DraftChanged;
    public bool HasChanges => !_duties.SequenceEqual(_baseline);
    public bool SnapToGrid { get; set; } = true;

    public FanCurveEditor(string title)
    {
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 4, 0, 4);
        AccessibleName = title;
        var area = new ChartArea("Curve");
        area.AxisX.Minimum = 20;
        area.AxisX.Maximum = 110;
        area.AxisX.Interval = 10;
        area.AxisX.Title = "温度（℃）";
        area.AxisY.Minimum = 0;
        area.AxisY.Maximum = 100;
        area.AxisY.Interval = 20;
        area.AxisY.Title = "风扇（%）";
        area.AxisX.IsMarginVisible = false;
        area.AxisX.LabelStyle.Font = area.AxisY.LabelStyle.Font = new Font("Segoe UI", 8F);
        _chart.ChartAreas.Add(area);
        _chart.Titles.Add(title);
        _rising = new Series("升温")
        {
            ChartType = SeriesChartType.Line, BorderWidth = 2,
            MarkerStyle = MarkerStyle.Circle, MarkerSize = 8, Color = RForm.colorStandard
        };
        _falling = new Series("降温")
        {
            ChartType = SeriesChartType.Line, BorderWidth = 1,
            BorderDashStyle = ChartDashStyle.Dash, Color = RForm.colorGray
        };
        _chart.Series.Add(_falling);
        _chart.Series.Add(_rising);
        _chart.MouseDown += OnChartMouseDown;
        _chart.MouseMove += OnChartMouseMove;
        _chart.MouseUp += (_, _) => { _dragging = false; _chart.Capture = false; };
        _chart.MouseCaptureChanged += (_, _) => _dragging = false;
        _chart.KeyDown += OnChartKeyDown;
        _chart.PreviewKeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down) e.IsInputKey = true;
        };
        _chart.AccessibleName = title + "，左右选择温度点，上下调整占空比";
        _duty.AccessibleName = title + "所选节点占空比";
        _duty.ValueChanged += (_, _) => { if (!_updating) SetDuty((int)_duty.Value); };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var footer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        footer.Controls.Add(_point, 0, 0);
        footer.Controls.Add(_duty, 1, 0);
        root.Controls.Add(_chart, 0, 0);
        root.Controls.Add(footer, 0, 1);
        Controls.Add(root);
        UpdateSelection();
    }

    public void SetEditable(bool editable)
    {
        _editable = editable;
        UpdateSelection();
    }

    public void SetCurve(IReadOnlyList<FanCurvePoint>? curve, string identity, bool discardDraft)
    {
        if (curve is null || curve.Count != 16 || curve.Take(11).Any(point => point.IsTail))
        {
            _points = [];
            _duties = _baseline = [];
            _identity = null;
            Redraw();
            return;
        }
        bool sameShape = _points.Select(p => (p.Id, p.UpTemperature, p.DownTemperature))
            .SequenceEqual(curve.Take(11).Select(p => (p.Id, p.UpTemperature, p.DownTemperature)));
        if (!discardDraft && identity == _identity && sameShape &&
            _baseline.SequenceEqual(curve.Take(11).Select(point => point.Duty))) return;
        bool preserve = !discardDraft && HasChanges && identity == _identity && sameShape;
        _points = curve.Take(11).ToArray();
        _baseline = _points.Select(point => point.Duty).ToArray();
        if (!preserve) _duties = (int[])_baseline.Clone();
        _identity = identity;
        _selected = Math.Clamp(_selected, 0, _points.Count - 1);
        Redraw();
    }

    public bool TryGetDuties(out int[] duties, out string error) => FanCurveDraft.TryBuild(_duties, out duties, out error);

    private void Redraw()
    {
        _rising.Points.Clear();
        _falling.Points.Clear();
        for (int i = 0; i < _points.Count; i++)
        {
            FanCurvePoint point = _points[i];
            int index = _rising.Points.AddXY(point.UpTemperature, _duties[i]);
            _falling.Points.AddXY(point.DownTemperature, _duties[i]);
            _rising.Points[index].ToolTip = $"升温 {point.UpTemperature} ℃ / 降温 {point.DownTemperature} ℃：{_duties[i]}%";
        }
        if (_points.Count > 0)
        {
            Axis axis = _chart.ChartAreas[0].AxisX;
            axis.Minimum = Math.Min(20, Math.Floor(_points.Min(p => p.DownTemperature) / 10d) * 10);
            axis.Maximum = Math.Max(100, Math.Ceiling(_points.Max(p => p.UpTemperature) / 10d) * 10 + 10);
        }
        UpdateSelection();
        _chart.Invalidate();
    }

    private void UpdateSelection()
    {
        _updating = true;
        try
        {
            _duty.Enabled = _editable && _points.Count > 0 && _selected > 0;
            if (_points.Count == 0)
            {
                _point.Text = "尚未收到曲线";
                _duty.Value = 0;
                return;
            }
            FanCurvePoint point = _points[_selected];
            _point.Text = $"升温 {point.UpTemperature} ℃ / 降温 {point.DownTemperature} ℃ · {_duties[_selected]}%" +
                (_selected == 0 ? "（固定）" : HasChanges ? " · 未应用" : "");
            _duty.Value = Math.Clamp(_duties[_selected], 0, 100);
            for (int i = 0; i < _rising.Points.Count; i++)
            {
                _rising.Points[i].MarkerSize = i == _selected ? 11 : 8;
                _rising.Points[i].MarkerColor = i == _selected ? RForm.colorCustom : RForm.colorStandard;
            }
        }
        finally { _updating = false; }
    }

    private void SetDuty(int duty)
    {
        if (!_editable || _selected <= 0 || _selected >= _duties.Length) return;
        int minimum = _duties[_selected - 1];
        int maximum = _selected + 1 < _duties.Length ? _duties[_selected + 1] : 100;
        if (minimum > maximum) return;
        _duties[_selected] = Math.Clamp(duty, minimum, maximum);
        Redraw();
        DraftChanged?.Invoke();
    }

    private void OnChartMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        HitTestResult hit = _chart.HitTest(e.X, e.Y);
        if (hit.ChartElementType != ChartElementType.DataPoint || hit.Series != _rising) return;
        _selected = hit.PointIndex;
        _chart.Focus();
        UpdateSelection();
        if (_editable && _selected > 0)
        {
            _dragging = true;
            _chart.Capture = true;
        }
    }

    private void OnChartMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging || !_editable) return;
        double value = _chart.ChartAreas[0].AxisY.PixelPositionToValue(e.Y);
        int step = SnapToGrid ? 5 : 1;
        SetDuty((int)Math.Round(Math.Clamp(value, 0, 100) / step) * step);
    }

    private void OnChartKeyDown(object? sender, KeyEventArgs e)
    {
        if (_points.Count == 0) return;
        if (e.KeyCode is Keys.Left or Keys.Right)
        {
            _selected = Math.Clamp(_selected + (e.KeyCode == Keys.Left ? -1 : 1), 0, _points.Count - 1);
            UpdateSelection();
        }
        else if (e.KeyCode is Keys.Up or Keys.Down)
            SetDuty(_duties[_selected] + (e.KeyCode == Keys.Up ? 1 : -1) * (e.Shift ? 5 : 1));
        else return;
        e.Handled = e.SuppressKeyPress = true;
    }
}
