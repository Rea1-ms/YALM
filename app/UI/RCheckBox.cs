using System.Drawing.Drawing2D;
using System.Windows.Forms.VisualStyles;

namespace GHelper.UI
{
    public class RCheckBox : CheckBox
    {
        private int borderRadius = 5;
        public int BorderRadius
        {
            get => borderRadius;
            set => borderRadius = value;
        }

        public RCheckBox()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            base.OnPaint(pevent);
            PaintDisabledText(this, pevent.Graphics);

            if (Parent == null || FlatAppearance.BorderColor.A == 0 || RForm.flatTheme) return;

            float ratio = pevent.Graphics.DpiX / 192.0f;
            int radius = (int)Math.Round(ratio * borderRadius, MidpointRounding.AwayFromZero);

            Rectangle borderRect = new Rectangle(0, 0, ClientRectangle.Width - 1, ClientRectangle.Height - 1);
            Color sideColor = FlatAppearance.BorderColor.A > 0 ? FlatAppearance.BorderColor : RForm.borderSecond;

            pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath roundedPath = RComboBox.RoundedRect(borderRect, radius, radius))
            using (GraphicsPath cutoutPath = new GraphicsPath())
            using (Brush parentBrush = new SolidBrush(Parent.BackColor))
            {
                cutoutPath.AddRectangle(ClientRectangle);
                cutoutPath.AddPath(roundedPath, false);
                cutoutPath.FillMode = FillMode.Alternate;
                pevent.Graphics.FillPath(parentBrush, cutoutPath);
            }

            ControlHelper.DrawGradientBorder(pevent.Graphics, borderRect, sideColor, radius, 1f, PenAlignment.Center);
        }

        internal static void PaintDisabledText(CheckBox check, Graphics graphics)
        {
            if (check.Enabled ||
                check.Appearance != Appearance.Normal || string.IsNullOrEmpty(check.Text)) return;

            // Keep the native three-state glyph. WinForms paints disabled captions
            // almost black on dark backgrounds; replace only the text, including
            // the padding used by the upstream overlay toggles.
            int glyphWidth = CheckBoxRenderer.GetGlyphSize(graphics, CheckBoxState.UncheckedDisabled).Width;
            int textLeft = check.Padding.Left + glyphWidth + 3;
            var bounds = new Rectangle(textLeft, check.Padding.Top,
                Math.Max(0, check.Width - textLeft - check.Padding.Right),
                Math.Max(0, check.Height - check.Padding.Vertical));
            using var background = new SolidBrush(check.BackColor);
            graphics.FillRectangle(background, bounds);
            bounds.Offset(0, -1);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            if (!check.UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            Color color = check.BackColor.GetBrightness() < 0.5F ? RForm.colorGray : SystemColors.GrayText;
            TextRenderer.DrawText(graphics, check.Text, check.Font, bounds, color, flags);
        }
    }
}
