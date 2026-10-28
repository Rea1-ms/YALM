using GHelper.UI;
using System.Windows.Forms.VisualStyles;

namespace YALM.Mechrevo;

// The quick settings use the standard left-aligned glyph and a single text line.
internal sealed class QuickSettingCheckBox : CheckBox
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Enabled || BackColor.GetBrightness() >= 0.5F) return;

        // WinForms derives disabled text from the background and paints it almost
        // black in dark mode. Keep its native three-state glyph; replace only text.
        int glyphWidth = CheckBoxRenderer.GetGlyphSize(e.Graphics, CheckBoxState.UncheckedDisabled).Width;
        // Match the standard adapter's fixed glyph gap and text layout.
        int textLeft = glyphWidth + 3;
        var textBounds = new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft), Height);
        using var background = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(background, textBounds);
        textBounds.Offset(0, -1);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, RForm.colorGray,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}
