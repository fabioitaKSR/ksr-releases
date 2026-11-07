using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KsrLauncher.App;
using KsrLauncher.Core;

class Probe
{
    static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static string Output = "";
    static int Ticks;
    static int UiThread;
    static OrbitActivityIndicator Orbit = null!;
    static double LastAngle;
    static int MovingSamples;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static object? Invoke(object owner, string method, params object[] args) => owner.GetType().GetMethod(method, Private)!.Invoke(owner, args);
    [STAThread] static void Main(string[] args)
    {
        Output = Path.GetFullPath(args[0]);
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        UiThread = Environment.CurrentManagedThreadId;
        var window = new Window { Title = "KSR — SYNTHETIC LOCAL INSTALL TEST (NO NETWORK)", Width = 430, Height = 155,
            Background = new SolidColorBrush(Color.FromRgb(11, 16, 18)), WindowStartupLocation = WindowStartupLocation.CenterScreen };
        Orbit = new OrbitActivityIndicator { Width = 64, Height = 64 };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20) };
        panel.Children.Add(Orbit); panel.Children.Add(new TextBlock { Text = "Synthetic slow installation...\nNo game or network access.", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16,0,0,0) });
        window.Content = panel;
        int exit = 0;
        window.Loaded += async (_, _) =>
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) => { Interlocked.Increment(ref Ticks); var angle = ((RotateTransform)Orbit.FindName("InnerRotation")).Angle; if (Math.Abs(angle - LastAngle) > 0.1) MovingSamples++; LastAngle = angle; };
            timer.Start();
            try
            {
                await RunScenario("success"); await RunScenario("cancel"); await RunScenario("error");
                Orbit.IsActive = true; await Task.Delay(200);
                Check(((RotateTransform)Orbit.FindName("InnerRotation")).HasAnimatedProperties, "No animation clock");
                panel.Visibility = Visibility.Collapsed; await Task.Delay(100);
                Check(!((RotateTransform)Orbit.FindName("InnerRotation")).HasAnimatedProperties, "Hidden indicator kept running");
                panel.Visibility = Visibility.Visible; await Task.Delay(150);
                Check(((RotateTransform)Orbit.FindName("InnerRotation")).HasAnimatedProperties, "Visible active indicator did not restart");
                Orbit.IsActive = false;
                RenderScreens();
                Console.WriteLine("PASS WPF: success/error/cancel, real worker extraction/install, responsive dispatcher and advancing orbit, review on UI thread, lock, rollback, idle/hidden clocks stopped; shared normal/Full/Probalism/future beta layouts. Synthetic local fixtures; no network or settings writes.");
            }
            catch (Exception e) { Console.WriteLine(e); exit = 1; }
            finally { timer.Stop(); Orbit.IsActive = false; window.Close(); app.Shutdown(exit); }
        };
        app.Run(window); Environment.ExitCode = exit;
    }
    static async Task RunScenario(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "ksr-orbit-ui-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "game"); var data = Path.Combine(root, "data"); var assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(game); Directory.CreateDirectory(assets); File.WriteAllText(Path.Combine(game, "KSP_x64.exe"), "synthetic");
        var manifest = new ReleaseManifest { SchemaVersion = 1, Product = "KerbalSpaceRace", Version = "1.2.0", Preserve = ["GameData/**/PluginData/**"] };
        for (int i = 0; i < 2; i++)
        {
            var target = Path.Combine(game, "GameData", "Example" + i); Directory.CreateDirectory(Path.Combine(target, "PluginData"));
            File.WriteAllText(Path.Combine(target, "example.dll"), "original" + i); File.WriteAllText(Path.Combine(target, "PluginData", "settings.cfg"), "private" + i);
            File.WriteAllText(Path.Combine(target, "extra-part.cfg"), "preserve installed component" + i);
            var zipPath = Path.Combine(assets, "example" + i + ".zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("Example/example.dll").Open())) writer.Write("updated" + i);
                for (int j = 0; j < 200; j++) { using var writer = new StreamWriter(zip.CreateEntry("Example/parts/part" + j + ".cfg").Open()); writer.Write("synthetic part"); }
            }
            manifest.Components.Add(new ComponentManifest { Id = "example" + i, Source = "Example", Target = "GameData/Example" + i, Asset = Path.GetFileName(zipPath), Sha256 = await PackageService.ComputeSha256Async(zipPath), Size = new FileInfo(zipPath).Length, RequiredFiles = ["example.dll"] });
        }
        var original = await Inventory(game);
        using var cancel = new CancellationTokenSource();
        int reviews = 0, installs = 0, checks = 0, extracts = 0;
        var engine = new UpdateEngine(reviewInstallation: (_, warnings) => { Check(Environment.CurrentManagedThreadId == UiThread && warnings.Count > 0, "Review lost UI context"); reviews++; return true; });
        Orbit.IsActive = true; await Task.Delay(160);
        var progress = new DirectProgress(p =>
        {
            if (p.Phase is "EXTRACTING" or "INSTALLING")
            {
                Check(Environment.CurrentManagedThreadId != UiThread, "Installation I/O remained on UI thread");
                if (p.Phase == "INSTALLING")
                {
                    installs++;
                    if (installs == 1)
                    {
                        try { using var unexpected = LauncherStorageCleanup.AcquireUpdateLock(data); throw new Exception("Parallel update lock missing"); }
                        catch (IOException) { }
                        var beforeTicks = Volatile.Read(ref Ticks); var beforeMoving = Volatile.Read(ref MovingSamples);
                        Thread.Sleep(900); // Test-only deterministic slow installation barrier.
                        Check(Volatile.Read(ref Ticks) - beforeTicks >= 5, "Dispatcher stalled during installation");
                        Check(Volatile.Read(ref MovingSamples) - beforeMoving >= 4, "Orbit stalled during installation"); checks++;
                        if (scenario == "cancel") cancel.Cancel();
                    }
                    if (scenario == "error" && installs == 2) throw new IOException("Synthetic second component failure");
                }
                else
                {
                    extracts++;
                    if (extracts == 1)
                    {
                        var beforeTicks = Volatile.Read(ref Ticks); var beforeMoving = Volatile.Read(ref MovingSamples);
                        Thread.Sleep(900); // Test-only deterministic extraction barrier.
                        Check(Volatile.Read(ref Ticks) - beforeTicks >= 5, "Dispatcher stalled during extraction");
                        Check(Volatile.Read(ref MovingSamples) - beforeMoving >= 4, "Orbit stalled during extraction");
                    }
                }
            }
        });
        try
        {
            var result = await engine.RunAsync(manifest, new(game, data), assets, true, UpdatePolicy.InstallOrRepair, progress, cancel.Token);
            Check(scenario == "success" && result.Applied, "Expected scenario failure did not occur");
        }
        catch (OperationCanceledException) when (scenario == "cancel") { }
        catch (IOException e) when (scenario == "error" && e.Message == "Synthetic second component failure") { }
        finally { Orbit.IsActive = false; }
        Check(reviews == 1 && checks == 1 && extracts == 2, "Missing scenario stages");
        Check(!((RotateTransform)Orbit.FindName("InnerRotation")).HasAnimatedProperties && Orbit.Visibility == Visibility.Collapsed, "Completion left animation running");
        var after = await Inventory(game);
        if (scenario == "success")
        {
            Check(File.ReadAllText(Path.Combine(game,"GameData/Example0/example.dll")) == "updated0", "Update missing");
            Check(original.Where(x => !x.Key.EndsWith("example.dll")).All(x => after.TryGetValue(x.Key, out var h) && h == x.Value), "Private/extra installed files changed");
        }
        else Check(original.Count == after.Count && original.All(x => after.TryGetValue(x.Key, out var h) && h == x.Value) && !File.Exists(StateStore.GetStatePath(data)), "Rollback did not restore files/state");
        using (LauncherStorageCleanup.AcquireUpdateLock(data)) { }
        Console.WriteLine($"PASS {scenario}: dispatcher responsive during 900ms post-download pause, review UI, worker installation, lock released, expected files/state; fixture {root}");
    }
    static async Task<Dictionary<string,string>> Inventory(string root)
    {
        var result = new Dictionary<string,string>(); foreach (var f in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)) result.Add(Path.GetRelativePath(root,f),await PackageService.ComputeSha256Async(f)); return result;
    }
    static void RenderScreens()
    {
        var main = new MainWindow();
        var generation = (int)Invoke(main,"ShowBlockingOperation","INSTALLING", "Synthetic component 2 / 4",true)!;
        Invoke(main,"ShowBlockingProgress",generation,"INSTALLING","Installing Example · 2 / 4",0L,0L,50d);
        Check(((OrbitActivityIndicator)main.FindName("UpdateActivity")).IsActive, "Normal indicator inactive");
        Check(!((DispatcherTimer)main.GetType().GetField("_downloadCountdownTimer",Private)!.GetValue(main)!).IsEnabled,"Installation kept download polling");
        Render(main,"normal-install.png",1080,720);
        Invoke(main,"ShowBlockingProgress",generation,"RESTORING","Restoring original files",0L,0L,0d);
        Check(((TextBlock)main.FindName("UpdateActivityText")).Text.Contains("Restoring"),"Wrong rollback label");
        Invoke(main,"HideBlockingOperation"); Check(!((OrbitActivityIndicator)main.FindName("UpdateActivity")).IsActive,"Normal completion animation active"); main.Close();
        var session = typeof(MainWindow).Assembly.GetType("KsrLauncher.App.LauncherSession")!;
        var access = typeof(MainWindow).Assembly.GetType("KsrLauncher.App.BetaAccess")!;
        session.GetProperty("Username")!.SetValue(null,"SYNTHETIC"); session.GetProperty("AccessToken")!.SetValue(null,"synthetic-not-a-token");
        foreach(var id in new[]{"ksr-full","probalism","future-beta"})
        {
            var module = new KsrBetaModule(id,id=="ksr-full"?"KSR Full Mode":id,"Synthetic test. No network.",0,"0.1.0","installation");
            session.GetProperty("CanAccessTestGame")!.SetValue(null,id=="ksr-full"); access.GetProperty("Modules")!.SetValue(null,new[]{module});
            var beta = new ServerSettingsWindow("https://synthetic.invalid",module);
            Check(!((OrbitActivityIndicator)beta.FindName("TestActivity")).IsActive,"Idle beta animation");
            ((StackPanel)beta.FindName("TestActivityPanel")).Visibility = Visibility.Visible;
            ((OrbitActivityIndicator)beta.FindName("TestActivity")).IsActive = true;
            ((TextBlock)beta.FindName("TestActivityText")).Text = "Installing files. This can take a few minutes...";
            ((ProgressBar)beta.FindName("TestKspMaintenanceProgress")).Value = 50;
            Render(beta,id+"-install.png",760,625);
            ((OrbitActivityIndicator)beta.FindName("TestActivity")).IsActive=false; beta.Close();
        }
        access.GetMethod("Clear")!.Invoke(null,null);
    }
    static void Render(Window w,string name,int width,int height)
    {
        var content=(Grid)w.Content; content.Background=w.Background; content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var f=File.Create(Path.Combine(Output,name));encoder.Save(f);
    }
    sealed class DirectProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress> { public void Report(UpdateProgress value)=>report(value); }
}
