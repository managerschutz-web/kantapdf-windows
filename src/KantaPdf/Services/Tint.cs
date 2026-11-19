using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KantaPdf.Services
{
    /// <summary>Färbt gerenderte Seiten für Sepia- und Nachtansicht um (nur Anzeige, nie die Datei).</summary>
    public static class Tint
    {
        public static BitmapSource Apply(BitmapSource src, ReadMode mode)
        {
            if (mode == ReadMode.Papier || src == null) return src;

            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);

            if (mode == ReadMode.Sepia)
            {
                // Weiß → warmes Papier, Schwarz → dunkles Braun
                byte[] lb = Lut(26, 216), lg = Lut(34, 236), lr = Lut(40, 246);
                for (int i = 0; i < px.Length; i += 4)
                {
                    px[i] = lb[px[i]];
                    px[i + 1] = lg[px[i + 1]];
                    px[i + 2] = lr[px[i + 2]];
                }
            }
            else
            {
                // Nacht: Helligkeit umkehren, Farbton grob erhalten
                const double loB = 28, loG = 26, loR = 24;     // Seitenhintergrund
                const double hiB = 200, hiG = 216, hiR = 220;  // Schrift
                for (int i = 0; i < px.Length; i += 4)
                {
                    double b = px[i], g = px[i + 1], r = px[i + 2];
                    double l = 0.114 * b + 0.587 * g + 0.299 * r;
                    double inv = (255 - l) / 255.0;
                    double nb = loB + inv * (hiB - loB) + (b - l) * 0.55;
                    double ng = loG + inv * (hiG - loG) + (g - l) * 0.55;
                    double nr = loR + inv * (hiR - loR) + (r - l) * 0.55;
                    px[i] = Clamp(nb);
                    px[i + 1] = Clamp(ng);
                    px[i + 2] = Clamp(nr);
                }
            }

            var outBmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            outBmp.Freeze();
            return outBmp;
        }

        private static byte[] Lut(int lo, int hi)
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++) t[i] = (byte)(lo + i * (hi - lo) / 255);
            return t;
        }

        private static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }
}
