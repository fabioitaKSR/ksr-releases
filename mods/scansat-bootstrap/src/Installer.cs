using System;
using System.IO;
using System.Security.Cryptography;

namespace KSR.ScanBootstrap
{
    public sealed class Result
    {
        public bool Changed;
        public bool Ready;
        public bool RetryAfterExit;
        public string Message;
    }

    public static class Installer
    {
        public const string PatchHash = "4d4ddeed1a42e69e32d9acbca28f2530e27d7eeac25a7add5120631760c0c999";
        public const string OriginalHash = "8ee4aa88956230bc828a5da1426eba922845b326e432bcb0c8ad5a78ccebbe90";
        public static Result Install(string root, string expectedPatch, string expectedOriginal)
        {
            string staged = null;
            try
            {
                root = Path.GetFullPath(root);
                string target = Path.Combine(root, "GameData/SCANsat/Plugins/SCANsat.dll");
                string payload = Path.Combine(root, "GameData/KerbalSpaceRace/Tools/SCANsatHeightCache/SCANsat.dll.payload");
                string state = Path.Combine(root, "GameData/KerbalSpaceRace/PluginData/SCANsatHeightCache");
                CheckPath(root, target); CheckPath(root, payload); CheckPath(root, state);
                if (!File.Exists(target)) return new Result { Message = "SCANsat is not installed. No files changed." };
                if (!File.Exists(payload) || Hash(payload) != expectedPatch)
                    return new Result { Message = "SCANsat patch payload is missing or invalid. No files changed." };
                string before = Hash(target);
                if (before == expectedPatch)
                    return new Result { Ready = true, Message = "SCANsat height cache is already installed; DLL unchanged." };
                if (before != expectedOriginal)
                    return new Result { Message = "Unrecognized SCANsat DLL. Automatic installation skipped to preserve it." };
                Directory.CreateDirectory(state);
                string backup = Path.Combine(state, "SCANsat-" + before + ".dll.backup");
                CheckPath(root, backup);
                if (!File.Exists(backup)) File.Copy(target, backup, false);
                if (Hash(backup) != before) throw new IOException("Original DLL backup failed validation.");
                staged = target + ".ksr-" + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(payload, staged, false);
                if (Hash(staged) != expectedPatch || Hash(target) != before)
                    throw new IOException("DLL changed during installation; replacement cancelled.");
                File.Replace(staged, target, null);
                staged = null;
                if (Hash(target) != expectedPatch) throw new IOException("Installed DLL failed validation.");
                string marker = Path.Combine(state, "installed.txt");
                CheckPath(root, marker);
                File.WriteAllText(marker, "SCANsat 21.1.0-ksr.1\nSHA256=" + expectedPatch + "\nBackup=" + Path.GetFileName(backup) + "\nInstalledUtc=" + DateTime.UtcNow.ToString("o") + "\n");
                return new Result { Changed = true, Ready = true, Message = "SCANsat height cache installed. Original DLL backed up. Restart KSP to load the replacement." };
            }
            catch (Exception ex)
            {
                bool locked = ex is IOException && ((ex.HResult & 65535) == 32 || (ex.HResult & 65535) == 33);
                return new Result { RetryAfterExit = locked, Message = "SCANsat automatic installation could not finish: " + ex.Message };
            }
            finally
            {
                if (staged != null) { try { File.Delete(staged); } catch { } }
            }
        }
        public static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static void CheckPath(string root, string path)
        {
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            path = Path.GetFullPath(path);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path outside KSP root.");
            for (string current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse points are not supported.");
                if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
            }
        }
    }
}
