using System;
using System.Windows;
using System.Windows.Threading;

namespace KantaPdf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // PDFsharp soll die normalen Windows-Schriften (Arial usw.) nutzen – wichtig für die OCR-Textschicht.
            PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true;

            DispatcherUnhandledException += OnUnhandled;

            var win = new MainWindow();
            MainWindow = win;
            win.Show();

            // „Öffnen mit KANTApdf“ bzw. Doppelklick: Pfad kommt als Argument
            if (e.Args.Length > 0)
                _ = win.OpenAsync(e.Args[0]);
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                "Da ist etwas schiefgelaufen:\n\n" + e.Exception.Message,
                "KANTApdf", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
