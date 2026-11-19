using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace KantaPdf.Services
{
    /// <summary>
    /// Darstellung über die in Windows 10/11 eingebaute PDF-Engine (Windows.Data.Pdf).
    /// Die Datei wird komplett in den Speicher geladen → keine Dateisperre auf dem Original.
    /// </summary>
    public sealed class PdfView
    {
        private readonly WinPdf.PdfDocument _doc;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public string Path { get; }
        public string Password { get; }
        public int PageCount { get; }
        /// <summary>Seitengrößen in DIP (1/96 Zoll), Drehung bereits berücksichtigt.</summary>
        public IReadOnlyList<Size> PageSizes { get; }

        private PdfView(string path, string password, WinPdf.PdfDocument doc)
        {
            Path = path;
            Password = password;
            _doc = doc;
            PageCount = (int)doc.PageCount;
            var sizes = new List<Size>(PageCount);
            for (uint i = 0; i < doc.PageCount; i++)
            {
                using var p = doc.GetPage(i);
                sizes.Add(new Size(p.Size.Width, p.Size.Height));
            }
            PageSizes = sizes;
        }

        /// <summary>HRESULT für „falsches oder fehlendes Kennwort“.</summary>
        public const int WrongPasswordHResult = unchecked((int)0x8007052B);

        public static async Task<PdfView> OpenAsync(string path, string password = null)
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var ras = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(ras))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            ras.Seek(0);

            WinPdf.PdfDocument doc = string.IsNullOrEmpty(password)
                ? await WinPdf.PdfDocument.LoadFromStreamAsync(ras)
                : await WinPdf.PdfDocument.LoadFromStreamAsync(ras, password);

            return new PdfView(path, password, doc);
        }

        /// <summary>Rendert eine Seite in der gewünschten Pixelbreite als eingefrorenes WPF-Bild.</summary>
        public async Task<BitmapSource> RenderAsync(int index, int widthPx)
        {
            await _lock.WaitAsync();
            try
            {
                using var page = _doc.GetPage((uint)index);
                using var stream = new InMemoryRandomAccessStream();
                var opts = new WinPdf.PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(16, widthPx) };
                await page.RenderToStreamAsync(stream, opts);
                stream.Seek(0);

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream.AsStream();
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
