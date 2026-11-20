using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace KantaPdf.Services
{
    /// <summary>
    /// Einrichtung ohne Installer und ohne Admin-Rechte:
    /// EXE nach %LOCALAPPDATA%\Programs\KANTApdf kopieren, Desktop- und Startmenü-Verknüpfung,
    /// Eintrag unter „Öffnen mit“ für PDFs.
    /// </summary>
    public static class AppSetup
    {
        private static string InstallDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "KANTApdf");

        public static string InstalledExe => Path.Combine(InstallDir, "KANTApdf.exe");

        private static string FlagFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KANTApdf", "eingerichtet.txt");

        private static string DesktopLink =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KANTApdf.lnk");

        private static string StartMenuLink =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "KANTApdf.lnk");

        /// <summary>Beim allerersten Start einmal nachfragen.</summary>
        public static void FirstStart(Window owner)
        {
            try
            {
                if (File.Exists(FlagFile)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(FlagFile));
                File.WriteAllText(FlagFile, DateTime.Now.ToString("s"));
            }
            catch { return; }

            Ask(owner);
        }

        /// <summary>Einrichtung anbieten (auch über das Werkzeuge-Menü erreichbar).</summary>
        public static void Ask(Window owner)
        {
            var r = MessageBox.Show(owner,
                "KANTApdf auf diesem PC einrichten?\n\n" +
                "•  Verknüpfung auf dem Desktop\n" +
                "•  Eintrag im Startmenü\n" +
                "•  bei PDFs unter „Öffnen mit“ auswählbar\n\n" +
                "Dafür wird die App in deinen Benutzerordner kopiert – nur für dich, ohne Admin-Rechte.",
                "KANTApdf einrichten", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            try
            {
                var exe = Install();
                MessageBox.Show(owner,
                    "Fertig! KANTApdf liegt jetzt auf dem Desktop und im Startmenü.\n\n" +
                    "Als Standard für PDFs: Rechtsklick auf eine PDF → Öffnen mit → KANTApdf → „Immer“.\n\n" +
                    (IsSamePath(Environment.ProcessPath, exe) ? "" : "Die heruntergeladene EXE wird nicht mehr gebraucht und darf gelöscht werden."),
                    "KANTApdf", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Einrichten hat nicht geklappt:\n\n" + ex.Message, "KANTApdf",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static bool IsSamePath(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        private static string Install()
        {
            var current = Environment.ProcessPath;
            Directory.CreateDirectory(InstallDir);
            var target = InstalledExe;
            if (!IsSamePath(current, target))
                File.Copy(current, target, true);

            CreateShortcut(DesktopLink, target);
            Directory.CreateDirectory(Path.GetDirectoryName(StartMenuLink));
            CreateShortcut(StartMenuLink, target);
            RegisterOpenWith(target);
            return target;
        }

        private static void CreateShortcut(string linkPath, string exe)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell")
                       ?? throw new InvalidOperationException("Windows Script Host ist nicht verfügbar.");
            dynamic shell = Activator.CreateInstance(type);
            try
            {
                dynamic link = shell.CreateShortcut(linkPath);
                link.TargetPath = exe;
                link.WorkingDirectory = Path.GetDirectoryName(exe);
                link.IconLocation = exe + ",0";
                link.Description = "KANTApdf – ruhig lesen, schnell erledigen";
                link.Save();
                Marshal.FinalReleaseComObject(link);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void RegisterOpenWith(string exe)
        {
            const string progId = "KANTApdf.Document";
            using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
            {
                k.SetValue("", "PDF-Dokument (KANTApdf)");
                using (var icon = k.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
                using (var cmd = k.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{exe}\" \"%1\"");
            }
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
                k.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\KANTApdf.exe"))
            {
                k.SetValue("FriendlyAppName", "KANTApdf");
                using (var cmd = k.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{exe}\" \"%1\"");
                using (var st = k.CreateSubKey("SupportedTypes")) st.SetValue(".pdf", "");
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // Explorer über neue Zuordnung informieren
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
