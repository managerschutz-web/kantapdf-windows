using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace KantaPdf
{
    public partial class App : Application
    {
        private const string MergeMutexName = "KANTApdf.MergeServer";
        private const string MergePipeName = "KANTApdf.Merge";
        private Mutex _mergeMutex;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // PDFsharp bekommt die Windows-Schriften (Arial) – wichtig für die OCR-Textschicht.
            PdfSharp.Fonts.GlobalFontSettings.FontResolver = new Services.WindowsFontResolver();
            DispatcherUnhandledException += OnUnhandled;

            var args = e.Args.ToList();
            bool merge = args.Count > 0 && args[0] == "--merge";
            if (merge) args.RemoveAt(0);

            // „Mit KANTApdf zusammenfügen“: Explorer startet pro markierter Datei einen Prozess.
            // Der erste sammelt, alle weiteren reichen ihre Datei an ihn weiter und beenden sich.
            if (merge)
            {
                _mergeMutex = new Mutex(true, MergeMutexName, out bool first);
                if (!first)
                {
                    SendToCollector(args);
                    Shutdown();
                    return;
                }
            }

            var win = new MainWindow();
            MainWindow = win;
            win.Show();

            if (merge)
            {
                var files = await CollectAsync(args);
                try { _mergeMutex.ReleaseMutex(); } catch { }
                _mergeMutex.Dispose();
                _mergeMutex = null;
                files = files.Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase).ToList();
                await win.OpenFilesAsync(files, -1);
            }
            else if (args.Count > 0)
            {
                await win.OpenFilesAsync(args, -1);
            }

            // Eingerichtete Version aktuell halten und Kontextmenü auffrischen
            var updateMsg = Services.AppSetup.MaintainInstallation();
            if (updateMsg != null) win.Notify(updateMsg);

            // Beim ersten Start: Desktop-Verknüpfung & Co. anbieten
            await Dispatcher.InvokeAsync(() => Services.AppSetup.FirstStart(win), DispatcherPriority.ContextIdle);
        }

        private static void SendToCollector(List<string> files)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", MergePipeName, PipeDirection.Out);
                client.Connect(5000);
                using var w = new StreamWriter(client);
                foreach (var f in files) w.WriteLine(f);
                w.Flush();
            }
            catch
            {
                // Sammler nicht erreichbar → nichts weiter tun
            }
        }

        /// <summary>Nimmt Dateien anderer Prozesse an, bis 1,5 s lang nichts mehr kommt.</summary>
        private static async Task<List<string>> CollectAsync(List<string> initial)
        {
            var list = new List<string>(initial);
            var last = DateTime.Now;
            var cts = new CancellationTokenSource();

            var server = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = new NamedPipeServerStream(MergePipeName, PipeDirection.In,
                            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await pipe.WaitForConnectionAsync(cts.Token);
                        using var r = new StreamReader(pipe);
                        string line;
                        while ((line = await r.ReadLineAsync()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            lock (list) list.Add(line.Trim());
                            last = DateTime.Now;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { await Task.Delay(50); }
                }
            });

            while ((DateTime.Now - last).TotalMilliseconds < 1500)
                await Task.Delay(100);
            cts.Cancel();
            try { await server; } catch { }
            lock (list) return list.ToList();
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
