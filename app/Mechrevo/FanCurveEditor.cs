using GHelper.UI;
using System.Windows.Forms.DataVisualization.Charting;

namespace YALM.Mechrevo;

// The chart uses the upstream control; firmware temperatures and the 16-slot
// wire format stay in the Mechrevo adapter. Editing never sends a command.
internal sealed class FanCurveEditor : UserControl
{
    private readonly RChart _chart = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, TabStop = true };
    private readonly Label _point = new() { AutoSize = true, Visible = false, Padding = new Padding(3), TabStop = false };
    private readonly Series _rising;
    private readonly Series _falling;
    private IReadOnlyList<FanCurvePoint> _points = [];
    private int[] _duties = [];
    private int[] _baseline = [];
    private string? _identity;
    private int _selected;
    private bool _dragging;
    private bool _editable;

    public event Action? DraftChanged;
    public bool HasChanges => !_duties.SequenceEqual(_baseline);
    public bool SnapToGrid { get; set; } = true;
    public bool ShowFallingThresholds
    {
        get => _falling.Enabled;
        set { _falling.Enabled = value; _chart.Invalidate(); }
    }

    public void AddCornerControl(Control control)
    {
        control.Location = new Point(4, 4);
        // Match the upstream fan panel: overlay options are chart siblings,
        // inheriting the window background instead of the chart background.
        Controls.Add(control);
        control.BringToFront();
    }

    public void ApplyThemeColors()
    {
        foreach (Control control in _chart.Controls)
            control.ForeColor = RForm.foreMain;
    }

    public FanCurveEditor(string title)
    {
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 6, 0, 6);
        AccessibleName = title;
        var area = new ChartArea("Curve");
        area.AxisX.Minimum = 45;
        area.AxisX.Maximum = 100;
        area.AxisX.Interval = 5;
        area.AxisY.Minimum = 0;
        area.AxisY.Maximum = 100;
        area.AxisY.Interval = 10;
        for (int i = 0; i <= 100; i += 10)
            area.AxisY.CustomLabels.Add(i - 2, i + 2, $"{i}%");
        area.AxisX.IsMarginVisible = false;
        area.AxisX.LabelStyle.Font = area.AxisY.LabelStyle.Font = new Font("Arial", 7F);
        area.AxisX.IsLabelAutoFit = area.AxisY.IsLabelAutoFit = false;
        area.AxisX.LabelStyle.IsStaggered = area.AxisY.LabelStyle.IsStaggered = false;
        area.AxisX.LabelStyle.Angle = 0;
        area.AxisX.LabelStyle.IsEndLabelVisible = true;
        area.AxisY.IsMarginVisible = false;
        area.AxisX.MajorGrid.LineColor = area.AxisY.MajorGrid.LineColor = RForm.chartGrid;
        _chart.ChartAreas.Add(area);
        _chart.Titles.Add(title);
        _rising = new Series("升温")
        {
            ChartType = SeriesChartType.Line, BorderWidth = 1,
            MarkerStyle = MarkerStyle.Circle, MarkerSize = 10, Color = Color.Gray
        };
        _falling = new Series("降温")
        {
            ChartType = SeriesChartType.Line, BorderWidth = 1,
            BorderDashStyle = ChartDashStyle.Dash, Color = Color.Gray, Enabled = false
        };
        _chart.Series.Add(_falling);
        _chart.Series.Add(_rising);
        _chart.MouseDown += OnChartMouseDown;
        _chart.MouseMove += OnChartMouseMove;
        _chart.MouseUp += (_, _) => { _dragging = false; _chart.Capture = false; };
        _chart.MouseCaptureChanged += (_, _) => _dragging = false;
        _chart.GotFocus += (_, _) => UpdateSelection();
        _chart.LostFocus += (_, _) => _point.Visible = false;
        _chart.KeyDown += OnChartKeyDown;
        _chart.PreviewKeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down) e.IsInputKey = true;
        };
        _chart.AccessibleName = title + "，左右选择温度点，上下调整占空比";
        _chart.Controls.Add(_point);
        Controls.Add(_chart);
        UpdateSelection();
    }

    public void SetEditable(bool editable)
    {
        _editable = editable;
        UpdateSelection();
    }

    public void SetCurve(IReadOnlyList<FanCurvePoint>? curve, string identity, bool discardDraft)
    {
        int pointCount = FanCurveDraft.EditablePointCount(curve);
        if (pointCount == 0)
        {
            _points = [];
            _duties = _baseline = [];
            _identity = null;
            Redraw();
            return;
        }
        bool sameShape = _points.Select(p => (p.Id, p.UpTemperature, p.DownTemperature))
            .SequenceEqual(curve!.Take(pointCount).Select(p => (p.Id, p.UpTemperature, p.DownTemperature)));
        if (!discardDraft && identity == _identity && sameShape &&
            _baseline.SequenceEqual(curve.Take(pointCount).Select(point => point.Duty))) return;
        bool preserve = !discardDraft && HasChanges && identity == _identity && sameShape;
        _points = curve.Take(pointCount).ToArray();
        _baseline = _points.Select(point => point.Duty).ToArray();
        if (!preserve) _duties = (int[])_baseline.Clone();
        _identity = identity;
        _selected = Math.Clamp(_selected, FirstRisingIndex, _points.Count - 1);
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
            if (!point.IsStopThreshold)
            {
                int index = _rising.Points.AddXY(point.UpTemperature, _duties[i]);
                _rising.Points[index].Tag = i;
                _rising.Points[index].ToolTip = PointTemperatures(point) + $"：{_duties[i]}%";
                if (i + 1 < _points.Count && point.UpTemperature == _points[i + 1].UpTemperature)
                    _rising.Points[index].ToolTip += $"\n同温度跳变：{_duties[i]}% → {_duties[i + 1]}%";
                else if (i > 0 && point.UpTemperature == _points[i - 1].UpTemperature)
                    _rising.Points[index].ToolTip += $"\n同温度跳变：{_duties[i - 1]}% → {_duties[i]}%";
            }
            if (point.DownTemperature < 255)
            {
                int fallingIndex = _falling.Points.AddXY(point.DownTemperature, _duties[i]);
                _falling.Points[fallingIndex].ToolTip = point.IsStopThreshold
                    ? $"停转 {point.DownTemperature} ℃（固定）" : $"降温 {point.DownTemperature} ℃：{_duties[i]}%";
                if (point.IsStopThreshold)
                {
                    _falling.Points[fallingIndex].MarkerStyle = MarkerStyle.Circle;
                    _falling.Points[fallingIndex].MarkerSize = 10;
                }
            }
        }
        UpdateSelection();
        _chart.Invalidate();
    }

    private void UpdateSelection()
    {
        _point.Visible = false;
        if (_points.Count == 0) return;
        FanCurvePoint point = _points[_selected];
        _point.Text = PointTemperatures(point) + $" · {_duties[_selected]}%" +
            (_selected == 0 ? "（固定）" : HasChanges ? " · 未应用" : "");
        _chart.AccessibleDescription = _point.Text;
        if (_chart.Focused || _dragging)
        {
            try
            {
                ChartArea area = _chart.ChartAreas[0];
                int x = (int)area.AxisX.ValueToPixelPosition(point.UpTemperature);
                int y = (int)area.AxisY.ValueToPixelPosition(_duties[_selected]);
                _point.Location = new Point(Math.Clamp(x - _point.PreferredWidth / 2, 0, Math.Max(0, _chart.Width - _point.PreferredWidth)),
                    Math.Max(0, y - _point.PreferredHeight - 10));
                _point.BackColor = RForm.chartMain;
                _point.ForeColor = RForm.foreMain;
                _point.Visible = true;
            }
            catch (InvalidOperationException) { }
        }
    }

    private int FirstRisingIndex => _points.Count > 0 && _points[0].IsStopThreshold ? 1 : 0;

    private static string PointTemperatures(FanCurvePoint point) => point.IsStopThreshold
        ? $"停转 {point.DownTemperature} ℃"
        : $"升温 {point.UpTemperature} ℃ / 降温 " +
          (point.DownTemperature == 255 ? "结束" : $"{point.DownTemperature} ℃");

    private void SetDuty(int duty)
    {
        if (!_editable || _selected <= 0 || _selected >= _duties.Length) return;
        int minimum = _duties[_selected - 1];
        int maximum = _selected + 1 < _duties.Length ? _duties[_selected + 1] : 100;
        if (minimum > maximum) return;
        int next = Math.Clamp(duty, minimum, maximum);
        if (next == _duties[_selected]) return;
        _duties[_selected] = next;
        Redraw();
        DraftChanged?.Invoke();
    }

    private void OnChartMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        HitTestResult hit = _chart.HitTest(e.X, e.Y);
        if (hit.ChartElementType != ChartElementType.DataPoint || hit.Series != _rising) return;
        _selected = (int)_rising.Points[hit.PointIndex].Tag;
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
            _selected = Math.Clamp(_selected + (e.KeyCode == Keys.Left ? -1 : 1), FirstRisingIndex, _points.Count - 1);
            UpdateSelection();
        }
        else if (e.KeyCode is Keys.Up or Keys.Down)
            SetDuty(_duties[_selected] + (e.KeyCode == Keys.Up ? 1 : -1) * (e.Shift ? 5 : 1));
        else return;
        e.Handled = e.SuppressKeyPress = true;
    }
}
