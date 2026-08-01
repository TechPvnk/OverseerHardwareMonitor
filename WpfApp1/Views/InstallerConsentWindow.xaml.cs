using System.Windows;

namespace Overseer.Views;

public partial class InstallerConsentWindow : Window
{
    public InstallerConsentWindow()
    {
        InitializeComponent();
        InstallButton.Click += (s, e) => { DialogResult = true; Close(); };
        CancelButton.Click += (s, e) => { DialogResult = false; Close(); };
    }

    public void SetMessage(string message)
    {
        MessageText.Text = message;
    }

    public void SetChecksumStatus(string status)
    {
        ChecksumStatus.Text = status;
    }
}
