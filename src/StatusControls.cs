using System.Drawing;
using System.Runtime.InteropServices;

namespace SteamLZXTray;

/// <summary>
/// Single-line label that never forces the window wider: text is cut with an ellipsis
/// (at the end, or in the middle for paths) and the full text is shown in a tooltip when cut.
/// Height comes from font metrics, so it follows DPI.
/// </summary>
internal sealed class EllipsisLabel : Control
{
    private readonly ToolTip _tip;
    private readonly bool _path;
    private string? _tipText;

    public EllipsisLabel(ToolTip tip, bool pathEllipsis = false)
    {
        _tip = tip;
        _path = pathEllipsis;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        Dock = DockStyle.Fill;
        Margin = new Padding(3, 1, 3, 1);
        TabStop = false;
    }

    private TextFormatFlags Flags => TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
                                     TextFormatFlags.Left | TextFormatFlags.NoPadding |
                                     (_path ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis);

    // Width 0: the table column decides the width. Height: one line of the current (DPI-scaled) font.
    public override Size GetPreferredSize(Size proposedSize) =>
        new(0, TextRenderer.MeasureText("Ag", Font, Size.Empty, TextFormatFlags.SingleLine).Height + 2);

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); UpdateTip(); Invalidate(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateTip(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateTip(); }

    private void UpdateTip()
    {
        string? want = null;
        if (Text.Length > 0 && Width > 0)
        {
            int needed = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
            if (needed > ClientSize.Width) want = Text;
        }
        if (want != _tipText) { _tipText = want; _tip.SetToolTip(this, want); }
    }

    protected override void OnPaint(PaintEventArgs e) =>
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : SystemColors.GrayText, Flags);
}

internal static class ListViewMetrics
{
    private const int LVM_GETHEADER = 0x101F;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Native.RECT rect);

    /// <summary>Header height in pixels (real header if available, else from font metrics).</summary>
    public static int HeaderHeight(ListView lv)
    {
        if (lv.IsHandleCreated && lv.HeaderStyle != ColumnHeaderStyle.None)
        {
            IntPtr hdr = SendMessageW(lv.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (hdr != IntPtr.Zero && GetWindowRect(hdr, out var r) && r.Bottom - r.Top > 0) return r.Bottom - r.Top;
        }
        return lv.HeaderStyle == ColumnHeaderStyle.None ? 0 : lv.Font.Height + lv.LogicalToDeviceUnits(8);
    }

    /// <summary>Row height in pixels (real row if one exists, else from font metrics).</summary>
    public static int RowHeight(ListView lv)
    {
        if (lv.IsHandleCreated && lv.Items.Count > 0)
        {
            int h = lv.GetItemRect(0).Height;
            if (h > 0) return h;
        }
        return lv.Font.Height + lv.LogicalToDeviceUnits(4);
    }
}
