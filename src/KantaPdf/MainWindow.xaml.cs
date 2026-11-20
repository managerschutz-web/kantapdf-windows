using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KantaPdf.Dialogs;
using KantaPdf.Services;

namespace KantaPdf
{
    public partial class MainWindow : Window
    {
        private enum Tool { Text, Marker, Note, Redact }

        // Dokument
        private readonly ObservableCollection<PageItem> _pages = new ObservableCollection<PageItem>();
        private readonly ObservableCollection<NoteItem> _notes = new ObservableCollection<NoteItem>();
        private readonly Stack<List<PageItem>> _undo = new Stack<List<PageItem>>();
        private string _savePath;          // Ziel für „Speichern“ (null = noch nie gespeichert)
        private bool _dirty;

        // Darstellung
        private readonly Queue<PageItem> _queue = new Queue<PageItem>();
        private readonly Queue<PageItem> _thumbQueue = new Queue<PageItem>();
        private readonly DispatcherTimer _debounce;
        private readonly DispatcherTimer _toastTimer;
        private ScrollViewer _scroll, _thumbScroll;
        private double _zoom = 1.0;
        private double _maxPageWidth = 600;
        private ReadMode _mode;
        private bool _rendering;
        private int _currentIndex;
        private bool _syncingThumbs;
        private double _sideWidth;

        // Werkzeuge
        private Tool _tool = Tool.Text;
        private Grid _dragGrid;
        private PageItem _dragPage;
        private Point _dragStart;
        private bool _dragActive;
        private RectItem _dragBox;
        private PageItem _selPage;
        private int _selA = -1, _selB = -1;
        private NoteItem _activeNote;
        private string _activeNoteOriginal;
        private DateTime _popupClosedAt;
        private bool _noTextHintShown;

        // Meldung
        private Action _toastAction;
        private string _toastPath;

        public MainWindow()
        {
            InitializeComponent();
            PageList.ItemsSource = _pages;
            ThumbList.ItemsSource = _pages;
            NotesList.ItemsSource = _notes;

            _mode = Settings.LoadMode();
            Theme.Apply(_mode);
            ModeText.Text = _mode.ToString();

            _sideWidth = Settings.LoadSidebarWidth();
            SideCol.Width = new GridLength(_sideWidth);

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); UpdateVisible(); UpdatePageInfo(); };

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

            Loaded += (s, e) =>
            {
                PageList.ApplyTemplate();
                _scroll = PageList.Template.FindName("PART_Scroll", PageList) as ScrollViewer;
                if (_scroll != null) _scroll.ScrollChanged += (s2, e2) => Kick();
                _thumbScroll = FindChild<ScrollViewer>(ThumbList);
                if (_thumbScroll != null) _thumbScroll.ScrollChanged += (s2, e2) => Kick();
            };
            SizeChanged += (s, e) => Kick();
            Sidebar.SizeChanged += (s, e) => { ApplyThumbWidths(); Kick(); };

            UpdateUi();
        }

        private void Kick()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private static T FindChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t) return t;
                var r = FindChild<T>(c);
                if (r != null) return r;
            }
            return null;
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && !(d is T))
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        // ═════════════════════════ Öffnen & Anhängen ═════════════════════════

        public Task OpenAsync(string path) => OpenFilesAsync(new[] { path }, -1);

        /// <summary>
        /// insertAt = -1: neues Dokument. Sonst: Seiten an dieser Stelle einfügen (Zusammenfügen).
        /// </summary>
        private async Task OpenFilesAsync(IList<string> paths, int insertAt)
        {
            var pdfs = paths.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
            if (pdfs.Count == 0) return;

            bool newDoc = insertAt < 0 || _pages.Count == 0;
            if (newDoc && !ConfirmDiscard()) return;

            var sources = new List<Source>();
            foreach (var p in pdfs)
            {
                var src = await OpenSourceAsync(p);
                if (src != null) sources.Add(src);
            }
            if (sources.Count == 0) return;

            var newPages = sources.SelectMany(s => Enumerable.Range(0, s.View.PageCount).Select(i => new PageItem(s, i))).ToList();

            if (newDoc)
            {
                CloseNotePopup();
                ClearSelection();
                _queue.Clear(); _thumbQueue.Clear(); _undo.Clear();
                _pages.Clear();
                foreach (var pg in newPages) _pages.Add(pg);

                if (sources.Count == 1)
                {
                    _savePath = sources[0].Path;
                    SetDirty(false);
                }
                else
                {
                    _savePath = null;
                    SetDirty(true);
                    ShowToast($"{sources.Count} Dateien zusammengefügt ({newPages.Count} Seiten) – jetzt nur noch speichern.",
                        "Speichern unter …", () => SaveAs_Click(null, null));
                }
                SelectTool(Tool.Text);
                AfterStructureChange();
                UpdateUi();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                FitWidth();
                _scroll?.ScrollToTop();
            }
            else
            {
                PushUndo();
                int at = Math.Max(0, Math.Min(insertAt, _pages.Count));
                foreach (var pg in newPages) _pages.Insert(at++, pg);
                foreach (var pg in newPages) pg.ApplyZoom(_zoom);
                SetDirty(true);
                AfterStructureChange();
                string what = sources.Count == 1 ? $"„{sources[0].Name}“" : $"{sources.Count} Dateien";
                ShowToast($"{what} eingefügt (+{newPages.Count} Seite{(newPages.Count == 1 ? "" : "n")}).", "Rückgängig", Undo);
            }
            Kick();
        }

        private async Task<Source> OpenSourceAsync(string path)
        {
            string password = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    return await Source.OpenAsync(path, password);
                }
                catch (Exception ex) when (ex.HResult == PdfView.WrongPasswordHResult)
                {
                    var dlg = new PasswordPrompt(Path.GetFileName(path), attempt > 0) { Owner = this };
                    if (dlg.ShowDialog() != true) return null;
                    password = dlg.Password;
                }
                catch (Exception ex)
                {
                    ShowError($"„{Path.GetFileName(path)}“ konnte nicht geöffnet werden.", ex);
                    return null;
                }
            }
            return null;
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "PDF-Dateien|*.pdf",
                Title = "PDF öffnen (mehrere = zusammenfügen)",
                Multiselect = true
            };
            if (dlg.ShowDialog(this) == true) _ = OpenFilesAsync(dlg.FileNames, -1);
        }

        private void AppendDialog(int at)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PDF-Dateien|*.pdf", Title = "PDFs anhängen", Multiselect = true };
            if (dlg.ShowDialog(this) == true) _ = OpenFilesAsync(dlg.FileNames, at);
        }

        private static List<string> DroppedPdfs(DragEventArgs e) =>
            e.Data.GetData(DataFormats.FileDrop) is string[] files
                ? files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<string>();

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            var pdfs = DroppedPdfs(e);
            if (pdfs.Count == 0) return;
            _ = OpenFilesAsync(pdfs, _pages.Count == 0 ? -1 : _pages.Count);
            e.Handled = true;
        }

        private void AppendZone_Click(object sender, MouseButtonEventArgs e)
        {
            if (_pages.Count == 0) Open_Click(null, null);
            else AppendDialog(_pages.Count);
        }

        private void AppendZone_Drop(object sender, DragEventArgs e)
        {
            var pdfs = DroppedPdfs(e);
            if (pdfs.Count > 0) _ = OpenFilesAsync(pdfs, _pages.Count == 0 ? -1 : _pages.Count);
            e.Handled = true;
        }

        private void Thumb_Drop(object sender, DragEventArgs e)
        {
            var pdfs = DroppedPdfs(e);
            if (pdfs.Count == 0) return;
            int at = _pages.Count;
            var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (lbi?.DataContext is PageItem target)
            {
                // obere Hälfte → davor, untere Hälfte → dahinter
                var pos = e.GetPosition(lbi);
                at = _pages.IndexOf(target) + (pos.Y < lbi.ActualHeight / 2 ? 0 : 1);
            }
            _ = OpenFilesAsync(pdfs, _pages.Count == 0 ? -1 : at);
            e.Handled = true;
        }

        private void UpdateUi()
        {
            bool has = _pages.Count > 0;
            EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            BtnSave.IsEnabled = BtnSaveAs.IsEnabled = has;
            ToolPill.IsEnabled = has;
            MenuSplit.IsEnabled = MenuOcr.IsEnabled = MenuCompress.IsEnabled = MenuProtect.IsEnabled = has;
            if (!has) { PageCountText.Text = ""; FileName.Text = ""; Title = "KANTApdf"; }
            else UpdateTitle();
        }

        private void UpdateTitle()
        {
            string name = _savePath != null ? Path.GetFileName(_savePath) : "Neues Dokument";
            FileName.Text = name + (_dirty ? "  ·  geändert" : "");
            Title = $"{(_dirty ? "● " : "")}{name} – KANTApdf";
        }

        private void SetDirty(bool dirty)
        {
            _dirty = dirty;
            DirtyDot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            UpdateTitle();
        }

        private bool ConfirmDiscard()
        {
            if (!_dirty || _pages.Count == 0) return true;
            return MessageBox.Show(this, "Die Änderungen am aktuellen Dokument sind noch nicht gespeichert und gehen verloren. Trotzdem fortfahren?",
                "KANTApdf", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        // ═════════════════════════ Seiten verwalten ═════════════════════════

        private void AfterStructureChange()
        {
            for (int i = 0; i < _pages.Count; i++) _pages[i].Number = i + 1;
            _maxPageWidth = _pages.Count == 0 ? 600 : _pages.Max(p => p.BaseWidth);
            PageCountText.Text = _pages.Count == 0 ? "" : $"{_pages.Count} Seite{(_pages.Count == 1 ? "" : "n")}";
            ApplyThumbWidths();
            RefreshNotes();
            UpdateUi();
            Kick();
        }

        private void PushUndo()
        {
            _undo.Push(_pages.ToList());
            if (_undo.Count > 30)
            {
                var keep = _undo.Take(30).Reverse().ToList();
                _undo.Clear();
                foreach (var k in keep) _undo.Push(k);
            }
        }

        private void Undo()
        {
            if (_undo.Count == 0) return;
            var snap = _undo.Pop();
            ClearSelection();
            _pages.Clear();
            foreach (var p in snap) { _pages.Add(p); p.ApplyZoom(_zoom); }
            SetDirty(true);
            AfterStructureChange();
            ShowToast("Rückgängig gemacht.", null, null);
        }

        private void DeletePages(IList<PageItem> items)
        {
            items = items.Distinct().ToList();
            if (items.Count == 0) return;
            if (items.Count >= _pages.Count)
            {
                MessageBox.Show(this, "Mindestens eine Seite muss im Dokument bleiben.", "KANTApdf",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            PushUndo();
            if (items.Contains(_selPage)) ClearSelection();
            foreach (var p in items) _pages.Remove(p);
            SetDirty(true);
            AfterStructureChange();
            ShowToast(items.Count == 1 ? "Seite gelöscht." : $"{items.Count} Seiten gelöscht.", "Rückgängig", Undo);
        }

        private void ThumbDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is PageItem page)) return;
            var sel = ThumbList.SelectedItems.Cast<PageItem>().ToList();
            DeletePages(sel.Count > 1 && sel.Contains(page) ? sel : new List<PageItem> { page });
            e.Handled = true;
        }

        private void Thumb_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                DeletePages(ThumbList.SelectedItems.Cast<PageItem>().ToList());
                e.Handled = true;
            }
        }

        private void Thumb_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingThumbs) return;
            if (ThumbList.SelectedItems.Count == 1 && ThumbList.SelectedItem is PageItem p) ScrollToPage(p, 0);
        }

        private void Thumb_Clicked(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return; // Papierkorb
            if (lbi?.DataContext is PageItem p) ScrollToPage(p, 0);
        }

        private void ScrollToPage(PageItem page, double innerOffset)
        {
            if (_scroll == null) return;
            int idx = _pages.IndexOf(page);
            if (idx < 0) return;
            double y = 8;
            for (int i = 0; i < idx; i++) y += _pages[i].Height + 16;
            _scroll.ScrollToVerticalOffset(Math.Max(0, y + innerOffset));
        }

        // ═════════════════════════ Seitenleiste ═════════════════════════

        private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        {
            bool visible = Sidebar.Visibility == Visibility.Visible;
            if (visible)
            {
                _sideWidth = SideCol.ActualWidth;
                Sidebar.Visibility = Visibility.Collapsed;
                Splitter.Visibility = Visibility.Collapsed;
                SideCol.Width = new GridLength(0);
            }
            else
            {
                Sidebar.Visibility = Visibility.Visible;
                Splitter.Visibility = Visibility.Visible;
                SideCol.Width = new GridLength(Math.Max(140, _sideWidth));
            }
            Kick();
        }

        private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (SideCol.ActualWidth < 110)
            {
                SidebarToggle_Click(null, null); // zu schmal gezogen → einklappen
                return;
            }
            _sideWidth = SideCol.ActualWidth;
            Settings.SaveSidebarWidth(_sideWidth);
            ApplyThumbWidths();
            Kick();
        }

        private void ApplyThumbWidths()
        {
            double w = Math.Max(60, Sidebar.ActualWidth - 58);
            foreach (var p in _pages) p.ApplyThumbWidth(w);
        }

        private void SideTab_Checked(object sender, RoutedEventArgs e)
        {
            if (ThumbList == null || NotesList == null) return;
            bool pages = TabPages.IsChecked == true;
            ThumbList.Visibility = pages ? Visibility.Visible : Visibility.Collapsed;
            AppendZone.Visibility = pages ? Visibility.Visible : Visibility.Collapsed;
            NotesList.Visibility = pages ? Visibility.Collapsed : Visibility.Visible;
            NotesEmpty.Visibility = !pages && _notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Kick();
        }

        private void RefreshNotes()
        {
            _notes.Clear();
            foreach (var p in _pages)
                foreach (var n in p.Notes)
                {
                    n.PageLabel = $"Seite {p.Number}";
                    _notes.Add(n);
                }
            NotesCount.Text = _notes.Count > 0 ? $"  {_notes.Count}" : "";
            if (NotesEmpty != null)
                NotesEmpty.Visibility = TabNotes.IsChecked == true && _notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void NotesList_Clicked(object sender, MouseButtonEventArgs e)
        {
            var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (lbi?.DataContext is NoteItem n)
                ScrollToPage(n.Page, Math.Max(0, n.Top - 140));
        }

        // ═════════════════════════ Darstellung ═════════════════════════

        private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

        private int TargetPx(PageItem p) => (int)Math.Min(4200, Math.Round(p.Width * DpiScale));
        private int ThumbPx(PageItem p) => (int)Math.Min(1200, Math.Ceiling(p.ThumbW * DpiScale / 40.0) * 40);

        private void UpdateVisible()
        {
            if (_pages.Count == 0) return;
            var gen = PageList.ItemContainerGenerator;
            bool thumbsVisible = Sidebar.Visibility == Visibility.Visible && ThumbList.Visibility == Visibility.Visible;
            var tgen = ThumbList.ItemContainerGenerator;

            foreach (var p in _pages)
            {
                if (gen.ContainerFromItem(p) != null)
                {
                    int px = TargetPx(p);
                    bool needs = p.Image == null || p.RenderedMode != _mode || Math.Abs(p.RenderedPx - px) > px * 0.06;
                    if (needs && !p.Queued) { p.Queued = true; _queue.Enqueue(p); }
                    if (p.Words == null && !p.WordsLoading) _ = EnsureWordsAsync(p);
                }
                else if (p.Image != null)
                {
                    p.Image = null;
                    p.RenderedPx = 0;
                }

                if (thumbsVisible && tgen.ContainerFromItem(p) != null)
                {
                    int tpx = ThumbPx(p);
                    bool needs = p.Thumb == null || p.ThumbMode != _mode || p.ThumbPx < tpx;
                    if (needs && !p.ThumbQueued) { p.ThumbQueued = true; _thumbQueue.Enqueue(p); }
                }
                else if (p.Thumb != null && !thumbsVisible)
                {
                    // Leiste zu → Vorschauen behalten wir (klein); nichts tun
                }
            }
            if (!_rendering) _ = PumpAsync();
        }

        private async Task PumpAsync()
        {
            _rendering = true;
            try
            {
                while (_queue.Count > 0 || _thumbQueue.Count > 0)
                {
                    bool isThumb = _queue.Count == 0;
                    var p = isThumb ? _thumbQueue.Dequeue() : _queue.Dequeue();
                    if (isThumb) p.ThumbQueued = false; else p.Queued = false;
                    if (!_pages.Contains(p)) continue;
                    if (!isThumb && PageList.ItemContainerGenerator.ContainerFromItem(p) == null) continue;

                    var mode = _mode;
                    int px = isThumb ? ThumbPx(p) : TargetPx(p);
                    try
                    {
                        var bmp = await p.Source.View.RenderAsync(p.SourceIndex, px);
                        var shown = mode == ReadMode.Papier ? bmp : await Task.Run(() => Tint.Apply(bmp, mode));
                        if (isThumb) { p.Thumb = shown; p.ThumbPx = px; p.ThumbMode = mode; }
                        else { p.Image = shown; p.RenderedPx = px; p.RenderedMode = mode; }
                    }
                    catch
                    {
                        // Seite nicht darstellbar → Platzhalter bleibt
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
            if (_pages.Count == 0 || _scroll == null) return;
            int current = 0;
            var gen = PageList.ItemContainerGenerator;
            for (int i = 0; i < _pages.Count; i++)
            {
                if (!(gen.ContainerFromItem(_pages[i]) is FrameworkElement c)) continue;
                try
                {
                    var y = c.TransformToAncestor(_scroll).Transform(new Point(0, 0)).Y;
                    if (y + c.ActualHeight > _scroll.ViewportHeight * 0.35) { current = i; break; }
                }
                catch { }
            }
            _currentIndex = current;
            PageCountText.Text = $"Seite {current + 1} von {_pages.Count}";

            // Vorschau mitführen (nur wenn nicht gerade mehrere Seiten ausgewählt sind)
            if (ThumbList.SelectedItems.Count <= 1 && ThumbList.SelectedItem != _pages[current])
            {
                _syncingThumbs = true;
                ThumbList.SelectedItem = _pages[current];
                ThumbList.ScrollIntoView(_pages[current]);
                _syncingThumbs = false;
            }
        }

        // ═════════════════════════ Zoom & Ansicht ═════════════════════════

        private void SetZoom(double z)
        {
            _zoom = Math.Max(0.2, Math.Min(5.0, z));
            foreach (var p in _pages) p.ApplyZoom(_zoom);
            ZoomText.Content = $"{Math.Round(_zoom * 100)} %";
            Kick();
        }

        private void FitWidth()
        {
            double avail = (_scroll != null && _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth - SideCol.ActualWidth) - 64;
            double fit = avail / _maxPageWidth;
            double comfy = 1100 / _maxPageWidth;
            SetZoom(Math.Min(fit, comfy));
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.15);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.15);
        private void Fit_Click(object sender, RoutedEventArgs e) => FitWidth();

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_pages.Count == 0 || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
            e.Handled = true;
        }

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            _mode = _mode == ReadMode.Papier ? ReadMode.Sepia : _mode == ReadMode.Sepia ? ReadMode.Nacht : ReadMode.Papier;
            Theme.Apply(_mode);
            Settings.SaveMode(_mode);
            ModeText.Text = _mode.ToString();
            Kick();
        }

        // ═════════════════════════ Tastatur ═════════════════════════

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool inText = Keyboard.FocusedElement is TextBox;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (e.Key == Key.Escape && !inText)
            {
                ClearSelection();
                if (_tool != Tool.Text) SelectTool(Tool.Text);
                e.Handled = true;
                return;
            }
            if (!ctrl) return;

            switch (e.Key)
            {
                case Key.O: Open_Click(null, null); e.Handled = true; break;
                case Key.S:
                    if (_pages.Count > 0) { if (shift) SaveAs_Click(null, null); else Save_Click(null, null); }
                    e.Handled = true; break;
                case Key.C:
                    if (!inText && _selPage != null) { CopySelection(); e.Handled = true; }
                    break;
                case Key.A:
                    if (!inText && _pages.Count > 0) { _ = SelectAllOnPageAsync(_pages[_currentIndex]); e.Handled = true; }
                    break;
                case Key.Z:
                    if (!inText) { Undo(); e.Handled = true; }
                    break;
                case Key.OemPlus: case Key.Add: if (_pages.Count > 0) SetZoom(_zoom * 1.15); e.Handled = true; break;
                case Key.OemMinus: case Key.Subtract: if (_pages.Count > 0) SetZoom(_zoom / 1.15); e.Handled = true; break;
                case Key.D0: case Key.NumPad0: if (_pages.Count > 0) FitWidth(); e.Handled = true; break;
            }
        }

        // ═════════════════════════ Werkzeuge (Text / Marker / Notiz / Schwärzen) ═════════════════════════

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (ModeBar == null) return; // während InitializeComponent
            var t = ToolMarker.IsChecked == true ? Tool.Marker
                  : ToolNote.IsChecked == true ? Tool.Note
                  : ToolRedact.IsChecked == true ? Tool.Redact
                  : Tool.Text;
            ApplyTool(t);
        }

        private void SelectTool(Tool t)
        {
            switch (t)
            {
                case Tool.Marker: ToolMarker.IsChecked = true; break;
                case Tool.Note: ToolNote.IsChecked = true; break;
                case Tool.Redact: ToolRedact.IsChecked = true; break;
                default: ToolText.IsChecked = true; break;
            }
            ApplyTool(t);
        }

        private void ApplyTool(Tool t)
        {
            _tool = t;
            ClearSelection();
            PageList.Cursor = t == Tool.Text || t == Tool.Marker ? Cursors.IBeam : Cursors.Cross;
            RedactActions.Visibility = t == Tool.Redact ? Visibility.Visible : Visibility.Collapsed;
            switch (t)
            {
                case Tool.Marker:
                    ModeTitle.Text = "Marker:"; ModeHint.Text = "Text mit gedrückter Maustaste überstreichen. Rechtsklick auf eine Markierung entfernt sie.";
                    break;
                case Tool.Note:
                    ModeTitle.Text = "Notiz:"; ModeHint.Text = "Auf die gewünschte Stelle klicken und lostippen. Esc beendet.";
                    break;
                case Tool.Redact:
                    ModeTitle.Text = "Schwärzen:"; ModeHint.Text = "Bereiche aufziehen. Klick auf einen Balken entfernt ihn. Beim Speichern werden die Seiten endgültig bereinigt.";
                    break;
            }
            ModeBar.Visibility = t == Tool.Text ? Visibility.Collapsed : Visibility.Visible;
            UpdateRedactCount();
        }

        private static Point Norm(Grid g, Point p) =>
            new Point(Math.Max(0, Math.Min(1, p.X / g.ActualWidth)), Math.Max(0, Math.Min(1, p.Y / g.ActualHeight)));

        private void Page_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Grid grid) || !(grid.DataContext is PageItem page)) return;
            var p = e.GetPosition(grid);
            var np = Norm(grid, p);

            switch (_tool)
            {
                case Tool.Text:
                case Tool.Marker:
                    if (e.ClickCount == 2) { SelectWordAt(page, np); e.Handled = true; return; }
                    ClearSelection();
                    _dragGrid = grid; _dragPage = page; _dragStart = p; _dragActive = false;
                    grid.CaptureMouse();
                    if (page.Words == null) _ = EnsureWordsAsync(page);
                    e.Handled = true;
                    break;

                case Tool.Note:
                    if ((DateTime.Now - _popupClosedAt).TotalMilliseconds < 250) return; // Klick hat nur den Editor geschlossen
                    var note = new NoteItem(page, np.X, np.Y);
                    page.Notes.Add(note);
                    RefreshNotes();
                    OpenNoteEditor(note, grid, new Rect(p.X, p.Y, 26, 26), isNew: true);
                    e.Handled = true;
                    break;

                case Tool.Redact:
                    _dragGrid = grid; _dragPage = page; _dragStart = p;
                    _dragBox = new RectItem(page, np.X, np.Y, 0, 0);
                    page.Redactions.Add(_dragBox);
                    grid.CaptureMouse();
                    e.Handled = true;
                    break;
            }
        }

        private void Page_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragGrid == null || _dragPage == null) return;
            var p = e.GetPosition(_dragGrid);

            if (_tool == Tool.Redact && _dragBox != null)
            {
                var a = Norm(_dragGrid, _dragStart);
                var b = Norm(_dragGrid, p);
                _dragBox.Set(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                return;
            }

            if (_tool == Tool.Text || _tool == Tool.Marker)
            {
                if (!_dragActive && (p - _dragStart).Length < 4) return;
                var words = _dragPage.Words;
                if (words == null || words.Count == 0) return;
                if (!_dragActive)
                {
                    _dragActive = true;
                    _selPage = _dragPage;
                    _selA = TextLayer.HitTest(words, Norm(_dragGrid, _dragStart));
                }
                _selB = TextLayer.HitTest(words, Norm(_dragGrid, p));
                DrawSelection();
            }
        }

        private void Page_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragGrid == null) return;
            _dragGrid.ReleaseMouseCapture();
            var page = _dragPage;

            if (_tool == Tool.Redact && _dragBox != null)
            {
                if (_dragBox.W < 4 || _dragBox.H < 4) page.Redactions.Remove(_dragBox);
                else SetDirty(true);
                _dragBox = null;
                UpdateRedactCount();
            }
            else if (_tool == Tool.Text || _tool == Tool.Marker)
            {
                if (!_dragActive && page != null && page.Words != null && page.Words.Count == 0 && !_noTextHintShown)
                {
                    _noTextHintShown = true;
                    ShowToast("Diese Seite enthält keinen Text (Scan). Mit OCR wird sie markier- und kopierbar.", "OCR starten", () => Ocr_Click(null, null));
                }
                if (_tool == Tool.Marker && _selPage != null) AddHighlightFromSelection();
            }

            _dragGrid = null;
            _dragPage = null;
            _dragActive = false;
            e.Handled = true;
        }

        private async Task EnsureWordsAsync(PageItem page)
        {
            if (page.Words != null || page.WordsLoading) return;
            page.WordsLoading = true;
            try { page.Words = await TextLayer.LoadAsync(page); }
            catch { page.Words = new List<WordBox>(); }
            finally { page.WordsLoading = false; }
        }

        private void DrawSelection()
        {
            if (_selPage == null) return;
            _selPage.Selection.Clear();
            foreach (var (x, y, w, h) in TextLayer.LineRects(SelectedWords()))
                _selPage.Selection.Add(new RectItem(_selPage, x, y - h * 0.08, w, h * 1.16));
        }

        private List<WordBox> SelectedWords()
        {
            if (_selPage?.Words == null || _selA < 0 || _selB < 0) return new List<WordBox>();
            int a = Math.Min(_selA, _selB), b = Math.Max(_selA, _selB);
            return _selPage.Words.GetRange(a, b - a + 1);
        }

        private void ClearSelection()
        {
            _selPage?.Selection.Clear();
            _selPage = null;
            _selA = _selB = -1;
        }

        private void SelectWordAt(PageItem page, Point np)
        {
            if (page.Words == null || page.Words.Count == 0) return;
            ClearSelection();
            int i = TextLayer.HitTest(page.Words, np);
            if (i < 0) return;
            _selPage = page; _selA = _selB = i;
            DrawSelection();
            if (_tool == Tool.Marker) AddHighlightFromSelection();
        }

        private async Task SelectAllOnPageAsync(PageItem page)
        {
            await EnsureWordsAsync(page);
            if (page.Words == null || page.Words.Count == 0) return;
            ClearSelection();
            _selPage = page; _selA = 0; _selB = page.Words.Count - 1;
            DrawSelection();
        }

        private void CopySelection()
        {
            var text = TextLayer.JoinText(SelectedWords());
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
                ShowToast($"Kopiert: {text.Length} Zeichen in der Zwischenablage.", null, null);
            }
            catch (Exception ex) { ShowError("Kopieren hat nicht geklappt.", ex); }
        }

        private void AddHighlightFromSelection()
        {
            var words = SelectedWords();
            if (words.Count == 0 || _selPage == null) return;
            var h = new HighlightItem(_selPage) { Text = TextLayer.JoinText(words) };
            foreach (var (x, y, w, hh) in TextLayer.LineRects(words))
                h.Rects.Add(new RectItem(_selPage, x, y - hh * 0.05, w, hh * 1.1));
            _selPage.Highlights.Add(h);
            ClearSelection();
            SetDirty(true);
        }

        private void Page_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Grid grid) || !(grid.DataContext is PageItem page)) return;
            var p = e.GetPosition(grid);
            var np = Norm(grid, p);
            var menu = new ContextMenu { PlacementTarget = grid };

            bool hasSel = _selPage == page && SelectedWords().Count > 0;
            if (hasSel)
            {
                menu.Items.Add(MenuItemFor("Kopieren", "Strg+C", CopySelection));
                menu.Items.Add(MenuItemFor("Mit Marker hervorheben", null, AddHighlightFromSelection));
                menu.Items.Add(new Separator());
            }

            var hl = page.Highlights.FirstOrDefault(h => h.Rects.Any(r =>
                np.X >= r.NX && np.X <= r.NX + r.NW && np.Y >= r.NY && np.Y <= r.NY + r.NH));
            if (hl != null)
            {
                menu.Items.Add(MenuItemFor("Markierung entfernen", null, () => { page.Highlights.Remove(hl); SetDirty(true); }));
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(MenuItemFor("Notiz hier anheften", null, () =>
            {
                var note = new NoteItem(page, np.X, np.Y);
                page.Notes.Add(note);
                RefreshNotes();
                OpenNoteEditor(note, grid, new Rect(p.X, p.Y, 26, 26), isNew: true);
            }));
            menu.Items.Add(MenuItemFor("Ganze Seite markieren", "Strg+A", () => _ = SelectAllOnPageAsync(page)));
            menu.IsOpen = true;
            e.Handled = true;
        }

        private static MenuItem MenuItemFor(string header, string gesture, Action action)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            mi.Click += (s, e) => action();
            return mi;
        }

        // ───────── Notizen ─────────

        private void Note_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is NoteItem note)) return;
            OpenNoteEditor(note, fe, Rect.Empty, isNew: false);
            e.Handled = true;
        }

        private void OpenNoteEditor(NoteItem note, UIElement target, Rect placement, bool isNew)
        {
            CloseNotePopup();
            _activeNote = note;
            _activeNoteOriginal = isNew ? null : note.Text;
            NoteMeta.Text = $"Seite {note.Page.Number} · {note.Meta}";
            NoteText.Text = note.Text;
            NotePopup.PlacementTarget = target;
            NotePopup.PlacementRectangle = placement;
            NotePopup.IsOpen = true;
            Dispatcher.BeginInvoke(new Action(() => { NoteText.Focus(); NoteText.CaretIndex = NoteText.Text.Length; }),
                DispatcherPriority.Input);
        }

        private void CloseNotePopup()
        {
            if (NotePopup.IsOpen) NotePopup.IsOpen = false;
        }

        private void NotePopup_Closed(object sender, EventArgs e)
        {
            _popupClosedAt = DateTime.Now;
            var note = _activeNote;
            _activeNote = null;
            if (note == null) return;

            var text = NoteText.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                note.Page.Notes.Remove(note);
                if (_activeNoteOriginal != null) SetDirty(true);
            }
            else if (text != _activeNoteOriginal)
            {
                note.Text = text;
                SetDirty(true);
            }
            RefreshNotes();
        }

        private void NoteDelete_Click(object sender, RoutedEventArgs e)
        {
            NoteText.Text = "";
            CloseNotePopup();
        }

        // ───────── Schwärzen ─────────

        private int RedactionTotal => _pages.Sum(p => p.Redactions.Count);

        private void UpdateRedactCount()
        {
            if (_tool != Tool.Redact) { ModeCount.Text = ""; return; }
            int n = RedactionTotal;
            int pages = _pages.Count(p => p.Redactions.Count > 0);
            ModeCount.Text = n == 0 ? "" : $"  ·  {n} Balken auf {pages} Seite{(pages == 1 ? "" : "n")}";
            BtnRedactApply.IsEnabled = n > 0;
        }

        private void Redaction_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_tool != Tool.Redact || !(sender is FrameworkElement fe) || !(fe.DataContext is RectItem box)) return;
            box.Page.Redactions.Remove(box);
            UpdateRedactCount();
            e.Handled = true;
        }

        private void RedactClear_Click(object sender, RoutedEventArgs e)
        {
            foreach (var p in _pages) p.Redactions.Clear();
            UpdateRedactCount();
        }

        private void RedactApply_Click(object sender, RoutedEventArgs e)
        {
            if (RedactionTotal == 0) return;
            _ = SaveAsFlowAsync("_geschwärzt");
        }

        // ═════════════════════════ Speichern ═════════════════════════

        private bool NeedsSaveAs =>
            _savePath == null ||
            RedactionTotal > 0 ||
            _pages.Any(p => !string.IsNullOrEmpty(p.Source.Password));

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_pages.Count == 0) return;
            if (NeedsSaveAs) _ = SaveAsFlowAsync(RedactionTotal > 0 ? "_geschwärzt" : null);
            else _ = SaveToAsync(_savePath);
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_pages.Count == 0) return;
            _ = SaveAsFlowAsync(null);
        }

        private async Task<bool> SaveAsFlowAsync(string suffix)
        {
            string baseName = _savePath != null ? Path.GetFileNameWithoutExtension(_savePath) : "Zusammengefügt";
            string dir = _savePath != null ? Path.GetDirectoryName(_savePath) : Path.GetDirectoryName(_pages[0].Source.Path);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF-Dateien|*.pdf",
                FileName = baseName + (suffix ?? "") + ".pdf",
                InitialDirectory = dir,
                Title = suffix == "_geschwärzt" ? "Geschwärzte PDF speichern" : "Speichern unter"
            };
            if (dlg.ShowDialog(this) != true) return false;

            if (RedactionTotal > 0 && _pages.Any(p => string.Equals(Path.GetFullPath(p.Source.Path), Path.GetFullPath(dlg.FileName), StringComparison.OrdinalIgnoreCase)))
            {
                if (MessageBox.Show(this, "Das Original wird mit der geschwärzten Fassung überschrieben. Eine Sicherung gibt es dann nicht. Fortfahren?",
                        "KANTApdf", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return false;
            }
            return await SaveToAsync(dlg.FileName);
        }

        private async Task<bool> SaveToAsync(string path)
        {
            CloseNotePopup();
            bool burned = RedactionTotal > 0;
            IsEnabled = false;
            var progress = new Progress<string>(t => ShowToast(t, null, null));
            try
            {
                await DocumentWriter.SaveAsync(_pages.ToList(), path, progress);
                _savePath = path;
                SetDirty(false);
                IsEnabled = true;

                if (burned)
                {
                    // Geschwärzte Fassung neu laden: so sieht man, was wirklich in der Datei steht
                    await OpenFilesAsync(new[] { path }, -1);
                    ShowToast($"Geschwärzt gespeichert: {Path.GetFileName(path)}", null, null, path);
                }
                else
                {
                    ShowToast($"Gespeichert: {Path.GetFileName(path)}", null, null, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                IsEnabled = true;
                ShowError("Speichern hat nicht geklappt.", ex);
                return false;
            }
        }

        /// <summary>Werkzeuge arbeiten auf einer gespeicherten Datei. Gibt (Pfad, Kennwort) oder null zurück.</summary>
        private async Task<(string path, string password)?> EnsureSavedAsync(string toolName)
        {
            if (_pages.Count == 0) return null;
            var sources = _pages.Select(p => p.Source).Distinct().ToList();
            bool untouched = !_dirty && _savePath != null && sources.Count == 1 &&
                             string.Equals(sources[0].Path, _savePath, StringComparison.OrdinalIgnoreCase) &&
                             _pages.Count == sources[0].View.PageCount;
            if (untouched) return (_savePath, sources[0].Password);
            if (!_dirty && _savePath != null && File.Exists(_savePath)) return (_savePath, null);

            var r = MessageBox.Show(this, $"Für „{toolName}“ muss das Dokument zuerst gespeichert werden. Jetzt speichern?",
                "KANTApdf", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return null;
            bool ok = NeedsSaveAs ? await SaveAsFlowAsync(null) : await SaveToAsync(_savePath);
            return ok ? (_savePath, (string)null) : ((string, string)?)null;
        }

        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!_dirty || _pages.Count == 0) return;
            var r = MessageBox.Show(this, "Änderungen speichern, bevor KANTApdf geschlossen wird?", "KANTApdf",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.No) return;
            e.Cancel = true;
            if (r == MessageBoxResult.Cancel) return;
            bool ok = NeedsSaveAs ? await SaveAsFlowAsync(null) : await SaveToAsync(_savePath);
            if (ok) { _dirty = false; Close(); }
        }

        // ═════════════════════════ Werkzeuge-Menü ═════════════════════════

        private void CloseToolsMenu() => ToolsToggle.IsChecked = false;

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            CloseToolsMenu();
            if (_pages.Count == 0) Open_Click(null, null);
            else AppendDialog(_pages.Count);
        }

        private void Setup_Click(object sender, RoutedEventArgs e)
        {
            CloseToolsMenu();
            AppSetup.Ask(this);
        }

        private async void Ocr_Click(object sender, RoutedEventArgs e)
        {
            CloseToolsMenu();
            var f = await EnsureSavedAsync("OCR");
            if (f == null) return;
            var dlg = new OcrWindow(f.Value.path, f.Value.password) { Owner = this };
            if (dlg.ShowDialog() == true)
                ShowToast(dlg.Info, "Öffnen", () => _ = OpenFilesAsync(new[] { dlg.ResultPath }, -1), dlg.ResultPath);
        }

        private async void Split_Click(object sender, RoutedEventArgs e)
        {
            CloseToolsMenu();
            var f = await EnsureSavedAsync("Teilen");
            if (f == null) return;
            var dlg = new SplitWindow(f.Value.path, f.Value.password, _pages.Count) { Owner = this };
            if (dlg.ShowDialog() == true) ShowToast(dlg.Info, null, null, dlg.ResultPath);
        }

        private async void Compress_Click(object sender, RoutedEventArgs e)
        {
            CloseToolsMenu();
            var f = await EnsureSavedAsync("Verkleinern");
            if (f == null) return;
            var dlg = new CompressWindow(f.Value.path, f.Value.password) { Owner = this };
            if (dlg.ShowDialog() == true)
                ShowToast(dlg.Info, "Öffnen", () => _ = OpenFilesAsync(new[] { dlg.ResultPath }, -1), dlg.ResultPath);
        }

        private async void Protect_Click(object sender, RoutedEventArgs e)
        {
            CloseToolsMenu();
            var f = await EnsureSavedAsync("Kennwort");
            if (f == null) return;
            var dlg = new ProtectWindow(f.Value.path, f.Value.password) { Owner = this };
            if (dlg.ShowDialog() == true) ShowToast(dlg.Info, null, null, dlg.ResultPath);
        }

        // ═════════════════════════ Meldungen ═════════════════════════

        private void ShowToast(string text, string actionLabel, Action action, string path = null)
        {
            ToastText.Text = text;
            _toastAction = action;
            _toastPath = path;
            ToastAction.Content = actionLabel;
            ToastAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
            ToastFolder.Visibility = path != null ? Visibility.Visible : Visibility.Collapsed;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void ToastAction_Click(object sender, RoutedEventArgs e)
        {
            Toast.Visibility = Visibility.Collapsed;
            var a = _toastAction;
            _toastAction = null;
            a?.Invoke();
        }

        private void ToastFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_toastPath)) return;
            try
            {
                if (File.Exists(_toastPath)) Process.Start("explorer.exe", $"/select,\"{_toastPath}\"");
                else if (Directory.Exists(_toastPath)) Process.Start("explorer.exe", $"\"{_toastPath}\"");
            }
            catch { }
        }

        private void ToastClose_Click(object sender, RoutedEventArgs e) => Toast.Visibility = Visibility.Collapsed;

        private void ShowError(string message, Exception ex) =>
            MessageBox.Show(this, message + "\n\n" + ex.Message, "KANTApdf", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
