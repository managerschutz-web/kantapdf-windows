using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    /// <summary>Eine Seite in der Leseansicht.</summary>
    public sealed class PageVM : Notify
    {
        public int Index { get; }
        public double BaseWidth { get; }
        public double BaseHeight { get; }

        private double _w, _h;
        private ImageSource _img;

        public double Width { get => _w; private set { _w = value; On(); } }
        public double Height { get => _h; private set { _h = value; On(); } }
        public ImageSource Image { get => _img; set { _img = value; On(); } }

        /// <summary>Schwärzungs-Balken dieser Seite (relative Koordinaten 0..1).</summary>
        public ObservableCollection<RedactBox> Redactions { get; } = new ObservableCollection<RedactBox>();

        // Render-Status (intern)
        public int RenderedPx;
        public ReadMode RenderedMode;
        public bool Queued;

        public PageVM(int index, Size baseSizeDip)
        {
            Index = index;
            BaseWidth = baseSizeDip.Width;
            BaseHeight = baseSizeDip.Height;
        }

        public void ApplyZoom(double zoom)
        {
            Width = BaseWidth * zoom;
            Height = BaseHeight * zoom;
            foreach (var r in Redactions) r.UpdateDisplay();
        }
    }

    /// <summary>Ein schwarzer Balken. Gespeichert relativ (0..1), angezeigt in DIP.</summary>
    public sealed class RedactBox : Notify
    {
        public PageVM Owner { get; }
        public double NX, NY, NW, NH;

        private double _l, _t, _w, _h;
        public double Left { get => _l; private set { _l = value; On(); } }
        public double Top { get => _t; private set { _t = value; On(); } }
        public double W { get => _w; private set { _w = value; On(); } }
        public double H { get => _h; private set { _h = value; On(); } }

        public RedactBox(PageVM owner) { Owner = owner; }

        public void SetNormalized(double x, double y, double w, double h)
        {
            NX = x; NY = y; NW = w; NH = h;
            UpdateDisplay();
        }

        public void UpdateDisplay()
        {
            Left = NX * Owner.Width;
            Top = NY * Owner.Height;
            W = NW * Owner.Width;
            H = NH * Owner.Height;
        }
    }
}
