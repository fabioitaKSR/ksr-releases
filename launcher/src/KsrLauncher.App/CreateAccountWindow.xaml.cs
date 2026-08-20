using System.Net.Http;
using System.Net.Mail;
using System.Windows;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class CreateAccountWindow : Window
{
    private readonly KsrPlatformClient _client;
    private readonly string _serverUrl;
    public string? CreatedUsername { get; private set; }

    public CreateAccountWindow(KsrPlatformClient client, string serverUrl)
    {
        InitializeComponent();
        _client = client;
        _serverUrl = serverUrl;
        UsernameTextBox.Focus();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameTextBox.Text.Trim();
        var email = EmailTextBox.Text.Trim();
        if (username.Length < 3)
        {
            ShowValidation("Username must contain at least 3 characters.");
            return;
        }
        if (!MailAddress.TryCreate(email, out _))
        {
            ShowValidation("Enter a valid email address.");
            return;
        }
        if (PasswordBox.Password.Length < 8)
        {
            ShowValidation("Password must contain at least 8 characters.");
            return;
        }
        if (!string.Equals(PasswordBox.Password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            ShowValidation("The two passwords do not match.");
            return;
        }
        if (EmailUseConsentCheckBox.IsChecked != true)
        {
            ShowValidation("Confirm the limited email-use notice before creating the account.");
            return;
        }

        SetBusy(true);
        try
        {
            await _client.RegisterAsync(_serverUrl, username, email, PasswordBox.Password);
            CreatedUsername = username;
            PasswordBox.Clear();
            ConfirmPasswordBox.Clear();
            MessageBox.Show(
                $"Your KSR account was created.\n\nA verification message has been sent to {email}. Check your inbox and spam folder, then verify your email before signing in.",
                "Verify Your Email",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (KsrApiException exception) { ShowValidation(ApiErrorMessages.ForRegistration(exception)); }
        catch (HttpRequestException) { ShowValidation("The KSR server could not be reached."); }
        catch (Exception exception) { ShowValidation(exception.Message); }
        finally { SetBusy(false); }
    }

    private void ShowValidation(string message) => StatusText.Text = message;

    private void SetBusy(bool busy)
    {
        UsernameTextBox.IsEnabled = !busy;
        EmailTextBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        ConfirmPasswordBox.IsEnabled = !busy;
        EmailUseConsentCheckBox.IsEnabled = !busy;
        CreateButton.IsEnabled = !busy;
        CreateButton.Content = busy ? "CREATING…" : "CREATE ACCOUNT";
        if (busy) StatusText.Text = "Creating your account securely…";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
