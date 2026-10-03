using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
namespace KSR.ScanBootstrap
{
    public static class Helper
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length != 2) return 2;
                int pid;
                if (!int.TryParse(args[1], out pid)) return 2;
                try { using (var parent = Process.GetProcessById(pid)) { if (!parent.WaitForExit(43200000)) return 3; } }
                catch (ArgumentException) { }
                Result result = null;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    result = Installer.Install(args[0], Installer.PatchHash, Installer.OriginalHash);
                    if (!result.RetryAfterExit) break;
                    Thread.Sleep(1000);
                }
                return result.Ready ? 0 : 1;
            }
            catch { return 1; }
        }
    }
}
