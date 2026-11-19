using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using KantaPdf.Services;

namespace KantaPdf.Dialogs
{
    public partial class SplitWindow : Window
    {
        private readonly string _path, _password;
        private readonly int _pageCount;

        public string ResultPath { get; private set; }
        public string Info { get; private set; }

        public SplitWindow(string path, string password, int pageCount)
        {
            InitializeComponent();
            _path = path; _password = password; _pageCount = pageCount;
            Sub.Text = $"{Path.GetFileName(_path)} · {_pageCount} Seiten";
        }

        private void Ranges_GotFocus(object sender, RoutedEventArgs e) => RRanges.IsChecked = true;
        private void Chunk_GotFocus(object sender, RoutedEventArgs e) => RChunk.IsChecked = true;

        private List<(int, int)> BuildRanges()
        {
            var list = new List<(int, int)>();
            if (REach.IsChecked == true)
            {
                for (int i = 1; i <= _pageCount; i++) list.Add((i, i));
            }
            else if (RRanges.IsChecked == true)
            {
                list = PdfTools.ParseRanges(Ranges.Text, _pageCount);
            }
            else
            {
                if (!int.TryParse(Chunk.Text.Trim(), out int n) || n < 1)
                    throw new FormatException("Bitte eine Seitenanzahl ab 1 eingeben.");
                for (int i = 1; i <= _pageCount; i += n) list.Add((i, Math.Min(_pageCount, i + n - 1)));
            }
            return list;
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            List<(int, int)> ranges;
            try { ranges = BuildRanges(); }
            catch (FormatException ex) { Status.Text = ex.Message; return; }

            if (ranges.Count > 50 &&
                MessageBox.Show(this, $"Es werden {ranges.Count} Dateien erzeugt. Fortfahren?", "KANTApdf",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var folder = Path.GetDirectoryName(_path);
            var baseName = Path.GetFileNameWithoutExtension(_path);

            IsEnabled = false;
            Status.Text = "Teile …";
            try
            {
                var files = await Task.Run(() => PdfTools.Split(_path, _password, ranges, folder, baseName));
                ResultPath = files.Count > 0 ? files[0] : folder;
                Info = $"{files.Count} Datei{(files.Count == 1 ? "" : "en")} erstellt in „{Path.GetFileName(folder)}“";
                DialogResult = true;
            }
            catch (Exception ex)
            {
                Status.Text = "Hat nicht geklappt: " + ex.Message;
                IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
