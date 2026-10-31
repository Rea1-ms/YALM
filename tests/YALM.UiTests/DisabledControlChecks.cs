using System.Reflection;
using GHelper;
using GHelper.UI;
using YALM.Mechrevo;

namespace YALM.UiTests;

internal static partial class Program
{
    private static void CheckDisabledControlThemes(bool render)
    {
        var config = (Dictionary<string, object?>)typeof(AppConfig)
            .GetField("Values", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        bool hadMode = config.TryGetValue("ui_mode", out object? originalMode);
        bool wasDark = RForm.formBack.GetBrightness() < 0.5F;
        try
        {
            foreach (bool dark in new[] { true, false })
            {
                config["ui_mode"] = dark ? "dark" : "light";
                using var main = CreateForm(demo: true, startup: true);
                Type moreType = MainForm.GetNestedType("MoreSettingsForm", Private)!;
                using var more = (Form)Activator.CreateInstance(moreType, main)!;
                using var overlay = new OverlayConfig(() => { }, () => { }, null, main,
                    limitedTelemetry: true, readOnly: true);
                Type customType = MainForm.Assembly.GetType("YALM.Mechrevo.CustomModeForm")!;
                using var custom = (Form)Activator.CreateInstance(customType,
                    null, (Func<EcFanSnapshot?>)(() => null), true, null)!;

                foreach (var (form, name) in new[] { (main, "main"), (more, "more"),
                    (overlay, "overlay"), (custom, "fans") })
                {
                    form.Opacity = 0;
                    form.ShowInTaskbar = false;
                    form.Show();
                    Application.DoEvents();
                    foreach (Control control in Descendants(form)) _ = control.Handle;
                    form.PerformLayout();
                    foreach (CheckBox check in Descendants(form).OfType<CheckBox>())
                    {
                        bool enabled = check.Enabled;
                        check.Enabled = false;
                        try { CheckCheckboxCaption(check, $"{name}/{check.Text}/{dark}", dark); }
                        finally { check.Enabled = enabled; }
                    }
                    if (render) RenderDisabledControls(form, name, dark);
                }

                // Both checkbox families must retain the native glyph for all
                // states, including the padded upstream overlay toggles.
                foreach (Type type in new[] { Get<CheckBox>(more, "_writePermission").GetType(), typeof(RCheckBox) })
                foreach (Padding padding in new[] { Padding.Empty, new Padding(10, 4, 10, 4) })
                {
                    using var sampleForm = new RForm { darkTheme = dark };
                    using var sample = (CheckBox)Activator.CreateInstance(type)!;
                    sample.Text = "显示功能键提示";
                    sample.Font = more.Font;
                    sample.Padding = padding;
                    sample.Size = new Size(240, sample.Font.Height + padding.Vertical + 8);
                    sample.ThreeState = true;
                    sample.Enabled = false;
                    sampleForm.Controls.Add(sample);
                    sampleForm.InitTheme();
                    sampleForm.Opacity = 0;
                    sampleForm.ShowInTaskbar = false;
                    sampleForm.Show();
                    Application.DoEvents();
                    foreach (CheckState state in Enum.GetValues<CheckState>())
                    {
                        sample.CheckState = state;
                        CheckCheckboxCaption(sample, $"{type.Name}/{padding}/{state}/{dark}", dark);
                    }
                    typeof(Control).GetMethod("RecreateHandle", Private)!.Invoke(sample, null);
                    ControlHelper.Adjust(sampleForm);
                    CheckCheckboxCaption(sample, $"{type.Name}/recreated/{dark}", dark);
                }
            }
        }
        finally
        {
            if (hadMode) config["ui_mode"] = originalMode!;
            else config.Remove("ui_mode");
            RForm.InitColors(wasDark);
        }
    }

    private static void CheckCheckboxCaption(CheckBox check, string name, bool dark)
    {
        string text = check.Text;
        bool autoSize = check.AutoSize;
        Size size = check.Size;
        check.AutoSize = false;
        try
        {
            using var caption = RenderControl(check);
            check.Text = "";
            using var blank = RenderControl(check);
            int glyphWidth;
            using (Graphics graphics = Graphics.FromImage(caption))
                glyphWidth = CheckBoxRenderer.GetGlyphSize(graphics,
                    System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled).Width;
            int glyphEnd = check.Padding.Left + glyphWidth;
            int readable = 0;
            double maximumContrast = 1;
            int glyphChanges = 0;
            for (int y = 0; y < caption.Height; y++)
            for (int x = 0; x < caption.Width; x++)
            {
                Color pixel = caption.GetPixel(x, y);
                if (pixel.ToArgb() == blank.GetPixel(x, y).ToArgb()) continue;
                if (x < glyphEnd) glyphChanges++;
                else
                {
                    double contrast = Contrast(pixel, check.BackColor);
                    maximumContrast = Math.Max(maximumContrast, contrast);
                    if (contrast >= (dark ? 3 : 2)) readable++;
                }
            }
            Require(glyphChanges == 0, name + ": caption correction preserves native checkbox glyph");
            if (readable < 10)
            {
                string directory = Path.GetFullPath(".cache/disabled-text-20261031/failures");
                Directory.CreateDirectory(directory);
                caption.Save(Path.Combine(directory, "caption.png"));
                blank.Save(Path.Combine(directory, "blank.png"));
            }
            Require(readable >= 10, $"{name}: disabled caption remains readable ({readable} pixels, contrast {maximumContrast:0.00}, {size}, {check.BackColor})");
        }
        finally
        {
            check.Text = text;
            check.Size = size;
            check.AutoSize = autoSize;
        }
    }

    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value)
        {
            double level = value / 255.0;
            return level <= 0.04045 ? level / 12.92 : Math.Pow((level + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) =>
            0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static Bitmap RenderControl(Control control)
    {
        var bitmap = new Bitmap(control.Width, control.Height);
        if (control is CheckBox)
        {
            // Hidden native checkboxes may omit their caption from WM_PRINT.
            // Ask for the client paint directly, as the existing UI capture does.
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.Clear(control.BackColor);
            nint dc = graphics.GetHdc();
            try { PrintNativeControl(control.Handle, 0x318, dc, 4); }
            finally { graphics.ReleaseHdc(dc); }
        }
        else control.DrawToBitmap(bitmap, control.ClientRectangle);
        return bitmap;
    }

    private static void RenderDisabledControls(Form form, string name, bool dark)
    {
        Control[] controls = Descendants(form).Where(control =>
            control is RComboBox or RNumericUpDown || !control.Enabled && control is CheckBox or RButton).ToArray();
        using var image = new Bitmap(680, Math.Max(1, controls.Length) * 65);
        using Graphics graphics = Graphics.FromImage(image);
        graphics.Clear(form.BackColor);
        for (int index = 0; index < controls.Length; index++)
        {
            Control control = controls[index];
            TextRenderer.DrawText(graphics, control.GetType().Name, form.Font,
                new Point(5, index * 65 + 8), form.ForeColor);
            bool enabled = control.Enabled;
            control.Enabled = false;
            try
            {
                using var bitmap = RenderControl(control);
                graphics.DrawImageUnscaled(bitmap, 175, index * 65 + 5);
            }
            finally { control.Enabled = enabled; }
        }
        string directory = Path.GetFullPath(".cache/disabled-text-20261031/ui");
        Directory.CreateDirectory(directory);
        image.Save(Path.Combine(directory, $"{name}-{(dark ? "dark" : "light")}-{form.DeviceDpi}.png"));
    }
}
