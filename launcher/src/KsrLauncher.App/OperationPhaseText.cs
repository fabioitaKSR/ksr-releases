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
        "CACHE CLEANUP" => "Removing temporary downloads from the full cache...",
        _ => "Checking the installation..."
    };
}
