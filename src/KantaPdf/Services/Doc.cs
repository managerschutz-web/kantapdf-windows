using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace KantaPdf.Services
{
    public abstract class Notify : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void On([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Eine geöffnete PDF-Datei (Quelle). Ein Dokument kann Seiten aus mehreren Quellen enthalten.</summary>
    public sealed class Source
    {
        public string Path { get; }
        public string Password { get; }
        public byte[] Bytes { get; }
        public PdfView View { get; }
        public string Name => System.IO.Path.GetFileName(Path);

        // Textschicht (PdfPig) – wird erst bei Bedarf geladen
        internal readonly object PigLock = new object();
        internal UglyToad.PdfPig.PdfDocument Pig;

        private Source(string path, string password, byte[] bytes, PdfView view)
        {
            Path = path; Password = password; Bytes = bytes; View = view;
        }

        public static async Task<Source> OpenAsync(string path, string password)
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var view = await PdfView.FromBytesAsync(path, password, bytes);
            return new Source(path, password, bytes, view);
        }
    }

    public static class Geo
    {
        /// <summary>Relatives Rechteck um 90° im Uhrzeigersinn drehen.</summary>
        public static (double x, double y, double w, double h) Rot90(double x, double y, double w, double h) =>
            (1 - (y + h), x, h, w);

        public static (double x, double y, double w, double h) Rot(double x, double y, double w, double h, int degCw)
        {
            int steps = (((degCw % 360) + 360) % 360) / 90;
            for (int i = 0; i < steps; i++) (x, y, w, h) = Rot90(x, y, w, h);
            return (x, y, w, h);
        }
    }

    /// <summary>Rechteck in relativen Seitenkoordinaten (0..1, oben links, so wie die Seite angezeigt wird).</summary>
    public sealed class RectItem : Notify
    {
        public PageItem Page { get; }
        public object Tag { get; set; }
        public double NX, NY, NW, NH;

        private double _l, _t, _w, _h;
        private Brush _fill;
        private bool _current;
        public double Left { get => _l; private set { _l = value; On(); } }
        public double Top { get => _t; private set { _t = value; On(); } }
        public double W { get => _w; private set { _w = value; On(); } }
        public double H { get => _h; private set { _h = value; On(); } }
        public Brush Fill { get => _fill; set { _fill = value; On(); } }
        public bool IsCurrent { get => _current; set { _current = value; On(); } }

        public RectItem(PageItem page) { Page = page; }

        /// <summary>Um 90° im Uhrzeigersinn mitdrehen (Seite wurde gedreht).</summary>
        public void Rotate90()
        {
            var (x, y, w, h) = Geo.Rot90(NX, NY, NW, NH);
            Set(x, y, w, h);
        }

        public RectItem(PageItem page, double x, double y, double w, double h) : this(page) { Set(x, y, w, h); }

        public void Set(double x, double y, double w, double h)
        {
            NX = x; NY = y; NW = w; NH = h;
            UpdateDisplay();
        }

        public void UpdateDisplay()
        {
            Left = NX * Page.FullWidth;
            Top = NY * Page.FullHeight;
            W = NW * Page.FullWidth;
            H = NH * Page.FullHeight;
        }
    }

    /// <summary>Ein Wort der Textschicht mit Position (relativ, angezeigte Ausrichtung).</summary>
    public sealed class WordBox
    {
        public string Text;
        public double X, Y, W, H;
        public bool Contains(Point p) => p.X >= X && p.X <= X + W && p.Y >= Y && p.Y <= Y + H;
        public double DistanceTo(Point p)
        {
            double dx = p.X < X ? X - p.X : p.X > X + W ? p.X - X - W : 0;
            double dy = p.Y < Y ? Y - p.Y : p.Y > Y + H ? p.Y - Y - H : 0;
            return dx * dx * 0.25 + dy * dy * 4; // horizontal großzügiger als vertikal
        }
    }

    /// <summary>Textmarker-Anmerkung.</summary>
    public sealed class HighlightItem
    {
        public PageItem Page { get; }
        public ObservableCollection<RectItem> Rects { get; } = new ObservableCollection<RectItem>();
        public Color Color { get; private set; } = Color.FromRgb(0xFF, 0xD8, 0x4A);

        public void SetColor(Color c)
        {
            Color = c;
            var b = new SolidColorBrush(Color.FromArgb(0x70, c.R, c.G, c.B));
            b.Freeze();
            foreach (var r in Rects) r.Fill = b;
        }
        public string Text { get; set; }
        public string Author { get; set; } = Environment.UserName;
        public DateTime Created { get; set; } = DateTime.Now;
        public HighlightItem(PageItem page) { Page = page; }
    }

    /// <summary>Notizzettel-Anmerkung.</summary>
    public sealed class NoteItem : Notify
    {
        public PageItem Page { get; }
        public double NX, NY;
        private string _text = "";
        private double _l, _t;
        private string _pageLabel;

        public string Text { get => _text; set { _text = value ?? ""; On(); On(nameof(Preview)); } }
        public string Author { get; set; } = Environment.UserName;
        public DateTime Created { get; set; } = DateTime.Now;
        public double Left { get => _l; private set { _l = value; On(); } }
        public double Top { get => _t; private set { _t = value; On(); } }
        public string PageLabel { get => _pageLabel; set { _pageLabel = value; On(); } }
        public string Preview => string.IsNullOrWhiteSpace(_text) ? "(leer)" : _text.Replace("\r", " ").Replace("\n", " ");
        public string Meta => $"{Author} · {Created:dd.MM.yyyy HH:mm}";

        public NoteItem(PageItem page, double nx, double ny) { Page = page; NX = nx; NY = ny; UpdateDisplay(); }

        public void UpdateDisplay()
        {
            Left = NX * Page.FullWidth - 6;
            Top = NY * Page.FullHeight - 6;
        }
    }

    /// <summary>Platziertes Bild (Unterschrift oder Stempel): Mitte, Breite in pt, eigene Drehung.</summary>
    public sealed class PlacedItem : Notify
    {
        public PageItem Page { get; }
        public byte[] Png { get; }
        public ImageSource Image { get; }
        public string Kind { get; }
        public double Aspect { get; }   // Höhe / Breite (Pixel)
        public double CX, CY;           // Mitte, relativ zur (ganzen) Seite
        public double WidthPt;          // Breite in PDF-Punkten (ungedreht)
        public int Angle;               // eigene Drehung im Uhrzeigersinn (0/90/180/270)

        private double _l, _t, _w, _h, _bw, _bh;
        private bool _sel;
        public double Left { get => _l; private set { _l = value; On(); } }
        public double Top { get => _t; private set { _t = value; On(); } }
        public double W { get => _w; private set { _w = value; On(); } }
        public double H { get => _h; private set { _h = value; On(); } }
        public double BoxW { get => _bw; private set { _bw = value; On(); } }
        public double BoxH { get => _bh; private set { _bh = value; On(); } }
        public double AngleD => Angle;
        public bool IsSelected { get => _sel; set { _sel = value; On(); } }

        public double HeightPt => WidthPt * Aspect;

        public PlacedItem(PageItem page, byte[] png, string kind, double cx, double cy, double widthPt)
        {
            Page = page; Png = png; Kind = kind;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            Image = bmp;
            Aspect = bmp.PixelHeight / (double)Math.Max(1, bmp.PixelWidth);
            CX = cx; CY = cy; WidthPt = widthPt;
            UpdateDisplay();
        }

        private double DipPerPt => 96.0 / 72.0 * (Page.BaseWidth > 0 ? Page.FullWidth / Page.BaseWidth : 1);

        /// <summary>Verschieben um Anzeige-DIP.</summary>
        public void Move(double dx, double dy)
        {
            if (Page.FullWidth <= 0) return;
            CX = Math.Max(0, Math.Min(1, CX + dx / Page.FullWidth));
            CY = Math.Max(0, Math.Min(1, CY + dy / Page.FullHeight));
            UpdateDisplay();
        }

        /// <summary>Neue Breite in Anzeige-DIP (ungedreht).</summary>
        public void ResizeTo(double widthDip)
        {
            WidthPt = Math.Max(12, Math.Min(1200, widthDip / DipPerPt));
            UpdateDisplay();
        }

        public void Rotate90()
        {
            (CX, CY) = (1 - CY, CX);
            Angle = (Angle + 90) % 360;
            On(nameof(AngleD));
            UpdateDisplay();
        }

        public void UpdateDisplay()
        {
            W = WidthPt * DipPerPt;
            H = W * Aspect;
            BoxW = Angle % 180 == 0 ? W : H;
            BoxH = Angle % 180 == 0 ? H : W;
            Left = CX * Page.FullWidth - BoxW / 2;
            Top = CY * Page.FullHeight - BoxH / 2;
        }
    }

    /// <summary>Zuschnitt-Rahmen während der Bearbeitung (mit abgedunkeltem Rand).</summary>
    public sealed class CropItem : Notify
    {
        public PageItem Page { get; }
        public double NX, NY, NW, NH;
        private double _l, _t, _w, _h, _fw, _fh;
        public double Left { get => _l; private set { _l = value; On(); } }
        public double Top { get => _t; private set { _t = value; On(); } }
        public double W { get => _w; private set { _w = value; On(); } }
        public double H { get => _h; private set { _h = value; On(); } }
        public double FW { get => _fw; private set { _fw = value; On(); } }
        public double FH { get => _fh; private set { _fh = value; On(); } }
        public double Right => Left + W;
        public double Bottom => Top + H;
        public double RightW => Math.Max(0, FW - Left - W);
        public double BottomH => Math.Max(0, FH - Top - H);

        public CropItem(PageItem page, double x, double y, double w, double h) { Page = page; Set(x, y, w, h); }

        public void Set(double x, double y, double w, double h)
        {
            NX = Math.Max(0, Math.Min(0.98, x)); NY = Math.Max(0, Math.Min(0.98, y));
            NW = Math.Max(0.02, Math.Min(1 - NX, w)); NH = Math.Max(0.02, Math.Min(1 - NY, h));
            UpdateDisplay();
        }

        public void UpdateDisplay()
        {
            FW = Page.FullWidth; FH = Page.FullHeight;
            Left = NX * FW; Top = NY * FH; W = NW * FW; H = NH * FH;
            On(nameof(Right)); On(nameof(Bottom)); On(nameof(RightW)); On(nameof(BottomH));
        }
    }

    public enum FieldKind { Text, Multiline, Check, Radio, Choice }

    /// <summary>Ausfüllbares Formularfeld (Widget) einer Seite.</summary>
    public sealed class FieldItem : Notify
    {
        public PageItem Page { get; }
        public int AnnotIndex { get; }
        public FieldKind Kind { get; }
        public string Name { get; }
        public string OnName { get; }
        public string Group { get; }
        public List<string> Options { get; }
        public double NX, NY, NW, NH;
        public bool Changed { get; private set; }

        private string _value;
        private bool _checked;
        private double _l, _t, _w, _h, _fs;

        public string Value { get => _value; set { if (_value == value) return; _value = value; Changed = true; On(); } }
        public bool IsChecked { get => _checked; set { if (_checked == value) return; _checked = value; Changed = true; On(); } }
        public double Left { get => _l; private set { _l = value; On(); } }
        public double Top { get => _t; private set { _t = value; On(); } }
        public double W { get => _w; private set { _w = value; On(); } }
        public double H { get => _h; private set { _h = value; On(); } }
        public double FontSize { get => _fs; private set { _fs = value; On(); } }

        public bool IsText => Kind == FieldKind.Text || Kind == FieldKind.Multiline;
        public bool IsMultiline => Kind == FieldKind.Multiline;
        public bool IsCheck => Kind == FieldKind.Check || Kind == FieldKind.Radio;
        public bool IsRadio => Kind == FieldKind.Radio;
        public bool IsChoice => Kind == FieldKind.Choice;

        public FieldItem(PageItem page, FieldDef d)
        {
            Page = page; AnnotIndex = d.AnnotIndex; Kind = d.Kind; Name = d.Name; OnName = d.OnName; Group = d.Group;
            Options = d.Options ?? new List<string>();
            _value = d.Value ?? ""; _checked = d.Checked;
            NX = d.X; NY = d.Y; NW = d.W; NH = d.H;
            UpdateDisplay();
        }

        /// <summary>Optionsfeld der Gruppe ausschalten, ohne es als „geändert“ zu verlieren.</summary>
        public void SetCheckedSilently(bool on) { if (_checked == on) return; _checked = on; Changed = true; On(nameof(IsChecked)); }

        public void Rotate90() { (NX, NY, NW, NH) = Geo.Rot90(NX, NY, NW, NH); UpdateDisplay(); }

        public void UpdateDisplay()
        {
            Left = NX * Page.FullWidth; Top = NY * Page.FullHeight; W = NW * Page.FullWidth; H = NH * Page.FullHeight;
            double z = Page.BaseWidth > 0 ? Page.FullWidth / Page.BaseWidth : 1;
            FontSize = IsMultiline ? Math.Max(6, 13 * z) : Math.Max(6, Math.Min(H * 0.62, 15 * z));
        }
    }

    /// <summary>Formularfeld, wie es aus der PDF gelesen wurde.</summary>
    public sealed class FieldDef
    {
        public int PageIndex, AnnotIndex;
        public FieldKind Kind;
        public string Name, Value, OnName, Group;
        public bool Checked;
        public List<string> Options;
        public double X, Y, W, H;
    }

    /// <summary>Frei eingefügter Text – wird beim Speichern fest in die Seite geschrieben.</summary>
    public sealed class TextItem : Notify
    {
        public PageItem Page { get; }
        public double NX, NY;
        public bool FocusOnLoad;

        private string _text = "";
        private double _pt = 11, _l, _t, _size;
        private Color _color = Colors.Black;
        private bool _bold;

        public string Text { get => _text; set { _text = value ?? ""; On(); } }
        public double FontPt { get => _pt; set { _pt = Math.Max(5, Math.Min(96, value)); On(); UpdateDisplay(); } }
        public Color Color { get => _color; set { _color = value; On(); On(nameof(Brush)); } }
        public bool Bold { get => _bold; set { _bold = value; On(); On(nameof(Weight)); } }
        public Brush Brush { get { var b = new SolidColorBrush(_color); b.Freeze(); return b; } }
        public FontWeight Weight => _bold ? FontWeights.Bold : FontWeights.Normal;
        public double Left { get => _l; private set { _l = value; On(); } }
        public double Top { get => _t; private set { _t = value; On(); } }
        public double DisplaySize { get => _size; private set { _size = value; On(); } }

        public int Angle;
        public double AngleD => Angle;

        public TextItem(PageItem page, double nx, double ny) { Page = page; NX = nx; NY = ny; UpdateDisplay(); }

        public void Rotate90()
        {
            (NX, NY) = (1 - NY, NX);
            Angle = (Angle + 90) % 360;
            On(nameof(AngleD));
            UpdateDisplay();
        }

        public void Move(double dx, double dy)
        {
            NX = Math.Max(0, Math.Min(1, NX + dx));
            NY = Math.Max(0, Math.Min(1, NY + dy));
            UpdateDisplay();
        }

        public void UpdateDisplay()
        {
            Left = NX * Page.FullWidth;
            Top = NY * Page.FullHeight;
            double z = Page.BaseWidth > 0 ? Page.FullWidth / Page.BaseWidth : 1;
            DisplaySize = _pt * 96.0 / 72.0 * z;
        }
    }

    /// <summary>Eine Seite im aktuellen Dokument (verweist auf Quelle + Seitennummer).</summary>
    public sealed class PageItem : Notify
    {
        public Source Source { get; }
        public int SourceIndex { get; }
        /// <summary>Größe der Quellseite in DIP (Drehung im PDF schon berücksichtigt).</summary>
        public Size SourceSize { get; }
        /// <summary>Zusätzliche Drehung durch den Nutzer (0/90/180/270, im Uhrzeigersinn).</summary>
        public int ExtraRotation { get; private set; }
        public double BaseWidth => ExtraRotation % 180 == 0 ? SourceSize.Width : SourceSize.Height;
        public double BaseHeight => ExtraRotation % 180 == 0 ? SourceSize.Height : SourceSize.Width;

        private double _w, _h, _fw, _fh, _ox, _oy, _tw, _th;
        private ImageSource _img, _thumb;
        private int _number;
        private double _zoom = 1;

        /// <summary>Sichtbarer (ggf. zugeschnittener) Bereich in Anzeige-DIP.</summary>
        public double Width { get => _w; private set { _w = value; On(); } }
        public double Height { get => _h; private set { _h = value; On(); } }
        /// <summary>Ganze Seite in Anzeige-DIP – Bezug für alle Overlays.</summary>
        public double FullWidth { get => _fw; private set { _fw = value; On(); } }
        public double FullHeight { get => _fh; private set { _fh = value; On(); } }
        public double CropOffsetX { get => _ox; private set { _ox = value; On(); } }
        public double CropOffsetY { get => _oy; private set { _oy = value; On(); } }

        /// <summary>Zuschnitt relativ zur ganzen (gedrehten) Seite. Standard: 0,0,1,1.</summary>
        public double CropX { get; private set; }
        public double CropY { get; private set; }
        public double CropW { get; private set; } = 1;
        public double CropH { get; private set; } = 1;
        public bool IsCropped => CropX > 0.0001 || CropY > 0.0001 || CropW < 0.9999 || CropH < 0.9999;

        public ObservableCollection<CropItem> CropEdit { get; } = new ObservableCollection<CropItem>();

        public void SetCrop(double x, double y, double w, double h)
        {
            x = Math.Max(0, Math.Min(0.95, x)); y = Math.Max(0, Math.Min(0.95, y));
            w = Math.Max(0.05, Math.Min(1 - x, w)); h = Math.Max(0.05, Math.Min(1 - y, h));
            CropX = x; CropY = y; CropW = w; CropH = h;
            ApplyZoom(_zoom);
            Thumb = null; ThumbPx = 0;
            ApplyThumbWidth(ThumbW);
        }
        public ImageSource Image { get => _img; set { _img = value; On(); } }
        public double ThumbW { get => _tw; private set { _tw = value; On(); } }
        public double ThumbH { get => _th; private set { _th = value; On(); } }
        public ImageSource Thumb { get => _thumb; set { _thumb = value; On(); } }
        public int Number { get => _number; set { _number = value; On(); } }

        public ObservableCollection<RectItem> Redactions { get; } = new ObservableCollection<RectItem>();
        public ObservableCollection<RectItem> Selection { get; } = new ObservableCollection<RectItem>();
        public ObservableCollection<HighlightItem> Highlights { get; } = new ObservableCollection<HighlightItem>();
        public ObservableCollection<NoteItem> Notes { get; } = new ObservableCollection<NoteItem>();
        public ObservableCollection<PlacedItem> Placed { get; } = new ObservableCollection<PlacedItem>();
        public ObservableCollection<RectItem> SearchHits { get; } = new ObservableCollection<RectItem>();
        public ObservableCollection<FieldItem> Fields { get; } = new ObservableCollection<FieldItem>();
        public ObservableCollection<TextItem> Texts { get; } = new ObservableCollection<TextItem>();

        // Textschicht (WordsRaw = Quellseite, Words = inkl. Nutzer-Drehung)
        public List<WordBox> WordsRaw;
        public List<WordBox> Words;
        public bool WordsLoading;

        public void SetWords(List<WordBox> raw)
        {
            WordsRaw = raw;
            RebuildWords();
        }

        private void RebuildWords()
        {
            if (WordsRaw == null) { Words = null; return; }
            Words = WordsRaw.Select(w =>
            {
                var (x, y, ww, hh) = Geo.Rot(w.X, w.Y, w.W, w.H, ExtraRotation);
                return new WordBox { Text = w.Text, X = x, Y = y, W = ww, H = hh };
            }).ToList();
        }

        /// <summary>Seite um 90° drehen (positiv = im Uhrzeigersinn). Anmerkungen drehen mit.</summary>
        public void RotateBy(int delta)
        {
            int steps = (((delta % 360) + 360) % 360) / 90;
            for (int i = 0; i < steps; i++)
            {
                foreach (var r in Redactions) r.Rotate90();
                foreach (var h in Highlights) foreach (var r in h.Rects) r.Rotate90();
                foreach (var n in Notes) { var (x, y, _, _) = Geo.Rot90(n.NX, n.NY, 0, 0); n.NX = x; n.NY = y; }
                foreach (var f in Fields) f.Rotate90();
                foreach (var pl in Placed) pl.Rotate90();
                foreach (var t in Texts) t.Rotate90();
                (CropX, CropY, CropW, CropH) = Geo.Rot90(CropX, CropY, CropW, CropH);
            }
            ExtraRotation = (ExtraRotation + delta % 360 + 360) % 360;
            Selection.Clear();
            SearchHits.Clear();
            RebuildWords();
            Image = null; RenderedPx = 0;
            Thumb = null; ThumbPx = 0;
        }

        // Render-Status
        public int RenderedPx, ThumbPx;
        public ReadMode RenderedMode, ThumbMode;
        public bool Queued, ThumbQueued;

        public PageItem(Source source, int sourceIndex)
        {
            Source = source;
            SourceIndex = sourceIndex;
            SourceSize = source.View.PageSizes[sourceIndex];
        }

        public void ApplyZoom(double zoom)
        {
            _zoom = zoom;
            FullWidth = BaseWidth * zoom;
            FullHeight = BaseHeight * zoom;
            Width = FullWidth * CropW;
            Height = FullHeight * CropH;
            CropOffsetX = -CropX * FullWidth;
            CropOffsetY = -CropY * FullHeight;
            foreach (var c in CropEdit) c.UpdateDisplay();
            foreach (var r in Redactions) r.UpdateDisplay();
            foreach (var r in Selection) r.UpdateDisplay();
            foreach (var h in Highlights) foreach (var r in h.Rects) r.UpdateDisplay();
            foreach (var n in Notes) n.UpdateDisplay();
            foreach (var p in Placed) p.UpdateDisplay();
            foreach (var r in SearchHits) r.UpdateDisplay();
            foreach (var f in Fields) f.UpdateDisplay();
            foreach (var t in Texts) t.UpdateDisplay();
        }

        public void ApplyThumbWidth(double w)
        {
            ThumbW = w;
            ThumbH = w * (BaseHeight * CropH) / (BaseWidth * CropW);
        }

        public bool HasEdits => Redactions.Count > 0 || Highlights.Count > 0 || Notes.Count > 0 || Placed.Count > 0 || Texts.Count > 0 || IsCropped;

        /// <summary>Drehung der Quellseite (0/90/180/270) – für Unterschriften/Stempel relevant.</summary>
        public int SourceRotation { get; set; }
    }
}
