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
        private enum Tool { Text, Marker, Note, Redact, Write }

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

        // Unterschrift/Stempel platzieren
        private byte[] _pendingPng;
        private string _pendingKind;
        private Tool _toolBeforePlace;

        // Seiten umsortieren
        private Point _thumbDownPos;
        private PageItem _thumbDownItem;

        // Vorschlag für „Speichern unter“, wenn das Dokument (noch) keinen Pfad hat
        private string _suggestName, _suggestDir;

        // Marker-Farbe
        private static readonly (string name, Color color)[] MarkerPalette =
        {
            ("Gelb", Color.FromRgb(0xFF, 0xD8, 0x4A)),
            ("Grün", Color.FromRgb(0x7C, 0xE0, 0x5A)),
            ("Blau", Color.FromRgb(0x5A, 0xB8, 0xFF)),
            ("Pink", Color.FromRgb(0xFF, 0x7E, 0xC8)),
            ("Orange", Color.FromRgb(0xFF, 0xA0, 0x3C)),
        };
        private Color _markerColor = Color.FromRgb(0xFF, 0xD8, 0x4A);

        // Suche
        private readonly List<RectItem> _hits = new List<RectItem>();
        private int _hitIndex = -1;
        private string _lastQuery;
        private int _searchGen;

        // Schreiben: zuletzt benutzter Stil
        private double _lastTextPt = 11;
        private Color _lastTextColor = Color.FromRgb(0x11, 0x11, 0x11);
        private bool _lastTextBold;

        // Fokusmodus
        private bool _focus;
        private WindowState _prevState;
        private Visibility _prevSidebar;

        // Startbildschirm
        private readonly ObservableCollection<RecentItem> _recent = new ObservableCollection<RecentItem>();

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
                if (_scroll != null) _scroll.ScrollChanged += (s2, e2) =>
                {
                    if (NotePopup.IsOpen && Math.Abs(e2.VerticalChange) + Math.Abs(e2.HorizontalChange) > 0.5) CloseNotePopup();
                    Kick();
                };
                _thumbScroll = FindChild<ScrollViewer>(ThumbList);
                if (_thumbScroll != null) _thumbScroll.ScrollChanged += (s2, e2) => Kick();
            };
            SizeChanged += (s, e) => { CloseNotePopup(); Kick(); };
            Deactivated += (s, e) => CloseNotePopup();
            LocationChanged += (s, e) => CloseNotePopup();
            Sidebar.SizeChanged += (s, e) => { ApplyThumbWidths(); Kick(); };

            RecentList.ItemsSource = _recent;
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = v != null ? $"KANTApdf {v.Major}.{v.Minor}.{v.Build}" : "KANTApdf";
            BuildMarkerPalette();
            UpdateUi();
            _ = LoadRecentAsync();
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
        public async Task OpenFilesAsync(IList<string> paths, int insertAt)
        {
            var files = paths.Where(p => File.Exists(p) && Converter.IsSupported(p)).ToList();
            var skipped = paths.Count(p => File.Exists(p) && !Converter.IsSupported(p));
            if (files.Count == 0)
            {
                if (skipped > 0) ShowToast("Dieser Dateityp wird nicht unterstützt.", null, null);
                return;
            }

            bool newDoc = insertAt < 0 || _pages.Count == 0;
            if (newDoc && !ConfirmDiscard()) return;

            var sources = new List<Source>();
            bool converted = false;
            foreach (var p in files)
            {
                string pdf = p;
                if (!Converter.IsPdf(p))
                {
                    ShowToast($"Wandle „{Path.GetFileName(p)}“ in PDF um …", null, null);
                    Cursor = Cursors.Wait;
                    try { pdf = await Converter.ToPdfAsync(p); converted = true; }
                    catch (Exception ex)
                    {
                        Cursor = null;
                        ShowError($"„{Path.GetFileName(p)}“ konnte nicht in PDF umgewandelt werden.", ex);
                        continue;
                    }
                    Cursor = null;
                }
                var src = await OpenSourceAsync(pdf);
                if (src != null) sources.Add(src);
            }
            if (sources.Count == 0) return;
            Toast.Visibility = Visibility.Collapsed;

            var newPages = sources.SelectMany(s => Enumerable.Range(0, s.View.PageCount).Select(i => new PageItem(s, i))).ToList();

            if (newDoc)
            {
                CloseNotePopup();
                ClearSelection();
                _queue.Clear(); _thumbQueue.Clear(); _undo.Clear();
                _pages.Clear();
                foreach (var pg in newPages) _pages.Add(pg);

                _suggestName = Path.GetFileNameWithoutExtension(files[0]);
                _suggestDir = Path.GetDirectoryName(files[0]);
                if (sources.Count == 1 && !converted)
                {
                    _savePath = sources[0].Path;
                    SetDirty(false);
                    Recent.Add(_savePath);
                }
                else if (sources.Count == 1)
                {
                    _savePath = null;
                    SetDirty(true);
                    ShowToast($"„{Path.GetFileName(files[0])}“ wurde in PDF umgewandelt – jetzt speichern.",
                        "Speichern unter …", () => SaveAs_Click(null, null));
                }
                else
                {
                    _savePath = null;
                    _suggestName = "Zusammengefügt";
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
            await AttachFieldsAsync(sources, newPages);
        }

        /// <summary>Formularfelder der neuen Seiten lesen und als Eingabefelder einblenden.</summary>
        private async Task AttachFieldsAsync(List<Source> sources, List<PageItem> pages)
        {
            int total = 0;
            foreach (var src in sources)
            {
                var defs = await FormReader.ReadAsync(src);
                if (defs.Count == 0) continue;
                foreach (var pg in pages.Where(p => p.Source == src))
                {
                    foreach (var d in defs.Where(d => d.PageIndex == pg.SourceIndex))
                    {
                        pg.Fields.Add(new FieldItem(pg, d));
                        total++;
                    }
                }
            }
            if (total > 0)
                ShowToast($"Ausfüllbares Formular erkannt ({total} Felder) – einfach reinklicken und tippen. Tab springt zum nächsten Feld.", null, null);
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
                Filter = Converter.OpenFilter,
                Title = "Öffnen – PDF, Word, Excel, PowerPoint, Bilder (mehrere = zusammenfügen)",
                Multiselect = true
            };
            if (dlg.ShowDialog(this) == true) _ = OpenFilesAsync(dlg.FileNames, -1);
        }

        private void AppendDialog(int at)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Converter.OpenFilter, Title = "Dateien anhängen", Multiselect = true };
            if (dlg.ShowDialog(this) == true) _ = OpenFilesAsync(dlg.FileNames, at);
        }

        private static List<string> DroppedPdfs(DragEventArgs e) =>
            e.Data.GetData(DataFormats.FileDrop) is string[] files
                ? files.Where(Converter.IsSupported).ToList()
                : new List<string>();

        private const string PageDragFormat = "KantaPages";

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
                      : e.Data.GetDataPresent(PageDragFormat) && sender == ThumbList ? DragDropEffects.Move
                      : DragDropEffects.None;
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
            int at = _pages.Count;
            var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (lbi?.DataContext is PageItem target)
            {
                // obere Hälfte → davor, untere Hälfte → dahinter
                var pos = e.GetPosition(lbi);
                at = _pages.IndexOf(target) + (pos.Y < lbi.ActualHeight / 2 ? 0 : 1);
            }

            if (e.Data.GetData(PageDragFormat) is List<PageItem> moving)
            {
                MovePages(moving, at);
                e.Handled = true;
                return;
            }

            var pdfs = DroppedPdfs(e);
            if (pdfs.Count == 0) return;
            _ = OpenFilesAsync(pdfs, _pages.Count == 0 ? -1 : at);
            e.Handled = true;
        }

        private void MovePages(List<PageItem> moving, int at)
        {
            moving = moving.Where(_pages.Contains).OrderBy(_pages.IndexOf).ToList();
            if (moving.Count == 0) return;
            int before = moving.Count(m => _pages.IndexOf(m) < at);
            var order = _pages.ToList();
            order.RemoveAll(moving.Contains);
            int target = Math.Max(0, Math.Min(order.Count, at - before));
            order.InsertRange(target, moving);
            if (order.SequenceEqual(_pages)) return;

            PushUndo();
            _syncingThumbs = true;
            _pages.Clear();
            foreach (var p in order) _pages.Add(p);
            ThumbList.SelectedItems.Clear();
            foreach (var m in moving) ThumbList.SelectedItems.Add(m);
            _syncingThumbs = false;
            SetDirty(true);
            AfterStructureChange();
            ShowToast(moving.Count == 1 ? $"Seite verschoben (jetzt Seite {moving[0].Number})." : $"{moving.Count} Seiten verschoben.", "Rückgängig", Undo);
        }

        private void Thumb_PreviewDown(object sender, MouseButtonEventArgs e)
        {
            _thumbDownItem = null;
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;
            var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (lbi?.DataContext is PageItem p)
            {
                _thumbDownItem = p;
                _thumbDownPos = e.GetPosition(ThumbList);
            }
        }

        private void Thumb_PreviewMove(object sender, MouseEventArgs e)
        {
            if (_thumbDownItem == null || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(ThumbList);
            if (Math.Abs(pos.Y - _thumbDownPos.Y) < 10 && Math.Abs(pos.X - _thumbDownPos.X) < 10) return;

            var sel = ThumbList.SelectedItems.Cast<PageItem>().ToList();
            var moving = sel.Contains(_thumbDownItem) && sel.Count > 1 ? sel : new List<PageItem> { _thumbDownItem };
            _thumbDownItem = null;
            var data = new DataObject(PageDragFormat, moving);
            DragDrop.DoDragDrop(ThumbList, data, DragDropEffects.Move);
        }

        private void UpdateUi()
        {
            bool has = _pages.Count > 0;
            EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            BtnSave.IsEnabled = BtnSaveAs.IsEnabled = has;
            ToolPill.IsEnabled = has;
            MenuSplit.IsEnabled = MenuOcr.IsEnabled = MenuCompress.IsEnabled = MenuProtect.IsEnabled = has;
            BtnPrint.IsEnabled = BtnSearch.IsEnabled = has;
            FileChip.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has) { PageCountText.Text = ""; FileName.Text = ""; Title = "KANTApdf"; }
            else UpdateTitle();
        }

        private void UpdateTitle()
        {
            string name = _savePath != null ? Path.GetFileName(_savePath) : "Neues Dokument";
            FileName.Text = name + (_dirty ? "  ·  geändert" : "");
            FileName.ToolTip = _savePath ?? name;
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

        /// <summary>Seite so rendern, wie sie angezeigt wird (inkl. Nutzer-Drehung). widthPx = Anzeigebreite.</summary>
        private static async Task<System.Windows.Media.Imaging.BitmapSource> RenderDisplayAsync(PageItem p, int widthPx)
        {
            int srcPx = p.ExtraRotation % 180 == 0
                ? widthPx
                : (int)Math.Round(widthPx * p.SourceSize.Width / p.SourceSize.Height);
            var bmp = await p.Source.View.RenderAsync(p.SourceIndex, Math.Max(16, srcPx));
            if (p.ExtraRotation == 0) return bmp;
            var rot = new System.Windows.Media.Imaging.TransformedBitmap(bmp, new RotateTransform(p.ExtraRotation));
            rot.Freeze();
            return rot;
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
                        var bmp = await RenderDisplayAsync(p, px);
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

            if (e.Key == Key.F11) { Focus_Click(null, null); e.Handled = true; return; }
            if (e.Key == Key.Escape && Keyboard.FocusedElement is FrameworkElement fe0 && (fe0.DataContext is TextItem || fe0.DataContext is FieldItem))
            {
                Keyboard.ClearFocus();
                PageList.Focus();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F3 && _pages.Count > 0)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) SearchPrev_Click(null, null); else SearchNext_Click(null, null);
                e.Handled = true; return;
            }
            if (e.Key == Key.Escape && _focus && !inText) { Focus_Click(null, null); e.Handled = true; return; }
            if (e.Key == Key.Escape && SearchBar.Visibility == Visibility.Visible) { SearchClose_Click(null, null); e.Handled = true; return; }
            if (e.Key == Key.Escape && _pendingPng != null)
            {
                EndPlacement();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Delete && !inText && !ThumbList.IsKeyboardFocusWithin && SelectedPlaced() is PlacedItem sp)
            {
                sp.Page.Placed.Remove(sp);
                SetDirty(true);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && !inText)
            {
                DeselectPlaced();
                ClearSelection();
                if (_tool != Tool.Text) SelectTool(Tool.Text);
                e.Handled = true;
                return;
            }
            if (!ctrl) return;

            switch (e.Key)
            {
                case Key.O: Open_Click(null, null); e.Handled = true; break;
                case Key.F: if (_pages.Count > 0) { Search_Click(null, null); e.Handled = true; } break;
                case Key.P: if (_pages.Count > 0) { Print_Click(null, null); e.Handled = true; } break;
                case Key.W: if (_pages.Count > 0) { CloseDoc_Click(null, null); e.Handled = true; } break;
                case Key.R: if (_pages.Count > 0 && !inText) { RotatePages(new List<PageItem> { _pages[_currentIndex] }, shift ? -90 : 90); e.Handled = true; } break;
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
                  : ToolWrite.IsChecked == true ? Tool.Write
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
                case Tool.Write: ToolWrite.IsChecked = true; break;
                default: ToolText.IsChecked = true; break;
            }
            ApplyTool(t);
        }

        private void ApplyTool(Tool t)
        {
            _tool = t;
            _pendingPng = null;
            _pendingKind = null;
            ClearSelection();
            PageList.Cursor = t == Tool.Text || t == Tool.Marker || t == Tool.Write ? Cursors.IBeam : Cursors.Cross;
            RedactActions.Visibility = t == Tool.Redact ? Visibility.Visible : Visibility.Collapsed;
            switch (t)
            {
                case Tool.Marker:
                    ModeTitle.Text = "Marker:"; ModeHint.Text = "Text mit gedrückter Maustaste überstreichen. Rechtsklick auf eine Markierung entfernt sie.";
                    break;
                case Tool.Note:
                    ModeTitle.Text = "Notiz:"; ModeHint.Text = "Auf die gewünschte Stelle klicken und lostippen. Esc beendet.";
                    break;
                case Tool.Write:
                    ModeTitle.Text = "Schreiben:"; ModeHint.Text = "Auf die Stelle klicken und lostippen. Größe, Farbe und Fett in der kleinen Leiste darüber. Wird beim Speichern fest in die Seite geschrieben.";
                    break;
                case Tool.Redact:
                    ModeTitle.Text = "Schwärzen:"; ModeHint.Text = "Bereiche aufziehen. Klick auf einen Balken entfernt ihn. Beim Speichern werden die Seiten endgültig bereinigt.";
                    break;
            }
            ModeBar.Visibility = t == Tool.Text ? Visibility.Collapsed : Visibility.Visible;
            MarkerColors.Visibility = t == Tool.Marker ? Visibility.Visible : Visibility.Collapsed;
            UpdateRedactCount();
        }

        private static Point Norm(Grid g, Point p) =>
            new Point(Math.Max(0, Math.Min(1, p.X / g.ActualWidth)), Math.Max(0, Math.Min(1, p.Y / g.ActualHeight)));

        private void Page_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Grid grid) || !(grid.DataContext is PageItem page)) return;
            var p = e.GetPosition(grid);
            var np = Norm(grid, p);

            // Ein offener Notiz-Editor wird mit dem Klick nur geschlossen
            if (NotePopup.IsOpen)
            {
                CloseNotePopup();
                e.Handled = true;
                return;
            }

            DeselectPlaced();

            if (_pendingPng != null)
            {
                PlacePending(page, np);
                e.Handled = true;
                return;
            }

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
                    if ((DateTime.Now - _popupClosedAt).TotalMilliseconds < 300) return; // Klick hat nur den Editor geschlossen
                    var note = new NoteItem(page, np.X, np.Y);
                    page.Notes.Add(note);
                    RefreshNotes();
                    OpenNoteEditor(note, grid, new Rect(p.X, p.Y, 26, 26), isNew: true);
                    e.Handled = true;
                    break;

                case Tool.Write:
                    if (Keyboard.FocusedElement is TextBox ftb && ftb.DataContext is TextItem)
                    {
                        // erster Klick daneben beendet nur das Schreiben
                        Keyboard.ClearFocus();
                        PageList.Focus();
                        e.Handled = true;
                        return;
                    }
                    if (page.SourceRotation != 0 || page.ExtraRotation != 0)
                    {
                        ShowToast("Diese Seite ist gedreht – dort kann noch kein Text eingefügt werden.", null, null);
                        return;
                    }
                    double off = 8.0 / Math.Max(1, grid.ActualHeight);
                    var ti = new TextItem(page, np.X - 3.0 / Math.Max(1, grid.ActualWidth), Math.Max(0, np.Y - off)) { FocusOnLoad = true, FontPt = _lastTextPt, Color = _lastTextColor, Bold = _lastTextBold };
                    page.Texts.Add(ti);
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
            try { page.SetWords(await TextLayer.LoadAsync(page)); }
            catch { page.SetWords(new List<WordBox>()); }
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
            h.SetColor(_markerColor);
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
                var colorMenu = new MenuItem { Header = "Farbe ändern" };
                foreach (var (name, color) in MarkerPalette)
                {
                    var c = color;
                    var mi = MenuItemFor(name, null, () => { hl.SetColor(c); SetDirty(true); });
                    mi.Icon = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(c) };
                    colorMenu.Items.Add(mi);
                }
                menu.Items.Add(colorMenu);
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

        private void NoteDone_Click(object sender, RoutedEventArgs e) => CloseNotePopup();

        private void NoteText_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0))
            {
                CloseNotePopup();
                e.Handled = true;
            }
        }

        // ───────── Unterschrift & Stempel ─────────

        private void Sign_Click(object sender, RoutedEventArgs e)
        {
            if (_pages.Count == 0) return;
            var dlg = new SignatureWindow { Owner = this };
            if (dlg.ShowDialog() == true && dlg.ResultPng != null) BeginPlacement(dlg.ResultPng, "Unterschrift");
        }

        private void Stamp_Click(object sender, RoutedEventArgs e)
        {
            if (_pages.Count == 0) return;
            var dlg = new StampWindow { Owner = this };
            if (dlg.ShowDialog() == true && dlg.ResultPng != null) BeginPlacement(dlg.ResultPng, "Stempel");
        }

        private void BeginPlacement(byte[] png, string kind)
        {
            _toolBeforePlace = _tool;
            ClearSelection();
            _pendingPng = png;
            _pendingKind = kind;
            PageList.Cursor = Cursors.Cross;
            RedactActions.Visibility = Visibility.Collapsed;
            MarkerColors.Visibility = Visibility.Collapsed;
            ModeTitle.Text = kind + ":";
            ModeHint.Text = "Auf die Stelle klicken, an der sie stehen soll. Danach verschieben oder an der grünen Ecke größer ziehen. Esc bricht ab.";
            ModeCount.Text = "";
            ModeBar.Visibility = Visibility.Visible;
        }

        private void EndPlacement()
        {
            _pendingPng = null;
            _pendingKind = null;
            ApplyTool(_toolBeforePlace);
        }

        private void PlacePending(PageItem page, Point np)
        {
            if (page.SourceRotation != 0 || page.ExtraRotation != 0)
            {
                ShowToast("Diese Seite ist im PDF gedreht – dort kann noch nicht unterschrieben/gestempelt werden.", null, null);
                return;
            }
            double nw = _pendingKind == "Unterschrift" ? 0.28 : 0.24;
            var item = new PlacedItem(page, _pendingPng, _pendingKind, 0, 0, nw);
            item.NX = np.X - nw / 2;
            item.NY = np.Y - item.NH / 2;
            item.Move(0, 0);
            item.IsSelected = true;
            page.Placed.Add(item);
            SetDirty(true);
            EndPlacement();
        }

        private void DeselectPlaced()
        {
            foreach (var p in _pages)
                foreach (var it in p.Placed)
                    if (it.IsSelected) it.IsSelected = false;
        }

        private PlacedItem SelectedPlaced() =>
            _pages.SelectMany(p => p.Placed).FirstOrDefault(i => i.IsSelected);

        private void Placed_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is PlacedItem item)) return;
            if (NotePopup.IsOpen) CloseNotePopup();
            if (!item.IsSelected) { DeselectPlaced(); item.IsSelected = true; }
        }

        private void Placed_Move(object sender, DragDeltaEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is PlacedItem item) || item.Page.Width <= 0) return;
            item.Move(e.HorizontalChange / item.Page.Width, e.VerticalChange / item.Page.Height);
        }

        private void Placed_Resize(object sender, DragDeltaEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is PlacedItem item) || item.Page.Width <= 0) return;
            item.Resize((item.W + e.HorizontalChange) / item.Page.Width);
        }

        private void Placed_DragCompleted(object sender, DragCompletedEventArgs e) => SetDirty(true);

        private void Placed_Delete(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is PlacedItem item)) return;
            item.Page.Placed.Remove(item);
            SetDirty(true);
            e.Handled = true;
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
            string baseName = _savePath != null ? Path.GetFileNameWithoutExtension(_savePath) : (_suggestName ?? "Dokument");
            string dir = _savePath != null ? Path.GetDirectoryName(_savePath) : (_suggestDir ?? Path.GetDirectoryName(_pages[0].Source.Path));
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
                Recent.Add(path);
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

        // ═════════════════════════ Dokument schließen ═════════════════════════

        private async void CloseDoc_Click(object sender, RoutedEventArgs e)
        {
            if (_pages.Count == 0) return;
            if (_dirty)
            {
                var r = MessageBox.Show(this, "Änderungen speichern, bevor das Dokument geschlossen wird?", "KANTApdf",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes)
                {
                    bool ok = NeedsSaveAs ? await SaveAsFlowAsync(null) : await SaveToAsync(_savePath);
                    if (!ok) return;
                }
            }
            CloseDocument();
        }

        private void CloseDocument()
        {
            CloseNotePopup();
            ClearSelection();
            SearchClose_Click(null, null);
            if (_focus) Focus_Click(null, null);
            _queue.Clear(); _thumbQueue.Clear(); _undo.Clear();
            _pages.Clear();
            _savePath = null;
            _suggestName = _suggestDir = null;
            SetDirty(false);
            SelectTool(Tool.Text);
            AfterStructureChange();
            UpdateUi();
            _ = LoadRecentAsync();
        }

        // ═════════════════════════ Startbildschirm ═════════════════════════

        private async Task LoadRecentAsync()
        {
            _recent.Clear();
            foreach (var path in Recent.Load().Where(File.Exists).Take(7))
                _recent.Add(new RecentItem(path));
            RecentEmpty.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var r in _recent.ToList())
                r.Thumb = await Recent.ThumbAsync(r.Path, 90);
        }

        private void Recent_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RecentItem r) _ = OpenFilesAsync(new[] { r.Path }, -1);
        }

        private void Recent_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is RecentItem r)) return;
            var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
            menu.Items.Add(MenuItemFor("Öffnen", null, () => _ = OpenFilesAsync(new[] { r.Path }, -1)));
            menu.Items.Add(MenuItemFor("Im Ordner zeigen", null, () =>
            {
                try { Process.Start("explorer.exe", $"/select,\"{r.Path}\""); } catch { }
            }));
            menu.Items.Add(MenuItemFor("Aus der Liste entfernen", null, () => { Recent.Remove(r.Path); _recent.Remove(r); RecentEmpty.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }));
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void DieApp_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://dieapp.info") { UseShellExecute = true }); } catch { }
        }

        private async void Tile_Click(object sender, RoutedEventArgs e)
        {
            var kind = (sender as FrameworkElement)?.Tag as string;
            string filter = kind == "convert"
                ? "Office & Bilder|" + string.Join(";", Converter.ConvertibleExt.Select(x => "*" + x))
                : Converter.OpenFilter;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = filter,
                Multiselect = kind == "merge" || kind == "convert",
                Title = kind switch
                {
                    "merge" => "Dateien zum Zusammenfügen wählen (mehrere markieren)",
                    "convert" => "Datei(en) in PDF umwandeln",
                    "sign" => "Welche Datei soll unterschrieben werden?",
                    "compress" => "Welche Datei soll kleiner werden?",
                    "ocr" => "Welcher Scan soll durchsuchbar werden?",
                    "protect" => "Welche Datei soll ein Kennwort bekommen?",
                    _ => "Öffnen"
                }
            };
            if (dlg.ShowDialog(this) != true) return;
            await OpenFilesAsync(dlg.FileNames, -1);
            if (_pages.Count == 0) return;
            switch (kind)
            {
                case "sign": Sign_Click(null, null); break;
                case "compress": Compress_Click(null, null); break;
                case "ocr": Ocr_Click(null, null); break;
                case "protect": Protect_Click(null, null); break;
            }
        }

        // ═════════════════════════ Marker-Farben ═════════════════════════

        private void BuildMarkerPalette()
        {
            foreach (var (name, color) in MarkerPalette)
            {
                var c = color;
                var dot = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(3, 0, 3, 0),
                    Background = new SolidColorBrush(c), Cursor = Cursors.Hand, ToolTip = name,
                    BorderThickness = new Thickness(c == _markerColor ? 3 : 1.5),
                    BorderBrush = c == _markerColor ? (Brush)FindResource("Ink") : new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                    Tag = c
                };
                dot.MouseLeftButtonUp += (s, e) =>
                {
                    _markerColor = c;
                    foreach (var child in MarkerColors.Children.OfType<Border>())
                    {
                        bool on = (Color)child.Tag == c;
                        child.BorderThickness = new Thickness(on ? 3 : 1.5);
                        child.BorderBrush = on ? (Brush)FindResource("Ink") : new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
                    }
                };
                MarkerColors.Children.Add(dot);
            }
        }

        // ═════════════════════════ Seiten drehen / extrahieren ═════════════════════════

        private void RotatePages(IList<PageItem> pages, int delta)
        {
            var list = pages.Where(p => p != null).Distinct().ToList();
            if (list.Count == 0) return;
            if (list.Any(p => p.Placed.Count > 0 || p.Texts.Count > 0))
            {
                ShowToast("Seiten mit Unterschrift, Stempel oder eingefügtem Text können (noch) nicht gedreht werden.", null, null);
                list = list.Where(p => p.Placed.Count == 0 && p.Texts.Count == 0).ToList();
                if (list.Count == 0) return;
            }
            if (list.Contains(_selPage)) ClearSelection();
            foreach (var p in list)
            {
                p.RotateBy(delta);
                p.ApplyZoom(_zoom);
            }
            SetDirty(true);
            _maxPageWidth = _pages.Max(p => p.BaseWidth);
            ApplyThumbWidths();
            Kick();
        }

        private void ThumbRotate_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is PageItem page)) return;
            var sel = ThumbList.SelectedItems.Cast<PageItem>().ToList();
            RotatePages(sel.Count > 1 && sel.Contains(page) ? sel : new List<PageItem> { page }, 90);
            e.Handled = true;
        }

        private void Thumb_RightClick(object sender, MouseButtonEventArgs e)
        {
            var lbi = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (!(lbi?.DataContext is PageItem page)) return;
            if (!lbi.IsSelected) { ThumbList.SelectedItems.Clear(); lbi.IsSelected = true; }
            var sel = ThumbList.SelectedItems.Cast<PageItem>().OrderBy(_pages.IndexOf).ToList();
            string n = sel.Count == 1 ? "Seite" : $"{sel.Count} Seiten";

            var menu = new ContextMenu { PlacementTarget = lbi };
            menu.Items.Add(MenuItemFor($"{n} nach rechts drehen", "Strg+R", () => RotatePages(sel, 90)));
            menu.Items.Add(MenuItemFor($"{n} nach links drehen", "Strg+Umschalt+R", () => RotatePages(sel, -90)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor($"{n} als eigene PDF speichern …", null, () => _ = ExtractAsync(sel)));
            menu.Items.Add(MenuItemFor("Datei hier einfügen …", null, () => AppendDialog(_pages.IndexOf(sel.Last()) + 1)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor($"{n} löschen", "Entf", () => DeletePages(sel)));
            menu.IsOpen = true;
            e.Handled = true;
        }

        private async Task ExtractAsync(List<PageItem> pages)
        {
            if (pages.Count == 0) return;
            string baseName = _savePath != null ? Path.GetFileNameWithoutExtension(_savePath) : (_suggestName ?? "Dokument");
            string label = pages.Count == 1 ? $"S{pages[0].Number}" : $"S{pages.First().Number}-{pages.Last().Number}";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF-Dateien|*.pdf",
                FileName = $"{baseName}_{label}.pdf",
                InitialDirectory = _savePath != null ? Path.GetDirectoryName(_savePath) : _suggestDir,
                Title = "Ausgewählte Seiten speichern"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                await DocumentWriter.SaveAsync(pages, dlg.FileName, null);
                ShowToast($"{pages.Count} Seite{(pages.Count == 1 ? "" : "n")} gespeichert: {Path.GetFileName(dlg.FileName)}",
                    "Öffnen", () => _ = OpenFilesAsync(new[] { dlg.FileName }, -1), dlg.FileName);
            }
            catch (Exception ex) { ShowError("Speichern hat nicht geklappt.", ex); }
        }

        // ═════════════════════════ Suchen ═════════════════════════

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            if (_pages.Count == 0) return;
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        private void SearchClose_Click(object sender, RoutedEventArgs e)
        {
            SearchBar.Visibility = Visibility.Collapsed;
            foreach (var p in _pages) p.SearchHits.Clear();
            _hits.Clear();
            _hitIndex = -1;
            _lastQuery = null;
            SearchInfo.Text = "";
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // kurze Pause abwarten, dann suchen
            int gen = ++_searchGen;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Delay(280);
                if (gen == _searchGen) await RunSearchAsync(SearchBox.Text);
            }));
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) SearchPrev_Click(null, null); else SearchNext_Click(null, null);
                e.Handled = true;
            }
        }

        private async Task RunSearchAsync(string query)
        {
            query = (query ?? "").Trim();
            if (query == _lastQuery) return;
            _lastQuery = query;
            foreach (var p in _pages) p.SearchHits.Clear();
            _hits.Clear();
            _hitIndex = -1;
            if (query.Length < 2) { SearchInfo.Text = ""; return; }

            int gen = _searchGen;
            SearchInfo.Text = "suche …";
            var q = query.ToLowerInvariant();
            foreach (var page in _pages.ToList())
            {
                await EnsureWordsAsync(page);
                if (gen != _searchGen) return;
                var words = page.Words;
                if (words == null || words.Count == 0) continue;

                // Text der Seite mit Zuordnung Zeichen → Wort
                var sb = new System.Text.StringBuilder();
                var map = new List<int>();
                for (int i = 0; i < words.Count; i++)
                {
                    if (i > 0) { sb.Append(' '); map.Add(i); }
                    foreach (var ch in words[i].Text) { sb.Append(char.ToLowerInvariant(ch)); map.Add(i); }
                }
                var text = sb.ToString();
                int pos = 0;
                while ((pos = text.IndexOf(q, pos, StringComparison.Ordinal)) >= 0)
                {
                    int a = map[pos], b = map[Math.Min(map.Count - 1, pos + q.Length - 1)];
                    foreach (var (x, y, w, h) in TextLayer.LineRects(words.GetRange(a, b - a + 1)))
                    {
                        var r = new RectItem(page, x - 0.002, y - h * 0.1, w + 0.004, h * 1.2);
                        page.SearchHits.Add(r);
                        _hits.Add(r);
                    }
                    pos += q.Length;
                }
            }
            if (_hits.Count == 0) { SearchInfo.Text = "0 Treffer"; return; }
            _hitIndex = -1;
            SearchNext_Click(null, null);
        }

        private void SearchNext_Click(object sender, RoutedEventArgs e) => GoToHit(+1);
        private void SearchPrev_Click(object sender, RoutedEventArgs e) => GoToHit(-1);

        private void GoToHit(int dir)
        {
            if (_hits.Count == 0) return;
            if (_hitIndex >= 0 && _hitIndex < _hits.Count) _hits[_hitIndex].IsCurrent = false;
            _hitIndex = ((_hitIndex + dir) % _hits.Count + _hits.Count) % _hits.Count;
            var hit = _hits[_hitIndex];
            hit.IsCurrent = true;
            SearchInfo.Text = $"{_hitIndex + 1} / {_hits.Count}";
            ScrollToPage(hit.Page, Math.Max(0, hit.Top - 160));
        }

        // ═════════════════════════ Drucken ═════════════════════════

        private async void Print_Click(object sender, RoutedEventArgs e)
        {
            if (_pages.Count == 0) return;
            var dlg = new PrintDialog { UserPageRangeEnabled = true, MinPage = 1, MaxPage = (uint)_pages.Count };
            dlg.PageRange = new PageRange(1, _pages.Count);
            if (dlg.ShowDialog() != true) return;

            int from = 1, to = _pages.Count;
            if (dlg.PageRangeSelection == PageRangeSelection.UserPages)
            {
                from = Math.Max(1, dlg.PageRange.PageFrom);
                to = Math.Min(_pages.Count, dlg.PageRange.PageTo);
            }
            var pages = _pages.Skip(from - 1).Take(to - from + 1).ToList();
            var size = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);

            IsEnabled = false;
            try
            {
                var jpgs = new List<byte[]>();
                int n = 0;
                foreach (var p in pages)
                {
                    n++;
                    ShowToast($"Bereite Druck vor … Seite {n} von {pages.Count}", null, null);
                    jpgs.Add(await RenderForPrintAsync(p));
                }
                ShowToast("Sende an Drucker …", null, null);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                dlg.PrintDocument(new PrintPaginator(jpgs, size), FileName.Text);
                ShowToast($"{pages.Count} Seite{(pages.Count == 1 ? "" : "n")} an den Drucker geschickt.", null, null);
            }
            catch (Exception ex) { ShowError("Drucken hat nicht geklappt.", ex); }
            finally { IsEnabled = true; }
        }

        /// <summary>Seite in Druckqualität inkl. Markern, Schwärzungen, Unterschriften und Stempeln.</summary>
        private async Task<byte[]> RenderForPrintAsync(PageItem p)
        {
            int px = (int)Math.Min(3400, Math.Round(p.BaseWidth / 96.0 * 220));
            var bmp = await RenderDisplayAsync(p, px);
            double w = bmp.PixelWidth, h = bmp.PixelHeight;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(bmp, new Rect(0, 0, w, h));
                foreach (var hl in p.Highlights)
                {
                    var c = hl.Color;
                    var b = new SolidColorBrush(Color.FromArgb(0x70, c.R, c.G, c.B));
                    foreach (var r in hl.Rects) dc.DrawRectangle(b, null, new Rect(r.NX * w, r.NY * h, r.NW * w, r.NH * h));
                }
                foreach (var r in p.Redactions) dc.DrawRectangle(Brushes.Black, null, new Rect(r.NX * w, r.NY * h, r.NW * w, r.NH * h));
                foreach (var pl in p.Placed) dc.DrawImage(pl.Image, new Rect(pl.NX * w, pl.NY * h, pl.NW * w, pl.NH * h));
                double k = w / Math.Max(1, p.BaseWidth); // Pixel pro DIP
                var arial = new Typeface("Arial");
                foreach (var t in p.Texts.Where(x => !string.IsNullOrWhiteSpace(x.Text)))
                {
                    var tf = new Typeface(new FontFamily("Arial"), FontStyles.Normal, t.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                    var ft = new FormattedText(t.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        tf, t.FontPt * 96.0 / 72.0 * k, t.Brush, 1.0);
                    dc.DrawText(ft, new Point(t.NX * w + 3 * k, t.NY * h + 2 * k));
                }
                foreach (var f in p.Fields.Where(x => x.Changed))
                {
                    var rect = new Rect(f.NX * w, f.NY * h, f.NW * w, f.NH * h);
                    if (f.IsCheck)
                    {
                        dc.DrawRectangle(Brushes.White, null, rect);
                        if (f.IsChecked)
                        {
                            var pen = new Pen(Brushes.Black, Math.Max(1, rect.Height * 0.12));
                            dc.DrawLine(pen, new Point(rect.Left + rect.Width * 0.2, rect.Top + rect.Height * 0.55), new Point(rect.Left + rect.Width * 0.42, rect.Bottom - rect.Height * 0.2));
                            dc.DrawLine(pen, new Point(rect.Left + rect.Width * 0.42, rect.Bottom - rect.Height * 0.2), new Point(rect.Right - rect.Width * 0.15, rect.Top + rect.Height * 0.2));
                        }
                    }
                    else
                    {
                        dc.DrawRectangle(Brushes.White, null, rect);
                        double fs = f.IsMultiline ? Math.Min(rect.Height * 0.8, 10 * 96.0 / 72.0 * k) : Math.Min(rect.Height * 0.66, 12 * 96.0 / 72.0 * k);
                        var ft = new FormattedText(f.Value ?? "", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                            arial, Math.Max(4, fs), Brushes.Black, 1.0) { MaxTextWidth = Math.Max(1, rect.Width - 4) };
                        if (!f.IsMultiline) ft.MaxLineCount = 1;
                        dc.DrawText(ft, new Point(rect.Left + 2, f.IsMultiline ? rect.Top + 2 : rect.Top + (rect.Height - ft.Height) / 2));
                    }
                }
            }
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var flat = new System.Windows.Media.Imaging.FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);
            var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 88 };
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(flat));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        // ═════════════════════════ Fokusmodus ═════════════════════════

        private void Focus_Click(object sender, RoutedEventArgs e)
        {
            if (_pages.Count == 0 && !_focus) return;
            _focus = !_focus;
            if (_focus)
            {
                _prevState = WindowState;
                _prevSidebar = Sidebar.Visibility;
                Header.Visibility = Visibility.Collapsed;
                ModeBar.Visibility = Visibility.Collapsed;
                if (Sidebar.Visibility == Visibility.Visible) SidebarToggle_Click(null, null);
                WindowStyle = WindowStyle.None;
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // Taskleiste mit abdecken
                WindowState = WindowState.Maximized;
                ShowToast("Fokusmodus – F11 oder Esc beendet ihn.", null, null);
            }
            else
            {
                Header.Visibility = Visibility.Visible;
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = _prevState;
                if (_prevSidebar == Visibility.Visible && Sidebar.Visibility != Visibility.Visible) SidebarToggle_Click(null, null);
                ApplyTool(_tool);
            }
            Dispatcher.BeginInvoke(new Action(FitWidth), DispatcherPriority.Loaded);
        }

        // ═════════════════════════ Formularfelder ═════════════════════════

        private void Field_Changed(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is FieldItem f && f.Changed && !_dirty) SetDirty(true);
        }

        private void FieldCheck_Checked(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is FieldItem f)) return;
            if (f.IsRadio && f.Group != null)
            {
                // Optionsfeld: alle anderen der Gruppe ausschalten
                foreach (var p in _pages)
                    foreach (var o in p.Fields)
                        if (o != f && o.IsRadio && o.Group == f.Group && o.Page.Source == f.Page.Source && o.IsChecked)
                            o.SetCheckedSilently(false);
            }
            if (f.Changed) SetDirty(true);
        }

        // ═════════════════════════ Text einfügen ═════════════════════════

        private void TextItem_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is TextItem t && t.FocusOnLoad)
            {
                t.FocusOnLoad = false;
                Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); Keyboard.Focus(tb); }), DispatcherPriority.Input);
            }
        }

        private void TextItem_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!(sender is TextBox tb) || !(tb.DataContext is TextItem t)) return;
            // Fokus wandert in die eigene Leiste? Dann nichts tun
            if (e.NewFocus is DependencyObject nf && FindAncestor<TextBox>(nf) == tb) return;
            if (string.IsNullOrWhiteSpace(t.Text)) t.Page.Texts.Remove(t);
        }

        private void TextItem_Changed(object sender, TextChangedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TextItem t && !string.IsNullOrEmpty(t.Text)) SetDirty(true);
        }

        private static TextItem TextItemOf(object sender) => (sender as FrameworkElement)?.DataContext as TextItem;

        private void TextItem_Move(object sender, DragDeltaEventArgs e)
        {
            var t = TextItemOf(sender);
            if (t == null || t.Page.Width <= 0) return;
            t.Move(e.HorizontalChange / t.Page.Width, e.VerticalChange / t.Page.Height);
        }

        private void TextSmaller_Click(object sender, RoutedEventArgs e)
        {
            var t = TextItemOf(sender); if (t == null) return;
            t.FontPt = Math.Round(t.FontPt - (t.FontPt > 14 ? 2 : 1));
            _lastTextPt = t.FontPt; SetDirty(true);
        }

        private void TextBigger_Click(object sender, RoutedEventArgs e)
        {
            var t = TextItemOf(sender); if (t == null) return;
            t.FontPt = Math.Round(t.FontPt + (t.FontPt >= 14 ? 2 : 1));
            _lastTextPt = t.FontPt; SetDirty(true);
        }

        private void TextBold_Click(object sender, RoutedEventArgs e)
        {
            var t = TextItemOf(sender); if (t == null) return;
            t.Bold = !t.Bold;
            _lastTextBold = t.Bold; SetDirty(true);
        }

        private void TextColor_Click(object sender, RoutedEventArgs e)
        {
            var t = TextItemOf(sender); if (t == null) return;
            try { t.Color = (Color)ColorConverter.ConvertFromString((string)((FrameworkElement)sender).Tag); } catch { }
            _lastTextColor = t.Color; SetDirty(true);
        }

        private void TextDelete_Click(object sender, RoutedEventArgs e)
        {
            var t = TextItemOf(sender); if (t == null) return;
            t.Page.Texts.Remove(t);
            SetDirty(true);
        }

        // ═════════════════════════ Meldungen ═════════════════════════

        public void Notify(string text) => ShowToast(text, null, null);

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
