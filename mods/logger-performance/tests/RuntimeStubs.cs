// Offline KSP/Unity/SpaceAge stand-ins. Production builds never include this file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

public sealed class KSPAddon : Attribute
{
    public enum Startup { Instantly }
    public KSPAddon(Startup startup, bool once) { }
}
namespace UnityEngine
{
    public class Object { public static void DontDestroyOnLoad(object value) { } }
    public class MonoBehaviour { public object gameObject = new object(); }
    public static class Debug
    {
        public static readonly List<string> Messages = new List<string>();
        public static void Log(object value) { Messages.Add(value.ToString()); }
        public static void LogWarning(object value) { Messages.Add("WARNING " + value); }
        public static void LogError(object value) { Messages.Add("ERROR " + value); }
    }
}
public sealed class ConfigNode
{
    public string name;
    public readonly Dictionary<string,string> Values = new Dictionary<string,string>();
    public readonly List<ConfigNode> Children = new List<ConfigNode>();
    public static int Loads;
    public ConfigNode(string name) { this.name = name; }
    public string GetValue(string key) { string value; return Values.TryGetValue(key, out value) ? value : null; }
    public ConfigNode GetNode(string key) { return Children.Find(node => node.name == key); }
    public ConfigNode[] GetNodes(string key) { return Children.FindAll(node => node.name == key).ToArray(); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ConfigNode Load(string path)
    {
        Loads++;
        string[] lines = File.ReadAllLines(path);
        var node = new ConfigNode(lines[0]);
        for (int i = 1; i < lines.Length; i++) { int split = lines[i].IndexOf('='); if (split >= 0) node.Values[lines[i].Substring(0,split)] = lines[i].Substring(split+1); }
        return node;
    }
}
public sealed class Game { public ConfigNode config; }
public static class HighLogic { public static string SaveFolder; public static Game CurrentGame; }
public static class KSPUtil { public static string ApplicationRootPath; }
public static class Planetarium { public static double Ut; public static double GetUniversalTime() { return Ut; } }
namespace SpaceAge
{
    public sealed class ProtoAchievement { public string Name; public bool IsBodySpecific, HasTime = true, HasValue = true, Unique; }
    public sealed class Achievement
    {
        public bool Valid = true;
        public ProtoAchievement Proto;
        public string Body, Hero, Ids;
        public double Value;
        public long Time;
    }
    public sealed class SpaceAgeScenario
    {
        public static SpaceAgeScenario Instance;
        private readonly Dictionary<string,Achievement> achievements = new Dictionary<string,Achievement>();
        public void Set(string key, Achievement value) { achievements[key] = value; }
        public void Remove(string key) { achievements.Remove(key); }
    }
}
namespace KSRParameterLogger
{
    public static class KsrRaceLoggerGate
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ConfigNode Read(string path) { return File.Exists(path) ? ConfigNode.Load(path) : null; }
    }
    public static class ParameterLogStore
    {
        public static int OriginalRefreshCalls;
        [MethodImpl(MethodImplOptions.NoInlining)] public static void RefreshLiveSpaceAgeRecords() { OriginalRefreshCalls++; }
        [MethodImpl(MethodImplOptions.NoInlining)] public static void FlushRecordExports() { RefreshLiveSpaceAgeRecords(); }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void ClearRuntimeState() { }
    }
}
namespace KerbalSpaceRace.RemoteLogger
{
    public static class LegacyCampaignMode
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsEnabled() { string path = Path.Combine(KSPUtil.ApplicationRootPath,"legacy-probe.cfg"); return File.Exists(path) && ConfigNode.Load(path) != null; }
    }
    public sealed class KSRRemoteLogger
    {
        public static class RaceLoggerState
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static ConfigNode TryLoad(string path) { return File.Exists(path) ? ConfigNode.Load(path) : null; }
        }
    }
}
