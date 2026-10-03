using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SCANsat.SCAN_Data
{
    internal static class SCANheightMapCache
    {
        internal sealed class Result { internal float[,] Map; internal Exception Error; internal double Milliseconds; }
        private sealed class SaveJob { internal string Body; internal Task<Result> Task; }
        private static IEnumerator<int> preparation;
        private sealed class Fingerprint { internal byte[] Bytes; internal int Files; internal string Manifest; }
        private static Task<Fingerprint> signatureJob;
        private static Stopwatch signatureClock;
        private static byte[] signature;
        private static bool failed;
        private static int lastPreparationFrame = -1;
        private static readonly List<SaveJob> saves = new List<SaveJob>();
        private static readonly string[] AssetExtensions = { "", ".dds", ".png", ".jpg", ".jpeg", ".tga", ".bin", ".raw" };
        private static int hits, misses;
        internal static int PendingWrites { get { return saves.Count; } }

        internal static bool Enabled
        {
            get { return !failed && SCAN_Settings_Config.Instance != null && SCAN_Settings_Config.Instance.PersistentHeightMapCacheEnabled; }
        }

        // Publish and log on main. Worker jobs never access Unity/KSP objects.
        internal static void Pump()
        {
            for (int i = saves.Count - 1; i >= 0; i--)
            {
                SaveJob job = saves[i];
                if (!job.Task.IsCompleted) continue;
                Result result = job.Task.Result;
                if (result.Error != null) Warn("Cannot save " + job.Body + "; RAM map remains available", result.Error);
                else SCANUtil.SCANlog("[Height Cache] Saved {0} on worker, 259200 data bytes, {1:F2} ms", job.Body, result.Milliseconds);
                saves.RemoveAt(i);
            }
        }

        internal static void LogSummary()
        {
            SCANUtil.SCANlog("[Height Cache] Session totals: disk hits={0}, misses/rejections={1}, pending writes={2}, enabled={3}", hits, misses, saves.Count, Enabled);
        }

        internal static bool Prepare()
        {
            Pump();
            if (!Enabled || signature != null) return true;
            try
            {
                if (signatureJob != null)
                {
                    if (!signatureJob.IsCompleted) return false;
                    Fingerprint result = signatureJob.GetAwaiter().GetResult();
                    signature = result.Bytes;
                    SCANUtil.SCANlog("[Height Cache] Terrain signature ready on worker; {0:F0} ms elapsed; files={1}; SHA256={2}; manifest={3}", signatureClock.Elapsed.TotalMilliseconds, result.Files, Hex(signature), result.Manifest);
                    return true;
                }
                if (lastPreparationFrame == UnityEngine.Time.frameCount) return false;
                lastPreparationFrame = UnityEngine.Time.frameCount;
                if (preparation == null) preparation = SnapshotInputs().GetEnumerator();
                var budget = Stopwatch.StartNew();
                do
                {
                    if (!preparation.MoveNext())
                    {
                        preparation.Dispose(); preparation = null;
                        return false;
                    }
                } while (budget.Elapsed.TotalMilliseconds < 2);
                return false;
            }
            catch (Exception e)
            {
                failed = true;
                if (preparation != null) { preparation.Dispose(); preparation = null; }
                Warn("Cannot validate terrain inputs; using original generator", e);
                return true;
            }
        }

        // Only immutable strings cross the boundary; snapshot game objects on main.
        private static IEnumerable<int> SnapshotInputs()
        {
            signatureClock = Stopwatch.StartNew();
            SCANUtil.SCANlog("[Height Cache] Preparing terrain signature (worker I/O and SHA256)");
            var references = new List<string>();
            foreach (var config in GameDatabase.Instance.GetConfigs("Kopernicus").OrderBy(c => c.url, StringComparer.Ordinal))
            {
                foreach (int unused in SnapshotReferences(config.config, references)) yield return unused;
            }
            string gameData = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "GameData")) + Path.DirectorySeparatorChar;
            signatureJob = Task.Run(() => ComputeSignature(references, gameData));
        }

        private static IEnumerable<int> SnapshotReferences(ConfigNode node, List<string> references)
        {
            foreach (ConfigNode.Value value in node.values) references.Add(value.value);
            yield return 0;
            foreach (ConfigNode child in node.nodes)
                foreach (int unused in SnapshotReferences(child, references)) yield return unused;
        }

        private static Fingerprint ComputeSignature(List<string> references, string gameData)
        {
            using (var sha = SHA256.Create())
            {
                AddText(sha, "KSR_HEIGHT_CACHE_STABLE_INPUTS_V2_ORIGINAL_SCAN21_1_GRID");
                var assets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                // Fingerprint stable installation inputs, never the transient set of
                // assemblies loaded into AppDomain or regenerated ModuleManager output.
                foreach (string configPath in Directory.GetFiles(gameData, "*.cfg", SearchOption.AllDirectories))
                    if (configPath.IndexOf(Path.DirectorySeparatorChar + "PluginData" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
                        assets.Add(configPath);
                foreach (string dllPath in Directory.GetFiles(gameData, "*.dll", SearchOption.AllDirectories)) assets.Add(dllPath);
                string gameRoot = Path.GetDirectoryName(gameData.TrimEnd(Path.DirectorySeparatorChar));
                foreach (string relative in new[] { "Physics.cfg", "KSP_x64_Data/Managed/Assembly-CSharp.dll", "KSP_x64_Data/Managed/UnityEngine.CoreModule.dll" })
                {
                    string path = Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path)) assets.Add(path);
                }
                foreach (string reference in references)
                {
                    string relative = reference.Trim().Trim('"').Split(',')[0].Trim().Replace('/', Path.DirectorySeparatorChar);
                    if (relative.StartsWith("BUILTIN", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(relative)) continue;
                    if (relative.IndexOfAny(new[] { ':', '*', '?', '<', '>', '|', '\n', '\r' }) >= 0) continue;
                    if (relative.StartsWith("GameData" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) relative = relative.Substring(9);
                    if (relative.IndexOf(Path.DirectorySeparatorChar) < 0 && !Path.HasExtension(relative)) continue;
                    foreach (string extension in AssetExtensions)
                    {
                        string candidate = Path.GetFullPath(Path.Combine(gameData, relative + extension));
                        if (candidate.StartsWith(gameData, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)) assets.Add(candidate);
                    }
                }
                var manifest = new StringBuilder();
                foreach (string path in assets)
                {
                    string key = path.Substring(gameRoot.Length + 1).Replace('\\', '/').ToLowerInvariant();
                    AddText(sha, key);
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        byte[] digest;
                        using (var fileHash = SHA256.Create()) digest = fileHash.ComputeHash(stream);
                        string hex = Hex(digest);
                        AddText(sha, hex);
                        manifest.Append(hex).Append(' ').Append(key).Append('\n');
                    }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                var result = new Fingerprint { Bytes = sha.Hash, Files = assets.Count };
                // Diagnostic output is outside the inputs; it cannot change the signature.
                try
                {
                    string dir = Path.Combine(gameData, "SCANsat", "PluginData", "TerrainCache");
                    Directory.CreateDirectory(dir);
                    result.Manifest = Path.Combine(dir, "inputs-" + Hex(result.Bytes) + ".txt");
                    File.WriteAllText(result.Manifest, manifest.ToString(), new UTF8Encoding(false));
                }
                catch (Exception e) { result.Manifest = "unavailable: " + e.Message; }
                return result;
            }
        }

        private static void AddText(HashAlgorithm sha, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            byte[] length = BitConverter.GetBytes(bytes.Length);
            sha.TransformBlock(length, 0, length.Length, length, 0);
            sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
        }

        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }

        private static string GetPath(string body)
        {
            string key;
            using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(body))).Replace("-", "").ToLowerInvariant();
            return Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "SCANsat", "PluginData", "TerrainCache", key + ".scnhmap");
        }

        internal static Task<Result> BeginLoad(CelestialBody body)
        {
            if (!Enabled || signature == null || body.pqsController == null) return null;
            string name = body.bodyName, path = GetPath(name);
            double radius = body.Radius;
            byte[] inputSignature = signature;
            return Task.Run(() =>
            {
                var result = new Result();
                var clock = Stopwatch.StartNew();
                try { if (File.Exists(path)) result.Map = SCANheightMapCacheFile.Read(path, name, radius, inputSignature); }
                catch (Exception e) { result.Error = e; }
                result.Milliseconds = clock.Elapsed.TotalMilliseconds;
                return result;
            });
        }

        internal static bool PollLoad(Task<Result> job, string body, out float[,] map)
        {
            map = null;
            if (job == null) return true;
            if (!job.IsCompleted) return false;
            Result result = job.Result;
            map = result.Map;
            if (result.Error != null) { misses++; Warn("Cache rejected for " + body + "; regenerating", result.Error); }
            else if (map != null) { hits++; SCANUtil.SCANlog("[Height Cache] Loaded {0} from disk on worker in {1:F2} ms", body, result.Milliseconds); }
            else { misses++; SCANUtil.SCANlog("[Height Cache] No cache for {0}; generating", body); }
            return true;
        }

        internal static void Save(CelestialBody body, float[,] map)
        {
            if (!Enabled || signature == null || body.pqsController == null) return;
            // The published map is immutable; hold its reference during the write.
            string name = body.bodyName, path = GetPath(name);
            double radius = body.Radius;
            byte[] inputSignature = signature;
            Task<Result> task = Task.Run(() =>
            {
                var result = new Result();
                var clock = Stopwatch.StartNew();
                try { SCANheightMapCacheFile.Write(path, name, radius, inputSignature, map); }
                catch (Exception e) { result.Error = e; }
                result.Milliseconds = clock.Elapsed.TotalMilliseconds;
                return result;
            });
            saves.Add(new SaveJob { Body = name, Task = task });
        }

        private static void Warn(string message, Exception e)
        {
            UnityEngine.Debug.LogWarning("[SCANsat Height Cache] " + message + ": " + e.Message);
        }
    }
}
