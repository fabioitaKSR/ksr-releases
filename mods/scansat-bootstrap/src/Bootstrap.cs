using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

[assembly: AssemblyTitle("KerbalSpaceRace.SCANsatBootstrap")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: KSPAssembly("KerbalSpaceRace.SCANsatBootstrap", 1, 0)]

namespace KSR.ScanBootstrap
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class Bootstrap : MonoBehaviour
    {
        internal static Task<Result> Work;
        internal static bool LoadedPatch;
        public void Awake()
        {
            if (Work != null) return;
            string root = KSPUtil.ApplicationRootPath;
            LoadedPatch = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "SCANsat")
                .Any(a => a.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                    .Cast<AssemblyInformationalVersionAttribute>().Any(v => v.InformationalVersion.EndsWith(".ksr-heightcache-v2", StringComparison.Ordinal)));
            Debug.Log("[KSR SCANsat Bootstrap] Checking verified height-cache patch on worker.");
            Work = Task.Run(() => Installer.Install(root, Installer.PatchHash, Installer.OriginalHash));
        }
    }

    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public sealed class RestartNotice : MonoBehaviour
    {
        private static bool reported;
        private bool visible;
        private string message;
        private Rect box;
        public void Update()
        {
            if (reported || Bootstrap.Work == null || !Bootstrap.Work.IsCompleted) return;
            reported = true;
            Result result = Bootstrap.Work.Result;
            Debug.Log("[KSR SCANsat Bootstrap] " + result.Message);
            if (result.RetryAfterExit)
            {
                try
                {
                    string root = System.IO.Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/');
                    string helper = System.IO.Path.Combine(root, "GameData/KerbalSpaceRace/Tools/SCANsatHeightCache/KSR.SCANsatInstaller.exe");
                    var start = new System.Diagnostics.ProcessStartInfo(helper, "\"" + root + "\" " + System.Diagnostics.Process.GetCurrentProcess().Id)
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = System.IO.Path.GetDirectoryName(helper) };
                    System.Diagnostics.Process.Start(start);
                    result.Message = "SCANsat is currently locked by KSP. The verified installer will finish automatically when KSP closes. Please quit KSP, wait a few seconds, then start it again.";
                    Debug.Log("[KSR SCANsat Bootstrap] " + result.Message);
                }
                catch (Exception ex) { result.Message += " Deferred installer could not start: " + ex.Message; }
            }
            visible = result.Changed || (result.Ready && !Bootstrap.LoadedPatch) || (!result.Ready && !result.Message.StartsWith("SCANsat is not installed", StringComparison.Ordinal));
            message = result.Ready
                ? "SCANsat height cache is installed. Please close and restart KSP before continuing, so the new DLL is loaded. Your scan data and settings are preserved."
                : result.Message;
            box = new Rect((Screen.width - 520) / 2, (Screen.height - 190) / 2, 520, 190);
        }
        public void OnGUI()
        {
            if (visible) box = GUI.ModalWindow(GetInstanceID(), box, Draw, "Are still kerbal - SCANsat update");
        }
        private void Draw(int id)
        {
            GUILayout.Space(12);
            GUILayout.Label(message, new GUIStyle(GUI.skin.label) { wordWrap = true });
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Understood")) visible = false;
        }
    }
}
