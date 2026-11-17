using System.Windows;
using DesktopSplitter.Services;

namespace DesktopSplitter.Views;

/// <summary>
/// License dialog: shows the machine MAC (the user sends it to the licensor),
/// accepts a pasted license code, and activates it.
/// </summary>
public partial class LicenseWindow : Window
{
    public LicenseWindow()
    {
        InitializeComponent();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        LicenseService.LicenseStatus status = LicenseService.GetStatus();
        StatusText.Text = status.DisplayText;
        MacText.Text = string.IsNullOrEmpty(status.PrimaryMac) ? "未检测到网卡" : status.PrimaryMac;
    }

    private void CopyMac_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(MacText.Text) && MacText.Text != "未检测到网卡")
        {
            Clipboard.SetText(MacText.Text);
            ResultText.Text = "MAC 地址已复制。";
        }
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        LicenseService.CodeResult result = LicenseService.ActivateCode(CodeText.Text);
        ResultText.Text = result.Message;
        if (result.Verification == LicenseService.CodeVerification.Valid)
        {
            RefreshStatus();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
