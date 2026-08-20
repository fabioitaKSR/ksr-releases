using System.Net.Http;
using System.Net.Mail;
using System.Windows;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class PasswordRecoveryWindow : Window
{
    private readonly KsrPlatformClient _client;
    private readonly string _serverUrl;

    public PasswordRecoveryWindow(KsrPlatformClient client, string serverUrl)
    {
        InitializeComponent();
        _client = client;
        _serverUrl = serverUrl;
        RecoveryEmailTextBox.Focus();
    }

    private async void Request_Click(object sender, RoutedEventArgs e)
    {
        var email = RecoveryEmailTextBox.Text.Trim();
        if (!MailAddress.TryCreate(email, out _))
        {
            StatusText.Text = "Enter a valid email address.";
            return;
        }
        SetBusy(true, "Requesting password reset…");
        try
        {
            await _client.RequestPasswordResetAsync(_serverUrl, email);
            StatusText.Text = "If a KSR account uses that address, password reset instructions will be sent.";
        }
        catch (KsrApiException exception) { StatusText.Text = exception.Message; }
        catch (HttpRequestException) { StatusText.Text = "The KSR server could not be reached."; }
        catch (Exception exception) { StatusText.Text = exception.Message; }
        finally { SetBusy(false, null); }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResetTokenTextBox.Text))
        {
            StatusText.Text = "Enter the reset token from the recovery email.";
            return;
        }
        if (NewPasswordBox.Password.Length < 8)
        {
            StatusText.Text = "Password must contain at least 8 characters.";
            return;
        }
        if (!string.Equals(NewPasswordBox.Password, ConfirmNewPasswordBox.Password, StringComparison.Ordinal))
        {
            StatusText.Text = "The two passwords do not match.";
            return;
        }
        SetBusy(true, "Resetting password…");
        try
        {
            await _client.ResetPasswordAsync(_serverUrl, ResetTokenTextBox.Text, NewPasswordBox.Password);
            ResetTokenTextBox.Clear();
            NewPasswordBox.Clear();
            ConfirmNewPasswordBox.Clear();
            MessageBox.Show("Your password was reset. You can now sign in.", "KSR Account", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (KsrApiException exception) { StatusText.Text = exception.Message; }
        catch (HttpRequestException) { StatusText.Text = "The KSR server could not be reached."; }
        catch (Exception exception) { StatusText.Text = exception.Message; }
        finally { SetBusy(false, null); }
    }

    private void SetBusy(bool busy, string? status)
    {
        RecoveryEmailTextBox.IsEnabled = !busy;
        ResetTokenTextBox.IsEnabled = !busy;
        NewPasswordBox.IsEnabled = !busy;
        ConfirmNewPasswordBox.IsEnabled = !busy;
        RequestButton.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
    }
}
