namespace KsrLauncher.App;

internal static class OperationPhaseText
{
    public static string Describe(string phase) => phase switch
    {
        "DOWNLOADING" => "Downloading and verifying files...",
        "EXTRACTING" => "Extracting verified files...",
        "INSTALLING" => "Installing files. This can take a few minutes...",
        "RESTORING" => "Restoring the previous installation...",
        "CLEANING" => "Cleaning completed update files...",
        _ => "Checking the installation..."
    };
}
