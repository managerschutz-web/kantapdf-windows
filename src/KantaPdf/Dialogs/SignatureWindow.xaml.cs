using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using KantaPdf.Services;

namespace KantaPdf.Dialogs
{
    public partial class SignatureWindow : Window
    {
        public byte[] ResultPng { get; private set; }

        public SignatureWindow()
        {
            InitializeComponent();
            Ink.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = Color.FromRgb(0x1A, 0x2B, 0x6B),
                Width = 2.6,
                Height = 2.6,
                FitToCurve = true,
                IgnorePressure = false
            };
            Ink.StrokeCollected += (s, e) => InkHint.Visibility = Visibility.Collapsed;
            LoadSaved();
        }

        private void LoadSaved()
        {
            SavedPanel.Children.Clear();
            var list = Library.Signatures();
            SavedBlock.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var file in list)
            {
                byte[] png;
                try { png = File.ReadAllBytes(file); } catch { continue; }

                var img = new Image { Source = Library.LoadPng(png), Height = 56, MaxWidth = 170, Stretch = Stretch.Uniform };
                var del = new Button
                {
                    Style = (Style)FindResource("IconChip"),
                    Content = "",
                    Width = 24, Height = 24, FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -8, -8, 0),
                    ToolTip = "Unterschrift löschen"
                };
                del.Click += (s, e) =>
                {
                    e.Handled = true;
                    if (MessageBox.Show(this, "Diese Unterschrift löschen?", "KANTApdf", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        try { File.Delete(file); } catch { }
                        LoadSaved();
                    }
                };
                var grid = new Grid();
                grid.Children.Add(img);
                grid.Children.Add(del);
                var btn = new Button
                {
                    Content = grid,
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 0, 10, 10),
                    Background = Brushes.White,
                    BorderBrush = (Brush)FindResource("Line"),
                    ToolTip = "Diese Unterschrift verwenden"
                };
                btn.Click += (s, e) => { ResultPng = png; DialogResult = true; };
                SavedPanel.Children.Add(btn);
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            Ink.Strokes.Clear();
            InkHint.Visibility = Visibility.Visible;
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff", Title = "Unterschrift als Bild laden" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var png = Library.ImageToTransparentPng(dlg.FileName, removeWhite: true);
                if (Remember.IsChecked == true) Library.SaveSignature(png);
                ResultPng = png;
                DialogResult = true;
            }
            catch (Exception ex) { Status.Text = "Bild ließ sich nicht verwenden: " + ex.Message; }
        }

        private void Use_Click(object sender, RoutedEventArgs e)
        {
            if (Ink.Strokes.Count == 0) { Status.Text = "Bitte zuerst im Feld unterschreiben – oder eine gespeicherte wählen."; return; }
            var png = Library.StrokesToPng(Ink.Strokes);
            if (Remember.IsChecked == true) Library.SaveSignature(png);
            ResultPng = png;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
