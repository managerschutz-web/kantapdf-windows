using System;
using System.IO;
using System.Windows;
using KantaPdf.Services;

namespace KantaPdf.Dialogs
{
    public partial class CompressWindow : Window
    {
        private readonly string _path, _password;

        public string ResultPath { get; private set; }
        public string Info { get; private set; }

        public CompressWindow(string path, string password)
        {
            InitializeComponent();
            _path = path; _password = password;
            Sub.Text = $"{Path.GetFileName(_path)} · aktuell {PdfTools.FormatSize(new FileInfo(_path).Length)}";
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            var level = RLight.IsChecked == true ? CompressLevel.Leicht
                      : RStrong.IsChecked == true ? CompressLevel.Stark
                      : CompressLevel.Mittel;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF-Dateien|*.pdf",
                FileName = Path.GetFileNameWithoutExtension(_path) + "_klein.pdf",
                InitialDirectory = Path.GetDirectoryName(_path),
                Title = "Verkleinerte PDF speichern"
            };
            if (dlg.ShowDialog(this) != true) return;
            var output = dlg.FileName;
            if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            {
                Status.Text = "Bitte unter neuem Namen speichern – das Original bleibt so erhalten.";
                return;
            }

            IsEnabled = false;
            Status.Text = "Verkleinere … das kann bei großen Scans einen Moment dauern.";
            try
            {
                var r = await PdfTools.RunSta(() => PdfTools.Compress(_path, _password, output, level));
                if (r.After >= r.Before)
                {
                    try { File.Delete(output); } catch { }
                    Status.Text = "Diese Datei ist schon optimal – kleiner geht es nicht. Es wurde nichts gespeichert.";
                    IsEnabled = true;
                    return;
                }
                double pct = 100.0 * (r.Before - r.After) / r.Before;
                ResultPath = output;
                Info = $"Verkleinert: {PdfTools.FormatSize(r.Before)} → {PdfTools.FormatSize(r.After)} (−{pct:0} %)  ·  {Path.GetFileName(output)}";
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
