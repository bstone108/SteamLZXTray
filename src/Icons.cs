using System.Drawing;
using System.Drawing.Drawing2D;

namespace SteamLZXTray;

/// <summary>Programmatically drawn tray icons: 12 spinning frames, idle, waiting, paused.</summary>
internal sealed class IconSet : IDisposable
{
    public Icon[] Spin { get; }
    public Icon Idle { get; }
    public Icon Waiting { get; }
    public Icon Paused { get; }
    private readonly List<IntPtr> _handles = new();

    public IconSet()
    {
        int sz = Math.Max(16, SystemInformation.SmallIconSize.Width);
        Spin = Enumerable.Range(0, 12).Select(i => Make(sz, g => DrawDisc(g, sz, Color.FromArgb(40, 120, 200), i * 30f, Color.FromArgb(120, 230, 120)))).ToArray();
        Idle = Make(sz, g => DrawDisc(g, sz, Color.FromArgb(40, 120, 200), null, Color.Empty));
        Waiting = Make(sz, g => { DrawDisc(g, sz, Color.FromArgb(200, 150, 30), null, Color.Empty); DrawHourglass(g, sz); });
        Paused = Make(sz, g => { DrawDisc(g, sz, Color.FromArgb(120, 120, 120), null, Color.Empty); DrawPause(g, sz); });
    }

    private Icon Make(int sz, Action<Graphics> draw)
    {
        using var bmp = new Bitmap(sz, sz, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            draw(g);
        }
        IntPtr h = bmp.GetHicon();
        _handles.Add(h);
        return Icon.FromHandle(h);
    }

    private static void DrawDisc(Graphics g, int sz, Color body, float? angle, Color arc)
    {
        float pad = sz * 0.06f;
        var r = new RectangleF(pad, pad, sz - 2 * pad, sz - 2 * pad);
        using (var b = new SolidBrush(body)) g.FillEllipse(b, r);
        using (var p = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1f, sz / 16f))) g.DrawEllipse(p, r);
        // hub
        float hub = sz * 0.22f;
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, sz / 2f - hub / 2, sz / 2f - hub / 2, hub, hub);
        if (angle is float a)
        {
            float w = Math.Max(2f, sz / 7f);
            var ar = RectangleF.Inflate(r, -w / 2 - sz * 0.05f, -w / 2 - sz * 0.05f);
            using var p = new Pen(arc, w) { StartCap = LineCap.Round, EndCap = LineCap.ArrowAnchor };
            g.DrawArc(p, ar, a, 120f);
            using var p2 = new Pen(Color.FromArgb(160, arc), w * 0.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(p2, ar, a + 180f, 60f);
        }
        else
        {
            // static "LZ" tick marks
            using var p = new Pen(Color.FromArgb(200, 255, 255, 255), Math.Max(1f, sz / 14f));
            float c = sz / 2f, ro = sz * 0.40f, ri = sz * 0.28f;
            for (int i = 0; i < 4; i++)
            {
                double t = Math.PI / 4 + i * Math.PI / 2;
                g.DrawLine(p, c + (float)(Math.Cos(t) * ri), c + (float)(Math.Sin(t) * ri), c + (float)(Math.Cos(t) * ro), c + (float)(Math.Sin(t) * ro));
            }
        }
    }

    private static void DrawPause(Graphics g, int sz)
    {
        float w = sz * 0.16f, h = sz * 0.5f, y = sz * 0.25f;
        using var b = new SolidBrush(Color.FromArgb(255, 240, 240, 240));
        using var o = new SolidBrush(Color.FromArgb(120, 120, 120));
        g.FillEllipse(o, sz * 0.2f, sz * 0.2f, sz * 0.6f, sz * 0.6f);
        g.FillRectangle(b, sz / 2f - w * 1.4f, y, w, h);
        g.FillRectangle(b, sz / 2f + w * 0.4f, y, w, h);
    }

    private static void DrawHourglass(Graphics g, int sz)
    {
        float c = sz / 2f, s = sz * 0.28f;
        using var o = new SolidBrush(Color.FromArgb(200, 150, 30));
        g.FillEllipse(o, sz * 0.2f, sz * 0.2f, sz * 0.6f, sz * 0.6f);
        using var b = new SolidBrush(Color.White);
        g.FillPolygon(b, new[] { new PointF(c - s, c - s), new PointF(c + s, c - s), new PointF(c, c) });
        g.FillPolygon(b, new[] { new PointF(c - s, c + s), new PointF(c + s, c + s), new PointF(c, c) });
    }

    public void Dispose()
    {
        foreach (var i in Spin) i.Dispose();
        Idle.Dispose(); Waiting.Dispose(); Paused.Dispose();
        foreach (var h in _handles) Native.DestroyIcon(h);
        _handles.Clear();
    }
}
