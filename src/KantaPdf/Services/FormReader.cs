using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace KantaPdf.Services
{
    /// <summary>Liest ausfüllbare Formularfelder (Widgets) samt Position aus einer PDF.</summary>
    public static class FormReader
    {
        public static Task<List<FieldDef>> ReadAsync(Source src) => Task.Run(() =>
        {
            try { return Read(src); }
            catch { return new List<FieldDef>(); }
        });

        internal static PdfItem Resolve(PdfItem item) => item is PdfReference r ? r.Value : item;

        internal static PdfArray AnnotsOf(PdfPage page) => Resolve(page.Elements["/Annots"]) as PdfArray;

        internal static PdfDictionary ParentOf(PdfDictionary d) => Resolve(d.Elements["/Parent"]) as PdfDictionary;

        /// <summary>Wert suchen, auch in übergeordneten Feldern (Vererbung).</summary>
        internal static PdfItem Inherit(PdfDictionary d, string key)
        {
            for (int i = 0; d != null && i < 12; i++, d = ParentOf(d))
                if (d.Elements.ContainsKey(key)) return Resolve(d.Elements[key]);
            return null;
        }

        private static string Str(PdfItem item) => item switch
        {
            PdfString s => s.Value,
            PdfName n => n.Value.TrimStart('/'),
            PdfArray a when a.Elements.Count > 0 => Str(Resolve(a.Elements[0])),
            _ => null
        };

        private static List<FieldDef> Read(Source src)
        {
            var list = new List<FieldDef>();
            using var ms = new MemoryStream(src.Bytes);
            using var doc = string.IsNullOrEmpty(src.Password)
                ? PdfReader.Open(ms, PdfDocumentOpenMode.Import)
                : PdfReader.Open(ms, src.Password, PdfDocumentOpenMode.Import);

            for (int pi = 0; pi < doc.PageCount; pi++)
            {
                var page = doc.Pages[pi];
                var annots = AnnotsOf(page);
                if (annots == null) continue;

                var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
                double left = Math.Min(box.X1, box.X2), right = Math.Max(box.X1, box.X2);
                double bottom = Math.Min(box.Y1, box.Y2), top = Math.Max(box.Y1, box.Y2);
                double bw = right - left, bh = top - bottom;
                if (bw <= 0 || bh <= 0) continue;
                int rot = ((page.Elements.GetInteger("/Rotate") % 360) + 360) % 360;

                for (int ai = 0; ai < annots.Elements.Count; ai++)
                {
                    if (!(Resolve(annots.Elements[ai]) is PdfDictionary w)) continue;
                    if (w.Elements.GetName("/Subtype") != "/Widget") continue;
                    int annotFlags = w.Elements.GetInteger("/F");
                    if ((annotFlags & 2) != 0) continue; // versteckt

                    var ft = Inherit(w, "/FT") as PdfName;
                    if (ft == null) continue;
                    int ff = Inherit(w, "/Ff") is PdfInteger fi ? fi.Value : 0;
                    if ((ff & 1) != 0) continue; // schreibgeschützt

                    FieldKind kind;
                    string onName = null;
                    var apN = Resolve((Resolve(w.Elements["/AP"]) as PdfDictionary)?.Elements["/N"]) as PdfDictionary;

                    switch (ft.Value)
                    {
                        case "/Tx":
                            kind = (ff & 4096) != 0 ? FieldKind.Multiline : FieldKind.Text;
                            break;
                        case "/Btn":
                            if ((ff & 65536) != 0) continue; // Schaltfläche
                            kind = (ff & 32768) != 0 ? FieldKind.Radio : FieldKind.Check;
                            onName = apN?.Elements.Keys.FirstOrDefault(k => k != "/Off")?.TrimStart('/') ?? "Yes";
                            break;
                        case "/Ch":
                            kind = FieldKind.Choice;
                            break;
                        default:
                            continue;
                    }

                    // vollständiger Feldname aus der /T-Kette
                    var names = new List<string>();
                    for (var d = w; d != null && names.Count < 12; d = ParentOf(d))
                        if (Resolve(d.Elements["/T"]) is PdfString t) names.Insert(0, t.Value);
                    string name = string.Join(".", names);

                    string value = Str(Inherit(w, "/V"));
                    bool isChecked = false;
                    if (kind == FieldKind.Check || kind == FieldKind.Radio)
                    {
                        var asName = w.Elements.GetName("/AS");
                        isChecked = !string.IsNullOrEmpty(asName) && asName != "/Off";
                    }

                    List<string> options = null;
                    if (kind == FieldKind.Choice && Inherit(w, "/Opt") is PdfArray opt)
                    {
                        options = new List<string>();
                        foreach (var o in opt.Elements)
                        {
                            var ro = Resolve(o);
                            if (ro is PdfArray pair && pair.Elements.Count > 1) options.Add(Str(Resolve(pair.Elements[1])) ?? "");
                            else options.Add(Str(ro) ?? "");
                        }
                    }

                    string group = null;
                    if (kind == FieldKind.Radio)
                    {
                        var parent = ParentOf(w);
                        group = parent != null && parent.Reference != null ? parent.Reference.ObjectID.ToString() : name;
                    }

                    var r = w.Elements.GetRectangle("/Rect");
                    double x0 = Math.Min(r.X1, r.X2), x1 = Math.Max(r.X1, r.X2);
                    double y0 = Math.Min(r.Y1, r.Y2), y1 = Math.Max(r.Y1, r.Y2);
                    double u0 = (x0 - left) / bw, u1 = (x1 - left) / bw;
                    double v0 = (top - y1) / bh, v1 = (top - y0) / bh;
                    var (a0, b0) = TextLayer.Rotate(u0, v0, rot);
                    var (a1, b1) = TextLayer.Rotate(u1, v1, rot);

                    list.Add(new FieldDef
                    {
                        PageIndex = pi,
                        AnnotIndex = ai,
                        Kind = kind,
                        Name = name,
                        Value = value,
                        Checked = isChecked,
                        OnName = onName,
                        Group = group,
                        Options = options,
                        X = Math.Min(a0, a1),
                        Y = Math.Min(b0, b1),
                        W = Math.Abs(a1 - a0),
                        H = Math.Abs(b1 - b0)
                    });
                }
            }
            return list;
        }
    }
}
