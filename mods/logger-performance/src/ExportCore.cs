using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace KSRLoggerPerformance
{
    public struct FileStamp : IEquatable<FileStamp>
    {
        public bool Exists;
        public long Length, WriteTicks;
        public static FileStamp Read(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp { Exists = true, Length = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks } : new FileStamp();
        }
        public bool Equals(FileStamp other) { return Exists == other.Exists && Length == other.Length && WriteTicks == other.WriteTicks; }
    }

    public struct AchievementRow : IEquatable<AchievementRow>
    {
        public string Name, Body, Hero, Ids;
        public double Time, Value;
        public bool HasValue;
        public bool Equals(AchievementRow other)
        {
            return Name == other.Name && Body == other.Body && Hero == other.Hero && Ids == other.Ids &&
                Time.Equals(other.Time) && Value.Equals(other.Value) && HasValue == other.HasValue;
        }
    }

    public sealed class ExportSnapshot
    {
        public readonly string Path, Save;
        public readonly int Year;
        public readonly double Ut, CampaignStart;
        public readonly AchievementRow[] Rows;
        public readonly CultureInfo SortCulture;
        public ExportSnapshot(string path, string save, double ut, double campaignStart, AchievementRow[] rows)
        {
            Path = path; Save = save; Ut = ut; CampaignStart = campaignStart;
            Year = (int)Math.Floor(Math.Max(0, ut - campaignStart) / 9203545.0);
            Rows = (AchievementRow[])rows.Clone();
            SortCulture = CultureInfo.CurrentCulture;
        }
    }

    public static class CsvExport
    {
        public const string Header = "saveName,snapshotYear,snapshotUt,snapshot DATA PER TABELLA,snapshotDate,achievementName,body,hero,value,timeUt,achievement DATA PER TABELLA,date,ids";
        private static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static string Quote(string value) { return "\"" + (value ?? "").Replace("\"", "\"\"") + "\""; }
        public static string Date(double ut, double start, bool table)
        {
            double elapsed = Math.Max(0, ut - start);
            int year = (int)Math.Floor(elapsed / 9203545.0);
            double rest = elapsed % 9203545.0;
            int day = (int)Math.Floor(rest / 21600.0);
            if (!table) return "anno " + year.ToString(CultureInfo.InvariantCulture) + ", giorno " + day.ToString(CultureInfo.InvariantCulture);
            rest %= 21600.0;
            return "Year " + year.ToString(CultureInfo.InvariantCulture) + ", Day " + day.ToString(CultureInfo.InvariantCulture) +
                " - " + ((int)Math.Floor(rest / 3600.0)).ToString(CultureInfo.InvariantCulture) + "h, " +
                ((int)Math.Floor(rest % 3600.0 / 60.0)).ToString(CultureInfo.InvariantCulture) + "m, " +
                ((int)Math.Floor(rest % 60.0)).ToString(CultureInfo.InvariantCulture) + "s";
        }
        public static void Write(ExportSnapshot snapshot)
        {
            // This method consumes plain copied data only: no KSP, Unity or ConfigNode access.
            var rows = (AchievementRow[])snapshot.Rows.Clone();
            var comparer = StringComparer.Create(snapshot.SortCulture, false);
            Array.Sort(rows, delegate(AchievementRow a, AchievementRow b) {
                int result = comparer.Compare(a.Name, b.Name);
                return result != 0 ? result : comparer.Compare(a.Body, b.Body);
            });
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(snapshot.Path));
            string temporary = snapshot.Path + ".ksr-perf-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string currentTable = Date(snapshot.Ut, snapshot.CampaignStart, true);
                string currentDate = Date(snapshot.Ut, snapshot.CampaignStart, false);
                using (var writer = new StreamWriter(temporary, false, Encoding.UTF8))
                {
                    writer.WriteLine(Header);
                    foreach (var row in rows)
                    {
                        writer.WriteLine(string.Join(",", new[] {
                            Quote(snapshot.Save), Quote(snapshot.Year.ToString(CultureInfo.InvariantCulture)), Quote(Number(snapshot.Ut)),
                            Quote(currentTable), Quote(currentDate), Quote(row.Name), Quote(row.Body), Quote(row.Hero),
                            Quote(row.HasValue ? Number(row.Value) : ""), Quote(Number(row.Time)),
                            Quote(row.Time > 0 ? Date(row.Time, snapshot.CampaignStart, true) : ""),
                            Quote(row.Time > 0 ? Date(row.Time, snapshot.CampaignStart, false) : ""), Quote(row.Ids)
                        }));
                    }
                }
                // Readers see either the previous complete CSV or the new complete CSV.
                if (File.Exists(snapshot.Path)) File.Replace(temporary, snapshot.Path, null);
                else File.Move(temporary, snapshot.Path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public sealed class ExportResult
    {
        public string Path, Error;
        public FileStamp Stamp;
        public double Milliseconds;
    }

    public sealed class ExportCoordinator
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, ExportSnapshot> pending = new Dictionary<string, ExportSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<ExportResult> results = new Queue<ExportResult>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly ManualResetEvent idle = new ManualResetEvent(true);
        private Thread worker;
        public void Enqueue(ExportSnapshot snapshot)
        {
            lock (sync)
            {
                pending[snapshot.Path] = snapshot;
                idle.Reset();
                if (worker == null)
                {
                    worker = new Thread(Run) { IsBackground = true, Name = "KSR achievement CSV export" };
                    worker.Start();
                }
            }
            wake.Set();
        }
        public bool Flush(int milliseconds) { return idle.WaitOne(milliseconds); }
        public ExportResult TakeResult()
        {
            lock (sync) { return results.Count == 0 ? null : results.Dequeue(); }
        }
        private void Run()
        {
            while (true)
            {
                wake.WaitOne();
                while (true)
                {
                    ExportSnapshot job = null;
                    lock (sync)
                    {
                        foreach (var entry in pending) { job = entry.Value; break; }
                        if (job == null) { idle.Set(); break; }
                        pending.Remove(job.Path);
                    }
                    var result = new ExportResult { Path = job.Path };
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try { CsvExport.Write(job); result.Stamp = FileStamp.Read(job.Path); }
                    catch (Exception exception) { result.Error = exception.ToString(); }
                    result.Milliseconds = watch.Elapsed.TotalMilliseconds;
                    lock (sync) { results.Enqueue(result); }
                }
            }
        }
    }
}
