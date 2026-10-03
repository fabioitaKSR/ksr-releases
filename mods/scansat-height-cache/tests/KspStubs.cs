using System.Collections.Generic;
internal static class MainThreadGuard
{
    private static readonly int owner = System.Threading.Thread.CurrentThread.ManagedThreadId;
    internal static void Assert() { if (System.Threading.Thread.CurrentThread.ManagedThreadId != owner) throw new System.Exception("Game API called from worker"); }
}
// Test doubles exercise the production wrapper without loading a Unity process.
public static class KSPUtil { public static string ApplicationRootPath; }
public class CelestialBody { public string bodyName; public double Radius; public object pqsController; }
public class ConfigNode
{
    public class Value { public string value; }
    public List<Value> values = new List<Value>();
    public List<ConfigNode> nodes = new List<ConfigNode>();
    private string name;
    public ConfigNode(string name) { this.name = name; }
    public override string ToString() { return name + string.Join("|", values.ConvertAll(v => v.value)); }
}
public class TestConfig { public string url; public ConfigNode config; }
public class GameDatabase
{
    public static GameDatabase Instance = new GameDatabase();
    public TestConfig[] Configs;
    public TestConfig[] GetConfigs(string name) { MainThreadGuard.Assert(); return Configs; }
}
namespace UnityEngine
{
    public static class Time { public static int frameCount; }
    public static class Debug { public static void LogWarning(object message) { MainThreadGuard.Assert(); System.Console.WriteLine(message); } }
}
namespace SCANsat
{
    public class SCAN_Settings_Config
    {
        public static SCAN_Settings_Config Instance = new SCAN_Settings_Config();
        public bool PersistentHeightMapCacheEnabled = true;
    }
    public static class SCANUtil
    {
        public static void SCANlog(string message, params object[] values) { MainThreadGuard.Assert(); System.Console.WriteLine(message, values); }
    }
}
