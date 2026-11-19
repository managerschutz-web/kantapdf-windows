using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace KantaPdf.Services
{
    public enum CompressLevel { Leicht, Mittel, Stark }

    /// <summary>Die Werkzeuge: Zusammenfügen, Teilen, Verkleinern, Kennwortschutz, Schwärzen.</summary>
    public static class PdfTools
    {
        // ───────────────────────── Hilfen ─────────────────────────

        public static PdfDocument Open(string path, string password, PdfDocumentOpenMode mode)
        {
            try
            {
                return string.IsNullOrEmpty(password)
                    ? PdfReader.Open(path, mode)
                    : PdfReader.Open(path, password, mode);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"„{Path.GetFileName(path)}“ ließ sich nicht verarbeiten (Kennwortschutz oder beschädigt): {ex.Message}", ex);
            }
        }

        /// <summary>Hängt (2), (3) … an, falls die Datei schon existiert.</summary>
        public static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                var p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(p)) return p;
            }
        }

        public static string FormatSize(long bytes)
        {
            var de = CultureInfo.GetCultureInfo("de-DE");
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0", de) + " MB";
            return Math.Max(1, bytes / 1024).ToString(de) + " KB";
        }

        /// <summary>WPF-Bildfunktionen laufen am zuverlässigsten in einem STA-Thread.</summary>
        public static Task<T> RunSta<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>();
            var t = new Thread(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            return tcs.Task;
        }

        // ───────────────────────── Zusammenfügen ─────────────────────────

        public static void Merge(IList<string> inputs, IList<string> passwords, string output)
        {
            var opened = new List<PdfDocument>();
            try
            {
                using var outDoc = new PdfDocument();
                for (int k = 0; k < inputs.Count; k++)
                {
                    var src = Open(inputs[k], passwords?[k], PdfDocumentOpenMode.Import);
                    opened.Add(src);
                    for (int i = 0; i < src.PageCount; i++)
                        outDoc.AddPage(src.Pages[i]);
                }
                outDoc.Save(output);
            }
            finally
            {
                foreach (var d in opened) d.Dispose();
            }
        }

        // ───────────────────────── Teilen ─────────────────────────

        /// <summary>„1-3, 5, 8-“ → Liste von (von, bis), 1-basiert.</summary>
        public static List<(int from, int to)> ParseRanges(string text, int pageCount)
        {
            var result = new List<(int, int)>();
            if (string.IsNullOrWhiteSpace(text))
                throw new FormatException("Bitte Seitenbereiche angeben, z. B. 1-3, 4-6, 7-");

            foreach (var raw in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var part = raw.Trim().Replace('–', '-').Replace(" ", "");
                if (part.Length == 0) continue;
                int from, to;
                int dash = part.IndexOf('-');
                if (dash < 0)
                {
                    from = to = ParsePage(part);
                }
                else
                {
                    var a = part.Substring(0, dash);
                    var b = part.Substring(dash + 1);
                    from = a.Length == 0 ? 1 : ParsePage(a);
                    to = b.Length == 0 ? pageCount : ParsePage(b);
                }
                if (from < 1 || to > pageCount || from > to)
                    throw new FormatException($"„{raw.Trim()}“ passt nicht – das Dokument hat {pageCount} Seiten.");
                result.Add((from, to));
            }
            if (result.Count == 0) throw new FormatException("Keine gültigen Seitenbereiche gefunden.");
            return result;

            static int ParsePage(string s)
            {
                if (!int.TryParse(s, out var n)) throw new FormatException($"„{s}“ ist keine Seitenzahl.");
                return n;
            }
        }

        public static List<string> Split(string input, string password, List<(int from, int to)> ranges, string folder, string baseName)
        {
            var created = new List<string>();
            using var src = Open(input, password, PdfDocumentOpenMode.Import);
            int digits = src.PageCount.ToString().Length;
            foreach (var (from, to) in ranges)
            {
                using var part = new PdfDocument();
                for (int p = from; p <= to; p++) part.AddPage(src.Pages[p - 1]);
                string suffix = from == to
                    ? $"S{from.ToString().PadLeft(digits, '0')}"
                    : $"S{from.ToString().PadLeft(digits, '0')}-{to.ToString().PadLeft(digits, '0')}";
                var path = UniquePath(Path.Combine(folder, $"{baseName}_{suffix}.pdf"));
                part.Save(path);
                created.Add(path);
            }
            return created;
        }

        // ───────────────────────── Verkleinern ─────────────────────────

        public sealed class CompressResult
        {
            public long Before, After;
            public int ImagesFound, ImagesShrunk;
        }

        /// <summary>
        /// Rechnet große Fotos/Scans herunter und komprimiert sie als JPEG neu,
        /// packt unkomprimierte Daten ein. Text und Vektoren bleiben unverändert (durchsuchbar).
        /// Muss in einem STA-Thread laufen (siehe RunSta).
        /// </summary>
        public static CompressResult Compress(string input, string password, string output, CompressLevel level)
        {
            int maxEdge; int quality;
            switch (level)
            {
                case CompressLevel.Leicht: maxEdge = 3000; quality = 82; break;
                case CompressLevel.Stark: maxEdge = 1500; quality = 55; break;
                default: maxEdge = 2200; quality = 68; break;
            }

            var res = new CompressResult { Before = new FileInfo(input).Length };

            using (var doc = Open(input, password, PdfDocumentOpenMode.Modify))
            {
                doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
                doc.Options.CompressContentStreams = true;
                doc.Options.NoCompression = false;
                doc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Never;

                var all = doc.Internals.GetAllObjects();

                // Bilder, die als Transparenzmaske dienen, nicht anfassen
                var masks = new HashSet<PdfObject>();
                foreach (var o in all)
                    if (o is PdfDictionary d && d.Elements["/SMask"] is PdfReference r && r.Value != null)
                        masks.Add(r.Value);

                foreach (var o in all)
                {
                    if (!(o is PdfDictionary d) || d.Stream == null) continue;
                    try
                    {
                        if (d.Elements.GetName("/Subtype") == "/Image")
                        {
                            res.ImagesFound++;
                            if (!masks.Contains(d) && RecompressImage(d, maxEdge, quality)) res.ImagesShrunk++;
                        }
                        else
                        {
                            ZipIfUncompressed(d);
                        }
                    }
                    catch
                    {
                        // Ein Bild, das nicht klappt, bleibt einfach wie es ist.
                    }
                }

                for (int i = 0; i < doc.PageCount; i++)
                    doc.Pages[i].Elements.Remove("/Thumb");

                doc.Save(output);
            }

            res.After = new FileInfo(output).Length;
            return res;
        }

        private static PdfItem Resolve(PdfItem item) => item is PdfReference r ? r.Value : item;

        private static bool RecompressImage(PdfDictionary d, int maxEdge, int quality)
        {
            var filter = Resolve(d.Elements["/Filter"]) as PdfName;
            if (filter == null) return false;
            if (d.Elements.ContainsKey("/SMask") || d.Elements.ContainsKey("/Mask") || d.Elements.ContainsKey("/Decode")) return false;
            if (d.Elements.GetBoolean("/ImageMask")) return false;

            int w = d.Elements.GetInteger("/Width"), h = d.Elements.GetInteger("/Height");
            if (w <= 0 || h <= 0 || (long)w * h < 150_000) return false;

            byte[] encoded = d.Stream.Value;
            if (encoded == null || encoded.Length < 30_000) return false;

            BitmapSource src;
            bool gray;

            if (filter.Value == "/DCTDecode")
            {
                var dec = new JpegBitmapDecoder(new MemoryStream(encoded), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                src = dec.Frames[0];
                var f = src.Format;
                if (f == PixelFormats.Gray8) gray = true;
                else if (f == PixelFormats.Bgr24 || f == PixelFormats.Bgr32 || f == PixelFormats.Rgb24 || f == PixelFormats.Bgra32) gray = false;
                else return false; // z. B. CMYK → lieber nicht anfassen
            }
            else if (filter.Value == "/FlateDecode")
            {
                if (d.Elements.ContainsKey("/DecodeParms")) return false;
                if (d.Elements.GetInteger("/BitsPerComponent") != 8) return false;
                var cs = Resolve(d.Elements["/ColorSpace"]) as PdfName;
                if (cs == null) return false;
                int comps;
                if (cs.Value == "/DeviceRGB") { comps = 3; gray = false; }
                else if (cs.Value == "/DeviceGray") { comps = 1; gray = true; }
                else return false;

                byte[] raw = d.Stream.UnfilteredValue;
                if (raw == null || raw.Length < (long)w * h * comps) return false;
                src = BitmapSource.Create(w, h, 96, 96, gray ? PixelFormats.Gray8 : PixelFormats.Rgb24, null, raw, w * comps);
            }
            else
            {
                return false; // JBIG2, CCITT usw. sind schon sehr klein
            }

            int longEdge = Math.Max(src.PixelWidth, src.PixelHeight);
            if (longEdge > maxEdge)
            {
                double s = (double)maxEdge / longEdge;
                src = new TransformedBitmap(src, new ScaleTransform(s, s));
            }

            BitmapSource toEncode = gray
                ? (src.Format == PixelFormats.Gray8 ? src : new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0))
                : (src.Format == PixelFormats.Bgr24 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0));

            var enc = new JpegBitmapEncoder { QualityLevel = quality };
            enc.Frames.Add(BitmapFrame.Create(toEncode));
            byte[] jpg;
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                jpg = ms.ToArray();
            }

            if (jpg.Length >= encoded.Length * 0.9) return false; // lohnt sich nicht

            d.Stream.Value = jpg;
            d.Elements.SetInteger("/Length", jpg.Length);
            d.Elements.SetName("/Filter", "/DCTDecode");
            d.Elements.Remove("/DecodeParms");
            d.Elements.SetInteger("/Width", toEncode.PixelWidth);
            d.Elements.SetInteger("/Height", toEncode.PixelHeight);
            d.Elements.SetInteger("/BitsPerComponent", 8);
            return true;
        }

        private static void ZipIfUncompressed(PdfDictionary d)
        {
            if (d.Elements.ContainsKey("/Filter")) return;
            var type = d.Elements.GetName("/Type");
            if (type == "/XRef" || type == "/ObjStm" || type == "/Metadata") return;

            var raw = d.Stream.Value;
            if (raw == null || raw.Length < 512) return;

            byte[] packed;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                    z.Write(raw, 0, raw.Length);
                packed = ms.ToArray();
            }
            if (packed.Length >= raw.Length) return;

            d.Stream.Value = packed;
            d.Elements.SetInteger("/Length", packed.Length);
            d.Elements.SetName("/Filter", "/FlateDecode");
        }

        // ───────────────────────── Kennwortschutz ─────────────────────────

        /// <summary>Setzt ein Öffnen-Kennwort. Gibt zurück, ob AES-256 aktiv ist.</summary>
        public static bool Protect(string input, string currentPassword, string output, string newPassword,
                                   bool allowPrint, bool allowCopy, bool allowEdit)
        {
            using var doc = Open(input, currentPassword, PdfDocumentOpenMode.Modify);
            var s = doc.SecuritySettings;
            s.UserPassword = newPassword;
            // Zufälliges Besitzer-Kennwort: so gelten die Rechte-Einschränkungen wirklich.
            s.OwnerPassword = Guid.NewGuid().ToString("N");
            s.PermitPrint = allowPrint;
            s.PermitFullQualityPrint = allowPrint;
            s.PermitExtractContent = allowCopy;
            s.PermitModifyDocument = allowEdit;
            s.PermitAnnotations = allowEdit;
            s.PermitAssembleDocument = allowEdit;
            s.PermitFormsFill = true;

            bool aes = TryEnableAes256(doc);
            doc.Save(output);
            return aes;
        }

        /// <summary>AES-256 einschalten, sofern die PDFsharp-Version das kann (sonst Standard-Verschlüsselung).</summary>
        private static bool TryEnableAes256(PdfDocument doc)
        {
            try
            {
                var handler = doc.GetType().GetProperty("SecurityHandler")?.GetValue(doc);
                var m = handler?.GetType().GetMethod("SetEncryptionToV5", Type.EmptyTypes);
                if (m == null) return false;
                m.Invoke(handler, null);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Entfernt den Kennwortschutz (Seiten werden in ein neues, offenes PDF übernommen).</summary>
        public static void Unprotect(string input, string currentPassword, string output)
        {
            using var src = Open(input, currentPassword, PdfDocumentOpenMode.Import);
            using var outDoc = new PdfDocument();
            for (int i = 0; i < src.PageCount; i++) outDoc.AddPage(src.Pages[i]);
            outDoc.Save(output);
        }

        // ───────────────────────── Schwärzen ─────────────────────────

        /// <summary>
        /// Seiten mit Balken werden in 200 dpi gerendert, die Balken in die Pixel gebrannt
        /// und die Seite durch dieses Bild ersetzt. Damit ist darunter garantiert nichts mehr
        /// auslesbar. Seiten ohne Balken bleiben unverändert.
        /// </summary>
        public static async Task<int> RedactAsync(PdfView view, IReadOnlyList<PageVM> pages, string output, IProgress<string> progress)
        {
            var burned = new Dictionary<int, (byte[] jpg, double wPt, double hPt)>();
            var todo = pages.Where(p => p.Redactions.Count > 0).ToList();
            int n = 0;

            foreach (var vm in todo)
            {
                n++;
                progress?.Report($"Schwärze Seite {vm.Index + 1} ({n} von {todo.Count}) …");
                var sizeDip = view.PageSizes[vm.Index];
                int px = (int)Math.Min(3400, Math.Round(sizeDip.Width / 96.0 * 200));
                var bmp = await view.RenderAsync(vm.Index, px);

                var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 3;
                var buf = new byte[stride * h];
                conv.CopyPixels(buf, stride, 0);

                var boxes = vm.Redactions.Select(r => (r.NX, r.NY, r.NW, r.NH)).ToList();
                await Task.Run(() =>
                {
                    foreach (var (nx, ny, nw, nh) in boxes)
                    {
                        int x0 = Math.Max(0, (int)Math.Floor(nx * w) - 1);
                        int y0 = Math.Max(0, (int)Math.Floor(ny * h) - 1);
                        int x1 = Math.Min(w, (int)Math.Ceiling((nx + nw) * w) + 1);
                        int y1 = Math.Min(h, (int)Math.Ceiling((ny + nh) * h) + 1);
                        for (int y = y0; y < y1; y++)
                            Array.Clear(buf, y * stride + x0 * 3, (x1 - x0) * 3);
                    }
                });

                var outBmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, buf, stride);
                var enc = new JpegBitmapEncoder { QualityLevel = 88 };
                enc.Frames.Add(BitmapFrame.Create(outBmp));
                using var ms = new MemoryStream();
                enc.Save(ms);
                burned[vm.Index] = (ms.ToArray(), sizeDip.Width * 0.75, sizeDip.Height * 0.75);
            }

            progress?.Report("Speichere …");
            await Task.Run(() =>
            {
                var keepAlive = new List<IDisposable>();
                try
                {
                    using var src = Open(view.Path, view.Password, PdfDocumentOpenMode.Import);
                    using var outDoc = new PdfDocument();
                    for (int i = 0; i < src.PageCount; i++)
                    {
                        if (burned.TryGetValue(i, out var b))
                        {
                            var pg = outDoc.AddPage();
                            pg.Width = XUnit.FromPoint(b.wPt);
                            pg.Height = XUnit.FromPoint(b.hPt);
                            var stream = new MemoryStream(b.jpg);
                            keepAlive.Add(stream);
                            var img = XImage.FromStream(stream);
                            keepAlive.Add(img);
                            using (var gfx = XGraphics.FromPdfPage(pg))
                                gfx.DrawImage(img, 0, 0, b.wPt, b.hPt);
                        }
                        else
                        {
                            outDoc.AddPage(src.Pages[i]);
                        }
                    }
                    outDoc.Save(output);
                }
                finally
                {
                    foreach (var k in keepAlive) k.Dispose();
                }
            });

            return todo.Count;
        }
    }
}
