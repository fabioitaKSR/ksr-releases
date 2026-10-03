using System;
using System.IO;
using KSR.ScanBootstrap;
class Tests
{
    static int count;
    static void Assert(bool value, string name) { if (!value) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
    static string Fixture(string parent, string name)
    {
        string root = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(root, "GameData/SCANsat/Plugins"));
        Directory.CreateDirectory(Path.Combine(root, "GameData/KerbalSpaceRace/Tools/SCANsatHeightCache"));
        File.WriteAllText(Target(root), "original fixture");
        File.WriteAllText(Payload(root), "patched fixture");
        return root;
    }
    static string Target(string r) { return Path.Combine(r, "GameData/SCANsat/Plugins/SCANsat.dll"); }
    static string Payload(string r) { return Path.Combine(r, "GameData/KerbalSpaceRace/Tools/SCANsatHeightCache/SCANsat.dll.payload"); }
    static void Main(string[] args)
    {
        string root = Fixture(args[0], "normal");
        string original = Installer.Hash(Target(root)), patch = Installer.Hash(Payload(root));
        var first = Installer.Install(root, patch, original);
        Assert(first.Changed && first.Ready && Installer.Hash(Target(root)) == patch, "first launch installs exact payload");
        string state = Path.Combine(root, "GameData/KerbalSpaceRace/PluginData/SCANsatHeightCache");
        Assert(Installer.Hash(Path.Combine(state, "SCANsat-" + original + ".dll.backup")) == original, "original backup is exact");
        string marker = Path.Combine(state, "installed.txt");
        string markerText = File.ReadAllText(marker);
        var next = Installer.Install(root, patch, original);
        Assert(next.Ready && !next.Changed && File.ReadAllText(marker) == markerText, "second launch does not rewrite DLL or marker");
        root = Fixture(args[0], "foreign"); File.WriteAllText(Target(root), "another mod version");
        Assert(!Installer.Install(root, patch, original).Ready && File.ReadAllText(Target(root)) == "another mod version", "unknown DLL preserved");
        root = Fixture(args[0], "corrupt-payload"); File.WriteAllText(Payload(root), "corrupted");
        Assert(!Installer.Install(root, patch, original).Ready && Installer.Hash(Target(root)) == original, "corrupt payload rejected");
        root = Fixture(args[0], "absent"); File.Delete(Target(root));
        Assert(!Installer.Install(root, patch, original).Ready && !File.Exists(Target(root)), "missing SCANsat not installed");
        root = Fixture(args[0], "locked");
        using (var locked = File.Open(Target(root), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var blocked = Installer.Install(root, patch, original);
            Assert(!blocked.Changed && blocked.RetryAfterExit && Installer.Hash(Target(root)) == original, "locked DLL requests deferred install and remains original");
        }
        root = Fixture(args[0], "bad-backup");
        state = Path.Combine(root, "GameData/KerbalSpaceRace/PluginData/SCANsatHeightCache"); Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "SCANsat-" + original + ".dll.backup"), "corrupt backup");
        Assert(!Installer.Install(root, patch, original).Ready && Installer.Hash(Target(root)) == original, "invalid backup blocks replacement");
        Console.WriteLine("All " + count + " installer checks passed.");
    }
}
