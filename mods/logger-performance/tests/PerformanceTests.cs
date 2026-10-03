using System;
using System.IO;
using System.Linq;
using System.Threading;
using KSRLoggerPerformance;

public static class PerformanceTests
{
    private static int checks;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS " + name); }
    private static SpaceAge.Achievement Achievement(string name, double value)
    {
        return new SpaceAge.Achievement { Proto = new SpaceAge.ProtoAchievement { Name = name, Unique = true }, Time = 21601, Value = value, Hero = "Иван, \"test\"", Ids = "vessel-A" };
    }
    public static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            KSPUtil.ApplicationRootPath = root; HighLogic.SaveFolder = "CCCP"; Planetarium.Ut = 9203545 + 21601;
            string legacy = Path.Combine(root,"GameData","KerbalSpaceRace","PluginData","LegacyCampaign.cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(legacy));
            File.WriteAllText(legacy,"KSR_LEGACY_CAMPAIGN\nenabled=true\nprobe=1234");
            var originalStamp = FileStamp.Read(legacy);
            ConfigCache.Clear(); ConfigNode.Loads = 0;
            Check(ConfigCache.Load(legacy).GetValue("probe") == "1234", "initial configuration loaded");
            for (int i=0;i<1000;i++) { ConfigCache.Exists(legacy); ConfigCache.Load(legacy); }
            Check(ConfigNode.Loads == 1, "1000 cache accesses cause one content read");
            File.WriteAllText(legacy,"KSR_LEGACY_CAMPAIGN\nenabled=true\nprobe=5678");
            File.SetLastWriteTimeUtc(legacy,new DateTime(originalStamp.WriteTicks,DateTimeKind.Utc).AddSeconds(2));
            Thread.Sleep(1100);
            Check(FileStamp.Read(legacy).Length == originalStamp.Length && ConfigCache.Load(legacy).GetValue("probe") == "5678", "same-size modification detected by timestamp");
            Check(ConfigNode.Loads == 2, "only changed config reparsed");
            var plugin = new LoggerPerformance(); plugin.Awake();
            Check(UnityEngine.Debug.Messages.Any(message=>message.Contains("v1.0 active")), "Harmony activates all production patch targets");
            ConfigCache.Clear(); ConfigNode.Loads=0;
            for(int i=0;i<100;i++) KSRParameterLogger.KsrRaceLoggerGate.Read(legacy);
            Check(ConfigNode.Loads == 1, "real Harmony transpiler caches gate reads");
            for(int i=0;i<100;i++) KerbalSpaceRace.RemoteLogger.KSRRemoteLogger.RaceLoggerState.TryLoad(legacy);
            Check(ConfigNode.Loads == 1, "remote state shares cached configuration");
            SpaceAge.SpaceAgeScenario.Instance = new SpaceAge.SpaceAgeScenario();
            var record = Achievement("Zeta",1234); SpaceAge.SpaceAgeScenario.Instance.Set("Zeta",record);
            string csv=Path.Combine(root,"GameData","KSRParameterLogger","PluginData","CCCP","CCCP-spaceage-achievements.csv");
            KSRParameterLogger.ParameterLogStore.FlushRecordExports();
            Check(KSRParameterLogger.ParameterLogStore.OriginalRefreshCalls == 0 && File.Exists(csv), "patched exporter replaces original and flush completes CSV");
            string first=File.ReadAllText(csv);
            Check(first.Split('\n')[0].TrimEnd('\r') == CsvExport.Header, "original 13-column CSV header preserved");
            Check(first.Contains("Иван, \"\"test\"\"") && first.Contains("Year 1, Day 1 - 0h, 0m, 1s"), "UTF-8, quoted values and original date conversion");
            var unchanged=FileStamp.Read(csv); Planetarium.Ut+=100;
            int readsBefore=ConfigNode.Loads;
            KSRParameterLogger.ParameterLogStore.RefreshLiveSpaceAgeRecords(); Thread.Sleep(100);
            Check(FileStamp.Read(csv).Equals(unchanged), "unchanged data skip export despite advancing clock");
            Check(ConfigNode.Loads==readsBefore, "unchanged polling does not reread config content");
            record.Value=5678;
            KSRParameterLogger.ParameterLogStore.RefreshLiveSpaceAgeRecords(); WaitForText(csv,"5678");
            Check(File.ReadAllText(csv).Contains("5678"), "same-size record value change exported");
            SpaceAge.SpaceAgeScenario.Instance.Set("Alpha",Achievement("Alpha",1));
            KSRParameterLogger.ParameterLogStore.FlushRecordExports();
            string[] lines=File.ReadAllLines(csv);
            Check(lines.Length==3 && lines[1].Contains("Alpha") && lines[2].Contains("Zeta"), "new achievement exported and sorted");
            SpaceAge.SpaceAgeScenario.Instance.Remove("Alpha"); KSRParameterLogger.ParameterLogStore.FlushRecordExports();
            Check(File.ReadAllLines(csv).Length==2, "rollback/removal detected");
            SpaceAge.SpaceAgeScenario.Instance=new SpaceAge.SpaceAgeScenario();
            SpaceAge.SpaceAgeScenario.Instance.Set("Zeta",Achievement("Zeta",5678));
            KSRParameterLogger.ParameterLogStore.RefreshLiveSpaceAgeRecords(); WaitForStamp(csv,FileStamp.Read(csv));
            Check(File.ReadAllText(csv).Contains("5678"), "new SpaceAge instance after reload supported");
            KSRParameterLogger.ParameterLogStore.FlushRecordExports(); File.Delete(csv);
            KSRParameterLogger.ParameterLogStore.RefreshLiveSpaceAgeRecords(); WaitForText(csv,"5678");
            Check(File.Exists(csv), "deleted output regenerated");
            HighLogic.SaveFolder="NASA"; KSRParameterLogger.ParameterLogStore.FlushRecordExports();
            string nasa=Path.Combine(root,"GameData","KSRParameterLogger","PluginData","NASA","NASA-spaceage-achievements.csv");
            Check(File.Exists(nasa) && File.ReadAllText(nasa).Contains("\"NASA\""), "save switch isolates output paths");
            SpaceAge.SpaceAgeScenario.Instance=null;
            HighLogic.CurrentGame=null;
            var beforeUnavailable=FileStamp.Read(nasa);
            KSRParameterLogger.ParameterLogStore.RefreshLiveSpaceAgeRecords(); Thread.Sleep(100);
            Check(FileStamp.Read(nasa).Equals(beforeUnavailable), "unavailable source does not erase existing export");
            var gameConfig=new ConfigNode("GAME"); var scenario=new ConfigNode("SCENARIO");
            scenario.Values["name"]="SpaceAgeScenario";
            var achievementsNode=new ConfigNode("ACHIEVEMENTS"); var savedAchievement=new ConfigNode("ACHIEVEMENT");
            savedAchievement.Values["name"]="Fallback"; savedAchievement.Values["time"]="21601";
            achievementsNode.Children.Add(savedAchievement); scenario.Children.Add(achievementsNode); gameConfig.Children.Add(scenario);
            HighLogic.CurrentGame=new Game {config=gameConfig};
            KSRParameterLogger.ParameterLogStore.FlushRecordExports();
            Check(File.ReadAllText(nasa).Contains("Fallback"), "loaded-save fallback before SpaceAge instance starts");
            File.WriteAllText(legacy,"KSR_LEGACY_CAMPAIGN\nenabled=false\nprobe=5678");
            string state=Path.Combine(root,"saves","NASA","KSR","KSRRaceState.cfg");
            Directory.CreateDirectory(Path.GetDirectoryName(state));
            File.WriteAllText(state,"KSR_RACE_STATE\ncampaignStartUt="+(Planetarium.Ut-21601).ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            ConfigCache.Clear();
            KSRParameterLogger.ParameterLogStore.FlushRecordExports();
            Check(File.ReadAllText(nasa).Contains("Year 0, Day 1 - 0h, 0m, 1s"), "Launch and Logs campaign origin preserved in non-legacy mode");
            Check(CsvExport.Date(21601,21601,true)=="Year 0, Day 0 - 0h, 0m, 0s" && CsvExport.Date(1,100,true)=="Year 0, Day 0 - 0h, 0m, 0s", "campaign offset and pre-start clamp");
            string block=Path.Combine(root,"not-a-directory"); File.WriteAllText(block,"block");
            var coordinator=new ExportCoordinator();
            coordinator.Enqueue(new ExportSnapshot(Path.Combine(block,"x.csv"),"CCCP",1,0,new AchievementRow[0]));
            Check(coordinator.Flush(3000) && coordinator.TakeResult().Error!=null, "background write failure returned without deadlock");
            string coalesced=Path.Combine(root,"coalesced.csv");
            for(int i=0;i<100;i++) coordinator.Enqueue(new ExportSnapshot(coalesced,"CCCP",i,0,new[]{new AchievementRow{Name="latest",Value=i,HasValue=true}}));
            Check(coordinator.Flush(5000) && File.ReadAllText(coalesced).Contains("\"99\""), "single writer preserves latest queued snapshot");
            Check(!UnityEngine.Debug.Messages.Any(message=>message.StartsWith("ERROR") || message.StartsWith("WARNING")), "no runtime patch/export warnings");
            Console.WriteLine("ALL " + checks + " CHECKS PASSED"); return 0;
        }
        catch(Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static void WaitForText(string path,string text)
    {
        for(int i=0;i<100;i++) { if(File.Exists(path) && File.ReadAllText(path).Contains(text)) return; Thread.Sleep(20); }
        throw new Exception("Timed out waiting for CSV content");
    }
    private static void WaitForStamp(string path,FileStamp stamp)
    {
        for(int i=0;i<100;i++) { if(!FileStamp.Read(path).Equals(stamp)) return; Thread.Sleep(20); }
        throw new Exception("Timed out waiting for reload export");
    }
}
