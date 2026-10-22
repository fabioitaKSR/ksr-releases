using KsrLauncher.Core;

namespace KsrLauncher.App;

// UI-thread state. No cached authorization is persisted to disk.
internal static class BetaAccess
{
    private static int _generation;
    private static int _request;
    private static Task<bool>? _refresh;
    private static CancellationTokenSource? _cancellation;
    public static IReadOnlyList<KsrBetaModule> Modules { get; private set; } = [];
    public static event Action? Changed;

    public static void Clear()
    {
        ++_generation;
        _cancellation?.Cancel();
        _refresh = null;
        Modules = [];
        LauncherSession.CanAccessTestGame = false;
        Changed?.Invoke();
    }

    public static Task<bool> RefreshAsync()
    {
        if (_refresh is { IsCompleted: false }) return _refresh;
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        return _refresh = RefreshCoreAsync(_cancellation.Token);
    }

    private static async Task<bool> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var generation = _generation;
        var request = ++_request;
        var user = LauncherSession.UserId;
        var server = LauncherSession.ServerUrl;
        if (!LauncherSession.IsAuthenticated || user <= 0 || server is null) { Clear(); return false; }
        try
        {
            await LauncherSession.EnsureFreshAccessTokenAsync(server);
            cancellationToken.ThrowIfCancellationRequested();
            var modules = await new BetaModulesClient().GetAsync(server, LauncherSession.AccessToken!, cancellationToken);
            if (generation != _generation || request != _request || user != LauncherSession.UserId ||
                server != LauncherSession.ServerUrl || !LauncherSession.IsAuthenticated) return false;
            Modules = modules;
            LauncherSession.CanAccessTestGame = modules.Any(m => m.Id == "ksr-full");
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or
            UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException or KeyNotFoundException or ArgumentException or FormatException or KsrApiException)
        {
            if (generation == _generation && request == _request)
            {
                Modules = [];
                LauncherSession.CanAccessTestGame = false;
                Changed?.Invoke();
            }
            return false;
        }
    }

    public static async Task<KsrBetaModule?> RequireAsync(string id)
    {
        if (!await RefreshAsync()) return null;
        return Modules.FirstOrDefault(m => m.Id == id);
    }
}
