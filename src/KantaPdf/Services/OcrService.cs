using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace KantaPdf.Services
{
    /// <summary>
    /// OCR mit der in Windows eingebauten Texterkennung (Windows.Media.Ocr), komplett offline.
    /// Ergebnis: das Original bleibt optisch gleich, bekommt aber eine unsichtbare Textschicht
    /// → durchsuchbar und markierbar.
    /// </summary>
    public static class OcrService
    {
        public sealed class Report
        {
            public int Recognized, SkippedText, SkippedRotated, Words;
        }

        public static OcrEngine CreateEngine()
        {
            try
            {
                return OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("de-DE"))
                       ?? OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch { return null; }
        }

        public static string EngineLanguageName()
        {
            var e = CreateEngine();
            return e?.RecognizerLanguage?.DisplayName;
        }

        public static Task<Report> MakeSearchableAsync(string input, string password, string output, bool skipPagesWithText,
                                                       IProgress<string> progress, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                var engine = CreateEngine()
                    ?? throw new InvalidOperationException(
                        "Windows hat keine Texterkennung für Deutsch installiert.\n" +
                        "Einstellungen → Zeit und Sprache → Sprache und Region → Deutsch → Sprachoptionen → „Optische Zeichenerkennung“ hinzufügen.");

                // Darstellung (für die Bilder, die die OCR liest)
                var bytes = await File.ReadAllBytesAsync(input, ct);
                var ras = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(ras))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                ras.Seek(0);
                var wdoc = string.IsNullOrEmpty(password)
                    ? await WinPdf.PdfDocument.LoadFromStreamAsync(ras)
                    : await WinPdf.PdfDocument.LoadFromStreamAsync(ras, password);

                var report = new Report();
                var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));
                uint maxDim = OcrEngine.MaxImageDimension;

                using var doc = PdfTools.Open(input, password, PdfDocumentOpenMode.Modify);
                int count = doc.PageCount;

                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Seite {i + 1} von {count} …");

                    var page = doc.Pages[i];
                    if (page.Rotate % 360 != 0) { report.SkippedRotated++; continue; }
                    if (skipPagesWithText && HasText(page)) { report.SkippedText++; continue; }

                    double wPt = page.Width.Point, hPt = page.Height.Point;
                    if (wPt <= 0 || hPt <= 0) continue;

                    // ca. 300 dpi, aber innerhalb der OCR-Grenze
                    double px = wPt / 72.0 * 300.0;
                    double scaleLimit = Math.Min(maxDim / px, maxDim / (hPt / 72.0 * 300.0));
                    if (scaleLimit < 1) px *= scaleLimit;

                    SoftwareBitmap sbmp;
                    using (var wpage = wdoc.GetPage((uint)i))
                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        await wpage.RenderToStreamAsync(stream, new WinPdf.PdfPageRenderOptions { DestinationWidth = (uint)px });
                        stream.Seek(0);
                        var decoder = await BitmapDecoder.CreateAsync(stream);
                        sbmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    }

                    OcrResult result;
                    using (sbmp)
                        result = await engine.RecognizeAsync(sbmp);

                    double sx = px / wPt;                       // Pixel pro Punkt
                    double sy = sx;                             // gleiches Seitenverhältnis

                    using (var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
                    {
                        foreach (var line in result.Lines)
                        foreach (var word in line.Words)
                        {
                            var r = word.BoundingRect;
                            if (string.IsNullOrWhiteSpace(word.Text) || r.Width < 1 || r.Height < 1) continue;
                            double x = r.X / sx, y = r.Y / sy, w = r.Width / sx, h = r.Height / sy;

                            var font = new XFont("Arial", Math.Max(1, h * 0.9));
                            var size = gfx.MeasureString(word.Text, font);
                            if (size.Width <= 0 || size.Height <= 0) continue;

                            var state = gfx.Save();
                            gfx.TranslateTransform(x, y);
                            gfx.ScaleTransform(w / size.Width, h / size.Height);
                            gfx.DrawString(word.Text, font, invisible, 0, 0, XStringFormats.TopLeft);
                            gfx.Restore(state);
                            report.Words++;
                        }
                    }
                    report.Recognized++;
                }

                progress?.Report("Speichere …");
                doc.Save(output);
                return report;
            }, ct);
        }

        /// <summary>Grobe Prüfung, ob die Seite schon Text enthält (Text-Operatoren im Inhalt).</summary>
        private static bool HasText(PdfPage page)
        {
            try
            {
                var contents = page.Contents;
                for (int k = 0; k < contents.Elements.Count; k++)
                {
                    var dict = contents.Elements.GetDictionary(k);
                    var data = dict?.Stream?.UnfilteredValue;
                    if (data == null) continue;
                    var s = Encoding.Latin1.GetString(data);
                    if (s.Contains("Tj") || s.Contains("TJ")) return true;
                }
            }
            catch { }
            return false;
        }
    }
}
