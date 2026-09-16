using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BangGang;

/// <summary>GDI 屏幕区域抓取（物理像素坐标）。</summary>
internal static class ScreenGrab
{
    /// <summary>抓取屏幕区域，坐标为物理像素（以主屏左上为原点）。失败返回 null。</summary>
    public static Bitmap? CaptureRegion(Rectangle region)
    {
        Rectangle vs = SystemInformation.VirtualScreen;
        region.Intersect(vs);
        if (region.Width <= 0 || region.Height <= 0) return null;

        // GDI 屏幕 DC 原点为主屏左上；左/上副屏坐标为负，需换算到 DC 坐标系
        int sx = region.X - vs.X;
        int sy = region.Y - vs.Y;

        var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            IntPtr dest = g.GetHdc();
            try
            {
                IntPtr screen = Win32.GetDC(IntPtr.Zero);
                try
                {
                    Win32.BitBlt(dest, 0, 0, region.Width, region.Height, screen,
                        sx, sy, Win32.SRCCOPY | Win32.CAPTUREBLT);
                }
                finally { Win32.ReleaseDC(IntPtr.Zero, screen); }
            }
            finally { g.ReleaseHdc(dest); }
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            return null;
        }
    }
}
