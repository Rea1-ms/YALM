using GHelper;
using GHelper.UI;
using YALM.Mechrevo;

namespace YALM.UiTests;

internal static partial class Program
{
    private static void CheckChildWindowLayouts(Form main, bool render)
    {
        Type moreType = MainForm.GetNestedType("MoreSettingsForm", Private)!;
        using var more = (Form)Activator.CreateInstance(moreType, main)!;
        CheckChildWindow(more, main, "more", render);
        Size moreSize = more.Size;
        more.Close();
        CheckChildWindow(more, main, "more-reopened", false);
        Require(more.Size == moreSize, "reopening settings keeps a stable window size");

        Type customType = MainForm.Assembly.GetType("YALM.Mechrevo.CustomModeForm")!;
        using var custom = (Form)Activator.CreateInstance(customType,
            null, (Func<EcFanSnapshot?>)(() => null), true, null)!;
        CheckChildWindow(custom, main, "fans", false);
        Size fanSize = custom.Size;
        foreach (string page in new[] { "CPU", "GPU", "更多" })
        {
            Call(custom, "SelectPage", page);
            CheckChildWindow(custom, main, "fans-" + page, render);
            Require(custom.Size == fanSize, "switching fan pages keeps a stable window size");
        }

        var live = new GcuKeyboardSettings
        {
            Type = "SingleZone", Brightness = 3, MonochromeIndex = 1,
            ManualIndices = [1, 2, 3, 4, 5, 6], ManualInterval = 4, BreathingIndex = 1
        };
        var draft = new GcuKeyboardDraft(live, "Manual", 1, live.ManualIndices,
            4, Enumerable.Repeat(new GcuRgb(255, 0, 0), 7).ToArray());
        Type keyboardType = MainForm.Assembly.GetType("YALM.Mechrevo.KeyboardSettingsForm")!;
        using var keyboard = (Form)Activator.CreateInstance(keyboardType, draft,
            (Func<Task<GcuKeyboardDraft?>>)(() => Task.FromResult<GcuKeyboardDraft?>(null)), true)!;
        CheckChildWindow(keyboard, main, "keyboard", render);
        Size keyboardSize = keyboard.Size;
        Get<Label>(keyboard, "_status").Text = "已读取服务颜色序号与厂商保存的自定义颜色，尚未应用。";
        CheckChildWindow(keyboard, main, "keyboard-status", false);
        Require(keyboard.Size == keyboardSize, "keyboard feedback fits without resizing the window");

        using var overlay = new OverlayConfig(() => { }, () => { }, null, main,
            limitedTelemetry: true, readOnly: true);
        CheckChildWindow(overlay, main, "overlay", render);
        using var picker = new RColorPicker(Color.Red);
        CheckChildWindow(picker, main, "color", render);
    }

    private static void CheckChildWindow(Form form, Form owner, string name, bool render)
    {
        form.Opacity = 0;
        form.ShowInTaskbar = false;
        if (!form.Visible) form.Show(owner);
        Application.DoEvents();
        foreach (Control control in Descendants(form)) _ = control.Handle;
        form.PerformLayout();
        Require((ReadWindowStyle(form.Handle, -16) & 0x00040000) == 0,
            name + ": native window has no resizable border");
        Require(!form.MaximizeBox && !form.MinimizeBox, name + ": no maximize/minimize buttons");
        Rectangle area = Screen.FromControl(form).WorkingArea;
        Require(form.Width <= area.Width && form.Height <= area.Height,
            name + ": complete window fits the screen work area");
        foreach (Control control in Descendants(form).Prepend(form).Where(control => control.Visible))
        {
            if (control is ScrollableControl scroll)
                Require(!scroll.HorizontalScroll.Visible && !scroll.VerticalScroll.Visible,
                    name + ": no scrollbars in " + control.GetType().Name);
            if (control == form) continue;
            Require(control.Parent!.ClientRectangle.Contains(control.Bounds),
                $"{name}: {control.GetType().Name} '{control.Text}' fits its parent: {control.Bounds} in {control.Parent.ClientRectangle}");
            if (control is Label label)
                Require(label.Height >= label.GetPreferredSize(new Size(label.Width, 0)).Height,
                    name + ": complete label text: " + label.Text);
        }
        Console.WriteLine($"LAYOUT {name}: DPI {form.DeviceDpi}, client {form.ClientSize}");
        if (!render) return;
        string directory = Path.GetFullPath(".cache/child-windows-20261031/ui");
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(directory, $"{name}-{form.DeviceDpi}.png"));
    }
}
