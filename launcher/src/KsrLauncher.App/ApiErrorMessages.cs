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

    public static string ForCampaignJoin(KsrApiException exception) => exception.Code switch
    {
        "campaign_not_found" or "not_found" => "No campaign was found with this Campaign ID.",
        "campaign_closed" => "This campaign is closed and no longer accepts players.",
        "campaign_cancelled" => "This campaign was cancelled and no longer accepts players.",
        "campaign_full" => "This campaign has no available player slots.",
        "invite_required" or "invalid_invite" => "This Campaign ID does not grant access to the campaign.",
        "email_not_verified" => "Verify your email address before joining a campaign.",
        "forbidden" => "Your account is not allowed to join this campaign.",
        _ => SafeFallback(exception, "The campaign could not be joined.")
    };

    private static string SafeFallback(KsrApiException exception, string fallback) =>
        string.IsNullOrWhiteSpace(exception.Message) ? fallback : exception.Message;
}
