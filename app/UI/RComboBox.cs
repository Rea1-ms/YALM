using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace GHelper.UI
{
    public class RComboBox : ComboBox
    {
        private const int TextLeftPad = 1;
        public bool UseCustomTextPadding = true;
        public string PlaceholderText { get; set; } = string.Empty;
        private bool _darkTheme;

        public RComboBox()
        {
            DrawMode = UseCustomTextPadding ? DrawMode.OwnerDrawFixed : DrawMode.Normal;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var args = (HandledMouseEventArgs)e;
            if (!DroppedDown)
                args.Handled = true;
            else
                base.OnMouseWheel(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            CalibrateItemHeight();
            ApplyNativeTheme();
        }

        public void ApplyTheme(bool dark)
        {
            _darkTheme = dark;
            if (IsHandleCreated) ApplyNativeTheme();
            Invalidate();
        }

        private void ApplyNativeTheme()
        {
            // The native combo also paints during focus and dropdown transitions,
            // outside WM_PAINT. Keep those frames and the popup in the same theme.
            SetWindowTheme(Handle, _darkTheme ? "DarkMode_CFD" : "Explorer", null);
            var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf<COMBOBOXINFO>() };
            if (GetComboBoxInfo(Handle, ref info) && info.hwndList != nint.Zero)
                SetWindowTheme(info.hwndList, _darkTheme ? "DarkMode_Explorer" : "Explorer", null);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            CalibrateItemHeight();
        }

        public bool NativeHeight { get; set; }

        private void CalibrateItemHeight()
        {
            if (DrawMode != DrawMode.OwnerDrawFixed || NativeHeight) return;
            // PreferredHeight can retain the font's original height after handle
            // recreation. Use the actual native height so calibration is stable.
            int chrome = Height - ItemHeight;
            int target = (int)Math.Round(44 * (DeviceDpi / 192f));
            ItemHeight = Math.Max(1, target - chrome);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 && PlaceholderText.Length > 0)
            {
                using var brush = new SolidBrush(BackColor);
                e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, PlaceholderText, e.Font, e.Bounds, SystemColors.GrayText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                return;
            }
            if (!UseCustomTextPadding || e.Index < 0)
            {
                base.OnDrawItem(e);
                return;
            }

            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color bg = selected ? RForm.borderMain : BackColor;
            Color fg = ForeColor;

            using (var bgBrush = new SolidBrush(bg))
                e.Graphics.FillRectangle(bgBrush, e.Bounds);

            int pad = (int)Math.Round(TextLeftPad * e.Graphics.DpiX / 96f);
            var textRect = new Rectangle(e.Bounds.X + pad, e.Bounds.Y,
                e.Bounds.Width - pad, e.Bounds.Height);

            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), e.Font, textRect, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            e.DrawFocusRectangle();
        }

        private Color borderColor = Color.Gray;
        [DefaultValue(typeof(Color), "Gray")]
        public Color BorderColor
        {
            get { return borderColor; }
            set
            {
                if (borderColor != value)
                {
                    borderColor = value;
                    Invalidate();
                }
            }
        }


        private Color buttonColor = Color.FromArgb(255, 255, 255, 255);
        [DefaultValue(typeof(Color), "255, 255, 255")]
        public Color ButtonColor
        {
            get { return buttonColor; }
            set
            {
                if (buttonColor != value)
                {
                    buttonColor = value;
                    Invalidate();
                }
            }
        }

        private Color arrowColor = Color.Black;
        [DefaultValue(typeof(Color), "Black")]
        public Color ArrowColor
        {
            get { return arrowColor; }
            set
            {
                if (arrowColor != value)
                {
                    arrowColor = value;
                    Invalidate();
                }
            }
        }


        public static GraphicsPath RoundedRect(Rectangle bounds, int radiusL, int radiusR)
        {
            int diameterL = radiusL * 2;
            int diameterR = radiusR * 2;

            Size sizeL = new Size(diameterL, diameterL);
            Size sizeR = new Size(diameterR, diameterR);

            Rectangle arcL = new Rectangle(bounds.Location, sizeL);
            Rectangle arcR = new Rectangle(bounds.Location, sizeR);

            GraphicsPath path = new GraphicsPath();

            // top left arc  
            path.AddArc(arcL, 180, 90);

            // top right arc  
            arcR.X = bounds.Right - diameterR;
            arcR.Y = bounds.Top;
            path.AddArc(arcR, 270, 90);

            // bottom right arc  
            arcR.Y = bounds.Bottom - diameterR;
            arcR.X = bounds.Right - diameterR;
            path.AddArc(arcR, 0, 90);

            // bottom left arc 
            arcL.X = bounds.Left;
            arcL.Y = bounds.Bottom - diameterL;
            path.AddArc(arcL, 90, 90);

            path.CloseFigure();
            return path;
        }

        public static void DrawRoundedRectangle(Graphics graphics, Pen pen, Rectangle bounds, int cornerRadiusL = 5, int cornerRadiusR = 5)
        {
            if (graphics == null)
                throw new ArgumentNullException(nameof(graphics));
            if (pen == null)
                throw new ArgumentNullException(nameof(pen));

            using (GraphicsPath path = RoundedRect(bounds, cornerRadiusL, cornerRadiusR))
            {
                graphics.DrawPath(pen, path);
            }
        }

        public static void FillRoundedRectangle(Graphics graphics, Brush brush, Rectangle bounds, int cornerRadiusL = 5, int cornerRadiusR = 5)
        {
            if (graphics == null)
                throw new ArgumentNullException(nameof(graphics));
            if (brush == null)
                throw new ArgumentNullException(nameof(brush));

            using (GraphicsPath path = RoundedRect(bounds, cornerRadiusL, cornerRadiusR))
            {
                graphics.FillPath(brush, path);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND && DropDownStyle != ComboBoxStyle.Simple)
            {
                // The complete background is painted below; native erasing flashes white.
                m.Result = (nint)1;
                return;
            }
            if ((m.Msg == WM_PAINT || m.Msg == WM_PRINTCLIENT) && DropDownStyle != ComboBoxStyle.Simple)
            {
                var clientRect = ClientRectangle;
                var dropDownButtonWidth = SystemInformation.HorizontalScrollBarArrowWidth;
                var outerBorder = new Rectangle(clientRect.Location,
                    new Size(clientRect.Width - 1, clientRect.Height - 1));
                var innerBorder = new Rectangle(outerBorder.X + 1, outerBorder.Y + 1,
                    outerBorder.Width - dropDownButtonWidth - 2, outerBorder.Height - 2);
                var innerInnerBorder = new Rectangle(innerBorder.X + 1, innerBorder.Y + 1,
                    innerBorder.Width - 2, innerBorder.Height - 2);
                var dropDownRect = new Rectangle(innerBorder.Right + 1, innerBorder.Y,
                    dropDownButtonWidth, innerBorder.Height + 1);
                if (RightToLeft == RightToLeft.Yes)
                {
                    innerBorder.X = clientRect.Width - innerBorder.Right;
                    innerInnerBorder.X = clientRect.Width - innerInnerBorder.Right;
                    dropDownRect.X = clientRect.Width - dropDownRect.Right;
                    dropDownRect.Width += 1;
                }
                var innerBorderColor = BackColor;
                var outerBorderColor = BorderColor;
                var buttonColor = ButtonColor;
                var middle = new Point(dropDownRect.Left + dropDownRect.Width / 2,
                    dropDownRect.Top + dropDownRect.Height / 2);
                var arrow = new Point[]
                {
                new Point(middle.X - 3, middle.Y - 2),
                new Point(middle.X + 4, middle.Y - 2),
                new Point(middle.X, middle.Y + 2)
                };
                int outerRadius = Math.Max(1, (int)Math.Round(4 * DeviceDpi / 192f));
                var formBackColor = Parent is not null ? Parent.BackColor : BackColor;

                var ps = new PAINTSTRUCT();
                bool shouldEndPaint = m.Msg == WM_PAINT && m.WParam == nint.Zero;
                nint dc;
                if (shouldEndPaint)
                {
                    dc = BeginPaint(Handle, ref ps);
                }
                else
                {
                    dc = m.WParam;
                }

                int savedDc = SaveDC(dc);
                try
                {
                    using (var g = Graphics.FromHdc(dc))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var b = new SolidBrush(formBackColor))
                            g.FillRectangle(b, clientRect);
                        using (var b = new SolidBrush(buttonColor))
                            FillRoundedRectangle(g, b, outerBorder, outerRadius, outerRadius);
                    }

                    int textDc = SaveDC(dc);
                    try
                    {
                        IntersectClipRect(dc, innerInnerBorder.Left, innerInnerBorder.Top,
                            innerInnerBorder.Right, innerInnerBorder.Bottom);
                        var textMessage = Message.Create(Handle, WM_PRINTCLIENT, dc, (nint)PRF_CLIENT);
                        DefWndProc(ref textMessage);
                    }
                    finally
                    {
                        RestoreDC(dc, textDc);
                    }

                    using (var g = Graphics.FromHdc(dc))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var b = new SolidBrush(arrowColor))
                            g.FillPolygon(b, arrow);
                        if (DropDownStyle == ComboBoxStyle.DropDown)
                            using (var b = new SolidBrush(innerBorderColor))
                                g.FillRectangle(b, innerInnerBorder);
                        if (RForm.flatTheme)
                            using (var p = new Pen(BackColor))
                                DrawRoundedRectangle(g, p, outerBorder, outerRadius, outerRadius);
                        else
                            ControlHelper.DrawGradientBorder(g, outerBorder, outerBorderColor, outerRadius);
                    }
                }
                finally
                {
                    RestoreDC(dc, savedDc);
                    if (shouldEndPaint) EndPaint(Handle, ref ps);
                }
                m.Result = nint.Zero;
            }
            else
                base.WndProc(ref m);
        }

        private const int WM_PAINT = 0xF;
        private const int WM_ERASEBKGND = 0x14;
        private const int WM_PRINTCLIENT = 0x318;
        private const int PRF_CLIENT = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct COMBOBOXINFO
        {
            public int cbSize;
            public RECT rcItem, rcButton;
            public int stateButton;
            public nint hwndCombo, hwndItem, hwndList;
        }

        [DllImport("user32.dll")]
        private static extern bool GetComboBoxInfo(nint hwnd, ref COMBOBOXINFO info);

        [DllImport("uxtheme.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(nint hwnd, string appName, string? idList);

        [DllImport("gdi32.dll")]
        private static extern int SaveDC(nint dc);

        [DllImport("gdi32.dll")]
        private static extern bool RestoreDC(nint dc, int savedDc);

        [DllImport("gdi32.dll")]
        private static extern int IntersectClipRect(nint dc, int left, int top, int right, int bottom);
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int L, T, R, B;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct PAINTSTRUCT
        {
            public nint hdc;
            public bool fErase;
            public int rcPaint_left;
            public int rcPaint_top;
            public int rcPaint_right;
            public int rcPaint_bottom;
            public bool fRestore;
            public bool fIncUpdate;
            public int reserved1;
            public int reserved2;
            public int reserved3;
            public int reserved4;
            public int reserved5;
            public int reserved6;
            public int reserved7;
            public int reserved8;
        }
        [DllImport("user32.dll")]
        private static extern nint BeginPaint(nint hWnd,
            [In, Out] ref PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

        public static void DrawBindingItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || sender is not ComboBox cmb) return;

            object obj = cmb.Items[e.Index];
            bool isSep = obj is BindingSeparator;

            Color back = isSep ? RForm.buttonSecond : RForm.buttonMain;
            if (!isSep && (e.State & DrawItemState.Selected) != 0)
                back = RForm.borderMain;

            using var backBrush = new SolidBrush(back);
            e.Graphics.FillRectangle(backBrush, e.Bounds);

            string text = obj.ToString() ?? string.Empty;
            Font font = isSep
                ? new Font(e.Font ?? SystemFonts.DefaultFont, FontStyle.Bold)
                : (e.Font ?? SystemFonts.DefaultFont);

            int indent = isSep ? 6 : 14;
            var textRect = new Rectangle(e.Bounds.X + indent, e.Bounds.Y,
                                         e.Bounds.Width - indent, e.Bounds.Height);

            using var foreBrush = new SolidBrush(RForm.foreMain);
            e.Graphics.DrawString(text, font, foreBrush, textRect,
                new StringFormat { LineAlignment = StringAlignment.Center });

            if (isSep) font.Dispose();
        }
    }

    public sealed class BindingItem
    {
        public ushort Code { get; }
        public string DisplayName { get; }
        public ushort[]? ComboKeys { get; }
        public BindingItem(ushort code, string name, ushort[]? comboKeys = null)
        { Code = code; DisplayName = name; ComboKeys = comboKeys; }
        public override string ToString() => DisplayName;
    }

    public sealed class BindingSeparator
    {
        public string Label { get; }
        public BindingSeparator(string label) { Label = label; }
        public override string ToString() => Label;
    }
}
