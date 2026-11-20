using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace KantaPdf.Services
{
    /// <summary>
    /// Liest die Textschicht mit PdfPig (Apache-2.0) und rechnet Wortpositionen
    /// in relative, gedrehte Anzeige-Koordinaten um.
    /// </summary>
    public static class TextLayer
    {
        public static Task<List<WordBox>> LoadAsync(PageItem page) =>
            Task.Run(() => Load(page.Source, page.SourceIndex));

        private static List<WordBox> Load(Source src, int index)
        {
            var list = new List<WordBox>();
            lock (src.PigLock)
            {
                if (src.Pig == null)
                {
                    src.Pig = string.IsNullOrEmpty(src.Password)
                        ? PigDocument.Open(src.Bytes)
                        : PigDocument.Open(src.Bytes, new ParsingOptions { Password = src.Password });
                }

                var page = src.Pig.GetPage(index + 1);
                var box = page.CropBox.Bounds;
                double bw = box.Width, bh = box.Height;
                if (bw <= 0 || bh <= 0) return list;
                int rot = ((page.Rotation.Value % 360) + 360) % 360;

                foreach (var w in page.GetWords())
                {
                    if (string.IsNullOrWhiteSpace(w.Text)) continue;
                    var b = w.BoundingBox;
                    double u0 = (b.Left - box.Left) / bw, u1 = (b.Right - box.Left) / bw;
                    double v0 = (box.Top - b.Top) / bh, v1 = (box.Top - b.Bottom) / bh;
                    var (x0, y0) = Rotate(u0, v0, rot);
                    var (x1, y1) = Rotate(u1, v1, rot);
                    list.Add(new WordBox
                    {
                        Text = w.Text,
                        X = Math.Min(x0, x1),
                        Y = Math.Min(y0, y1),
                        W = Math.Abs(x1 - x0),
                        H = Math.Abs(y1 - y0)
                    });
                }
            }
            return list;
        }

        /// <summary>Ungedreht (u,v) → angezeigt (x,y); Drehung im Uhrzeigersinn wie /Rotate.</summary>
        public static (double x, double y) Rotate(double u, double v, int rot)
        {
            switch (rot)
            {
                case 90: return (1 - v, u);
                case 180: return (1 - u, 1 - v);
                case 270: return (v, 1 - u);
                default: return (u, v);
            }
        }

        /// <summary>Angezeigt (x,y) → ungedreht (u,v).</summary>
        public static (double u, double v) Unrotate(double x, double y, int rot)
        {
            switch (rot)
            {
                case 90: return (y, 1 - x);
                case 180: return (1 - x, 1 - y);
                case 270: return (1 - y, x);
                default: return (x, y);
            }
        }

        /// <summary>Wörter einer Auswahl zu Zeilen-Rechtecken zusammenfassen.</summary>
        public static List<(double x, double y, double w, double h)> LineRects(IList<WordBox> words)
        {
            var rects = new List<(double, double, double, double)>();
            WordBox first = null;
            double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            foreach (var w in words)
            {
                bool sameLine = first != null &&
                                Math.Abs((w.Y + w.H / 2) - (y0 + y1) / 2) < Math.Max(w.H, y1 - y0) * 0.5 &&
                                w.X >= x0 - w.H;
                if (!sameLine)
                {
                    if (first != null) rects.Add((x0, y0, x1 - x0, y1 - y0));
                    first = w; x0 = w.X; y0 = w.Y; x1 = w.X + w.W; y1 = w.Y + w.H;
                }
                else
                {
                    x0 = Math.Min(x0, w.X); y0 = Math.Min(y0, w.Y);
                    x1 = Math.Max(x1, w.X + w.W); y1 = Math.Max(y1, w.Y + w.H);
                }
            }
            if (first != null) rects.Add((x0, y0, x1 - x0, y1 - y0));
            return rects;
        }

        /// <summary>Text einer Auswahl mit Leerzeichen und Zeilenumbrüchen.</summary>
        public static string JoinText(IList<WordBox> words)
        {
            var sb = new StringBuilder();
            WordBox prev = null;
            foreach (var w in words)
            {
                if (prev != null)
                {
                    bool newLine = Math.Abs((w.Y + w.H / 2) - (prev.Y + prev.H / 2)) > Math.Max(w.H, prev.H) * 0.5;
                    sb.Append(newLine ? Environment.NewLine : " ");
                }
                sb.Append(w.Text);
                prev = w;
            }
            return sb.ToString();
        }

        public static int HitTest(List<WordBox> words, System.Windows.Point p)
        {
            if (words == null || words.Count == 0) return -1;
            for (int i = 0; i < words.Count; i++)
                if (words[i].Contains(p)) return i;
            int best = -1; double bd = double.MaxValue;
            for (int i = 0; i < words.Count; i++)
            {
                var d = words[i].DistanceTo(p);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }
    }
}
