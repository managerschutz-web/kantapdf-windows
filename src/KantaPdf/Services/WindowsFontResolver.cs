using System;
using System.IO;
using PdfSharp.Fonts;

namespace KantaPdf.Services
{
    /// <summary>
    /// Liefert PDFsharp die Schriftdateien aus C:\Windows\Fonts.
    /// Gebraucht wird nur Arial (für die unsichtbare OCR-Textschicht).
    /// </summary>
    public sealed class WindowsFontResolver : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic)
        {
            string face = bold && italic ? "arialbi" : bold ? "arialbd" : italic ? "ariali" : "arial";
            return new FontResolverInfo(face);
        }

        public byte[] GetFont(string faceName)
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var path = Path.Combine(dir, faceName + ".ttf");
            if (!File.Exists(path)) path = Path.Combine(dir, "arial.ttf");
            return File.ReadAllBytes(path);
        }
    }
}
