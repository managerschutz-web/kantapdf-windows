using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KantaPdf.Services
{
    /// <summary>
    /// Druckt vorgerenderte Seiten (JPEG im Speicher, damit auch große Dokumente passen).
    /// Querformat-Seiten werden auf Hochformat-Papier automatisch gedreht.
    /// </summary>
    public sealed class PrintPaginator : DocumentPaginator
    {
        private readonly List<byte[]> _pages;
        private Size _size;

        public PrintPaginator(List<byte[]> pages, Size pageSize)
        {
            _pages = pages;
            _size = pageSize;
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => _pages.Count;
        public override Size PageSize { get => _size; set => _size = value; }
        public override IDocumentPaginatorSource Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(_pages[pageNumber]);
            bmp.EndInit();
            bmp.Freeze();

            double pw = _size.Width, ph = _size.Height;
            double iw = bmp.PixelWidth, ih = bmp.PixelHeight;
            bool rotate = (iw > ih) != (pw > ph);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                if (rotate)
                {
                    dc.PushTransform(new TranslateTransform(pw, 0));
                    dc.PushTransform(new RotateTransform(90));
                    (pw, ph) = (ph, pw);
                }
                double s = Math.Min(pw / iw, ph / ih);
                double w = iw * s, h = ih * s;
                dc.DrawImage(bmp, new Rect((pw - w) / 2, (ph - h) / 2, w, h));
                if (rotate) { dc.Pop(); dc.Pop(); }
            }
            var rect = new Rect(_size);
            return new DocumentPage(dv, _size, rect, rect);
        }
    }
}
