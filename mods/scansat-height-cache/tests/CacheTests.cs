using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SCANsat.SCAN_Data;

internal static class CacheTests
{
    private static int checks;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        checks++;
    }
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidDataException) { checks++; return; }
        catch (EndOfStreamException) { checks++; return; }
        throw new Exception("Expected rejection: " + label);
    }
    public static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(root);
            if (args.Length > 1) { Integration(root, args[1]); return 0; }
            string path = Path.Combine(root, "roundtrip.bin");
            var map = new float[360, 180];
            byte[] original = new byte[259200];
            new Random(51321).NextBytes(original); // Includes signed zero, NaNs, subnormals and infinities.
            Buffer.BlockCopy(original, 0, map, 0, original.Length);
            byte[] signature = SHA256.Create().ComputeHash(new byte[] { 1, 2, 3 });
            const string name = "Gael-è-月";
            SCANheightMapCacheFile.Write(path, name, 600000, signature, map);
            byte[] roundtrip = new byte[original.Length];
            Buffer.BlockCopy(SCANheightMapCacheFile.Read(path, name, 600000, signature), 0, roundtrip, 0, roundtrip.Length);
            Check(original.SequenceEqual(roundtrip), "Bit-for-bit roundtrip including special float values");
            byte[] valid = File.ReadAllBytes(path);
            Reject(() => SCANheightMapCacheFile.Read(path, "Iota", 600000, signature), "Wrong body");
            Reject(() => SCANheightMapCacheFile.Read(path, name, 600001, signature), "Wrong radius");
            Reject(() => SCANheightMapCacheFile.Read(path, name, 600000, new byte[32]), "Wrong terrain signature");
            foreach (int offset in new[] { 0, 8, 12, 16, 20, valid.Length - 40, valid.Length - 1 })
            {
                var broken = (byte[])valid.Clone(); broken[offset] ^= 0xff;
                File.WriteAllBytes(path, broken);
                Reject(() => SCANheightMapCacheFile.Read(path, name, 600000, signature), "Corruption at " + offset);
            }
            foreach (int length in new[] { 0, 100, valid.Length - 1, valid.Length + 1 })
            {
                using (var stream = File.Create(path)) { stream.Write(valid, 0, Math.Min(length, valid.Length)); stream.SetLength(length); }
                Reject(() => SCANheightMapCacheFile.Read(path, name, 600000, signature), "Invalid length " + length);
            }
            File.WriteAllBytes(path, valid);
            map[24, 61] = -999.25f;
            SCANheightMapCacheFile.Write(path, name, 600000, signature, map);
            Check(SCANheightMapCacheFile.Read(path, name, 600000, signature)[24, 61] == -999.25f, "Atomic replacement");
            File.WriteAllText(path + ".incomplete.tmp", "unfinished");
            Check(SCANheightMapCacheFile.Read(path, name, 600000, signature)[24, 61] == -999.25f, "Incomplete temporary file ignored");
            string invalidDirectory = Path.Combine(root, "directory-is-a-file"); File.WriteAllText(invalidDirectory, "x");
            bool failedWrite = false;
            try { SCANheightMapCacheFile.Write(Path.Combine(invalidDirectory, "map.bin"), name, 1, signature, map); }
            catch (IOException) { failedWrite = true; }
            Check(failedWrite, "Filesystem error propagated to fallback wrapper");
            Console.WriteLine("PASS: " + checks + " binary cache checks");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void Integration(string root, string mode)
    {
        if (mode == "load-extra-runtime") System.Reflection.Assembly.Load("System.Data, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
        KSPUtil.ApplicationRootPath = root;
        var node = new ConfigNode("Kopernicus");
        node.values.Add(new ConfigNode.Value { value = "GPP/height" });
        GameDatabase.Instance.Configs = new[] { new TestConfig { url = "GPP/body", config = node } };
        if (mode == "disabled") SCANsat.SCAN_Settings_Config.Instance.PersistentHeightMapCacheEnabled = false;
        int frames = 0;
        while (!SCANheightMapCache.Prepare())
        {
            UnityEngine.Time.frameCount++;
            System.Threading.Thread.Sleep(1);
            if (++frames > 10000) throw new Exception("Preparation did not complete");
        }
        var body = new CelestialBody { bodyName = "Gael", Radius = 600000, pqsController = new object() };
        float[,] map;
        var job = SCANheightMapCache.BeginLoad(body);
        while (!SCANheightMapCache.PollLoad(job, body.bodyName, out map)) System.Threading.Thread.Sleep(1);
        bool loaded = map != null;
        if (mode.StartsWith("load")) Check(loaded && map[0, 0] == 1234.5f, "Second process uses saved terrain map");
        else
        {
            Check(!loaded, "Missing, invalid or disabled cache returns generator fallback");
            map = new float[360, 180]; map[0, 0] = 1234.5f;
            SCANheightMapCache.Save(body, map);
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (SCANheightMapCache.PendingWrites > 0)
            {
                SCANheightMapCache.Pump();
                System.Threading.Thread.Sleep(1);
                if (deadline.Elapsed.TotalSeconds > 10) throw new Exception("Write did not finish");
            }
        }
        body.pqsController = null;
        Check(SCANheightMapCache.BeginLoad(body) == null, "Bodies without PQS bypass cache");
        Console.WriteLine("PASS: integration " + mode + " (" + frames + " preparation frames)");
    }
}
