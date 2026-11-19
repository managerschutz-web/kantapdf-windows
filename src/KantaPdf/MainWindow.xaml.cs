using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KantaPdf.Dialogs;
using KantaPdf.Services;

namespace KantaPdf
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<PageVM> _pages = new ObservableCollection<PageVM>();
        private readonly Queue<PageVM> _queue = new Queue<PageVM>();
        private readonly DispatcherTimer _debounce;
        private readonly DispatcherTimer _toastTimer;

        private PdfView _view;
        private ScrollViewer _scroll;
        private double _zoom = 1.0;
        private double _maxPageWidth = 600;
        private ReadMode _mode;
        private bool _rendering;

        private string _toastPath;
        private bool _toastOpenable;

        // Schwärzen
        private bool _redactMode;
        private Grid _dragGrid;
        private RedactBox _dragBox;
        private Point _dragStart;

        public MainWindow()
        {
            InitializeComponent();
            PageList.ItemsSource = _pages;

            _mode = Settings.LoadMode();
            Theme.Apply(_mode);
            BtnMode.Content = _mode.ToString();

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); UpdateVisible(); UpdatePageInfo(); };

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(14) };
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

            Loaded += (s, e) =>
            {
                PageList.ApplyTemplate();
                _scroll = PageList.Template.FindName("PART_Scroll", PageList) as ScrollViewer;
                if (_scroll != null) _scroll.ScrollChanged += (s2, e2) => Kick();
            };
            SizeChanged += (s, e) => Kick();

            UpdateUi();
        }

        private void Kick()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        // ───────────────────────── Öffnen ─────────────────────────

        public async Task OpenAsync(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (!ConfirmLeaveRedaction()) return;

            PdfView view = null;
            string password = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    view = await PdfView.OpenAsync(path, password);
                    break;
                }
                catch (Exception ex) when (ex.HResult == PdfView.WrongPasswordHResult)
                {
                    var dlg = new PasswordPrompt(Path.GetFileName(path), attempt > 0) { Owner = this };
                    if (dlg.ShowDialog() != true) return;
                    password = dlg.Password;
                }
                catch (Exception ex)
                {
                    ShowError("Die Datei konnte nicht geöffnet werden.", ex);
                    return;
                }
            }
            if (view == null) return;

            SetRedactMode(false);
            _view = view;
            _queue.Clear();
            _pages.Clear();
            _maxPageWidth = 1;
            for (int i = 0; i < view.PageCount; i++)
            {
                var size = view.PageSizes[i];
                _maxPageWidth = Math.Max(_maxPageWidth, size.Width);
                _pages.Add(new PageVM(i, size));
            }

            FileName.Text = Path.GetFileName(path);
            Title = $"{FileName.Text} – KANTApdf";
            UpdateUi();

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            FitWidth();
            _scroll?.ScrollToTop();
            Kick();
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PDF-Dateien|*.pdf", Title = "PDF öffnen" };
            if (dlg.ShowDialog(this) == true) _ = OpenAsync(dlg.FileName);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;
            var pdfs = files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
            if (pdfs.Count == 1) _ = OpenAsync(pdfs[0]);
            else if (pdfs.Count > 1)
            {
                // Mehrere PDFs reingezogen → direkt zusammenfügen anbieten
                var dlg = new MergeWindow(pdfs) { Owner = this };
                if (dlg.ShowDialog() == true) ShowToast($"Zusammengefügt: {Path.GetFileName(dlg.ResultPath)}", dlg.ResultPath, true);
            }
        }

        private void UpdateUi()
        {
            bool has = _view != null;
            EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            PageList.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            BtnOcr.IsEnabled = BtnSplit.IsEnabled = BtnCompress.IsEnabled = BtnRedact.IsEnabled = BtnProtect.IsEnabled = has;
            if (!has) { PageInfo.Text = ""; FileName.Text = ""; Title = "KANTApdf"; }
        }

        // ───────────────────────── Darstellung ─────────────────────────

        private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

        private int TargetPx(PageVM vm) => (int)Math.Min(4200, Math.Round(vm.Width * DpiScale));

        private void UpdateVisible()
        {
            if (_view == null) return;
            var gen = PageList.ItemContainerGenerator;
            foreach (var vm in _pages)
            {
                bool realized = gen.ContainerFromIndex(vm.Index) != null;
                if (realized)
                {
                    int px = TargetPx(vm);
                    bool needs = vm.Image == null || vm.RenderedMode != _mode || Math.Abs(vm.RenderedPx - px) > px * 0.06;
                    if (needs && !vm.Queued)
                    {
                        vm.Queued = true;
                        _queue.Enqueue(vm);
                    }
                }
                else if (vm.Image != null)
                {
                    vm.Image = null; // Speicher freigeben
                    vm.RenderedPx = 0;
                }
            }
            if (!_rendering) _ = PumpAsync();
        }

        private async Task PumpAsync()
        {
            _rendering = true;
            try
            {
                while (_queue.Count > 0)
                {
                    var vm = _queue.Dequeue();
                    vm.Queued = false;
                    var view = _view;
                    if (view == null) break;
                    if (PageList.ItemContainerGenerator.ContainerFromIndex(vm.Index) == null) continue;

                    int px = TargetPx(vm);
                    var mode = _mode;
                    try
                    {
                        var bmp = await view.RenderAsync(vm.Index, px);
                        if (view != _view) break;
                        var shown = mode == ReadMode.Papier ? bmp : await Task.Run(() => Tint.Apply(bmp, mode));
                        if (view != _view) break;
                        vm.Image = shown;
                        vm.RenderedPx = px;
                        vm.RenderedMode = mode;
                    }
                    catch
                    {
                        // Einzelne Seite nicht darstellbar → Platzhalter bleibt
                    }
                }
            }
            finally
            {
                _rendering = false;
            }
        }

        private void UpdatePageInfo()
        {
            if (_view == null || _scroll == null) { PageInfo.Text = ""; return; }
            int current = 1;
            var gen = PageList.ItemContainerGenerator;
            for (int i = 0; i < _pages.Count; i++)
            {
                if (!(gen.ContainerFromIndex(i) is FrameworkElement c)) continue;
                try
                {
                    var y = c.TransformToAncestor(_scroll).Transform(new Point(0, 0)).Y;
                    if (y + c.ActualHeight > _scroll.ViewportHeight * 0.35) { current = i + 1; break; }
                }
                catch { }
            }
            PageInfo.Text = $"Seite {current} von {_pages.Count}";
        }

        // ───────────────────────── Zoom ─────────────────────────

        private void SetZoom(double z)
        {
            _zoom = Math.Max(0.2, Math.Min(5.0, z));
            foreach (var vm in _pages) vm.ApplyZoom(_zoom);
            ZoomText.Content = $"{Math.Round(_zoom * 100)} %";
            Kick();
        }

        private void FitWidth()
        {
            double avail = (_scroll != null && _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth) - 64;
            double fit = avail / _maxPageWidth;
            double comfy = 1100 / _maxPageWidth; // auf großen Bildschirmen nicht übertrieben breit
            SetZoom(Math.Min(fit, comfy));
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.15);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.15);
        private void Fit_Click(object sender, RoutedEventArgs e) => FitWidth();

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_view == null || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
            e.Handled = true;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _redactMode) { RedactExit_Click(null, null); e.Handled = true; return; }
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            switch (e.Key)
            {
                case Key.O: Open_Click(null, null); e.Handled = true; break;
                case Key.OemPlus: case Key.Add: if (_view != null) SetZoom(_zoom * 1.15); e.Handled = true; break;
                case Key.OemMinus: case Key.Subtract: if (_view != null) SetZoom(_zoom / 1.15); e.Handled = true; break;
                case Key.D0: case Key.NumPad0: if (_view != null) FitWidth(); e.Handled = true; break;
            }
        }

        // ───────────────────────── Ansicht ─────────────────────────

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            _mode = _mode == ReadMode.Papier ? ReadMode.Sepia : _mode == ReadMode.Sepia ? ReadMode.Nacht : ReadMode.Papier;
            Theme.Apply(_mode);
            Settings.SaveMode(_mode);
            BtnMode.Content = _mode.ToString();
            Kick();
        }

        // ───────────────────────── Werkzeuge ─────────────────────────

        private void Ocr_Click(object sender, RoutedEventArgs e)
        {
            if (_view == null) return;
            var dlg = new OcrWindow(_view.Path, _view.Password) { Owner = this };
            if (dlg.ShowDialog() == true) ShowToast(dlg.Info, dlg.ResultPath, true);
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            var start = new List<string>();
            if (_view != null && string.IsNullOrEmpty(_view.Password)) start.Add(_view.Path);
            var dlg = new MergeWindow(start) { Owner = this };
            if (dlg.ShowDialog() == true) ShowToast($"Zusammengefügt: {Path.GetFileName(dlg.ResultPath)}", dlg.ResultPath, true);
        }

        private void Split_Click(object sender, RoutedEventArgs e)
        {
            if (_view == null) return;
            var dlg = new SplitWindow(_view.Path, _view.Password, _view.PageCount) { Owner = this };
            if (dlg.ShowDialog() == true) ShowToast(dlg.Info, dlg.ResultPath, false);
        }

        private void Compress_Click(object sender, RoutedEventArgs e)
        {
            if (_view == null) return;
            var dlg = new CompressWindow(_view.Path, _view.Password) { Owner = this };
            if (dlg.ShowDialog() == true) ShowToast(dlg.Info, dlg.ResultPath, true);
        }

        private void Protect_Click(object sender, RoutedEventArgs e)
        {
            if (_view == null) return;
            var dlg = new ProtectWindow(_view.Path, _view.Password) { Owner = this };
            if (dlg.ShowDialog() == true) ShowToast(dlg.Info, dlg.ResultPath, true);
        }

        // ───────────────────────── Schwärzen ─────────────────────────

        private void Redact_Click(object sender, RoutedEventArgs e)
        {
            if (_view == null) return;
            SetRedactMode(!_redactMode);
        }

        private void SetRedactMode(bool on)
        {
            _redactMode = on;
            RedactBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            PageList.Cursor = on ? Cursors.Cross : null;
            BtnRedact.Content = on ? "Schwärzen ✓" : "Schwärzen";
            if (!on) foreach (var vm in _pages) vm.Redactions.Clear();
            UpdateRedactCount();
        }

        private int RedactionTotal => _pages.Sum(p => p.Redactions.Count);

        private void UpdateRedactCount()
        {
            int n = RedactionTotal;
            int pages = _pages.Count(p => p.Redactions.Count > 0);
            RedactCount.Text = n == 0 ? "" : $"  ·  {n} Balken auf {pages} Seite{(pages == 1 ? "" : "n")}";
            BtnRedactApply.IsEnabled = n > 0;
        }

        private bool ConfirmLeaveRedaction()
        {
            if (!_redactMode || RedactionTotal == 0) return true;
            return MessageBox.Show(this, "Die gesetzten Schwärzungen wurden noch nicht gespeichert und gehen verloren. Fortfahren?",
                "KANTApdf", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private void Page_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_redactMode || !(sender is Grid grid) || !(grid.DataContext is PageVM vm)) return;
            _dragGrid = grid;
            _dragStart = e.GetPosition(grid);
            _dragBox = new RedactBox(vm);
            _dragBox.SetNormalized(_dragStart.X / grid.ActualWidth, _dragStart.Y / grid.ActualHeight, 0, 0);
            vm.Redactions.Add(_dragBox);
            grid.CaptureMouse();
            e.Handled = true;
        }

        private void Page_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragBox == null || _dragGrid == null) return;
            var p = e.GetPosition(_dragGrid);
            double w = _dragGrid.ActualWidth, h = _dragGrid.ActualHeight;
            double x0 = Clamp01(Math.Min(_dragStart.X, p.X) / w), y0 = Clamp01(Math.Min(_dragStart.Y, p.Y) / h);
            double x1 = Clamp01(Math.Max(_dragStart.X, p.X) / w), y1 = Clamp01(Math.Max(_dragStart.Y, p.Y) / h);
            _dragBox.SetNormalized(x0, y0, x1 - x0, y1 - y0);
        }

        private void Page_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragBox == null || _dragGrid == null) return;
            _dragGrid.ReleaseMouseCapture();
            if (_dragBox.W < 4 || _dragBox.H < 4) _dragBox.Owner.Redactions.Remove(_dragBox);
            _dragBox = null;
            _dragGrid = null;
            UpdateRedactCount();
            e.Handled = true;
        }

        private void Redaction_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_redactMode || !(sender is FrameworkElement fe) || !(fe.DataContext is RedactBox box)) return;
            box.Owner.Redactions.Remove(box);
            UpdateRedactCount();
            e.Handled = true;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        private void RedactClear_Click(object sender, RoutedEventArgs e)
        {
            foreach (var vm in _pages) vm.Redactions.Clear();
            UpdateRedactCount();
        }

        private void RedactExit_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmLeaveRedaction()) return;
            SetRedactMode(false);
        }

        private async void RedactApply_Click(object sender, RoutedEventArgs e)
        {
            if (_view == null || RedactionTotal == 0) return;
            var src = _view.Path;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF-Dateien|*.pdf",
                FileName = Path.GetFileNameWithoutExtension(src) + "_geschwärzt.pdf",
                InitialDirectory = Path.GetDirectoryName(src),
                Title = "Geschwärzte PDF speichern"
            };
            if (dlg.ShowDialog(this) != true) return;
            if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Bitte unter einem neuen Namen speichern – das Original bleibt so als Sicherung erhalten.",
                    "KANTApdf", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BtnRedactApply.IsEnabled = false;
            var progress = new Progress<string>(t => RedactCount.Text = "  ·  " + t);
            try
            {
                int pages = await PdfTools.RedactAsync(_view, _pages.ToList(), dlg.FileName, progress);
                SetRedactMode(false);
                ShowToast($"Geschwärzt gespeichert ({pages} Seite{(pages == 1 ? "" : "n")} bereinigt): {Path.GetFileName(dlg.FileName)}",
                    dlg.FileName, true);
            }
            catch (Exception ex)
            {
                ShowError("Schwärzen hat nicht geklappt.", ex);
                UpdateRedactCount();
            }
        }

        // ───────────────────────── Meldungen ─────────────────────────

        private void ShowToast(string text, string path, bool openable)
        {
            _toastPath = path;
            _toastOpenable = openable && path != null && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            ToastText.Text = text;
            ToastOpen.Visibility = _toastOpenable ? Visibility.Visible : Visibility.Collapsed;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void ToastOpen_Click(object sender, RoutedEventArgs e)
        {
            Toast.Visibility = Visibility.Collapsed;
            if (_toastOpenable) _ = OpenAsync(_toastPath);
        }

        private void ToastFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_toastPath)) return;
            try
            {
                if (File.Exists(_toastPath))
                    Process.Start("explorer.exe", $"/select,\"{_toastPath}\"");
                else if (Directory.Exists(_toastPath))
                    Process.Start("explorer.exe", $"\"{_toastPath}\"");
            }
            catch { }
        }

        private void ToastClose_Click(object sender, RoutedEventArgs e) => Toast.Visibility = Visibility.Collapsed;

        private void ShowError(string message, Exception ex) =>
            MessageBox.Show(this, message + "\n\n" + ex.Message, "KANTApdf", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
