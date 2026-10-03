using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace KSRLoggerPerformance
{
    public static class ConfigCache
    {
        private sealed class Entry
        {
            public FileStamp Stamp;
            public long NextCheck;
            public ConfigNode Node;
            public bool Loaded;
        }
        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static Entry Get(string path)
        {
            Entry entry;
            if (!entries.TryGetValue(path, out entry)) { entry = new Entry(); entries.Add(path, entry); }
            long now = Stopwatch.GetTimestamp();
            if (now >= entry.NextCheck)
            {
                var stamp = FileStamp.Read(path);
                if (!stamp.Equals(entry.Stamp)) { entry.Stamp = stamp; entry.Node = null; entry.Loaded = false; }
                entry.NextCheck = now + Stopwatch.Frequency;
            }
            return entry;
        }
        public static bool Exists(string path) { return Get(path).Stamp.Exists; }
        public static ConfigNode Load(string path)
        {
            Entry entry = Get(path);
            if (!entry.Loaded)
            {
                entry.Node = entry.Stamp.Exists ? ConfigNode.Load(path) : null;
                entry.Loaded = true;
            }
            return entry.Node;
        }
        public static void Clear() { entries.Clear(); }
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class LoggerPerformance : MonoBehaviour
    {
        private const string PatchId = "KerbalSpaceRace.LoggerPerformance.20261003";
        private static readonly ExportCoordinator exports = new ExportCoordinator();
        private static readonly Dictionary<string, AchievementRow> scratch = new Dictionary<string, AchievementRow>(StringComparer.Ordinal);
        private static readonly Dictionary<string, AchievementRow> previous = new Dictionary<string, AchievementRow>(StringComparer.Ordinal);
        private static readonly Dictionary<string, FileStamp> outputStamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> awaiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly FieldInfo achievementsField = typeof(SpaceAge.SpaceAgeScenario).GetField("achievements", BindingFlags.NonPublic | BindingFlags.Instance);
        private static string previousPath;
        private static object previousSource;
        private static double previousStart;
        private static int previousYear;
        private static bool forced, announced, warned;
        private Harmony harmony;

        public void Awake()
        {
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            try
            {
                Type store = FindType("KSRParameterLogger.ParameterLogStore");
                Type gate = FindType("KSRParameterLogger.KsrRaceLoggerGate");
                if (store == null || gate == null || achievementsField == null) throw new InvalidOperationException("Installed logger or SpaceAge layout is unsupported.");
                MethodInfo refresh = Required(store, "RefreshLiveSpaceAgeRecords");
                MethodInfo flush = Required(store, "FlushRecordExports");
                harmony = new Harmony(PatchId);
                foreach (MethodInfo method in gate.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    if (method.DeclaringType == gate) PatchConfigLoads(method);
                // RemoteLogger also polls legacy/race state; share the metadata-based configuration cache.
                Type remoteLegacy = FindType("KerbalSpaceRace.RemoteLogger.LegacyCampaignMode");
                if (remoteLegacy != null) PatchConfigLoads(Required(remoteLegacy, "IsEnabled"));
                Type remote = FindType("KerbalSpaceRace.RemoteLogger.KSRRemoteLogger");
                Type remoteState = remote == null ? null : remote.GetNestedType("RaceLoggerState", BindingFlags.Public | BindingFlags.NonPublic);
                if (remoteState != null) PatchConfigLoads(Required(remoteState, "TryLoad"));
                harmony.Patch(refresh, prefix: new HarmonyMethod(typeof(LoggerPerformance), "BeforeRefresh"));
                harmony.Patch(flush, prefix: new HarmonyMethod(typeof(LoggerPerformance), "BeforeFlush"),
                    postfix: new HarmonyMethod(typeof(LoggerPerformance), "AfterFlush"),
                    finalizer: new HarmonyMethod(typeof(LoggerPerformance), "FinishFlush"));
                harmony.Patch(Required(store, "ClearRuntimeState"), postfix: new HarmonyMethod(typeof(LoggerPerformance), "ResetSave"));
                UnityEngine.Debug.Log("[KSR Logger Performance] v1.0 active: metadata config cache; SpaceAge exports on change; single background CSV writer.");
            }
            catch (Exception exception)
            {
                if (harmony != null) harmony.UnpatchAll(PatchId);
                UnityEngine.Debug.LogError("[KSR Logger Performance] Patch not activated; original logger retained: " + exception);
            }
        }

        private static Type FindType(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            return null;
        }
        private static MethodInfo Required(Type type, string name)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            return method;
        }
        private void PatchConfigLoads(MethodInfo method)
        {
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(LoggerPerformance), "CachedLoads"));
        }
        private static IEnumerable<CodeInstruction> CachedLoads(IEnumerable<CodeInstruction> instructions)
        {
            var exists = typeof(File).GetMethod("Exists", new[] { typeof(string) });
            var load = typeof(ConfigNode).GetMethod("Load", new[] { typeof(string) });
            var cachedExists = typeof(ConfigCache).GetMethod("Exists");
            var cachedLoad = typeof(ConfigCache).GetMethod("Load");
            foreach (CodeInstruction instruction in instructions)
            {
                if (Equals(instruction.operand, exists)) instruction.operand = cachedExists;
                else if (Equals(instruction.operand, load)) instruction.operand = cachedLoad;
                yield return instruction;
            }
        }

        private static void ResetSave()
        {
            previous.Clear(); scratch.Clear(); previousSource = null; previousPath = null;
            ConfigCache.Clear();
        }
        private static double CampaignStart(string root, string save)
        {
            ConfigNode legacy = ConfigCache.Load(Path.Combine(root, "GameData", "KerbalSpaceRace", "PluginData", "LegacyCampaign.cfg"));
            legacy = legacy == null ? null : (legacy.name == "KSR_LEGACY_CAMPAIGN" ? legacy : legacy.GetNode("KSR_LEGACY_CAMPAIGN"));
            bool enabled;
            if (legacy != null && bool.TryParse(legacy.GetValue("enabled"), out enabled) && enabled) return 0;
            ConfigNode state = ConfigCache.Load(Path.Combine(root, "saves", save, "KSR", "KSRRaceState.cfg"));
            state = state == null ? null : (state.name == "KSR_RACE_STATE" ? state : state.GetNode("KSR_RACE_STATE"));
            double start;
            return state != null && double.TryParse(state.GetValue("campaignStartUt"), NumberStyles.Float, CultureInfo.InvariantCulture, out start)
                && !double.IsNaN(start) && !double.IsInfinity(start) && start >= 0 ? start : 0;
        }

        private static object CaptureRows()
        {
            scratch.Clear();
            var instance = SpaceAge.SpaceAgeScenario.Instance;
            if (instance != null)
            {
                var source = achievementsField.GetValue(instance) as IDictionary<string, SpaceAge.Achievement>;
                if (source == null) throw new InvalidOperationException("SpaceAge achievement dictionary unavailable.");
                foreach (var entry in source)
                {
                    var achievement = entry.Value;
                    if (achievement == null || !achievement.Valid || achievement.Proto == null) continue;
                    var proto = achievement.Proto;
                    scratch[entry.Key] = new AchievementRow {
                        Name = proto.Name, Body = proto.IsBodySpecific ? achievement.Body : null,
                        Time = proto.HasTime ? achievement.Time : 0,
                        HasValue = proto.HasValue, Value = proto.HasValue ? achievement.Value : 0,
                        Hero = achievement.Hero, Ids = proto.Unique ? achievement.Ids : null
                    };
                }
                return instance;
            }
            // Startup fallback: use the already-loaded save node, never read persistent.sfs to poll it.
            ConfigNode config = HighLogic.CurrentGame == null ? null : HighLogic.CurrentGame.config;
            if (config == null) return null;
            foreach (ConfigNode scenario in config.GetNodes("SCENARIO"))
            {
                if (!string.Equals(scenario.GetValue("name"), "SpaceAgeScenario", StringComparison.OrdinalIgnoreCase)) continue;
                ConfigNode achievements = scenario.GetNode("ACHIEVEMENTS");
                if (achievements == null) return null;
                int index = 0;
                foreach (ConfigNode node in achievements.GetNodes("ACHIEVEMENT"))
                {
                    double time, value;
                    double.TryParse(node.GetValue("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out time);
                    double.TryParse(node.GetValue("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                    scratch[(index++).ToString(CultureInfo.InvariantCulture)] = new AchievementRow {
                        Name = node.GetValue("name"), Body = node.GetValue("body"), Hero = node.GetValue("hero"), Ids = node.GetValue("ids"),
                        Time = time, Value = value, HasValue = node.GetValue("value") != null
                    };
                }
                return achievements;
            }
            return null;
        }

        private static void DrainResults()
        {
            ExportResult result;
            while ((result = exports.TakeResult()) != null)
            {
                awaiting.Remove(result.Path);
                if (result.Error != null)
                {
                    outputStamps.Remove(result.Path);
                    if (result.Path == previousPath) previousPath = null;
                    UnityEngine.Debug.LogError("[KSR Logger Performance] CSV export failed; will retry: " + result.Error);
                }
                else
                {
                    outputStamps[result.Path] = result.Stamp;
                    if (!announced)
                    {
                        announced = true;
                        UnityEngine.Debug.Log("[KSR Logger Performance] First background export complete in " + result.Milliseconds.ToString("F1", CultureInfo.InvariantCulture) + " ms. Unchanged achievements skip subsequent exports.");
                    }
                }
            }
        }

        private static bool BeforeRefresh()
        {
            try
            {
                DrainResults();
                string rawSave = HighLogic.SaveFolder;
                if (string.IsNullOrEmpty(rawSave)) return false;
                string save = rawSave;
                foreach (char invalid in Path.GetInvalidFileNameChars()) save = save.Replace(invalid, '_');
                string root = KSPUtil.ApplicationRootPath;
                string path = Path.Combine(root, "GameData", "KSRParameterLogger", "PluginData", save, save + "-spaceage-achievements.csv");
                object source = CaptureRows();
                if (source == null) return false;
                double ut = Planetarium.GetUniversalTime();
                double start = CampaignStart(root, rawSave);
                int year = (int)Math.Floor(Math.Max(0, ut - start) / 9203545.0);
                bool changed = forced || previousPath != path || !ReferenceEquals(source, previousSource) ||
                    previousStart != start || previousYear != year || scratch.Count != previous.Count;
                if (!changed)
                {
                    foreach (var row in scratch)
                    {
                        AchievementRow old;
                        if (!previous.TryGetValue(row.Key, out old) || !row.Value.Equals(old)) { changed = true; break; }
                    }
                }
                if (!changed && !awaiting.Contains(path))
                {
                    FileStamp last;
                    changed = !outputStamps.TryGetValue(path, out last) || !last.Equals(FileStamp.Read(path));
                }
                if (!changed) return false;
                var rows = new AchievementRow[scratch.Count];
                scratch.Values.CopyTo(rows, 0);
                exports.Enqueue(new ExportSnapshot(path, save, ut, start, rows));
                awaiting.Add(path);
                previous.Clear();
                foreach (var row in scratch) previous.Add(row.Key, row.Value);
                previousPath = path; previousSource = source; previousStart = start; previousYear = year;
                return false;
            }
            catch (Exception exception)
            {
                if (!warned)
                {
                    warned = true;
                    UnityEngine.Debug.LogWarning("[KSR Logger Performance] Using original exporter after capture failure: " + exception);
                }
                return true;
            }
        }
        private static void BeforeFlush() { forced = true; }
        private static void AfterFlush()
        {
            forced = false;
            if (!exports.Flush(10000)) UnityEngine.Debug.LogError("[KSR Logger Performance] Export flush timed out; export is still pending.");
            DrainResults();
        }
        private static Exception FinishFlush(Exception __exception) { forced = false; return __exception; }
        public void OnApplicationQuit() { exports.Flush(2000); }
    }
}
