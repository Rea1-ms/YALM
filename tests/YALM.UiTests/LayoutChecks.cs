namespace YALM.UiTests;

internal static partial class Program
{
    private static void CheckMainLayout(bool render)
    {
        // Demo data and startup-test mode avoid hardware access and tray registration.
        using var main = CreateForm(demo: true, startup: true);
        main.Opacity = 0;
        main.ShowInTaskbar = false;
        foreach (Control control in Descendants(main)) _ = control.Handle;
        main.PerformLayout();
        CheckCompactHeadings(main, "initial layout");

        // Exercise the same child scaling even on machines with a single monitor.
        for (int pass = 0; pass < 3; pass++)
        {
            main.Scale(new SizeF(0.8F, 0.8F));
            main.Scale(new SizeF(1.25F, 1.25F));
            main.PerformLayout();
            CheckCompactHeadings(main, $"scale round trip {pass}");
        }

        main.Show();
        Application.DoEvents();
        int initialDpi = main.DeviceDpi;
        Size initialClientSize = main.ClientSize;
        var initialLayout = Descendants(main).Select(control =>
            (Control: control, Bounds: control.Bounds, FontSize: control.Font.SizeInPoints)).ToArray();
        Screen initial = Screen.FromControl(main);
        Screen[] screens = Screen.AllScreens;
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (Screen screen in screens.Append(initial))
            {
                main.Location = new Point(screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40);
                Application.DoEvents();
                main.PerformLayout();
                CheckCompactHeadings(main, $"monitor round trip {pass}, DPI {main.DeviceDpi}");
                Require(!main.VerticalScroll.Visible && !main.HorizontalScroll.Visible,
                    $"main content fits after changing monitors, DPI {main.DeviceDpi}");
                if (Application.HighDpiMode == HighDpiMode.SystemAware)
                {
                    Require(main.DeviceDpi == initialDpi && main.ClientSize == initialClientSize,
                        "changing monitors keeps the logical window size and DPI");
                    Require(initialLayout.All(item => item.Control.Bounds == item.Bounds &&
                        item.Control.Font.SizeInPoints == item.FontSize),
                        "Windows scales the whole window without reflowing or enlarging individual controls");
                }
                if (render && pass == 0) RenderLayout(main);
            }
            main.Hide();
            main.Show();
            Application.DoEvents();
            CheckCompactHeadings(main, "reopened window");
        }
        Call(main, "ExitApplication");
    }

    private static void CheckCompactHeadings(Form main, string context)
    {
        Label[] titles = [Get<Label>(main, "_reportedMode"), Get<Label>(main, "_gpuModeTitle"),
            Descendants(main).OfType<Label>().Single(label => label.Text == "笔记本键盘")];
        foreach (Label title in titles)
        {
            Control heading = title.Parent!;
            int contentHeight = Math.Max(heading.MinimumSize.Height,
                heading.Controls.Cast<Control>().Max(control => control.Height + control.Margin.Vertical));
            // The keyboard switches can be slightly taller than the title at some DPIs.
            int tolerance = (int)Math.Ceiling(6F * main.DeviceDpi / 96F);
            Require(heading.Height <= contentHeight + tolerance,
                $"{context}: {title.Text} heading has no retained blank space ({heading.Height} vs {contentHeight})");
            Require(title.Bottom <= heading.ClientSize.Height,
                $"{context}: heading text remains visible");
        }
    }

    private static void RenderLayout(Form main)
    {
        string directory = Path.GetFullPath(".cache/layout-gap-20261028/verified");
        Directory.CreateDirectory(directory);
        using var image = new Bitmap(main.Width, main.Height);
        main.DrawToBitmap(image, new Rectangle(Point.Empty, main.Size));
        image.Save(Path.Combine(directory, $"main-{main.DeviceDpi}.png"));
    }
}
