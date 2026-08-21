using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using KsrLauncher.Core;

namespace KsrLauncher.App;

public partial class MainWindow : Window
{
    private string? _kspRoot;
    private string? _referenceSavePath;
    private readonly ObservableCollection<CampaignListItem> _campaigns = [];
    private readonly ObservableCollection<string> _ignoredGameDataFolders = [];
    private readonly KsrPlatformClient _platformClient = new();
    private readonly Dictionary<string, CampaignBaselinePackage> _localBaselines = new(StringComparer.OrdinalIgnoreCase);
    private CampaignComplianceResult? _lastCompliance;
    private RememberedSession? _rememberedSession;
    private bool _campaignCloseInProgress;
    private bool _campaignUploadInProgress;

    public MainWindow()
    {
        InitializeComponent();
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        LauncherVersionText.Text = version is null
            ? "LAUNCHER"
            : $"LAUNCHER  ·  v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        CampaignsList.ItemsSource = _campaigns;
        IgnoredGameDataFoldersList.ItemsSource = _ignoredGameDataFolders;
        LauncherSession.ServerUrl = LauncherSettingsStore.LoadServerUrl() ?? LauncherSession.ServerUrl;
        try
        {
            _rememberedSession = LauncherCredentialStore.Load();
            if (_rememberedSession is not null)
            {
                LoginUsernameTextBox.Text = _rememberedSession.Username;
                RememberMeCheckBox.IsChecked = true;
            }
        }
        catch
        {
            _rememberedSession = null;
        }
        _kspRoot = Environment.GetEnvironmentVariable("KSR_KSP_ROOT");
        UpdateSessionVisuals();
        RefreshCampaignState();
        RefreshIgnoredFolderControls();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshServerStatusAsync();
        await TryRestoreSessionAsync();
    }

    private void PlayerTab_Click(object sender, RoutedEventArgs e)
    {
        PlayerArea.Visibility = Visibility.Visible;
        AdminArea.Visibility = Visibility.Collapsed;
        UpdateAreaTabVisuals(PlayerTabButton, AdminTabButton);
    }

    private void AdminTab_Click(object sender, RoutedEventArgs e)
    {
        PlayerArea.Visibility = Visibility.Collapsed;
        AdminArea.Visibility = Visibility.Visible;
        UpdateAreaTabVisuals(AdminTabButton, PlayerTabButton);
    }

    private void UpdateAreaTabVisuals(Button activeTab, Button inactiveTab)
    {
        var orange = (System.Windows.Media.Brush)FindResource("OrangeBrush");
        activeTab.Foreground = orange;
        activeTab.BorderBrush = orange;
        inactiveTab.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        inactiveTab.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
    }

    private void BrowseReferenceSave_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Select the Career or Science save to use as the campaign starting point",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(_kspRoot)) picker.InitialDirectory = Path.Combine(_kspRoot, "saves");
        if (picker.ShowDialog(this) != true) return;

        KspCareerSave selection;
        try
        {
            selection = KspCareerSaveLocator.Resolve(picker.FolderName);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _referenceSavePath = null;
            _kspRoot = null;
            _ignoredGameDataFolders.Clear();
            ReferenceSavePathTextBox.Text = "No valid Career or Science save selected";
            ResolvedKspPathText.Text = "Not resolved";
            CampaignCreationStatusText.Text = exception.Message;
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            SetBaselineActivity("> SAVE REJECTED", exception.Message, "ErrorBrush");
            RefreshIgnoredFolderControls();
            UpdateCreateRaceState();
            return;
        }

        var installationChanged = !string.Equals(_kspRoot, selection.KspRoot, StringComparison.OrdinalIgnoreCase);
        _referenceSavePath = selection.SavePath;
        _kspRoot = selection.KspRoot;
        if (installationChanged) _ignoredGameDataFolders.Clear();
        ReferenceSavePathTextBox.Text = _referenceSavePath;
        ReferenceSavePathTextBox.Foreground = (System.Windows.Media.Brush)FindResource("ForegroundBrush");
        ResolvedKspPathText.Text = _kspRoot;
        ResolvedKspPathText.Foreground = (System.Windows.Media.Brush)FindResource("ForegroundBrush");
        CampaignCreationStatusText.Text = "Career or Science save validated. Enter a campaign name to continue.";
        CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
        SetBaselineActivity("> SYSTEM READY", "Save validated. Press CREATE RACE to read GameData and build the campaign baseline.");
        RefreshIgnoredFolderControls();
        UpdateCreateRaceState();
    }

    private void SelectIgnoredFolders_Click(object sender, RoutedEventArgs e)
    {
        if (_referenceSavePath is null || string.IsNullOrWhiteSpace(_kspRoot)) return;
        var gameDataRoot = Path.GetFullPath(Path.Combine(_kspRoot, "GameData"));
        if (!Directory.Exists(gameDataRoot))
        {
            MessageBox.Show("The selected KSP installation does not contain GameData.", "KSR Campaign", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var picker = new OpenFolderDialog
        {
            Title = "Select optional mod folders to ignore during campaign checks",
            InitialDirectory = gameDataRoot,
            Multiselect = true
        };
        if (picker.ShowDialog(this) != true) return;

        var rejected = new List<string>();
        foreach (var selectedPath in picker.FolderNames)
        {
            try
            {
                var fullPath = Path.GetFullPath(selectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Directory.GetParent(fullPath)?.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(fullPath) || !string.Equals(parent, gameDataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Only folders directly inside GameData can be selected.");
                SafePaths.RejectReparsePoints(gameDataRoot, fullPath);
                var folderName = Path.GetFileName(fullPath);
                CampaignBaselineBuilder.NormalizeIgnoredFolders([folderName]);
                if (!_ignoredGameDataFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase))
                    _ignoredGameDataFolders.Add(folderName);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                rejected.Add($"{Path.GetFileName(selectedPath)} — {exception.Message}");
            }
        }

        SortIgnoredFolders();
        RefreshIgnoredFolderControls();
        if (rejected.Count > 0)
        {
            MessageBox.Show(
                "Some folders were not added:\n\n" + string.Join("\n", rejected),
                "KSR Ignored Mods", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveIgnoredFolders_Click(object sender, RoutedEventArgs e)
    {
        var selected = IgnoredGameDataFoldersList.SelectedItems.Cast<string>().ToList();
        foreach (var folder in selected) _ignoredGameDataFolders.Remove(folder);
        RefreshIgnoredFolderControls();
    }

    private void ClearIgnoredFolders_Click(object sender, RoutedEventArgs e)
    {
        _ignoredGameDataFolders.Clear();
        RefreshIgnoredFolderControls();
    }

    private void IgnoredGameDataFoldersList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshIgnoredFolderControls();

    private void SortIgnoredFolders()
    {
        var sorted = _ignoredGameDataFolders.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
        _ignoredGameDataFolders.Clear();
        foreach (var folder in sorted) _ignoredGameDataFolders.Add(folder);
    }

    private void RefreshIgnoredFolderControls()
    {
        if (!IsInitialized) return;
        var hasFolders = _ignoredGameDataFolders.Count > 0;
        var campaignCreationAvailable = LauncherSession.IsAuthenticated && GetBlockingAdminCampaign() is null;
        IgnoredFoldersEmptyText.Visibility = hasFolders ? Visibility.Collapsed : Visibility.Visible;
        SelectIgnoredFoldersButton.IsEnabled = campaignCreationAvailable && _referenceSavePath is not null &&
            !string.IsNullOrWhiteSpace(_kspRoot) && Directory.Exists(Path.Combine(_kspRoot, "GameData"));
        RemoveIgnoredFoldersButton.IsEnabled = campaignCreationAvailable && IgnoredGameDataFoldersList.SelectedItems.Count > 0;
        ClearIgnoredFoldersButton.IsEnabled = campaignCreationAvailable && hasFolders;
    }

    private void CampaignCreationInput_Changed(object sender, TextChangedEventArgs e) => UpdateCreateRaceState();

    private void UpdateCreateRaceState()
    {
        if (!IsInitialized) return;
        var blockingCampaign = GetBlockingAdminCampaign();
        var campaignCreationAvailable = LauncherSession.IsAuthenticated && blockingCampaign is null;
        var retryableDraft = blockingCampaign is not null &&
            blockingCampaign.Status.Equals("DRAFT", StringComparison.OrdinalIgnoreCase) &&
            _localBaselines.ContainsKey(blockingCampaign.CampaignCode);
        CampaignNameTextBox.IsEnabled = campaignCreationAvailable;
        BrowseReferenceSaveButton.IsEnabled = campaignCreationAvailable;
        CreateRaceButton.Visibility = blockingCampaign is null ? Visibility.Visible : Visibility.Collapsed;
        RetryCampaignUploadButton.Visibility = retryableDraft ? Visibility.Visible : Visibility.Collapsed;
        RetryCampaignUploadButton.IsEnabled = retryableDraft && LauncherSession.IsAuthenticated && !_campaignUploadInProgress;
        RetryCampaignUploadButton.Content = _campaignUploadInProgress ? "UPLOADING CAMPAIGN…" : "RETRY CAMPAIGN UPLOAD";
        CloseCampaignButton.Visibility = blockingCampaign is null || _campaignUploadInProgress ? Visibility.Collapsed : Visibility.Visible;
        CloseCampaignButton.Content = blockingCampaign?.Status.Equals("DRAFT", StringComparison.OrdinalIgnoreCase) == true
            ? "DISCARD LOCAL DRAFT"
            : _campaignCloseInProgress ? "CLOSING CAMPAIGN…" : "CLOSE CAMPAIGN";
        CloseCampaignButton.IsEnabled = blockingCampaign is not null && LauncherSession.IsAuthenticated &&
            !_campaignCloseInProgress && !_campaignUploadInProgress;
        CreateRaceButton.IsEnabled = campaignCreationAvailable &&
            !string.IsNullOrWhiteSpace(_referenceSavePath) &&
            !string.IsNullOrWhiteSpace(CampaignNameTextBox.Text);
        RefreshIgnoredFolderControls();
        if (blockingCampaign is not null)
        {
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("OrangeBrush");
            CampaignCreationStatusText.Text = _campaignUploadInProgress
                ? $"Uploading {blockingCampaign.Name} to the KSR server..."
                : _campaignCloseInProgress
                ? $"Closing {blockingCampaign.Name} ({blockingCampaign.CampaignCode})..."
                : $"You already administer {blockingCampaign.Name} ({blockingCampaign.CampaignCode}). Close that campaign before creating another one.";
        }
    }

    private CampaignListItem? GetBlockingAdminCampaign() =>
        _campaigns.FirstOrDefault(campaign => CampaignRules.BlocksNewAdminCampaign(campaign.Role, campaign.Status));

    private async void CloseCampaign_Click(object sender, RoutedEventArgs e)
    {
        var campaign = GetBlockingAdminCampaign();
        if (campaign is null || _campaignCloseInProgress) return;

        if (campaign.Status.Equals("DRAFT", StringComparison.OrdinalIgnoreCase))
        {
            var confirmDraft = MessageBox.Show(
                $"Discard the local draft '{campaign.Name}' ({campaign.CampaignCode})?\n\nThe generated Master Save and baseline files will remain on disk inside the selected save.",
                "Discard KSR Draft", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirmDraft != MessageBoxResult.Yes) return;
            _localBaselines.Remove(campaign.CampaignCode);
            _campaigns.Remove(campaign);
            RefreshCampaignState();
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
            CampaignCreationStatusText.Text = "Local draft discarded. You can now create a new campaign.";
            SetBaselineActivity("> LOCAL DRAFT DISCARDED", "Generated files were kept safely inside the selected KSP save.");
            return;
        }

        var confirm = MessageBox.Show(
            $"Close '{campaign.Name}' ({campaign.CampaignCode}) for every player?\n\nThe campaign will no longer be playable or accept new results. Master Save, baseline, members, achievements and standings will be retained. A closed campaign cannot be reopened in V1.",
            "Close KSR Campaign", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        if (string.IsNullOrWhiteSpace(LauncherSession.ServerUrl) || string.IsNullOrWhiteSpace(LauncherSession.AccessToken))
        {
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            CampaignCreationStatusText.Text = "Sign in to close this campaign.";
            return;
        }

        _campaignCloseInProgress = true;
        UpdateCreateRaceState();
        try
        {
            await _platformClient.CloseCampaignAsync(
                LauncherSession.ServerUrl,
                LauncherSession.AccessToken,
                campaign.CampaignCode);
            var index = _campaigns.IndexOf(campaign);
            var closedCampaign = campaign with { Status = "CLOSED" };
            if (index >= 0) _campaigns[index] = closedCampaign;
            if (CampaignsList.SelectedItem is CampaignListItem selectedCampaign && selectedCampaign == campaign)
                CampaignsList.SelectedItem = closedCampaign;
            RefreshCampaignState();
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
            CampaignCreationStatusText.Text = $"{campaign.Name} is closed. You can now create a new campaign.";
            SetBaselineActivity("> CAMPAIGN CLOSED", "Server state updated. Historical campaign data has been retained.");
        }
        catch (KsrApiException exception)
        {
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            CampaignCreationStatusText.Text = exception.Message;
            SetBaselineActivity("> CLOSE REQUEST FAILED", exception.Message, "ErrorBrush");
        }
        catch (HttpRequestException)
        {
            const string message = "The KSR server is unreachable. The campaign was not closed.";
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            CampaignCreationStatusText.Text = message;
            SetBaselineActivity("> CLOSE REQUEST FAILED", message, "ErrorBrush");
        }
        finally
        {
            _campaignCloseInProgress = false;
            UpdateCreateRaceState();
        }
    }

    private async void CreateRace_Click(object sender, RoutedEventArgs e)
    {
        if (_referenceSavePath is null || GetBlockingAdminCampaign() is not null)
        {
            UpdateCreateRaceState();
            return;
        }
        CreateRaceButton.IsEnabled = false;
        CampaignNameTextBox.IsEnabled = false;
        CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("OrangeBrush");
        CampaignCreationStatusText.Text = "Baseline scan in progress. Follow the scanner activity above.";
        SetBaselineActivity("> INITIALIZING BASELINE SCAN", "Preparing the campaign workspace...", isIndeterminate: true);
        var progress = new Progress<BaselineProgress>(UpdateBaselineActivity);
        try
        {
            var drafts = Path.Combine(_referenceSavePath, "KSR_CampaignData");
            var package = await new CampaignBaselineBuilder().CreateAsync(
                CampaignNameTextBox.Text,
                _referenceSavePath,
                drafts,
                _ignoredGameDataFolders,
                progress);
            var draftCode = $"DRAFT-{package.Manifest.CreatedAtUtc:yyyyMMdd-HHmmss}";
            _localBaselines[draftCode] = package;
            var item = new CampaignListItem(
                draftCode, package.Manifest.CampaignName, "ADMIN", "NOT SELECTED", "DRAFT", true,
                package.Manifest.MasterSaveSha256, package.Manifest.MasterSaveSize,
                package.Manifest.SchemaVersion, await PackageService.ComputeSha256Async(package.ManifestPath));
            _campaigns.Add(item);
            RefreshCampaignState();
            CampaignsList.SelectedItem = item;
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
            CampaignCreationStatusText.Text = $"Baseline ready: {package.Manifest.GameDataFiles.Count} files captured; {package.Manifest.IgnoredGameDataFolders.Count} GameData folder(s) ignored.";
            SetBaselineActivity(
                "> READING COMPLETE",
                $"{package.Manifest.GameDataFiles.Count} GameData files catalogued. {package.Manifest.IgnoredGameDataFolders.Count} optional mod folder(s) ignored.",
                "TerminalGreenBrush", package.Manifest.GameDataFiles.Count, Math.Max(1, package.Manifest.GameDataFiles.Count));
            await UploadCampaignDraftAsync(item, package);
        }
        catch (Exception exception)
        {
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            CampaignCreationStatusText.Text = exception.Message;
            SetBaselineActivity("> SCAN FAILED", exception.Message, "ErrorBrush");
        }
        finally
        {
            CampaignNameTextBox.IsEnabled = true;
            UpdateCreateRaceState();
        }
    }

    private async void RetryCampaignUpload_Click(object sender, RoutedEventArgs e)
    {
        var draft = GetBlockingAdminCampaign();
        if (draft is null || !draft.Status.Equals("DRAFT", StringComparison.OrdinalIgnoreCase) ||
            !_localBaselines.TryGetValue(draft.CampaignCode, out var package)) return;
        await UploadCampaignDraftAsync(draft, package);
    }

    private async Task UploadCampaignDraftAsync(CampaignListItem draft, CampaignBaselinePackage package)
    {
        if (_campaignUploadInProgress) return;
        if (string.IsNullOrWhiteSpace(LauncherSession.ServerUrl) || string.IsNullOrWhiteSpace(LauncherSession.AccessToken))
        {
            SetCampaignUploadFailure(draft, "Sign in before uploading this campaign.");
            return;
        }

        _campaignUploadInProgress = true;
        var uploading = draft with { Status = "UPLOADING" };
        ReplaceCampaignItem(draft, uploading);
        CampaignsList.SelectedItem = uploading;
        SetBaselineActivity("> UPLOADING CAMPAIGN", "Sending Master Save and baseline to the KSR server...", isIndeterminate: true);
        UpdateCreateRaceState();
        try
        {
            var created = await _platformClient.CreateCampaignAsync(
                LauncherSession.ServerUrl, LauncherSession.AccessToken, package);
            var active = new CampaignListItem(
                created.CampaignCode,
                created.Name,
                created.Role.ToUpperInvariant(),
                created.NationId ?? "NOT SELECTED",
                created.Status.ToUpperInvariant(),
                !string.IsNullOrWhiteSpace(created.MasterSaveSha256),
                created.MasterSaveSha256,
                created.MasterSaveSize,
                created.BaselineSchemaVersion,
                created.BaselineSha256);
            ReplaceCampaignItem(uploading, active);
            _localBaselines.Remove(draft.CampaignCode);
            _localBaselines[active.CampaignCode] = package;
            CampaignsList.SelectedItem = active;
            CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
            CampaignCreationStatusText.Text = $"Campaign created: {active.CampaignCode}. The Master Save and baseline are active on the server.";
            SetBaselineActivity("> CAMPAIGN ACTIVE", $"SERVER CAMPAIGN ID: {active.CampaignCode}", "TerminalGreenBrush", 1, 1);
            MessageBox.Show(
                $"The campaign is active.\n\nCampaign ID: {active.CampaignCode}\nName: {active.Name}\n\nPlayers can use this Campaign ID to join.",
                "KSR Campaign Created", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (KsrApiException exception)
        {
            SetCampaignUploadFailure(uploading, exception.Message);
        }
        catch (HttpRequestException)
        {
            SetCampaignUploadFailure(uploading,
                "The KSR server is unreachable. The local draft is safe; retry the upload when the server is available.");
        }
        catch (TaskCanceledException)
        {
            SetCampaignUploadFailure(uploading,
                "The campaign upload timed out. The local draft is safe and can be retried without creating a duplicate campaign.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetCampaignUploadFailure(uploading, exception.Message);
        }
        finally
        {
            _campaignUploadInProgress = false;
            UpdateCreateRaceState();
        }
    }

    private void SetCampaignUploadFailure(CampaignListItem current, string message)
    {
        var draft = current with { Status = "DRAFT" };
        ReplaceCampaignItem(current, draft);
        CampaignsList.SelectedItem = draft;
        CampaignCreationStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
        CampaignCreationStatusText.Text = message;
        SetBaselineActivity("> UPLOAD FAILED — DRAFT SAVED", message, "ErrorBrush");
    }

    private void ReplaceCampaignItem(CampaignListItem current, CampaignListItem replacement)
    {
        var index = _campaigns.IndexOf(current);
        if (index >= 0) _campaigns[index] = replacement;
    }

    private void UpdateBaselineActivity(BaselineProgress progress)
    {
        var headline = progress.Stage switch
        {
            "Discovering Master Save files" => "> LOCATING MASTER SAVE FILES...",
            "Packaging Master Save" => "> PACKAGING MASTER SAVE",
            "Discovering GameData files" => "> SEARCHING GAMEDATA...",
            "Scanning GameData" => "> READING GAMEDATA",
            "GameData reading complete" => "> GAMEDATA READING COMPLETE",
            "Baseline ready" => "> FINALIZING BASELINE",
            _ => $"> {progress.Stage.ToUpperInvariant()}"
        };
        var detail = progress.Total > 0
            ? progress.CurrentPath is null
                ? $"{progress.Completed} of {progress.Total} files processed."
                : $"FILE {Math.Min(progress.Completed + 1, progress.Total)} OF {progress.Total}\n{progress.CurrentPath}"
            : "Building the file list. This may take a moment on large mod installations.";
        SetBaselineActivity(
            headline,
            detail,
            "TerminalGreenBrush",
            Math.Min(progress.Completed, progress.Total),
            Math.Max(1, progress.Total),
            progress.Total <= 0);
    }

    private void SetBaselineActivity(
        string headline,
        string detail,
        string brushResource = "TerminalGreenBrush",
        double value = 0,
        double maximum = 1,
        bool isIndeterminate = false)
    {
        var brush = (System.Windows.Media.Brush)FindResource(brushResource);
        BaselineActivityStageText.Text = headline;
        BaselineActivityStageText.Foreground = brush;
        BaselineActivityDetailText.Text = detail;
        BaselineActivityDetailText.Foreground = brushResource == "ErrorBrush"
            ? brush
            : (System.Windows.Media.Brush)FindResource("TerminalDimGreenBrush");
        BaselineActivityProgressBar.Foreground = brush;
        BaselineActivityProgressBar.IsIndeterminate = isIndeterminate;
        BaselineActivityProgressBar.Maximum = Math.Max(1, maximum);
        BaselineActivityProgressBar.Value = Math.Clamp(value, 0, BaselineActivityProgressBar.Maximum);
    }

    private void CampaignsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyCampaignSelection(CampaignsList.SelectedItem as CampaignListItem);
    }

    private void JoinCampaign_Click(object sender, RoutedEventArgs e)
    {
        if (!LauncherSession.IsAuthenticated || string.IsNullOrWhiteSpace(LauncherSession.ServerUrl) ||
            string.IsNullOrWhiteSpace(LauncherSession.AccessToken))
        {
            MessageBox.Show("Sign in before joining a campaign.", "Join KSR Campaign",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new JoinCampaignWindow(
            _platformClient, LauncherSession.ServerUrl, LauncherSession.AccessToken) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.JoinedCampaign is null) return;
        var item = ToCampaignListItem(dialog.JoinedCampaign);
        var existing = _campaigns.FirstOrDefault(value =>
            value.CampaignCode.Equals(item.CampaignCode, StringComparison.OrdinalIgnoreCase));
        if (existing is null) _campaigns.Add(item);
        else
        {
            var index = _campaigns.IndexOf(existing);
            if (index >= 0) _campaigns[index] = item;
        }
        RefreshCampaignState();
        CampaignsList.SelectedItem = item;
    }

    private async void RefreshCampaigns_Click(object sender, RoutedEventArgs e) =>
        await RefreshCampaignsFromServerAsync(true);

    private async Task RefreshCampaignsFromServerAsync(bool showFeedback)
    {
        if (!LauncherSession.IsAuthenticated || string.IsNullOrWhiteSpace(LauncherSession.ServerUrl) ||
            string.IsNullOrWhiteSpace(LauncherSession.AccessToken))
        {
            if (showFeedback)
                MessageBox.Show("Sign in to refresh your campaigns.", "KSR Campaigns",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RefreshCampaignsButton.IsEnabled = false;
        RefreshCampaignsButton.Content = "REFRESHING…";
        try
        {
            var selectedCode = (CampaignsList.SelectedItem as CampaignListItem)?.CampaignCode;
            var campaigns = await _platformClient.GetCampaignsAsync(
                LauncherSession.ServerUrl, LauncherSession.AccessToken);
            ReplaceCampaigns(campaigns.Select(ToCampaignListItem));
            if (selectedCode is not null)
                CampaignsList.SelectedItem = _campaigns.FirstOrDefault(item =>
                    item.CampaignCode.Equals(selectedCode, StringComparison.OrdinalIgnoreCase));
        }
        catch (KsrApiException exception)
        {
            if (showFeedback)
                MessageBox.Show(exception.Message, "Campaign Refresh Failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (HttpRequestException)
        {
            if (showFeedback)
                MessageBox.Show("The KSR server is unreachable. Your existing campaign list was kept.",
                    "Campaign Refresh Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshCampaignsButton.IsEnabled = true;
            RefreshCampaignsButton.Content = "REFRESH";
        }
    }

    private async void CopyCampaignId_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not CampaignListItem campaign ||
            string.IsNullOrWhiteSpace(campaign.CampaignCode)) return;
        try
        {
            Clipboard.SetText(campaign.CampaignCode);
            button.Content = "COPIED";
            button.IsEnabled = false;
            await Task.Delay(1200);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"The Campaign ID could not be copied.\n\n{exception.Message}",
                "Copy Campaign ID", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.Content = "COPY ID";
            button.IsEnabled = true;
        }
    }

    private void ApplyCampaignSelection(CampaignListItem? campaign)
    {
        LauncherSession.CampaignCode = campaign?.CampaignCode;
        LauncherSession.CampaignName = campaign?.Name;
        ActiveCampaignText.Text = campaign?.Name?.ToUpperInvariant() ?? "NO CAMPAIGN SELECTED";
        SelectedCampaignText.Text = campaign is null
            ? "NO CAMPAIGN SELECTED"
            : $"{campaign.Name}  ·  {campaign.Nation}  ·  {campaign.Role}";
        SelectedCampaignText.Foreground = (System.Windows.Media.Brush)FindResource(campaign is null ? "MutedBrush" : "ForegroundBrush");
        OpenCampaignButton.IsEnabled = campaign is not null;
        DownloadMasterSaveButton.IsEnabled = campaign?.MasterSaveAvailable == true;
        MasterSaveStatusText.Text = campaign is null
            ? "Select a campaign to check its Master Save."
            : campaign.MasterSaveAvailable
                ? "The selected campaign Master Save is available and verified."
                : "No Master Save is currently available for the selected campaign.";
        var hasBaseline = campaign is not null && _localBaselines.ContainsKey(campaign.CampaignCode);
        CheckInstallationButton.IsEnabled = hasBaseline;
        ViewIgnoredModsButton.IsEnabled = hasBaseline;
        CampaignComplianceStatusText.Text = hasBaseline
            ? "Baseline available. Run the installation check before launching the campaign."
            : "The campaign baseline has not been downloaded yet.";
        CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        _lastCompliance = null;
        LaunchKspButton.IsEnabled = !hasBaseline && !CampaignRules.IsTerminalStatus(campaign?.Status);
    }

    internal void ReplaceCampaigns(IEnumerable<CampaignListItem> campaigns)
    {
        CampaignsList.SelectedItem = null;
        _campaigns.Clear();
        foreach (var campaign in campaigns) _campaigns.Add(campaign);
        RefreshCampaignState();
    }

    private void RefreshCampaignState()
    {
        EmptyCampaignsPanel.Visibility = _campaigns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CampaignsList.Visibility = _campaigns.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_campaigns.Count == 0) ApplyCampaignSelection(null);
        UpdateCreateRaceState();
    }

    private void SendLog_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureKspRoot()) return;
        var log = Path.Combine(_kspRoot!, "KSP.log");
        if (!File.Exists(log))
        {
            MessageBox.Show("KSP.log was not found in the selected KSP folder.", "KSR Support", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        OpenSupportDialog(SupportReportType.Log, log, null);
    }

    private void SendSave_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureKspRoot()) return;
        var picker = new OpenFolderDialog
        {
            Title = "Select the KSP save folder to send",
            InitialDirectory = Path.Combine(_kspRoot!, "saves"),
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;
        if (!File.Exists(Path.Combine(picker.FolderName, "persistent.sfs")))
        {
            MessageBox.Show("The selected folder is not a KSP save. persistent.sfs is missing.", "KSR Support", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        OpenSupportDialog(SupportReportType.Save, picker.FolderName, Path.GetFileName(picker.FolderName));
    }

    private void OpenSupportDialog(SupportReportType type, string sourcePath, string? saveName)
    {
        var dialog = new SupportReportWindow(type, sourcePath, saveName)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void LaunchKsp_Click(object sender, RoutedEventArgs e)
    {
        if (CampaignsList.SelectedItem is CampaignListItem campaign &&
            _localBaselines.ContainsKey(campaign.CampaignCode) && _lastCompliance?.ReadyToLaunch != true)
        {
            MessageBox.Show("Check this campaign installation before launching KSP.",
                "KSR Campaign", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!EnsureKspRoot()) return;
        var executable = Path.Combine(_kspRoot!, "KSP_x64.exe");
        Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = _kspRoot!, UseShellExecute = true });
    }

    private async void CheckInstallation_Click(object sender, RoutedEventArgs e)
    {
        if (CampaignsList.SelectedItem is not CampaignListItem campaign ||
            !_localBaselines.TryGetValue(campaign.CampaignCode, out var package)) return;
        if (!EnsureKspRoot()) return;
        var picker = new OpenFolderDialog
        {
            Title = "Select the installed campaign save to verify",
            InitialDirectory = Path.Combine(_kspRoot!, "saves"),
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;

        await RunInstallationCheckAsync(campaign, package, picker.FolderName);
    }

    private async void DownloadMasterSave_Click(object sender, RoutedEventArgs e)
    {
        if (CampaignsList.SelectedItem is not CampaignListItem campaign || !campaign.MasterSaveAvailable) return;
        if (!LauncherSession.IsAuthenticated || string.IsNullOrWhiteSpace(LauncherSession.ServerUrl) ||
            string.IsNullOrWhiteSpace(LauncherSession.AccessToken))
        {
            MessageBox.Show("Sign in before downloading a campaign.", "KSR Campaign",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!EnsureKspRoot()) return;

        DownloadMasterSaveButton.IsEnabled = false;
        DownloadMasterSaveButton.Content = "DOWNLOADING…";
        CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("OrangeBrush");
        var progress = new Progress<string>(message => CampaignComplianceStatusText.Text = message);
        string? temporarySave = null;
        try
        {
            var campaignDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KSRLauncher", "Campaigns", campaign.CampaignCode);
            var serverCampaign = new KsrCampaign(
                campaign.CampaignCode, campaign.Name, campaign.Status, campaign.Role,
                campaign.Nation == "NOT SELECTED" ? null : campaign.Nation,
                campaign.MasterSaveSha256, campaign.MasterSaveSize,
                campaign.BaselineSchemaVersion, campaign.BaselineSha256);
            var package = await _platformClient.DownloadCampaignArtifactsAsync(
                LauncherSession.ServerUrl, LauncherSession.AccessToken, serverCampaign, campaignDirectory, progress);

            var savesRoot = Path.Combine(_kspRoot!, "saves");
            Directory.CreateDirectory(savesRoot);
            var installedSave = Path.Combine(savesRoot, $"{campaign.CampaignCode} Start");
            if (!Directory.Exists(installedSave))
            {
                temporarySave = Path.Combine(savesRoot, $".ksr-install-{Guid.NewGuid():N}");
                PackageService.ExtractSafely(package.MasterSavePath, temporarySave);
                if (!File.Exists(Path.Combine(temporarySave, "persistent.sfs")))
                    throw new InvalidDataException("The verified Master Save does not contain persistent.sfs.");
                Directory.Move(temporarySave, installedSave);
                temporarySave = null;
            }
            else if (!File.Exists(Path.Combine(installedSave, "persistent.sfs")))
            {
                throw new InvalidDataException($"The existing folder '{installedSave}' is not a valid KSP save.");
            }

            _localBaselines[campaign.CampaignCode] = package;
            ApplyCampaignSelection(campaign);
            CampaignComplianceStatusText.Text = $"Verified campaign save installed in: {installedSave}";
            await RunInstallationCheckAsync(campaign, package, installedSave);
        }
        catch (KsrApiException exception)
        {
            CampaignComplianceStatusText.Text = exception.Message;
            CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            LaunchKspButton.IsEnabled = false;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            CampaignComplianceStatusText.Text = exception.Message;
            CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            LaunchKspButton.IsEnabled = false;
        }
        finally
        {
            if (temporarySave is not null && Directory.Exists(temporarySave)) Directory.Delete(temporarySave, true);
            DownloadMasterSaveButton.Content = "DOWNLOAD VERIFIED SAVE";
            DownloadMasterSaveButton.IsEnabled = CampaignsList.SelectedItem is CampaignListItem selected && selected.MasterSaveAvailable;
        }
    }

    private async Task RunInstallationCheckAsync(
        CampaignListItem campaign,
        CampaignBaselinePackage package,
        string savePath)
    {
        CheckInstallationButton.IsEnabled = false;
        CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("OrangeBrush");
        var progress = new Progress<BaselineProgress>(value =>
            CampaignComplianceStatusText.Text = $"{value.Stage}: {value.Completed}/{value.Total}");
        try
        {
            _lastCompliance = await new CampaignBaselineComparer().CompareAsync(
                package.Manifest, savePath, progress);
            _kspRoot = KspCareerSaveLocator.Resolve(savePath).KspRoot;
            if (_lastCompliance.ReadyToLaunch)
            {
                CampaignComplianceStatusText.Text = "READY TO LAUNCH — save settings and GameData match the campaign baseline.";
                CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
                LaunchKspButton.IsEnabled = true;
            }
            else
            {
                var modDifferences = _lastCompliance.Differences.Count(item => item.Area == BaselineDifferenceArea.GameData);
                var settingDifferences = _lastCompliance.Differences.Count - modDifferences;
                CampaignComplianceStatusText.Text = $"CAMPAIGN NOT READY — {modDifferences} mod/file differences, {settingDifferences} setting differences.";
                CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
                LaunchKspButton.IsEnabled = false;
                ShowComplianceDifferences(_lastCompliance);
                if (modDifferences == 0 && settingDifferences > 0 && MessageBox.Show(
                        "Campaign difficulty or mod settings differ. Align them with the official baseline?\n\nA backup will be created first. Your progress, vessels and contracts will not be replaced.",
                        "Align Campaign Settings", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                {
                    var aligned = await new CampaignSettingsAligner().AlignAsync(package, savePath, _lastCompliance);
                    _lastCompliance = await new CampaignBaselineComparer().CompareAsync(
                        package.Manifest, savePath, progress);
                    if (_lastCompliance.ReadyToLaunch)
                    {
                        CampaignComplianceStatusText.Text = $"READY TO LAUNCH — {aligned.FilesUpdated} setting file(s) aligned. Backup: {aligned.BackupDirectory}";
                        CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("GreenBrush");
                        LaunchKspButton.IsEnabled = true;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            CampaignComplianceStatusText.Text = exception.Message;
            CampaignComplianceStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            LaunchKspButton.IsEnabled = false;
        }
        finally
        {
            CheckInstallationButton.IsEnabled = true;
        }
    }

    private static void ShowComplianceDifferences(CampaignComplianceResult result)
    {
        var lines = result.Differences.Take(18).Select(item => item.Kind == BaselineDifferenceKind.ValueMismatch
            ? $"{item.DisplayName}: player '{item.Actual}' / campaign '{item.Expected}'"
            : $"{item.Kind.ToString().ToUpperInvariant()}: {item.Path}");
        var more = result.Differences.Count > 18 ? $"\n…and {result.Differences.Count - 18} more differences." : string.Empty;
        MessageBox.Show(string.Join("\n", lines) + more, "Campaign Installation Differences",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ViewIgnoredMods_Click(object sender, RoutedEventArgs e)
    {
        if (CampaignsList.SelectedItem is not CampaignListItem campaign ||
            !_localBaselines.TryGetValue(campaign.CampaignCode, out var package)) return;
        var folders = package.Manifest.IgnoredGameDataFolders;
        var text = folders.Count == 0
            ? "This campaign does not ignore any GameData mod folders."
            : "These exact GameData folders are ignored by campaign installation checks:\n\n" +
              string.Join("\n", folders.Select(folder => $"• {folder}"));
        MessageBox.Show(text, "Campaign Ignored Mods — Read Only", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var username = LoginUsernameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(username) || LoginPasswordBox.Password.Length == 0)
        {
            ShowAuthFeedback("Enter your KSR username or email and password.");
            return;
        }
        if (string.IsNullOrWhiteSpace(LauncherSession.ServerUrl))
        {
            ShowAuthFeedback("The KSR server has not been configured.");
            return;
        }

        HideAuthFeedback();
        SetLoginBusy(true);
        try
        {
            var session = await _platformClient.LoginAsync(
                LauncherSession.ServerUrl,
                username,
                LoginPasswordBox.Password);
            LoginPasswordBox.Clear();
            await ApplySessionAsync(session);
            if (RememberMeCheckBox.IsChecked == true)
            {
                _rememberedSession = new RememberedSession(session.User.Username, LauncherSession.ServerUrl, session.RefreshToken);
                LauncherCredentialStore.Save(_rememberedSession);
            }
            else
            {
                _rememberedSession = null;
                LauncherCredentialStore.Clear();
            }
        }
        catch (KsrApiException exception)
        {
            ShowAuthFeedback(ApiErrorMessages.ForAuthentication(exception));
        }
        catch (HttpRequestException)
        {
            ShowAuthFeedback("The KSR server is currently unreachable. Check your connection and try again.");
        }
        catch (Exception exception)
        {
            ShowAuthFeedback(exception.Message);
        }
        finally
        {
            SetLoginBusy(false);
        }
    }

    private async Task TryRestoreSessionAsync()
    {
        if (_rememberedSession is null ||
            string.IsNullOrWhiteSpace(LauncherSession.ServerUrl) ||
            !string.Equals(_rememberedSession.ServerUrl.TrimEnd('/'), LauncherSession.ServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return;

        HideAuthFeedback();
        SetLoginBusy(true, "RESTORING…");
        try
        {
            KsrLoginSession session;
            try
            {
                session = await _platformClient.RefreshAsync(LauncherSession.ServerUrl, _rememberedSession.RefreshToken);
            }
            catch (KsrApiException exception)
            {
                _rememberedSession = null;
                RememberMeCheckBox.IsChecked = false;
                TryClearRememberedSession();
                ShowAuthFeedback(exception.StatusCode == 401
                    ? "Your saved session has expired. Sign in again."
                    : ApiErrorMessages.ForAuthentication(exception));
                return;
            }

            // The API rotates refresh tokens: replace the old credential immediately.
            _rememberedSession = new RememberedSession(session.User.Username, LauncherSession.ServerUrl, session.RefreshToken);
            LauncherCredentialStore.Save(_rememberedSession);
            await ApplySessionAsync(session);
        }
        catch (HttpRequestException)
        {
            ShowAuthFeedback("Your saved session could not be restored because the KSR server is unreachable.");
        }
        catch (Exception exception)
        {
            ShowAuthFeedback($"Your saved session could not be restored: {exception.Message}");
        }
        finally
        {
            SetLoginBusy(false);
        }
    }

    private async Task ApplySessionAsync(KsrLoginSession session)
    {
        var serverUrl = LauncherSession.ServerUrl ?? throw new InvalidOperationException("The KSR server has not been configured.");
        var campaigns = await _platformClient.GetCampaignsAsync(serverUrl, session.AccessToken);
        LauncherSession.Username = session.User.Username;
        LauncherSession.AccessToken = session.AccessToken;
        LauncherSession.RefreshToken = session.RefreshToken;
        LauncherSession.AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresIn);
        ReplaceCampaigns(campaigns.Select(ToCampaignListItem));
        UpdateSessionVisuals();
        await RefreshServerStatusAsync();
    }

    private static CampaignListItem ToCampaignListItem(KsrCampaign campaign) => new(
        campaign.CampaignCode,
        campaign.Name,
        campaign.Role.ToUpperInvariant(),
        campaign.NationId ?? "NOT SELECTED",
        campaign.Status.ToUpperInvariant(),
        !string.IsNullOrWhiteSpace(campaign.MasterSaveSha256),
        campaign.MasterSaveSha256,
        campaign.MasterSaveSize,
        campaign.BaselineSchemaVersion,
        campaign.BaselineSha256);

    private void SetLoginBusy(bool busy, string busyText = "SIGNING IN…")
    {
        LoginUsernameTextBox.IsEnabled = !busy;
        LoginPasswordBox.IsEnabled = !busy;
        RememberMeCheckBox.IsEnabled = !busy;
        SignInButton.IsEnabled = !busy;
        SignInButton.Content = busy ? busyText : "SIGN IN";
    }

    private void ShowAuthFeedback(string message)
    {
        AuthFeedbackText.Text = message;
        AuthFeedbackText.Visibility = Visibility.Visible;
    }

    private void HideAuthFeedback()
    {
        AuthFeedbackText.Text = string.Empty;
        AuthFeedbackText.Visibility = Visibility.Collapsed;
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        _rememberedSession = null;
        RememberMeCheckBox.IsChecked = false;
        TryClearRememberedSession();
        LauncherSession.AccessToken = null;
        LauncherSession.RefreshToken = null;
        LauncherSession.AccessTokenExpiresAtUtc = null;
        _campaigns.Clear();
        RefreshCampaignState();
        LoginPasswordBox.Clear();
        HideAuthFeedback();
        UpdateSessionVisuals();
    }

    private static void TryClearRememberedSession()
    {
        try { LauncherCredentialStore.Clear(); }
        catch { /* Signing out must still clear the in-memory session. */ }
    }

    private void CreateAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureServerConfigured()) return;
        var dialog = new CreateAccountWindow(_platformClient, LauncherSession.ServerUrl!) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.CreatedUsername))
        {
            LoginUsernameTextBox.Text = dialog.CreatedUsername;
            ShowAuthFeedback("Account created. Verify your email before signing in.");
            LoginPasswordBox.Focus();
        }
    }

    private void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureServerConfigured()) return;
        new PasswordRecoveryWindow(_platformClient, LauncherSession.ServerUrl!) { Owner = this }.ShowDialog();
    }

    private bool EnsureServerConfigured()
    {
        if (!string.IsNullOrWhiteSpace(LauncherSession.ServerUrl)) return true;
        MessageBox.Show(
            "Configure the KSR server URL in Settings before using account services.",
            "KSR Account", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ServerSettingsWindow(LauncherSession.ServerUrl) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        LauncherSession.ServerUrl = dialog.ServerUrl;
        UpdateSessionVisuals();
        await RefreshServerStatusAsync();
    }

    private void UpdateSessionVisuals()
    {
        var signedIn = LauncherSession.IsAuthenticated;
        AccountPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        LoginPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        CreateAccountButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountNameText.Text = LauncherSession.Username;
        UpdateCreateRaceState();

        var serverConfigured = !string.IsNullOrWhiteSpace(LauncherSession.ServerUrl);
        var statusBrush = (System.Windows.Media.Brush)FindResource("OrangeBrush");
        ServerStatusDot.Foreground = statusBrush;
        ServerStatusText.Foreground = statusBrush;
        ServerStatusText.Text = serverConfigured ? "CHECKING SERVER…" : "SERVER SETUP PENDING";
        LoginServerStatusDot.Foreground = statusBrush;
        LoginServerStatusText.Foreground = statusBrush;
        LoginServerStatusText.Text = serverConfigured ? "CHECKING…" : "SETUP PENDING";
    }

    private async Task RefreshServerStatusAsync()
    {
        if (string.IsNullOrWhiteSpace(LauncherSession.ServerUrl)) return;
        try
        {
            await _platformClient.GetHealthAsync(LauncherSession.ServerUrl);
            SetServerStatus("SERVER ONLINE", "ONLINE", "GreenBrush");
        }
        catch
        {
            SetServerStatus("SERVER OFFLINE", "OFFLINE", "MutedBrush");
        }
    }

    private void SetServerStatus(string headerText, string loginText, string brushKey)
    {
        var brush = (System.Windows.Media.Brush)FindResource(brushKey);
        ServerStatusDot.Foreground = brush;
        ServerStatusText.Foreground = brush;
        ServerStatusText.Text = headerText;
        LoginServerStatusDot.Foreground = brush;
        LoginServerStatusText.Foreground = brush;
        LoginServerStatusText.Text = loginText;
    }

    private bool EnsureKspRoot()
    {
        if (!string.IsNullOrWhiteSpace(_kspRoot) && File.Exists(Path.Combine(_kspRoot, "KSP_x64.exe"))) return true;
        var picker = new OpenFolderDialog { Title = "Select the Kerbal Space Program folder", Multiselect = false };
        if (picker.ShowDialog(this) != true) return false;
        if (!File.Exists(Path.Combine(picker.FolderName, "KSP_x64.exe")))
        {
            MessageBox.Show("KSP_x64.exe was not found in the selected folder.", "KSR Platform", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _kspRoot = picker.FolderName;
        return true;
    }
}

internal static class LauncherSession
{
    public static string Username { get; set; } = "PLAYER";
    public static string? ServerUrl { get; set; } =
        Environment.GetEnvironmentVariable("KSR_SERVER_URL") ?? KsrPlatformClient.ProductionServerUrl;
    public static string? AccessToken { get; set; }
    public static string? RefreshToken { get; set; }
    public static DateTimeOffset? AccessTokenExpiresAtUtc { get; set; }
    public static string? CampaignCode { get; set; }
    public static string? CampaignName { get; set; }
    public static bool IsAuthenticated => !string.IsNullOrWhiteSpace(AccessToken);
}

internal sealed record CampaignListItem(
    string CampaignCode,
    string Name,
    string Role,
    string Nation,
    string Status,
    bool MasterSaveAvailable,
    string? MasterSaveSha256 = null,
    long? MasterSaveSize = null,
    int? BaselineSchemaVersion = null,
    string? BaselineSha256 = null)
{
    public string RoleColor => string.Equals(Role, "ADMIN", StringComparison.OrdinalIgnoreCase) ? "#FFF57C00" : "#FF4AB8F1";
    public string StatusColor => string.Equals(Status, "ACTIVE", StringComparison.OrdinalIgnoreCase) ? "#FF80D420" : "#FF9BA0A3";
}
