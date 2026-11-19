using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using KantaPdf.Services;

namespace KantaPdf.Dialogs
{
    public partial class MergeWindow : Window
    {
        public sealed class FileItem
        {
            public string Path { get; }
            public string Name => System.IO.Path.GetFileName(Path);
            public string Folder => System.IO.Path.GetDirectoryName(Path);
            public FileItem(string path) { Path = path; }
        }

        private readonly ObservableCollection<FileItem> _files = new ObservableCollection<FileItem>();
        public string ResultPath { get; private set; }

        public MergeWindow(IEnumerable<string> start)
        {
            InitializeComponent();
            FileList.ItemsSource = _files;
            foreach (var f in start ?? Enumerable.Empty<string>()) AddFile(f);
            UpdateState();
        }

        private void AddFile(string path)
        {
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return;
            _files.Add(new FileItem(path));
        }

        private void UpdateState()
        {
            BtnRun.IsEnabled = _files.Count >= 2;
            Status.Text = _files.Count < 2 ? "Mindestens zwei Dateien hinzufügen." : $"{_files.Count} Dateien";
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PDF-Dateien|*.pdf", Multiselect = true, Title = "PDFs hinzufügen" };
            if (dlg.ShowDialog(this) != true) return;
            foreach (var f in dlg.FileNames) AddFile(f);
            UpdateState();
        }

        private void Move(int delta)
        {
            int i = FileList.SelectedIndex;
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _files.Count) return;
            _files.Move(i, j);
            FileList.SelectedIndex = j;
        }

        private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
        private void Down_Click(object sender, RoutedEventArgs e) => Move(+1);

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            int i = FileList.SelectedIndex;
            if (i < 0) return;
            _files.RemoveAt(i);
            FileList.SelectedIndex = Math.Min(i, _files.Count - 1);
            UpdateState();
        }

        private void List_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void List_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files) AddFile(f);
            UpdateState();
            e.Handled = true;
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            if (_files.Count < 2) return;
            var first = _files[0].Path;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF-Dateien|*.pdf",
                FileName = "Zusammengefügt.pdf",
                InitialDirectory = Path.GetDirectoryName(first),
                Title = "Zusammengefügte PDF speichern"
            };
            if (dlg.ShowDialog(this) != true) return;
            var output = dlg.FileName;
            var inputs = _files.Select(f => f.Path).ToList();
            if (inputs.Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)))
            {
                Status.Text = "Bitte einen neuen Dateinamen wählen – eine der Quelldateien hat diesen Namen.";
                return;
            }

            IsEnabled = false;
            Status.Text = "Füge zusammen …";
            try
            {
                await Task.Run(() => PdfTools.Merge(inputs, null, output));
                ResultPath = output;
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
