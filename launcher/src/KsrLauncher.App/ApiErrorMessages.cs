using KsrLauncher.Core;

namespace KsrLauncher.App;

internal static class ApiErrorMessages
{
    public static string ForAuthentication(KsrApiException exception) => exception.Code switch
    {
        "invalid_credentials" => "Incorrect username/email or password.",
        "email_not_verified" or "email_verification_required" or "unverified_email" =>
            "Your email has not been verified. Open the verification message we sent you, then try again.",
        "rate_limited" => "Too many sign-in attempts. Please wait and try again later.",
        "token_expired" => "Your session has expired. Please sign in again.",
        "unauthorized" => "This account is not authorized to sign in.",
        _ => SafeFallback(exception, "Sign-in could not be completed.")
    };

    public static string ForRegistration(KsrApiException exception) => exception.Code switch
    {
        "invalid_username" => "Choose a valid username containing at least 3 characters.",
        "invalid_email" => "Enter a valid email address.",
        "weak_password" => "Choose a stronger password containing at least 8 characters.",
        "user_exists" => "An account already exists with this username or email address.",
        "rate_limited" => "Too many account requests. Please wait and try again later.",
        _ => SafeFallback(exception, "The account could not be created.")
    };

    private static string SafeFallback(KsrApiException exception, string fallback) =>
        string.IsNullOrWhiteSpace(exception.Message) ? fallback : exception.Message;
}
