using System.Drawing.Drawing2D;

namespace GHelper.UI
{
    public class RButton : Button
    {

        // Design tokens
        private const float HoverShiftAmount = 0.04f;
        private const float ActiveTopLighten = 0.15f;
        private const float RestTopLighten = 0.1f;
        private const int ActiveBgTopAlpha = 32;
        private const float ActiveBgEndFraction = 0.20f;

        //Fields
        private int borderSize = 5;

        private int borderRadius = 5;
        public int BorderRadius
        {
            get { return borderRadius; }
            set
            {
                borderRadius = value;
            }
        }

        private Color borderColor = Color.Transparent;
        public Color BorderColor
        {
            get { return borderColor; }
            set
            {
                borderColor = value;
            }
        }


        private bool activated = false;
        public bool Activated
        {
            get { return activated; }
            set
            {
                if (activated != value)
                    Invalidate();
                activated = value;
                AccessibleDescription = activated ? "Active" : null;
            }
        }

        private bool secondary = false;
        public bool Secondary
        {
            get { return secondary; }
            set
            {
                secondary = value;
            }
        }

        public bool Borderless { get; set; } = false;

        protected override bool ShowFocusCues => false;

        public RButton()
        {
            DoubleBuffered = true;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColorChanged += (s, e) => UpdateHoverColor();
            UpdateHoverColor();
        }

        private void UpdateHoverColor()
        {
            int lum = (BackColor.R * 30 + BackColor.G * 59 + BackColor.B * 11) / 100;
            Color target = lum > 128 ? Color.Black : Color.White;
            FlatAppearance.MouseOverBackColor = Shift(BackColor, target, HoverShiftAmount);
            FlatAppearance.MouseDownBackColor = Shift(BackColor, target, HoverShiftAmount * 2f);
        }

        private static Color Shift(Color from, Color target, float amount)
        {
            return Color.FromArgb(from.A,
                (int)(from.R + (target.R - from.R) * amount),
                (int)(from.G + (target.G - from.G) * amount),
                (int)(from.B + (target.B - from.B) * amount));
        }

        private GraphicsPath GetFigurePath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            float curveSize = radius * 2F;

            path.StartFigure();
            path.AddArc(rect.X, rect.Y, curveSize, curveSize, 180, 90);
            path.AddArc(rect.Right - curveSize, rect.Y, curveSize, curveSize, 270, 90);
            path.AddArc(rect.Right - curveSize, rect.Bottom - curveSize, curveSize, curveSize, 0, 90);
            path.AddArc(rect.X, rect.Bottom - curveSize, curveSize, curveSize, 90, 90);
            path.CloseFigure();
            return path;
        }


        protected override void OnPaint(PaintEventArgs pevent)
        {

            bool drawDisabledText = !Enabled && ForeColor != SystemColors.ControlText;
            if (!drawDisabledText)
                base.OnPaint(pevent);

            float ratio = pevent.Graphics.DpiX / 192.0f;
            int border = (int)Math.Round((ratio * borderSize - 1) / 2) * 2 + 1;
            int radius = (int)Math.Round(ratio * borderRadius, MidpointRounding.AwayFromZero);

            Rectangle rectSurface = ClientRectangle;

            using (GraphicsPath pathSurface = GetFigurePath(rectSurface, radius + border))
            using (Pen penSurface = new Pen(Parent.BackColor, border))
            {
                pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Region = new Region(pathSurface);

                // Native disabled text is black in dark mode. Paint the complete
                // disabled content here so image buttons also get one readable pass.
                if (drawDisabledText)
                {
                    using (var brush = new SolidBrush(BackColor))
                        pevent.Graphics.FillPath(brush, pathSurface);
                }

                pevent.Graphics.DrawPath(penSurface, pathSurface);

                bool drawActive = Enabled && !Borderless && activated && borderColor.A > 0;
                bool drawRest = Enabled && !Borderless && !activated && FlatAppearance.BorderColor.A > 0 && !RForm.flatTheme;

                if (drawActive)
                {
                    Rectangle borderRect = new Rectangle(border, border, rectSurface.Width - 2 * border, rectSurface.Height - 2 * border);

                    Color bgTop = Color.FromArgb(ActiveBgTopAlpha, borderColor);
                    Color bgTransparent = Color.FromArgb(0, borderColor);
                    float bgEndPos = ActiveBgEndFraction;

                    using (GraphicsPath bgPath = GetFigurePath(borderRect, radius))
                    using (LinearGradientBrush bgBrush = new LinearGradientBrush(
                        ControlHelper.DarkMode ? new PointF(0, borderRect.Y) : new PointF(0, borderRect.Bottom),
                        ControlHelper.DarkMode ? new PointF(0, borderRect.Bottom) : new PointF(0, borderRect.Y),
                        bgTop, bgTransparent))
                    {
                        bgBrush.InterpolationColors = new ColorBlend
                        {
                            Colors = new[] { bgTop, bgTransparent, bgTransparent },
                            Positions = new[] { 0f, bgEndPos, 1f }
                        };
                        if (!RForm.flatTheme)
                            pevent.Graphics.FillPath(bgBrush, bgPath);
                    }

                    ControlHelper.DrawGradientBorder(pevent.Graphics, borderRect, borderColor, radius, border, PenAlignment.Outset, RForm.flatTheme ? 0f : ActiveTopLighten);
                }
                else if (drawRest)
                {
                    int inset = border / 2 + 1;
                    Rectangle borderRect = new Rectangle(inset, inset, rectSurface.Width - 2 * inset, rectSurface.Height - 2 * inset);
                    ControlHelper.DrawGradientBorder(pevent.Graphics, borderRect, FlatAppearance.BorderColor, radius + border - inset, 1f, PenAlignment.Inset, RestTopLighten);
                }
            }

            if (drawDisabledText)
                PaintDisabledContent(pevent.Graphics, rectSurface);

            if (!Enabled && !Borderless && activated && borderColor.A > 0)
            {
                Rectangle borderRect = new Rectangle(border, border, rectSurface.Width - 2 * border, rectSurface.Height - 2 * border);
                ControlHelper.DrawGradientBorder(pevent.Graphics, borderRect, borderColor, radius, border, PenAlignment.Outset, RForm.flatTheme ? 0f : ActiveTopLighten);
            }


        }

        private void PaintDisabledContent(Graphics graphics, Rectangle bounds)
        {
            const int gap = 4;
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak;
            if (Image is null)
            {
                TextRenderer.DrawText(graphics, Text, Font, bounds, Color.Gray, flags);
                return;
            }

            Size imageSize = Image.Size;
            Size textSize = TextRenderer.MeasureText(graphics, Text, Font, bounds.Size, flags | TextFormatFlags.NoPadding);
            Rectangle imageBounds;
            Rectangle textBounds;

            switch (TextImageRelation)
            {
                case TextImageRelation.ImageAboveText:
                {
                    int top = bounds.Top + (bounds.Height - imageSize.Height - gap - textSize.Height) / 2;
                    imageBounds = new Rectangle(bounds.Left + (bounds.Width - imageSize.Width) / 2, top, imageSize.Width, imageSize.Height);
                    textBounds = new Rectangle(bounds.Left, imageBounds.Bottom + gap, bounds.Width, textSize.Height);
                    break;
                }
                case TextImageRelation.TextAboveImage:
                {
                    int top = bounds.Top + (bounds.Height - imageSize.Height - gap - textSize.Height) / 2;
                    textBounds = new Rectangle(bounds.Left, top, bounds.Width, textSize.Height);
                    imageBounds = new Rectangle(bounds.Left + (bounds.Width - imageSize.Width) / 2, textBounds.Bottom + gap, imageSize.Width, imageSize.Height);
                    break;
                }
                case TextImageRelation.ImageBeforeText:
                {
                    int left = bounds.Left + (bounds.Width - imageSize.Width - gap - textSize.Width) / 2;
                    imageBounds = new Rectangle(left, bounds.Top + (bounds.Height - imageSize.Height) / 2, imageSize.Width, imageSize.Height);
                    textBounds = new Rectangle(imageBounds.Right + gap, bounds.Top, textSize.Width, bounds.Height);
                    break;
                }
                case TextImageRelation.TextBeforeImage:
                {
                    int left = bounds.Left + (bounds.Width - imageSize.Width - gap - textSize.Width) / 2;
                    textBounds = new Rectangle(left, bounds.Top, textSize.Width, bounds.Height);
                    imageBounds = new Rectangle(textBounds.Right + gap, bounds.Top + (bounds.Height - imageSize.Height) / 2, imageSize.Width, imageSize.Height);
                    break;
                }
                default:
                    imageBounds = new Rectangle(bounds.Left + (bounds.Width - imageSize.Width) / 2, bounds.Top + (bounds.Height - imageSize.Height) / 2, imageSize.Width, imageSize.Height);
                    textBounds = bounds;
                    break;
            }

            ControlPaint.DrawImageDisabled(graphics, Image, imageBounds.X, imageBounds.Y, BackColor);
            TextRenderer.DrawText(graphics, Text, Font, textBounds, Color.Gray, flags);
        }

    }
}
