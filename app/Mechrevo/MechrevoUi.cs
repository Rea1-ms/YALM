namespace YALM.Mechrevo;

internal static class MechrevoUi
{
    // Upstream section icons are 32 px at the 192-DPI design baseline.
    public static Control Heading(Control owner, Label title, Image icon)
    {
        int iconSize = (int)Math.Round(32F * owner.DeviceDpi / 192F);
        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var picture = new PictureBox
        {
            BackgroundImage = icon, BackgroundImageLayout = ImageLayout.Zoom,
            Size = new Size(iconSize, iconSize), Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 4, 0), TabStop = false
        };
        title.Anchor = AnchorStyles.Left;
        heading.Controls.Add(picture, 0, 0);
        heading.Controls.Add(title, 1, 0);
        return heading;
    }
}
