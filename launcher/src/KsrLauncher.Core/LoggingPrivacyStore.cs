using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KerbalSpaceRace.LoggingPrivacy
{
    // Shared source for the Windows launcher and the KSP companion. No KSP or
    // Unity dependency, no background polling. Owners decide when to retry.
    public sealed class LoggingPrivacyStore : IDisposable
    {
        readonly string directory;
        readonly object sync = new object();
        readonly HashSet<string> stopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,long> acknowledged = new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        internal Func<Uri,string,byte[],string> Transport;
        FileSystemWatcher watcher;
        bool failed;
        public event Action<string> Stopped;
        public LoggingPrivacyStore(string gameRoot)
        {
            directory = Path.Combine(Path.GetFullPath(gameRoot), "GameData", "KSRLite", "PluginData", "LoggingPrivacy");
            Directory.CreateDirectory(directory);
            foreach (string path in Directory.GetFiles(directory, "*.stop"))
            {
                if (stopped.Count >= 1000) throw new IOException("Too many privacy records; review is required.");
                stopped.Add(Path.GetFileNameWithoutExtension(path));
                string ack=Path.ChangeExtension(path,".ack");
                LoadAck(Path.GetFileNameWithoutExtension(path));
            }
            watcher = new FileSystemWatcher(directory, "*");
            watcher.Created += Changed; watcher.Changed += Changed;
            watcher.Renamed += delegate(object sender, RenamedEventArgs args) { Changed(sender,args); };
            watcher.Error += delegate { lock(sync) failed = true; };
            watcher.EnableRaisingEvents = true;
        }
        static string Code(string code)
        {
            if (!Regex.IsMatch(code ?? "", "^[A-Za-z0-9_-]{1,80}$")) throw new ArgumentException("Invalid campaign code.");
            return code.ToLowerInvariant();
        }
        void Changed(object sender, FileSystemEventArgs args)
        {
            string code=Path.GetFileNameWithoutExtension(args.Name);
            if(Path.GetExtension(args.Name)==".stop") Mark(code);
            if(Path.GetExtension(args.Name)==".pending" && !string.Equals(code,"test1",StringComparison.OrdinalIgnoreCase)) Mark(code);
            if(Path.GetExtension(args.Name)==".ack") LoadAck(code);
        }
        void LoadAck(string code)
        {
            try
            {
                string marker=Path.Combine(directory,code+".stop"),ack=Path.Combine(directory,code+".ack");
                string[] fields=File.Exists(ack)?File.ReadAllText(ack).Trim().Split('\n'):new string[0];
                long owner;
                lock(sync)
                {
                    if(fields.Length==2 && File.Exists(marker) && fields[0]==File.ReadAllText(marker).Trim() && long.TryParse(fields[1],out owner)) acknowledged[code]=owner;
                    else acknowledged.Remove(code);
                }
            }
            catch(IOException) { lock(sync) acknowledged.Remove(code); }
            catch(UnauthorizedAccessException) { lock(sync) acknowledged.Remove(code); }
        }
        void Mark(string code)
        {
            lock(sync) stopped.Add(code);
            LoadAck(code);
            var callback = Stopped;
            if(callback != null) callback(code);
        }
        public bool IsStopped(string code) { if(string.Equals(code,"test1",StringComparison.OrdinalIgnoreCase)) return false;lock(sync) return failed || stopped.Contains(Code(code)); }
        public bool IsConfirmed(string code,long? userId=null) { lock(sync) { long owner;return acknowledged.TryGetValue(Code(code),out owner) && (!userId.HasValue || owner==userId.Value); } }
        IDisposable Acquire()
        {
            byte[] hash;using(var sha=SHA256.Create()) hash=sha.ComputeHash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()));
            var mutex=new Mutex(false,"Local\\KSRPrivacy"+BitConverter.ToString(hash).Replace("-",""));
            try { if(!mutex.WaitOne(10000)) { mutex.Dispose();throw new IOException("Privacy state is busy."); } }
            catch(AbandonedMutexException) { }
            return new Lease(mutex);
        }
        sealed class Lease : IDisposable { readonly Mutex mutex;public Lease(Mutex value) {mutex=value;}public void Dispose() {mutex.ReleaseMutex();mutex.Dispose();} }
        public string Stop(string code, long userId, string serverUrl, string gameToken)
        {
            code = Code(code);
            if(code=="test1") throw new InvalidOperationException("The legacy test campaign is unchanged by this feature.");
            string path = Path.Combine(directory, code + ".stop");
            // Memory first: no new collection/request may start while persistence
            // is being written. A persistence error remains fail-closed.
            Mark(code);
            lock(sync)
            using(Acquire())
            {
                string operation = File.Exists(path)?File.ReadAllText(path).Trim():Guid.NewGuid().ToString("N");
                string pending = Path.Combine(directory, code + ".pending");
                if(userId<=0)
                {
                    if(!File.Exists(path)) Atomic(path,Encoding.UTF8.GetBytes(operation));
                    throw new InvalidOperationException("Logging is blocked locally. Sign in to the launcher to complete the server exit.");
                }
                if(File.Exists(pending))
                {
                    string[] previous=Encoding.UTF8.GetString(Protect(File.ReadAllBytes(pending),true)).Split('\n');
                    if(previous.Length!=5 || previous[1]!=userId.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new InvalidOperationException("Finish the other account's pending exit before starting another one.");
                    return operation;
                }
                LoadAck(code);
                if(IsConfirmed(code,userId)) return operation;
                if(IsConfirmed(code)) operation=Guid.NewGuid().ToString("N");
                ValidateServer(serverUrl);
                string text = string.Join("\n", new[] { code, userId.ToString(System.Globalization.CultureInfo.InvariantCulture), operation, serverUrl, gameToken ?? "" });
                // Durable deny marker comes before the encrypted delivery job.
                // A missing job never means permission to restart.
                Atomic(path,Encoding.UTF8.GetBytes(operation));
                Atomic(pending,Protect(Encoding.UTF8.GetBytes(text),false));
                return operation;
            }
        }
        public bool HasPending { get { return Directory.GetFiles(directory,"*.pending").Length != 0; } }
        public string[] PendingCodes()
        {
            var paths=Directory.GetFiles(directory,"*.pending");
            if(paths.Length>1000) throw new IOException("Too many pending privacy requests.");
            var result=new List<string>();
            for(int i=0;i<paths.Length;i++) { string code=Code(Path.GetFileNameWithoutExtension(paths[i]));if(code!="test1") result.Add(code); }
            return result.ToArray();
        }
        public bool Deliver(string code, long signedInUserId, string accessToken)
        {
            code=Code(code);
            if(code=="test1") return false;
            string path=Path.Combine(directory,code+".pending");
            byte[] bytes=File.ReadAllBytes(path);
            if(bytes.Length>65536) throw new IOException("Invalid privacy delivery file.");
            string[] fields=Encoding.UTF8.GetString(Protect(bytes,true)).Split('\n');
            if(fields.Length!=5 || Code(fields[0])!=code || !Regex.IsMatch(fields[2],"^[a-f0-9]{32}$")) throw new IOException("Invalid privacy delivery record.");
            long owner=long.Parse(fields[1],System.Globalization.CultureInfo.InvariantCulture);
            string credential=fields[4];
            if(owner>0 && owner==signedInUserId && !string.IsNullOrEmpty(accessToken)) credential=accessToken;
            if(string.IsNullOrEmpty(credential))
            {
                if(owner<=0 || owner!=signedInUserId || string.IsNullOrEmpty(accessToken)) return false;
                credential=accessToken;
            }
            Uri server=ValidateServer(fields[3]);
            byte[] body=Encoding.UTF8.GetBytes("{\"campaignCode\":\""+code+"\",\"operationId\":\""+fields[2]+"\",\"userId\":"+owner.ToString(System.Globalization.CultureInfo.InvariantCulture)+"}");
            string responseText;
            if(Transport!=null) responseText=Transport(server,credential,body);
            else
            {
                var request=(HttpWebRequest)WebRequest.Create(new Uri(server,"/api/v1/privacy/stop-logging"));
                request.AllowAutoRedirect=false; request.Method="POST"; request.Timeout=10000; request.ReadWriteTimeout=10000;
                request.UserAgent="KSR-Logging-Privacy/1.0"; request.ContentType="application/json";
                request.Headers["Authorization"]="Bearer "+credential;request.ContentLength=body.Length;
                using(var stream=request.GetRequestStream()) stream.Write(body,0,body.Length);
                using(var response=(HttpWebResponse)request.GetResponse())
                using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8))
                {
                    char[] buffer=new char[32769]; int length=0,read;
                    while(length<buffer.Length && (read=reader.Read(buffer,length,buffer.Length-length))>0) length+=read;
                    if(length==buffer.Length) return false;
                    responseText=new string(buffer,0,length);
                }
            }
            if(responseText.Length>32768 || !Regex.IsMatch(responseText,"\"loggingStopped\"\\s*:\\s*true\\s*[,}]")) return false;
            var actor=Regex.Match(responseText,"\"userId\"\\s*:\\s*(\\d+)\\s*[,}]");
            var race=Regex.Match(responseText,"\"campaignCode\"\\s*:\\s*\"([A-Za-z0-9_-]+)\"");
            long confirmedOwner;
            if(!actor.Success || !long.TryParse(actor.Groups[1].Value,out confirmedOwner) || confirmedOwner!=owner || !race.Success || !string.Equals(race.Groups[1].Value,code,StringComparison.OrdinalIgnoreCase)) return false;
            lock(sync)
            using(Acquire())
            {
                if(!File.Exists(path) || File.ReadAllText(Path.Combine(directory,code+".stop")).Trim()!=fields[2] ||
                   Convert.ToBase64String(File.ReadAllBytes(path))!=Convert.ToBase64String(bytes)) return false;
                Atomic(Path.Combine(directory,code+".ack"),Encoding.UTF8.GetBytes(fields[2]+"\n"+owner.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                acknowledged[code]=owner;
                File.Delete(path); // The persistent stop marker deliberately remains.
            }
            return true;
        }
        public void ResumeAfterConfirmedEnrolment(string code)
        {
            code=Code(code);
            lock(sync)
            {
                if(File.Exists(Path.Combine(directory,code+".pending"))) throw new InvalidOperationException("Complete the pending exit before rejoining.");
                File.Delete(Path.Combine(directory,code+".stop"));
                File.Delete(Path.Combine(directory,code+".ack"));
                stopped.Remove(code);
                acknowledged.Remove(code);
            }
        }
        static Uri ValidateServer(string url)
        {
            Uri uri;
            if(!Uri.TryCreate(url,UriKind.Absolute,out uri) || uri.Scheme!="https" || uri.Host!="play.kerbalspacerace.net" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("The privacy service must use the official HTTPS KSR server.");
            return new Uri("https://play.kerbalspacerace.net/");
        }
        static void Atomic(string path,byte[] bytes)
        {
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { output.Write(bytes,0,bytes.Length); output.Flush(true); }
                if(File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
            }
            finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
        [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public IntPtr Data; }
        [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CryptProtectData(ref Blob input,string description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
        [DllImport("crypt32.dll",SetLastError=true)] static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr value);
        static byte[] Protect(byte[] bytes,bool decrypt)
        {
            Blob input=new Blob { Length=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length) },output=new Blob();
            try
            {
                Marshal.Copy(bytes,0,input.Data,bytes.Length);
                bool ok=decrypt?CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptProtectData(ref input,"KSR privacy withdrawal",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
                if(!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                byte[] result=new byte[output.Length]; Marshal.Copy(output.Data,result,0,result.Length);return result;
            }
            finally
            {
                for(int i=0;i<input.Length;i++) Marshal.WriteByte(input.Data,i,0);
                Marshal.FreeHGlobal(input.Data); if(output.Data!=IntPtr.Zero) LocalFree(output.Data);
            }
        }
        public void Dispose() { if(watcher!=null) watcher.Dispose(); }
    }
}
