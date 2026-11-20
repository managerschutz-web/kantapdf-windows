using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace KantaPdf.Services
{
    /// <summary>
    /// Speichert das aktuelle Dokument: Seitenreihenfolge (auch aus mehreren Dateien),
    /// gelöschte Seiten, eingebrannte Schwärzungen, Notizen und Textmarker als echte PDF-Anmerkungen.
    /// </summary>
    public static class DocumentWriter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static string F(double v) => v.ToString("0.###", Inv);

        public static async Task SaveAsync(IReadOnlyList<PageItem> pages, string output, IProgress<string> progress)
        {
            // 1) Seiten mit Schwärzungen rendern und Balken in die Pixel brennen
            var burned = new Dictionary<PageItem, (byte[] jpg, double wPt, double hPt)>();
            var redacted = pages.Where(p => p.Redactions.Count > 0).ToList();
            int n = 0;
            foreach (var pg in redacted)
            {
                n++;
                progress?.Report($"Schwärze Seite {pg.Number} ({n} von {redacted.Count}) …");
                burned[pg] = await BurnAsync(pg);
            }

            // Anmerkungen einsammeln (UI-Objekte nicht im Hintergrund anfassen)
            var plans = pages.Select(p => new PagePlan
            {
                Page = p,
                Notes = p.Notes.Where(x => !string.IsNullOrWhiteSpace(x.Text))
                               .Select(x => (x.NX, x.NY, x.Text, x.Author, x.Created)).ToList(),
                Highlights = p.Highlights.Select(h => (h.Rects.Select(r => (r.NX, r.NY, r.NW, r.NH)).ToList(), h.Text, h.Author, h.Created, h.Color)).ToList(),
                Extra = p.ExtraRotation,
                Fields = p.Fields.Where(f => f.Changed).Select(f => (f.AnnotIndex, f.Kind, f.Value ?? "", f.IsChecked, f.OnName)).ToList(),
                Texts = p.Texts.Where(t => !string.IsNullOrWhiteSpace(t.Text)).Select(t => (t.NX, t.NY, t.Text, t.FontPt, t.Color, t.Bold)).ToList(),
                Placed = p.Placed.Select(x => (x.Png, x.NX, x.NY, x.NW, x.NH)).ToList()
            }).ToList();

            progress?.Report("Speichere …");
            await Task.Run(() =>
            {
                var opened = new Dictionary<Source, PdfDocument>();
                var keepAlive = new List<IDisposable>();
                string temp = output + ".kanta-tmp";
                try
                {
                    using (var outDoc = new PdfDocument())
                    {
                        outDoc.Info.Creator = "KANTApdf";
                        PdfDictionary helv = null;
                        var formPages = new List<PdfPage>();
                        foreach (var plan in plans)
                        {
                            PdfPage target;
                            int rot;
                            PdfRectangle box;

                            if (burned.TryGetValue(plan.Page, out var b))
                            {
                                target = outDoc.AddPage();
                                target.Width = XUnit.FromPoint(b.wPt);
                                target.Height = XUnit.FromPoint(b.hPt);
                                var ms = new MemoryStream(b.jpg);
                                keepAlive.Add(ms);
                                var img = XImage.FromStream(ms);
                                keepAlive.Add(img);
                                using (var gfx = XGraphics.FromPdfPage(target))
                                    gfx.DrawImage(img, 0, 0, b.wPt, b.hPt);
                                // Nutzer-Drehung über /Rotate, Koordinaten entsprechend
                                rot = plan.Extra;
                                if (rot != 0) target.Elements.SetInteger("/Rotate", rot);
                                box = new PdfRectangle(new XPoint(0, 0), new XPoint(b.wPt, b.hPt));
                            }
                            else
                            {
                                var src = plan.Page.Source;
                                if (!opened.TryGetValue(src, out var srcDoc))
                                {
                                    var stream = new MemoryStream(src.Bytes);
                                    keepAlive.Add(stream);
                                    srcDoc = string.IsNullOrEmpty(src.Password)
                                        ? PdfReader.Open(stream, PdfDocumentOpenMode.Import)
                                        : PdfReader.Open(stream, src.Password, PdfDocumentOpenMode.Import);
                                    opened[src] = srcDoc;
                                }
                                target = outDoc.AddPage(srcDoc.Pages[plan.Page.SourceIndex]);
                                formPages.Add(target);
                                rot = ((target.Elements.GetInteger("/Rotate") % 360) + 360) % 360;
                                if (plan.Extra != 0)
                                {
                                    rot = (rot + plan.Extra) % 360;
                                    target.Elements.SetInteger("/Rotate", rot);
                                }
                                box = target.Elements.ContainsKey("/CropBox") ? target.CropBox : target.MediaBox;
                            }

                            // Formularfelder: Werte setzen + Erscheinungsbild erzeugen
                            if (plan.Fields.Count > 0 && formPages.Contains(target))
                            {
                                helv ??= HelveticaFont(outDoc);
                                ApplyFields(outDoc, target, plan.Fields, helv);
                            }

                            // Eingefügte Texte fest in den Seiteninhalt schreiben
                            if (plan.Texts.Count > 0 && rot == 0)
                            {
                                var media = target.MediaBox;
                                double mLeft = Math.Min(media.X1, media.X2), mTop = Math.Max(media.Y1, media.Y2);
                                using var gfx = XGraphics.FromPdfPage(target, XGraphicsPdfPageOptions.Append);
                                foreach (var t in plan.Texts)
                                {
                                    var pos = ToPdf(box, 0, t.NX, t.NY);
                                    var font = new XFont("Arial", t.Pt, t.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                                    var brush = new XSolidBrush(XColor.FromArgb(t.Color.A, t.Color.R, t.Color.G, t.Color.B));
                                    double x = pos.x - mLeft + 2.2, y = mTop - pos.y + 1.6, lh = t.Pt * 1.15;
                                    var lines = t.Text.Replace("\r", "").Split('\n');
                                    for (int li = 0; li < lines.Length; li++)
                                        gfx.DrawString(lines[li], font, brush, x, y + li * lh, XStringFormats.TopLeft);
                                }
                            }

                            // Unterschriften & Stempel fest in den Seiteninhalt zeichnen
                            if (plan.Placed.Count > 0 && rot == 0)
                            {
                                var media = target.MediaBox;
                                double mLeft = Math.Min(media.X1, media.X2), mTop = Math.Max(media.Y1, media.Y2);
                                using var gfx = XGraphics.FromPdfPage(target, XGraphicsPdfPageOptions.Append);
                                foreach (var pl in plan.Placed)
                                {
                                    var a = ToPdf(box, 0, pl.NX, pl.NY);
                                    var c = ToPdf(box, 0, pl.NX + pl.NW, pl.NY + pl.NH);
                                    var ms = new MemoryStream(pl.Png);
                                    keepAlive.Add(ms);
                                    var img = XImage.FromStream(ms);
                                    keepAlive.Add(img);
                                    gfx.DrawImage(img, a.x - mLeft, mTop - a.y, c.x - a.x, a.y - c.y);
                                }
                            }

                            foreach (var h in plan.Highlights) AddHighlight(outDoc, target, box, rot, h.Item1, h.Item2, h.Item3, h.Item4, h.Item5);
                            foreach (var note in plan.Notes) AddNote(outDoc, target, box, rot, note.NX, note.NY, note.Text, note.Author, note.Created);
                        }
                        BuildAcroForm(outDoc, formPages, helv);
                        outDoc.Save(temp);
                    }
                    File.Copy(temp, output, true);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    foreach (var d in opened.Values) d.Dispose();
                    foreach (var k in keepAlive) k.Dispose();
                }
            });
        }

        private sealed class PagePlan
        {
            public PageItem Page;
            public List<(double NX, double NY, string Text, string Author, DateTime Created)> Notes;
            public List<(List<(double, double, double, double)>, string, string, DateTime, Color)> Highlights;
            public int Extra;
            public List<(int AnnotIndex, FieldKind Kind, string Value, bool Checked, string OnName)> Fields;
            public List<(double NX, double NY, string Text, double Pt, Color Color, bool Bold)> Texts;
            public List<(byte[] Png, double NX, double NY, double NW, double NH)> Placed;
        }

        // ───────────── Schwärzen (Pixel) ─────────────

        private static async Task<(byte[], double, double)> BurnAsync(PageItem pg)
        {
            var view = pg.Source.View;
            var sizeDip = view.PageSizes[pg.SourceIndex];
            int px = (int)Math.Min(3400, Math.Round(sizeDip.Width / 96.0 * 200));
            var bmp = await view.RenderAsync(pg.SourceIndex, px);

            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 3;
            var buf = new byte[stride * h];
            conv.CopyPixels(buf, stride, 0);

            int back = (360 - pg.ExtraRotation) % 360;
            var boxes = pg.Redactions.Select(r => Geo.Rot(r.NX, r.NY, r.NW, r.NH, back)).ToList();
            await Task.Run(() =>
            {
                foreach (var (nx, ny, nw, nh) in boxes)
                {
                    int x0 = Math.Max(0, (int)Math.Floor(nx * w) - 1);
                    int y0 = Math.Max(0, (int)Math.Floor(ny * h) - 1);
                    int x1 = Math.Min(w, (int)Math.Ceiling((nx + nw) * w) + 1);
                    int y1 = Math.Min(h, (int)Math.Ceiling((ny + nh) * h) + 1);
                    for (int y = y0; y < y1; y++)
                        Array.Clear(buf, y * stride + x0 * 3, Math.Max(0, x1 - x0) * 3);
                }
            });

            var outBmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, buf, stride);
            var enc = new JpegBitmapEncoder { QualityLevel = 88 };
            enc.Frames.Add(BitmapFrame.Create(outBmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return (ms.ToArray(), sizeDip.Width * 0.75, sizeDip.Height * 0.75);
        }

        // ───────────── Koordinaten ─────────────

        /// <summary>Relative Anzeige-Koordinate → PDF-Punkt der Seite.</summary>
        private static (double x, double y) ToPdf(PdfRectangle box, int rot, double nx, double ny)
        {
            var (u, v) = TextLayer.Unrotate(nx, ny, rot);
            double left = Math.Min(box.X1, box.X2), right = Math.Max(box.X1, box.X2);
            double bottom = Math.Min(box.Y1, box.Y2), top = Math.Max(box.Y1, box.Y2);
            return (left + u * (right - left), top - v * (top - bottom));
        }

        private static string PdfDate(DateTime d)
        {
            var off = TimeZoneInfo.Local.GetUtcOffset(d);
            string sign = off < TimeSpan.Zero ? "-" : "+";
            return $"D:{d:yyyyMMddHHmmss}{sign}{Math.Abs(off.Hours):00}'{Math.Abs(off.Minutes):00}'";
        }

        private static PdfArray Numbers(PdfDocument doc, params double[] values)
        {
            var a = new PdfArray(doc);
            foreach (var v in values) a.Elements.Add(new PdfReal(Math.Round(v, 3)));
            return a;
        }

        private static PdfArray AnnotsOf(PdfDocument doc, PdfPage page)
        {
            var item = page.Elements["/Annots"];
            if (item is PdfReference r) item = r.Value;
            var annots = item as PdfArray;
            if (annots == null)
            {
                annots = new PdfArray(doc);
                page.Elements["/Annots"] = annots;
            }
            return annots;
        }

        private static PdfDictionary FormXObject(PdfDocument doc, double w, double h, string content, PdfDictionary resources)
        {
            var form = new PdfDictionary(doc);
            form.Elements["/Type"] = new PdfName("/XObject");
            form.Elements["/Subtype"] = new PdfName("/Form");
            form.Elements["/BBox"] = Numbers(doc, 0, 0, w, h);
            if (resources != null) form.Elements["/Resources"] = resources;
            form.CreateStream(Encoding.ASCII.GetBytes(content));
            doc.Internals.AddObject(form);
            return form;
        }

        // ───────────── Formularfelder ─────────────

        private static PdfDictionary HelveticaFont(PdfDocument doc)
        {
            var f = new PdfDictionary(doc);
            f.Elements["/Type"] = new PdfName("/Font");
            f.Elements["/Subtype"] = new PdfName("/Type1");
            f.Elements["/BaseFont"] = new PdfName("/Helvetica");
            f.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
            doc.Internals.AddObject(f);
            return f;
        }

        /// <summary>Text als PDF-String-Literal in WinAnsi (Umlaute, €) mit Escapes.</summary>
        private static byte[] PdfLiteral(string text)
        {
            var bytes = new List<byte> { (byte)'(' };
            foreach (var ch in text)
            {
                if (ch == '(' || ch == ')' || ch == '\\') { bytes.Add((byte)'\\'); bytes.Add((byte)ch); }
                else if (ch == '€') bytes.Add(0x80);
                else if (ch == '„') bytes.Add(0x84);
                else if (ch == '“') bytes.Add(0x93);
                else if (ch == '–') bytes.Add(0x96);
                else if (ch < 256) bytes.Add((byte)ch);
                else bytes.Add((byte)'?');
            }
            bytes.Add((byte)')');
            return bytes.ToArray();
        }

        private static List<string> Wrap(string text, double fs, double width)
        {
            var result = new List<string>();
            double cw = fs * 0.52;
            int max = Math.Max(1, (int)(width / cw));
            foreach (var para in text.Replace("\r", "").Split('\n'))
            {
                var line = "";
                foreach (var word in para.Split(' '))
                {
                    var cand = line.Length == 0 ? word : line + " " + word;
                    if (cand.Length > max && line.Length > 0) { result.Add(line); line = word; }
                    else line = cand;
                }
                result.Add(line);
            }
            return result;
        }

        private static void ApplyFields(PdfDocument doc, PdfPage page, List<(int AnnotIndex, FieldKind Kind, string Value, bool Checked, string OnName)> fields, PdfDictionary helv)
        {
            var annots = FormReader.AnnotsOf(page);
            if (annots == null) return;
            foreach (var f in fields)
            {
                if (f.AnnotIndex < 0 || f.AnnotIndex >= annots.Elements.Count) continue;
                if (!(FormReader.Resolve(annots.Elements[f.AnnotIndex]) is PdfDictionary w)) continue;
                // Feld-Dictionary: das Widget selbst oder sein Elternteil (Wert steht beim Feld)
                var field = w.Elements.ContainsKey("/T") ? w : (FormReader.ParentOf(w) ?? w);

                switch (f.Kind)
                {
                    case FieldKind.Check:
                    case FieldKind.Radio:
                    {
                        var state = f.Checked ? "/" + (f.OnName ?? "Yes") : "/Off";
                        w.Elements["/AS"] = new PdfName(state);
                        if (f.Kind == FieldKind.Check || f.Checked) field.Elements["/V"] = new PdfName(state);
                        break;
                    }
                    default:
                    {
                        field.Elements["/V"] = new PdfString(f.Value, PdfStringEncoding.Unicode);
                        TextAppearance(doc, w, f.Value, f.Kind == FieldKind.Multiline, helv);
                        break;
                    }
                }
            }
        }

        private static void TextAppearance(PdfDocument doc, PdfDictionary w, string text, bool multiline, PdfDictionary helv)
        {
            var r = w.Elements.GetRectangle("/Rect");
            double wd = Math.Abs(r.X2 - r.X1), ht = Math.Abs(r.Y2 - r.Y1);
            if (wd <= 0 || ht <= 0) return;
            double fs = multiline ? Math.Min(10, Math.Max(5, ht * 0.8)) : Math.Max(4, Math.Min(ht * 0.66, 12));

            var ms = new MemoryStream();
            void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
            W($"/Tx BMC q 1 1 {F(wd - 2)} {F(ht - 2)} re W n BT /Helv {F(fs)} Tf 0 g ");
            if (multiline)
            {
                var lines = Wrap(text, fs, wd - 4);
                W($"2 {F(ht - 2 - fs)} Td ");
                for (int i = 0; i < lines.Count; i++)
                {
                    if (i > 0) W($"0 {F(-fs * 1.15)} Td ");
                    var lit = PdfLiteral(lines[i]); ms.Write(lit, 0, lit.Length); W(" Tj ");
                }
            }
            else
            {
                W($"2 {F((ht - fs) / 2 + fs * 0.22)} Td ");
                var lit = PdfLiteral(text.Replace("\r", "").Replace("\n", " ")); ms.Write(lit, 0, lit.Length); W(" Tj ");
            }
            W("ET Q EMC");

            var fonts = new PdfDictionary(doc);
            fonts.Elements["/Helv"] = helv.Reference;
            var res = new PdfDictionary(doc);
            res.Elements["/Font"] = fonts;

            var form = new PdfDictionary(doc);
            form.Elements["/Type"] = new PdfName("/XObject");
            form.Elements["/Subtype"] = new PdfName("/Form");
            form.Elements["/BBox"] = Numbers(doc, 0, 0, wd, ht);
            form.Elements["/Resources"] = res;
            form.CreateStream(ms.ToArray());
            doc.Internals.AddObject(form);

            var ap = new PdfDictionary(doc);
            ap.Elements["/N"] = form.Reference;
            w.Elements["/AP"] = ap;
        }

        /// <summary>Formular im neuen Dokument wieder als Formular anmelden (/AcroForm mit allen Feldern).</summary>
        private static void BuildAcroForm(PdfDocument doc, List<PdfPage> pages, PdfDictionary helv)
        {
            var fieldsArr = new PdfArray(doc);
            var seen = new HashSet<PdfObject>();
            foreach (var page in pages)
            {
                var annots = FormReader.AnnotsOf(page);
                if (annots == null) continue;
                foreach (var item in annots.Elements)
                {
                    if (!(item is PdfReference r) || !(r.Value is PdfDictionary w)) continue;
                    if (w.Elements.GetName("/Subtype") != "/Widget") continue;
                    // oberstes Feld suchen
                    PdfReference topRef = r;
                    var cur = w;
                    for (int i = 0; i < 12; i++)
                    {
                        if (!(cur.Elements["/Parent"] is PdfReference pr) || !(pr.Value is PdfDictionary pd)) break;
                        topRef = pr; cur = pd;
                    }
                    if (seen.Add(topRef.Value)) fieldsArr.Elements.Add(topRef);
                }
            }
            if (fieldsArr.Elements.Count == 0) return;

            helv ??= HelveticaFont(doc);
            var acro = new PdfDictionary(doc);
            acro.Elements["/Fields"] = fieldsArr;
            acro.Elements["/NeedAppearances"] = new PdfBoolean(true);
            acro.Elements["/DA"] = new PdfString("/Helv 0 Tf 0 g");
            var fonts = new PdfDictionary(doc);
            fonts.Elements["/Helv"] = helv.Reference;
            var dr = new PdfDictionary(doc);
            dr.Elements["/Font"] = fonts;
            acro.Elements["/DR"] = dr;
            doc.Internals.AddObject(acro);
            doc.Internals.Catalog.Elements["/AcroForm"] = acro.Reference;
        }

        // ───────────── Notiz (/Text) ─────────────

        private static void AddNote(PdfDocument doc, PdfPage page, PdfRectangle box, int rot,
                                    double nx, double ny, string text, string author, DateTime created)
        {
            const double S = 20;
            var (x, y) = ToPdf(box, rot, nx, ny);
            var annot = new PdfDictionary(doc);
            annot.Elements["/Type"] = new PdfName("/Annot");
            annot.Elements["/Subtype"] = new PdfName("/Text");
            annot.Elements["/Rect"] = Numbers(doc, x, y - S, x + S, y);
            annot.Elements["/Contents"] = new PdfString(text, PdfStringEncoding.Unicode);
            annot.Elements["/T"] = new PdfString(author ?? "", PdfStringEncoding.Unicode);
            annot.Elements["/M"] = new PdfString(PdfDate(DateTime.Now));
            annot.Elements["/CreationDate"] = new PdfString(PdfDate(created));
            annot.Elements["/Name"] = new PdfName("/Comment");
            annot.Elements["/C"] = Numbers(doc, 1, 0.82, 0.25);
            annot.Elements["/F"] = new PdfInteger(4 | 8 | 16); // Drucken, nicht zoomen, nicht drehen
            annot.Elements["/Open"] = new PdfBoolean(false);
            annot.Elements["/P"] = page.Reference;

            // Erscheinungsbild: gelber Zettel mit Linien
            string ap =
                "1 0.82 0.25 rg 0.62 0.45 0.05 RG 0.8 w " +
                "1.5 5.5 17 13 re B " +
                "1 0.82 0.25 rg 4 5.6 m 4 1.2 l 9 5.6 l h B " +
                "0.62 0.45 0.05 RG 0.9 w 4.5 15 m 15.5 15 l S 4.5 12 m 15.5 12 l S 4.5 9 m 12 9 l S";
            var form = FormXObject(doc, S, S, ap, null);
            var apDict = new PdfDictionary(doc);
            apDict.Elements["/N"] = form.Reference;
            annot.Elements["/AP"] = apDict;

            doc.Internals.AddObject(annot);
            AnnotsOf(doc, page).Elements.Add(annot.Reference);
        }

        // ───────────── Textmarker (/Highlight) ─────────────

        private static void AddHighlight(PdfDocument doc, PdfPage page, PdfRectangle box, int rot,
                                         List<(double nx, double ny, double nw, double nh)> rects,
                                         string text, string author, DateTime created, Color color)
        {
            if (rects.Count == 0) return;

            var quads = new List<double>();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            var pdfRects = new List<(double x0, double y0, double x1, double y1)>();
            foreach (var (nx, ny, nw, nh) in rects)
            {
                var a = ToPdf(box, rot, nx, ny);
                var c = ToPdf(box, rot, nx + nw, ny + nh);
                double x0 = Math.Min(a.x, c.x), x1 = Math.Max(a.x, c.x);
                double y0 = Math.Min(a.y, c.y), y1 = Math.Max(a.y, c.y);
                pdfRects.Add((x0, y0, x1, y1));
                // Reihenfolge wie Acrobat: oben-links, oben-rechts, unten-links, unten-rechts
                quads.AddRange(new[] { x0, y1, x1, y1, x0, y0, x1, y0 });
                minX = Math.Min(minX, x0); minY = Math.Min(minY, y0);
                maxX = Math.Max(maxX, x1); maxY = Math.Max(maxY, y1);
            }

            var annot = new PdfDictionary(doc);
            annot.Elements["/Type"] = new PdfName("/Annot");
            annot.Elements["/Subtype"] = new PdfName("/Highlight");
            annot.Elements["/Rect"] = Numbers(doc, minX, minY, maxX, maxY);
            annot.Elements["/QuadPoints"] = Numbers(doc, quads.ToArray());
            double cr = color.R / 255.0, cg = color.G / 255.0, cb = color.B / 255.0;
            annot.Elements["/C"] = Numbers(doc, cr, cg, cb);
            annot.Elements["/T"] = new PdfString(author ?? "", PdfStringEncoding.Unicode);
            if (!string.IsNullOrEmpty(text)) annot.Elements["/Contents"] = new PdfString(text, PdfStringEncoding.Unicode);
            annot.Elements["/M"] = new PdfString(PdfDate(DateTime.Now));
            annot.Elements["/CreationDate"] = new PdfString(PdfDate(created));
            annot.Elements["/F"] = new PdfInteger(4);
            annot.Elements["/P"] = page.Reference;

            // Erscheinungsbild: gelbe Flächen, „Multiplizieren“ – Text bleibt lesbar
            var gs = new PdfDictionary(doc);
            gs.Elements["/Type"] = new PdfName("/ExtGState");
            gs.Elements["/BM"] = new PdfName("/Multiply");
            gs.Elements["/CA"] = new PdfReal(1);
            gs.Elements["/ca"] = new PdfReal(1);
            var gsDict = new PdfDictionary(doc);
            gsDict.Elements["/G0"] = gs;
            var res = new PdfDictionary(doc);
            res.Elements["/ExtGState"] = gsDict;

            var sb = new StringBuilder($"/G0 gs {F(cr)} {F(cg)} {F(cb)} rg ");
            foreach (var (x0, y0, x1, y1) in pdfRects)
                sb.Append($"{F(x0 - minX)} {F(y0 - minY)} {F(x1 - x0)} {F(y1 - y0)} re ");
            sb.Append("f");

            var form = FormXObject(doc, maxX - minX, maxY - minY, sb.ToString(), res);
            // Form liegt an Rect-Ursprung
            form.Elements["/Matrix"] = Numbers(doc, 1, 0, 0, 1, 0, 0);
            var apDict = new PdfDictionary(doc);
            apDict.Elements["/N"] = form.Reference;
            annot.Elements["/AP"] = apDict;

            doc.Internals.AddObject(annot);
            AnnotsOf(doc, page).Elements.Add(annot.Reference);
        }
    }
}
