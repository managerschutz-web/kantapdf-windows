using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KantaPdf.Services
{
    /// <summary>Ein Stempel: Text-Stempel (mit optionalem Datum) oder Bild-Stempel.</summary>
    public sealed class StampDef
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 10);
        public string Text1 { get; set; } = "";
        public string Text2 { get; set; } = "";
        public string Color { get; set; } = "#3C6E12";
        public bool WithDate { get; set; }
        public string ImageFile { get; set; }      // gesetzt = Bild-Stempel
        public bool BuiltIn { get; set; }
    }

    /// <summary>Gespeicherte Unterschriften und Stempel (im Benutzerprofil).</summary>
    public static class Library
    {
        private static string Root =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KANTApdf");
        public static string SignatureDir => Path.Combine(Root, "Unterschriften");
        public static string StampDir => Path.Combine(Root, "Stempel");
        private static string StampFile => Path.Combine(StampDir, "stempel.json");

        // ───────────── Unterschriften ─────────────

        public static List<string> Signatures()
        {
            Directory.CreateDirectory(SignatureDir);
            return Directory.GetFiles(SignatureDir, "*.png").OrderByDescending(File.GetCreationTime).ToList();
        }

        public static string SaveSignature(byte[] png)
        {
            Directory.CreateDirectory(SignatureDir);
            var path = Path.Combine(SignatureDir, $"unterschrift_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            File.WriteAllBytes(path, png);
            return path;
        }

        /// <summary>Gezeichnete Striche → PNG mit transparentem Hintergrund, eng zugeschnitten.</summary>
        public static byte[] StrokesToPng(StrokeCollection strokes)
        {
            var b = strokes.GetBounds();
            const double pad = 6, scale = 3;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(-b.X + pad, -b.Y + pad));
                strokes.Draw(dc);
                dc.Pop();
            }
            int w = (int)Math.Ceiling((b.Width + 2 * pad) * scale), h = (int)Math.Ceiling((b.Height + 2 * pad) * scale);
            var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(dv);
            return ToPng(rtb);
        }

        /// <summary>Bild (z. B. eingescannte Unterschrift) → helle Fläche transparent, zugeschnitten.</summary>
        public static byte[] ImageToTransparentPng(string file, bool removeWhite)
        {
            var dec = BitmapDecoder.Create(new Uri(file), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var conv = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;
                    if (removeWhite)
                    {
                        int lum = (px[i] + px[i + 1] + px[i + 2]) / 3;
                        if (lum > 215) px[i + 3] = 0;
                        else if (lum > 160) px[i + 3] = (byte)(px[i + 3] * (215 - lum) / 55);
                    }
                    if (px[i + 3] > 20)
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                }
            if (maxX < 0) throw new InvalidOperationException("Auf dem Bild wurde nichts erkannt.");

            var full = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            int pad = 4;
            var rect = new Int32Rect(Math.Max(0, minX - pad), Math.Max(0, minY - pad),
                Math.Min(w, maxX + pad + 1) - Math.Max(0, minX - pad), Math.Min(h, maxY + pad + 1) - Math.Max(0, minY - pad));
            return ToPng(new CroppedBitmap(full, rect));
        }

        public static byte[] ToPng(BitmapSource src)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        // ───────────── Stempel ─────────────

        public static List<StampDef> BuiltInStamps() => new List<StampDef>
        {
            new StampDef { Id = "b_erledigt", Text1 = "ERLEDIGT", Color = "#2F7D32", WithDate = true, BuiltIn = true },
            new StampDef { Id = "b_eingang", Text1 = "EINGANG", Color = "#C62828", WithDate = true, BuiltIn = true },
            new StampDef { Id = "b_geprueft", Text1 = "GEPRÜFT", Color = "#1565C0", WithDate = true, BuiltIn = true },
            new StampDef { Id = "b_kopie", Text1 = "KOPIE", Color = "#1565C0", BuiltIn = true },
            new StampDef { Id = "b_vertraulich", Text1 = "VERTRAULICH", Color = "#C62828", BuiltIn = true },
            new StampDef { Id = "b_entwurf", Text1 = "ENTWURF", Color = "#616161", BuiltIn = true },
            new StampDef { Id = "b_gebucht", Text1 = "GEBUCHT", Color = "#2F7D32", WithDate = true, BuiltIn = true },
        };

        public static List<StampDef> OwnStamps()
        {
            try
            {
                if (File.Exists(StampFile))
                    return JsonSerializer.Deserialize<List<StampDef>>(File.ReadAllText(StampFile)) ?? new List<StampDef>();
            }
            catch { }
            return new List<StampDef>();
        }

        public static void SaveOwnStamps(List<StampDef> list)
        {
            Directory.CreateDirectory(StampDir);
            File.WriteAllText(StampFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static string ImportStampImage(string file)
        {
            Directory.CreateDirectory(StampDir);
            var png = ImageToTransparentPng(file, removeWhite: false);
            var path = Path.Combine(StampDir, $"bild_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            File.WriteAllBytes(path, png);
            return path;
        }

        /// <summary>Stempel als PNG – Text-Stempel bekommen das heutige Datum, wenn gewünscht.</summary>
        public static byte[] RenderStamp(StampDef d, DateTime date)
        {
            if (!string.IsNullOrEmpty(d.ImageFile) && File.Exists(d.ImageFile))
                return File.ReadAllBytes(d.ImageFile);

            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(d.Color); } catch { c = Colors.DarkGreen; }
            var brush = new SolidColorBrush(c);
            var fill = new SolidColorBrush(Color.FromArgb(0x16, c.R, c.G, c.B));
            var ui = new FontFamily("Segoe UI");
            var bold = new Typeface(ui, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var semi = new Typeface(ui, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var de = CultureInfo.GetCultureInfo("de-DE");

            var line1 = new FormattedText((d.Text1 ?? "").Trim().ToUpper(de), de, FlowDirection.LeftToRight, bold, 30, brush, 1.0);
            string second = (d.Text2 ?? "").Trim();
            if (d.WithDate) second = string.IsNullOrEmpty(second) ? date.ToString("dd.MM.yyyy", de) : $"{second} · {date:dd.MM.yyyy}";
            FormattedText line2 = string.IsNullOrEmpty(second) ? null
                : new FormattedText(second, de, FlowDirection.LeftToRight, semi, 15, brush, 1.0);

            double padX = 18, padY = 10;
            double contentW = Math.Max(line1.Width, line2?.Width ?? 0);
            double w = contentW + 2 * padX, h = line1.Height + (line2 != null ? line2.Height + 1 : 0) + 2 * padY;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRoundedRectangle(fill, new Pen(brush, 3.2), new Rect(2, 2, w - 4, h - 4), 9, 9);
                dc.DrawRoundedRectangle(null, new Pen(brush, 1), new Rect(6, 6, w - 12, h - 12), 6, 6);
                dc.DrawText(line1, new Point((w - line1.Width) / 2, padY));
                if (line2 != null) dc.DrawText(line2, new Point((w - line2.Width) / 2, padY + line1.Height + 1));
            }
            const double scale = 4;
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(dv);
            return ToPng(rtb);
        }

        public static BitmapImage LoadPng(byte[] png)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
    }
}
