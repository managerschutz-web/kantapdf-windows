using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using KantaPdf.Services;

namespace KantaPdf.Dialogs
{
    public partial class ProtectWindow : Window
    {
        private readonly string _path, _password;

        public string ResultPath { get; private set; }
        public string Info { get; private set; }

        public ProtectWindow(string path, string password)
        {
            InitializeComponent();
            _path = path; _password = password;

            bool isProtected = !string.IsNullOrEmpty(password);
            Sub.Text = isProtected
                ? $"„{Path.GetFileName(path)}“ ist bereits kennwortgeschützt."
                : $"„{Path.GetFileName(path)}“ ist derzeit nicht geschützt.";
            RRemove.IsEnabled = isProtected;
            if (isProtected) RRemove.IsChecked = true;
            Mode_Changed(null, null);
            Loaded += (s, e) => { if (RSet.IsChecked == true) Pw1.Focus(); };
        }

        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (SetPanel == null || BtnRun == null || Status == null) return; // während InitializeComponent
            bool set = RSet.IsChecked == true;
            SetPanel.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
            BtnRun.Content = set ? "Schützen & speichern" : "Schutz entfernen & speichern";
            Status.Text = "";
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            bool set = RSet.IsChecked == true;
            if (set)
            {
                if (Pw1.Password.Length < 4) { Status.Text = "Bitte ein Kennwort mit mindestens 4 Zeichen wählen."; Pw1.Focus(); return; }
                if (Pw1.Password != Pw2.Password) { Status.Text = "Die beiden Kennwörter stimmen nicht überein."; Pw2.Focus(); return; }
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF-Dateien|*.pdf",
                FileName = Path.GetFileNameWithoutExtension(_path) + (set ? "_geschützt.pdf" : "_offen.pdf"),
                InitialDirectory = Path.GetDirectoryName(_path),
                Title = set ? "Geschützte PDF speichern" : "PDF ohne Kennwort speichern"
            };
            if (dlg.ShowDialog(this) != true) return;
            var output = dlg.FileName;
            if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            {
                Status.Text = "Bitte unter neuem Namen speichern – das Original bleibt so erhalten.";
                return;
            }

            string pw = Pw1.Password;
            bool print = AllowPrint.IsChecked == true, copy = AllowCopy.IsChecked == true, edit = AllowEdit.IsChecked == true;

            IsEnabled = false;
            Status.Text = "Speichere …";
            try
            {
                if (set)
                {
                    bool aes = await Task.Run(() => PdfTools.Protect(_path, _password, output, pw, print, copy, edit));
                    Info = $"Geschützt gespeichert{(aes ? " (AES-256)" : "")}: {Path.GetFileName(output)}";
                }
                else
                {
                    await Task.Run(() => PdfTools.Unprotect(_path, _password, output));
                    Info = $"Ohne Kennwort gespeichert: {Path.GetFileName(output)}";
                }
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
