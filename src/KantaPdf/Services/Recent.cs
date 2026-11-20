using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KantaPdf.Services
{
    /// <summary>Eintrag „Zuletzt geöffnet“ auf dem Startbildschirm.</summary>
    public sealed class RecentItem : Notify
    {
        private ImageSource _thumb;
        public string Path { get; }
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path);
        public string When { get; }
        public ImageSource Thumb { get => _thumb; set { _thumb = value; On(); } }

        public RecentItem(string path)
        {
            Path = path;
            var t = File.GetLastWriteTime(path);
            When = t.Date == DateTime.Today ? $"heute, {t:HH:mm}"
                 : t.Date == DateTime.Today.AddDays(-1) ? $"gestern, {t:HH:mm}"
                 : t.ToString("dd.MM.yyyy");
        }
    }

    public static class Recent
    {
        private static string FilePath =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KANTApdf", "zuletzt.txt");

        public static List<string> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<string>();
                return File.ReadAllLines(FilePath).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { return new List<string>(); }
        }

        public static void Add(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            // temporäre Umwandlungen nicht merken
            if (path.StartsWith(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return;
            var list = Load();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            Save(list.Take(20));
        }

        public static void Remove(string path)
        {
            var list = Load();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            Save(list);
        }

        private static void Save(IEnumerable<string> list)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, list);
            }
            catch { }
        }

        /// <summary>Kleine Vorschau der ersten Seite (für den Startbildschirm).</summary>
        public static async Task<ImageSource> ThumbAsync(string path, int widthPx)
        {
            try
            {
                var view = await PdfView.OpenAsync(path);
                return await view.RenderAsync(0, widthPx);
            }
            catch { return null; }
        }
    }
}
