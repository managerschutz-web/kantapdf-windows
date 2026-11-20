using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace KantaPdf.Services
{
    public enum ReadMode { Papier, Sepia, Nacht }

    /// <summary>Setzt die Farben der Oberfläche passend zur Leseansicht.</summary>
    public static class Theme
    {
        public static void Apply(ReadMode mode)
        {
            switch (mode)
            {
                case ReadMode.Sepia:
                    Set("Bg", "#E8DFCB"); Set("Bar", "#F3ECDC"); Set("Ink", "#3A3122");
                    Set("Muted", "#8A7D64"); Set("Hover", "#E4D9C0"); Set("Line", "#D9CCB0");
                    Set("Field", "#FAF5EA"); Set("PagePlaceholder", "#F4ECD8"); Set("Side", "#EEE6D3"); Set("Pill", "#E1D6BE");
                    Set("Accent", "#3C6E12"); Set("AccentHover", "#4C8619"); Set("AccentSoft", "#E2E5C8");
                    break;
                case ReadMode.Nacht:
                    Set("Bg", "#16181A"); Set("Bar", "#1E2124"); Set("Ink", "#DCE0D6");
                    Set("Muted", "#8C9387"); Set("Hover", "#2A2E31"); Set("Line", "#2F3336");
                    Set("Field", "#24282B"); Set("PagePlaceholder", "#25282A"); Set("Side", "#1A1D1F"); Set("Pill", "#2A2E31");
                    Set("Accent", "#7CC24A"); Set("AccentHover", "#8FD25C"); Set("AccentSoft", "#26331C");
                    break;
                default:
                    Set("Bg", "#EEF0EA"); Set("Bar", "#FAFBF8"); Set("Ink", "#1F2A17");
                    Set("Muted", "#7A8473"); Set("Hover", "#E3E8DC"); Set("Line", "#DCE1D4");
                    Set("Field", "#FFFFFF"); Set("PagePlaceholder", "#FFFFFF"); Set("Side", "#F4F6F1"); Set("Pill", "#E9ECE4");
                    Set("Accent", "#3C6E12"); Set("AccentHover", "#4C8619"); Set("AccentSoft", "#E4EED8");
                    break;
            }
        }

        private static void Set(string key, string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            Application.Current.Resources[key] = new SolidColorBrush(c);
        }
    }

    /// <summary>Merkt sich die gewählte Ansicht (Papier/Sepia/Nacht).</summary>
    public static class Settings
    {
        private static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KANTApdf", "ansicht.txt");

        public static ReadMode LoadMode()
        {
            try
            {
                if (File.Exists(FilePath) && Enum.TryParse(File.ReadAllText(FilePath).Trim(), out ReadMode m))
                    return m;
            }
            catch { }
            return ReadMode.Papier;
        }

        private static string WidthPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KANTApdf", "leiste.txt");

        public static double LoadSidebarWidth()
        {
            try
            {
                if (File.Exists(WidthPath) && double.TryParse(File.ReadAllText(WidthPath).Trim(),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w))
                    return Math.Max(120, Math.Min(560, w));
            }
            catch { }
            return 210;
        }

        public static void SaveSidebarWidth(double w)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(WidthPath));
                File.WriteAllText(WidthPath, w.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { }
        }

        public static void SaveMode(ReadMode m)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, m.ToString());
            }
            catch { }
        }
    }
}
