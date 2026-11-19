using System.Windows;

namespace KantaPdf.Dialogs
{
    public partial class PasswordPrompt : Window
    {
        public string Password { get; private set; }

        public PasswordPrompt(string fileName, bool wrongBefore)
        {
            InitializeComponent();
            Sub.Text = $"„{fileName}“ ist geschützt. Bitte Kennwort eingeben.";
            if (wrongBefore)
            {
                Status.Text = "Das Kennwort stimmt leider nicht – bitte nochmal versuchen.";
                Status.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
            }
            Loaded += (s, e) => Pw.Focus();
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(Pw.Password)) { Pw.Focus(); return; }
            Password = Pw.Password;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
