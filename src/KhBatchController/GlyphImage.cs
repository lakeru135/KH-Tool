using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KhBatchController;

/// <summary>
/// Converts the add-in's ribbon glyphs (<c>UI/Icons</c>) into WinForms images.
///
/// The glyphs are drawn with GDI+ but handed out as WPF <see cref="BitmapSource"/>, because that is
/// what Revit's ribbon takes. The controller's launcher is WinForms and needs
/// <see cref="System.Drawing.Image"/>. Converting here rather than adding a second render path to
/// Icons keeps a single definition of every glyph — the controller shows literally the same
/// artwork as the ribbon button it corresponds to.
/// </summary>
internal static class GlyphImage
{
    /// <summary>
    /// Copies a glyph into a 32bpp ARGB bitmap. Alpha is preserved deliberately: these are
    /// transparent line-art glyphs, and the naive HBITMAP conversion flattens transparency to
    /// black — invisible on a dark background, a black box on a light one.
    /// </summary>
    public static Bitmap From(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;

        byte[] pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }
}
