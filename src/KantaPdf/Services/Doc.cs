using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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

    /// <summary>Rechteck in relativen Seitenkoordinaten (0..1, oben links, so wie die Seite angezeigt wird).</summary>
    public sealed class RectItem : Notify
    {
        public PageItem Page { get; }
        public object Tag { get; set; }
        public double NX, NY, NW, NH;

        private double _l, _t, _w, _h;
        public double Left { get => _l; private set { _l = value; On(); } }
        public double Top { get => _t; private set { _t = value; On(); } }
        public double W { get => _w; private set { _w = value; On(); } }
        public double H { get => _h; private set { _h = value; On(); } }

        public RectItem(PageItem page) { Page = page; }

        public RectItem(PageItem page, double x, double y, double w, double h) : this(page) { Set(x, y, w, h); }

        public void Set(double x, double y, double w, double h)
        {
            NX = x; NY = y; NW = w; NH = h;
            UpdateDisplay();
        }

        public void UpdateDisplay()
        {
            Left = NX * Page.Width;
            Top = NY * Page.Height;
            W = NW * Page.Width;
            H = NH * Page.Height;
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
            Left = NX * Page.Width - 6;
            Top = NY * Page.Height - 6;
        }
    }

    /// <summary>Platziertes Bild (Unterschrift oder Stempel), verschieb- und skalierbar.</summary>
    public sealed class PlacedItem : Notify
    {
        public PageItem Page { get; }
        public byte[] Png { get; }
        public ImageSource Image { get; }
        public string Kind { get; }
        public double Aspect { get; }   // Höhe / Breite (Pixel)
        public double NX, NY, NW;

        private double _l, _t, _w, _h;
        private bool _sel;
        public double Left { get => _l; private set { _l = value; On(); } }
        public double Top { get => _t; private set { _t = value; On(); } }
        public double W { get => _w; private set { _w = value; On(); } }
        public double H { get => _h; private set { _h = value; On(); } }
        public bool IsSelected { get => _sel; set { _sel = value; On(); } }

        /// <summary>Höhe relativ zur Seitenhöhe (aus Breite und Seitenverhältnis).</summary>
        public double NH => NW * Page.BaseWidth * Aspect / Page.BaseHeight;

        public PlacedItem(PageItem page, byte[] png, string kind, double nx, double ny, double nw)
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
            NX = nx; NY = ny; NW = nw;
            UpdateDisplay();
        }

        public void Move(double dxNorm, double dyNorm)
        {
            NX = Math.Max(-NW * 0.5, Math.Min(1 - NW * 0.5, NX + dxNorm));
            NY = Math.Max(-NH * 0.5, Math.Min(1 - NH * 0.5, NY + dyNorm));
            UpdateDisplay();
        }

        public void Resize(double newNw)
        {
            NW = Math.Max(0.03, Math.Min(1.2, newNw));
            UpdateDisplay();
        }

        public void UpdateDisplay()
        {
            Left = NX * Page.Width;
            Top = NY * Page.Height;
            W = NW * Page.Width;
            H = NH * Page.Height;
        }
    }

    /// <summary>Eine Seite im aktuellen Dokument (verweist auf Quelle + Seitennummer).</summary>
    public sealed class PageItem : Notify
    {
        public Source Source { get; }
        public int SourceIndex { get; }
        public double BaseWidth { get; }
        public double BaseHeight { get; }

        private double _w, _h, _tw, _th;
        private ImageSource _img, _thumb;
        private int _number;

        public double Width { get => _w; private set { _w = value; On(); } }
        public double Height { get => _h; private set { _h = value; On(); } }
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

        // Textschicht
        public List<WordBox> Words;
        public bool WordsLoading;

        // Render-Status
        public int RenderedPx, ThumbPx;
        public ReadMode RenderedMode, ThumbMode;
        public bool Queued, ThumbQueued;

        public PageItem(Source source, int sourceIndex)
        {
            Source = source;
            SourceIndex = sourceIndex;
            var size = source.View.PageSizes[sourceIndex];
            BaseWidth = size.Width;
            BaseHeight = size.Height;
        }

        public void ApplyZoom(double zoom)
        {
            Width = BaseWidth * zoom;
            Height = BaseHeight * zoom;
            foreach (var r in Redactions) r.UpdateDisplay();
            foreach (var r in Selection) r.UpdateDisplay();
            foreach (var h in Highlights) foreach (var r in h.Rects) r.UpdateDisplay();
            foreach (var n in Notes) n.UpdateDisplay();
            foreach (var p in Placed) p.UpdateDisplay();
        }

        public void ApplyThumbWidth(double w)
        {
            ThumbW = w;
            ThumbH = w * BaseHeight / BaseWidth;
        }

        public bool HasEdits => Redactions.Count > 0 || Highlights.Count > 0 || Notes.Count > 0 || Placed.Count > 0;

        /// <summary>Drehung der Quellseite (0/90/180/270) – für Unterschriften/Stempel relevant.</summary>
        public int SourceRotation { get; set; }
    }
}
