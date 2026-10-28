namespace YALM.Mechrevo;

internal static class MechrevoUi
{
    public static Icon AppIcon { get; } = GHelper.Properties.Resources.yalm;

    // Upstream buttons are 48 px high at its 192-DPI design baseline.
    // Programmatic forms use the current DPI, so convert that size once.
    public static void SizeCompactButtons(Control owner, params GHelper.UI.RButton[] buttons)
    {
        int height = (int)Math.Round(48F * owner.DeviceDpi / 192F);
        foreach (var button in buttons) button.Height = height;
    }

    // Upstream section icons are 32 px at the 192-DPI design baseline.
    public static Control Heading(Control owner, Label title, Image icon)
    {
        int iconSize = (int)Math.Round(32F * owner.DeviceDpi / 192F);
        int iconLeft = (int)Math.Round(8F * owner.DeviceDpi / 192F);
        int headingHeight = (int)Math.Round(40F * owner.DeviceDpi / 192F);
        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty,
            MinimumSize = new Size(0, headingHeight)
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, iconLeft + iconSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // Let the content determine the row height. WinForms scales an absolute
        // row from its stretched bounds during a DPI change, retaining blank space.
        heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var picture = new PictureBox
        {
            BackgroundImage = icon, BackgroundImageLayout = ImageLayout.Zoom,
            Size = new Size(iconSize, iconSize), Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Margin = new Padding(iconLeft, 0, 0, 0), TabStop = false
        };
        // Upstream places the icon, title and readings at the same top edge.
        // Default label margins otherwise shift text away from the icon.
        title.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        title.Margin = Padding.Empty;
        title.TextAlign = ContentAlignment.TopLeft;
        heading.Controls.Add(picture, 0, 0);
        heading.Controls.Add(title, 1, 0);
        return heading;
    }
}
