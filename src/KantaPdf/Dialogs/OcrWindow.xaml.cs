using System;
using System.IO;
using System.Threading;
using System.Windows;
using KantaPdf.Services;

namespace KantaPdf.Dialogs
{
    public partial class OcrWindow : Window
    {
        private readonly string _path, _password;
        private CancellationTokenSource _cts;

        public string ResultPath { get; private set; }
        public string Info { get; private set; }

        public OcrWindow(string path, string password)
        {
            InitializeComponent();
            _path = path; _password = password;

            var lang = OcrService.EngineLanguageName();
            if (lang == null)
            {
                Lang.Text = "Keine Texterkennung gefunden.";
                Status.Text = "Windows → Einstellungen → Zeit und Sprache → Sprache und Region → Deutsch → Sprachoptionen → „Optische Zeichenerkennung“ hinzufügen.";
                BtnRun.IsEnabled = false;
            }
            else
            {
                Lang.Text = "Sprache: " + lang;
            }
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF-Dateien|*.pdf",
                FileName = Path.GetFileNameWithoutExtension(_path) + "_OCR.pdf",
                InitialDirectory = Path.GetDirectoryName(_path),
                Title = "Durchsuchbare PDF speichern"
            };
            if (dlg.ShowDialog(this) != true) return;
            var output = dlg.FileName;
            if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            {
                Status.Text = "Bitte unter neuem Namen speichern – das Original bleibt so erhalten.";
                return;
            }

            _cts = new CancellationTokenSource();
            BtnRun.IsEnabled = false;
            SkipText.IsEnabled = false;
            var progress = new Progress<string>(t => Status.Text = t);
            try
            {
                var r = await OcrService.MakeSearchableAsync(_path, _password, output, SkipText.IsChecked == true, progress, _cts.Token);
                ResultPath = output;
                var extra = "";
                if (r.SkippedText > 0) extra += $", {r.SkippedText} hatten schon Text";
                if (r.SkippedRotated > 0) extra += $", {r.SkippedRotated} gedreht übersprungen";
                Info = $"Durchsuchbar: {r.Recognized} Seite{(r.Recognized == 1 ? "" : "n")} erkannt{extra}  ·  {Path.GetFileName(output)}";
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                Status.Text = "Abgebrochen.";
                BtnRun.IsEnabled = true;
                SkipText.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Status.Text = "Hat nicht geklappt: " + ex.Message;
                BtnRun.IsEnabled = true;
                SkipText.IsEnabled = true;
            }
            finally
            {
                _cts = null;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) { _cts.Cancel(); Status.Text = "Breche ab …"; return; }
            DialogResult = false;
        }
    }
}
