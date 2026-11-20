using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace KantaPdf.Services
{
    /// <summary>
    /// Wandelt Bilder (selbst) und Office-Dateien (über das installierte Microsoft Office) in PDF um.
    /// </summary>
    public static class Converter
    {
        public static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".jfif" };
        public static readonly string[] WordExt = { ".doc", ".docx", ".docm", ".rtf", ".odt", ".txt" };
        public static readonly string[] ExcelExt = { ".xls", ".xlsx", ".xlsm", ".csv", ".ods" };
        public static readonly string[] PptExt = { ".ppt", ".pptx", ".pptm", ".odp" };

        public static IEnumerable<string> ConvertibleExt => ImageExt.Concat(WordExt).Concat(ExcelExt).Concat(PptExt);

        public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

        public static bool IsSupported(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".pdf" || ConvertibleExt.Contains(ext);
        }

        public static string OpenFilter
        {
            get
            {
                string Pat(IEnumerable<string> e) => string.Join(";", e.Select(x => "*" + x));
                var all = new[] { ".pdf" }.Concat(ConvertibleExt);
                return $"Alle unterstützten Dateien|{Pat(all)}|PDF|*.pdf|Word|{Pat(WordExt)}|Excel|{Pat(ExcelExt)}|PowerPoint|{Pat(PptExt)}|Bilder|{Pat(ImageExt)}";
            }
        }

        private static string NewTempPdf(string source)
        {
            var dir = Path.Combine(Path.GetTempPath(), "KANTApdf", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(source) + ".pdf");
        }

        /// <summary>Wandelt eine Datei in eine temporäre PDF um und gibt deren Pfad zurück.</summary>
        public static Task<string> ToPdfAsync(string path) => PdfTools.RunSta(() => ToPdf(path));

        private static string ToPdf(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var output = NewTempPdf(path);
            if (ImageExt.Contains(ext)) ImagesToPdf(new[] { path }, output);
            else if (WordExt.Contains(ext)) WordToPdf(path, output);
            else if (ExcelExt.Contains(ext)) ExcelToPdf(path, output);
            else if (PptExt.Contains(ext)) PowerPointToPdf(path, output);
            else throw new NotSupportedException($"Dateityp {ext} wird nicht unterstützt.");
            if (!File.Exists(output)) throw new InvalidOperationException("Die Umwandlung hat keine PDF erzeugt.");
            return output;
        }

        // ───────────── Bilder ─────────────

        public static void ImagesToPdf(IEnumerable<string> images, string output)
        {
            var keep = new List<IDisposable>();
            try
            {
                using var doc = new PdfDocument();
                foreach (var file in images)
                {
                    BitmapDecoder dec;
                    using (var fs = File.OpenRead(file))
                        dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                    int frames = Path.GetExtension(file).ToLowerInvariant().StartsWith(".tif") ? dec.Frames.Count : 1;
                    for (int f = 0; f < frames; f++)
                    {
                        BitmapSource frame = ApplyExifOrientation(dec.Frames[f]);
                        var fmt = frame.Format;
                        bool alpha = fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32 || fmt == PixelFormats.Rgba64 ||
                                     fmt == PixelFormats.Prgba64 || fmt == PixelFormats.Rgba128Float || fmt == PixelFormats.Prgba128Float;

                        BitmapEncoder enc;
                        if (alpha) enc = new PngBitmapEncoder();
                        else
                        {
                            enc = new JpegBitmapEncoder { QualityLevel = 92 };
                            if (frame.Format != PixelFormats.Bgr24 && frame.Format != PixelFormats.Gray8)
                                frame = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
                        }
                        enc.Frames.Add(BitmapFrame.Create(frame));
                        var ms = new MemoryStream();
                        enc.Save(ms);
                        ms.Position = 0;
                        keep.Add(ms);
                        var img = XImage.FromStream(ms);
                        keep.Add(img);

                        // A4, Ausrichtung nach Bild, 20 pt Rand, mittig eingepasst
                        bool landscape = frame.PixelWidth > frame.PixelHeight;
                        double pw = landscape ? 842 : 595, ph = landscape ? 595 : 842, m = 20;
                        double s = Math.Min((pw - 2 * m) / frame.PixelWidth, (ph - 2 * m) / frame.PixelHeight);
                        double w = frame.PixelWidth * s, h = frame.PixelHeight * s;

                        var page = doc.AddPage();
                        page.Width = XUnit.FromPoint(pw);
                        page.Height = XUnit.FromPoint(ph);
                        using var gfx = XGraphics.FromPdfPage(page);
                        gfx.DrawImage(img, (pw - w) / 2, (ph - h) / 2, w, h);
                    }
                }
                doc.Save(output);
            }
            finally
            {
                foreach (var k in keep) k.Dispose();
            }
        }

        private static BitmapSource ApplyExifOrientation(BitmapFrame frame)
        {
            try
            {
                if (frame.Metadata is BitmapMetadata md && md.GetQuery("System.Photo.Orientation") is ushort o)
                {
                    double angle = o == 3 ? 180 : o == 6 ? 90 : o == 8 ? 270 : 0;
                    if (angle != 0) return new TransformedBitmap(frame, new RotateTransform(angle));
                }
            }
            catch { }
            return frame;
        }

        // ───────────── Office (über installiertes Microsoft Office) ─────────────

        private static dynamic StartOffice(string progId, string name)
        {
            var t = Type.GetTypeFromProgID(progId);
            if (t == null)
                throw new InvalidOperationException($"Microsoft {name} ist auf diesem PC nicht installiert – ohne {name} kann diese Datei nicht umgewandelt werden.");
            return Activator.CreateInstance(t);
        }

        private static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); } catch { }
        }

        private static void WordToPdf(string path, string output)
        {
            dynamic app = StartOffice("Word.Application", "Word");
            dynamic doc = null;
            try
            {
                app.Visible = false;
                app.DisplayAlerts = 0;
                doc = app.Documents.Open(path, false, true, false); // Datei, Konvertierung bestätigen, schreibgeschützt, Zuletzt verwendet
                doc.ExportAsFixedFormat(output, 17);                  // 17 = PDF
            }
            finally
            {
                try { doc?.Close(false); } catch { }
                try { app.Quit(false); } catch { }
                Release(doc); Release(app);
            }
        }

        private static void ExcelToPdf(string path, string output)
        {
            dynamic app = StartOffice("Excel.Application", "Excel");
            dynamic wb = null;
            try
            {
                app.Visible = false;
                app.DisplayAlerts = false;
                wb = app.Workbooks.Open(path, 0, true);              // Datei, Verknüpfungen nicht aktualisieren, schreibgeschützt
                wb.ExportAsFixedFormat(0, output);                    // 0 = PDF
            }
            finally
            {
                try { wb?.Close(false); } catch { }
                try { app.Quit(); } catch { }
                Release(wb); Release(app);
            }
        }

        private static void PowerPointToPdf(string path, string output)
        {
            dynamic app = StartOffice("PowerPoint.Application", "PowerPoint");
            dynamic pres = null;
            try
            {
                pres = app.Presentations.Open(path, -1, 0, 0);       // schreibgeschützt, ohne Titel, ohne Fenster
                pres.SaveAs(output, 32);                              // 32 = PDF
            }
            finally
            {
                try { pres?.Close(); } catch { }
                try { app.Quit(); } catch { }
                Release(pres); Release(app);
            }
        }
    }
}
