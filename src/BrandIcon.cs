using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace GameReplay;

/// <summary>The same original replay-and-play mark is used in the header, window, and tray.</summary>
static class BrandIcon
{
    public static Icon Shared { get; }=Create();

    public static void Draw(Graphics graphics,Rectangle bounds)
    {
        var saved=graphics.Save();
        try{
            graphics.SmoothingMode=SmoothingMode.AntiAlias;
            graphics.TranslateTransform(bounds.X,bounds.Y);graphics.ScaleTransform(bounds.Width/64f,bounds.Height/64f);
            using var background=new SolidBrush(Color.FromArgb(26,35,49));
            using var accent=new SolidBrush(Color.FromArgb(71,215,161));
            using var white=new SolidBrush(Color.FromArgb(242,248,246));
            using var outline=new Pen(Color.FromArgb(71,215,161),5){StartCap=LineCap.Round,EndCap=LineCap.Round};
            graphics.FillEllipse(background,1,1,62,62);
            graphics.DrawArc(outline,10,10,44,44,-60,292);
            graphics.FillPolygon(accent,[new PointF(8,37),new PointF(7,51),new PointF(21,48)]);
            graphics.FillPolygon(white,[new PointF(27,21),new PointF(44,32),new PointF(27,43)]);
        }finally{graphics.Restore(saved);}
    }

    static Icon Create()
    {
        using var bitmap=new Bitmap(64,64);using(var graphics=Graphics.FromImage(bitmap))Draw(graphics,new Rectangle(0,0,64,64));
        nint handle=bitmap.GetHicon();
        try{using var borrowed=Icon.FromHandle(handle);return (Icon)borrowed.Clone();}finally{DestroyIcon(handle);}
    }
    [DllImport("user32.dll")]static extern bool DestroyIcon(nint handle);
}
