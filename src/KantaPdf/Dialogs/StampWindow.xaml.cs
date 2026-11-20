using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KantaPdf.Services;

namespace KantaPdf.Dialogs
{
    public partial class StampWindow : Window
    {
        private static readonly string[] Palette = { "#3C6E12", "#2F7D32", "#1565C0", "#C62828", "#6A1B9A", "#37474F" };
        private string _color = "#C62828";
        private List<StampDef> _own;

        public byte[] ResultPng { get; private set; }

        public StampWindow()
        {
            InitializeComponent();
            _own = Library.OwnStamps();
            BuildColors();
            BuildList();
            UpdatePreview();
        }

        private void BuildColors()
        {
            foreach (var hex in Palette)
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var rb = new RadioButton
                {
                    GroupName = "stampcolor",
                    IsChecked = hex == _color,
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = hex
                };
                rb.Template = BuildSwatchTemplate(c);
                rb.Checked += (s, e) => { _color = hex; UpdatePreview(); };
                ColorPanel.Children.Add(rb);
            }
        }

        private static ControlTemplate BuildSwatchTemplate(Color c)
        {
            var t = new ControlTemplate(typeof(RadioButton));
            var ring = new FrameworkElementFactory(typeof(Border));
            ring.Name = "ring";
            ring.SetValue(Border.WidthProperty, 26.0);
            ring.SetValue(Border.HeightProperty, 26.0);
            ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
            ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            ring.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            var dot = new FrameworkElementFactory(typeof(Ellipse));
            dot.SetValue(Ellipse.WidthProperty, 18.0);
            dot.SetValue(Ellipse.HeightProperty, 18.0);
            dot.SetValue(Ellipse.FillProperty, new SolidColorBrush(c));
            ring.AppendChild(dot);
            t.VisualTree = ring;
            var trig = new Trigger { Property = RadioButton.IsCheckedProperty, Value = true };
            trig.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(c), "ring"));
            t.Triggers.Add(trig);
            return t;
        }

        private StampDef CurrentDef() => new StampDef
        {
            Text1 = Line1.Text.Trim(),
            Text2 = Line2.Text.Trim(),
            Color = _color,
            WithDate = WithDate.IsChecked == true
        };

        private void Def_Changed(object sender, RoutedEventArgs e)
        {
            if (Preview == null) return;
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (Preview == null || Line1 == null) return;
            var d = CurrentDef();
            if (string.IsNullOrWhiteSpace(d.Text1)) { Preview.Source = null; return; }
            try { Preview.Source = Library.LoadPng(Library.RenderStamp(d, DateTime.Now)); } catch { Preview.Source = null; }
        }

        private void BuildList()
        {
            StampPanel.Children.Clear();
            foreach (var d in Library.BuiltInStamps()) AddTile(d, own: false);
            foreach (var d in _own) AddTile(d, own: true);
        }

        private void AddTile(StampDef d, bool own)
        {
            byte[] png;
            try { png = Library.RenderStamp(d, DateTime.Now); } catch { return; }

            var stack = new StackPanel();
            stack.Children.Add(new Image { Source = Library.LoadPng(png), Height = 46, MaxWidth = 170, Stretch = Stretch.Uniform });
            if (own)
                stack.Children.Add(new TextBlock { Text = "eigener", FontSize = 10.5, Foreground = (Brush)FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });

            var btn = new Button
            {
                Content = stack,
                Padding = new Thickness(12, 10, 12, 8),
                Margin = new Thickness(0, 0, 10, 10),
                Background = Brushes.White,
                ToolTip = "Diesen Stempel verwenden"
            };
            btn.Click += (s, e) =>
            {
                ResultPng = Library.RenderStamp(d, DateTime.Now);
                DialogResult = true;
            };
            if (own)
            {
                var menu = new ContextMenu();
                var del = new MenuItem { Header = "Löschen" };
                del.Click += (s, e) =>
                {
                    _own.RemoveAll(x => x.Id == d.Id);
                    Library.SaveOwnStamps(_own);
                    if (!string.IsNullOrEmpty(d.ImageFile)) { try { File.Delete(d.ImageFile); } catch { } }
                    BuildList();
                };
                menu.Items.Add(del);
                btn.ContextMenu = menu;
            }
            StampPanel.Children.Add(btn);
        }

        private void SaveOwn_Click(object sender, RoutedEventArgs e)
        {
            var d = CurrentDef();
            if (string.IsNullOrWhiteSpace(d.Text1)) { Status.Text = "Bitte einen Text eingeben."; return; }
            _own.Add(d);
            Library.SaveOwnStamps(_own);
            BuildList();
            Status.Text = $"„{d.Text1}“ gespeichert – oben anklicken, um ihn zu verwenden.";
        }

        private void LoadImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Title = "Bild als Stempel laden" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var file = Library.ImportStampImage(dlg.FileName);
                _own.Add(new StampDef { Text1 = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), ImageFile = file });
                Library.SaveOwnStamps(_own);
                BuildList();
                Status.Text = "Bild-Stempel gespeichert – oben anklicken, um ihn zu verwenden.";
            }
            catch (Exception ex) { Status.Text = "Bild ließ sich nicht verwenden: " + ex.Message; }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
