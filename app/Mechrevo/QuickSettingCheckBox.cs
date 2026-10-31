using GHelper.UI;

namespace YALM.Mechrevo;

// The quick settings use the standard left-aligned glyph and a single text line.
internal sealed class QuickSettingCheckBox : CheckBox
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        RCheckBox.PaintDisabledText(this, e.Graphics);
    }
}
