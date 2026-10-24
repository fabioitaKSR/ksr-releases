using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using System.Threading;
using HarmonyLib;
using UnityEngine;

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace KerbalSpaceRace.LoggingPrivacy
{
    [KSPAddon(KSPAddon.Startup.Instantly,true)]
    public sealed class LoggingPrivacyRuntime : MonoBehaviour
    {
        static LoggingPrivacyStore store;
        static readonly object gate=new object();
        static readonly Dictionary<string,string> saves=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<WebRequest,string> requests=new Dictionary<WebRequest,string>();
        static readonly List<object> remotes=new List<object>();
        static readonly Dictionary<Type,Dictionary<string,FieldInfo>> fields=new Dictionary<Type,Dictionary<string,FieldInfo>>();
        sealed class TextConfig { internal long Length,Ticks;internal string Text; }
        static readonly Dictionary<string,TextConfig> configs=new Dictionary<string,TextConfig>(StringComparer.OrdinalIgnoreCase);
        static string saveName="",campaign="",ticket="";
        static string gameRoot;
        static int mainThread;
        static long userId;
        static bool legacy,failed=true,confirmation,working,pendingKnown;
        static float nextConfig,nextRetry;
        static int retryIndex;
        static string status="";
        [ThreadStatic] static string sending;
        [ThreadStatic] static List<WebRequest> threadRequests;
        static FieldInfo Field(Type type,string name)
        {
            lock(gate)
            {
                Dictionary<string,FieldInfo> table;
                if(!fields.TryGetValue(type,out table)) { table=new Dictionary<string,FieldInfo>();fields[type]=table; }
                FieldInfo field;
                if(!table.TryGetValue(name,out field)) { field=AccessTools.Field(type,name);table[name]=field; }
                return field;
            }
        }
        static object Get(object instance,string name) { var field=Field(instance.GetType(),name);return field==null?null:field.GetValue(instance); }
        static Type Find(string name) { return AccessTools.TypeByName(name); }
        public void Awake()
        {
            try
            {
                gameRoot=KSPUtil.ApplicationRootPath;mainThread=Thread.CurrentThread.ManagedThreadId;
                string legacyPath=Path.Combine(gameRoot,"GameData","KerbalSpaceRace","PluginData","LegacyCampaign.cfg");
                if(File.Exists(legacyPath) && string.Equals(Value(File.ReadAllText(legacyPath),"enabled"),"true",StringComparison.OrdinalIgnoreCase))
                {
                    // Explicit user exclusion: no hooks, state files, retry loop
                    // or controls are installed in an existing legacy process.
                    legacy=true;campaign="test1";failed=false;return;
                }
                store=new LoggingPrivacyStore(gameRoot);
                store.Stopped+=OnStopped;
                pendingKnown=store.HasPending;
                var harmony=new Harmony("KerbalSpaceRace.LoggingPrivacy.20261024");
                RegisterPatches(harmony);
                failed=false;
                Debug.Log("[KSR Logging Privacy] Active: persistent stop, queue cancellation and offline withdrawal delivery.");
            }
            catch(Exception ex) { failed=true;status="Logging privacy is unavailable. Do not enter a campaign; restart after repairing the installation.";Debug.LogError("[KSR Logging Privacy] "+ex.Message); }
        }
        static void RegisterPatches(Harmony harmony)
        {
            Type parameterGate=Find("KSRParameterLogger.KsrRaceLoggerGate");
            Type parameterStore=Find("KSRParameterLogger.ParameterLogStore");
            if(parameterGate==null || parameterStore==null) throw new InvalidOperationException("Required Parameter Logger is missing.");
            harmony.Patch(AccessTools.Method(parameterGate,"IsEnabledForCurrentSave"),prefix:new HarmonyMethod(typeof(LoggingPrivacyRuntime),"ParameterGate"));
            foreach(var method in parameterStore.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static))
                if(method.ReturnType==typeof(void) && (method.Name.StartsWith("Record") || method.Name.StartsWith("Write") || method.Name.StartsWith("Ensure") || method.Name.StartsWith("Check")))
                    harmony.Patch(method,prefix:new HarmonyMethod(typeof(LoggingPrivacyRuntime),"ParameterWrite"));
            bool found=false;
            foreach(string name in new[]{"KerbalSpaceRace.RemoteLogger.KSRRemoteLogger"})
            {
                Type remote=Find(name); if(remote==null) continue;
                var update=AccessTools.Method(remote,"Update"); var post=AccessTools.Method(remote,"PostEvent");
                if(update==null || post==null || Field(remote,"sync")==null || Field(remote,"pending")==null) throw new InvalidOperationException("Unsupported Remote Logger layout.");
                found=true;
                harmony.Patch(update,prefix:new HarmonyMethod(typeof(LoggingPrivacyRuntime),"RemoteUpdate"));
                harmony.Patch(post,prefix:new HarmonyMethod(typeof(LoggingPrivacyRuntime),"BeforePost"),finalizer:new HarmonyMethod(typeof(LoggingPrivacyRuntime),"AfterPost"));
                foreach(var method in remote.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
                    if(method.ReturnType==typeof(void) && (method.Name=="SyncNow" || method.Name=="FlushOne" || method.Name=="ScanLoggerCsv" || method.Name=="QueueRuntimeEvent" || method.Name.StartsWith("OnCrew")))
                        harmony.Patch(method,prefix:new HarmonyMethod(typeof(LoggingPrivacyRuntime),"RemoteWork"));
                PatchRequests(harmony,remote);
            }
            if(!found) throw new InvalidOperationException("Remote Logger is missing.");
            Type csv=Find("KSRLoggerPerformance.CsvExport");
            Type coordinator=Find("KSRLoggerPerformance.ExportCoordinator");
            if(csv==null || coordinator==null) throw new InvalidOperationException("Required logger performance companion is missing.");
            harmony.Patch(AccessTools.Method(coordinator,"Enqueue"),prefix:new HarmonyMethod(typeof(LoggingPrivacyRuntime),"BeforeEnqueue"));
            harmony.Patch(AccessTools.Method(csv,"Write"),prefix:new HarmonyMethod(typeof(LoggingPrivacyRuntime),"BeforeCsv"),transpiler:new HarmonyMethod(typeof(LoggingPrivacyRuntime),"GuardCsvPublish"));
        }
        static void PatchRequests(Harmony harmony,Type type)
        {
            foreach(var method in type.GetMethods(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
            {
                if(method.ContainsGenericParameters || method.GetMethodBody()==null) continue;
                bool needed=false;
                foreach(var instruction in PatchProcessor.GetOriginalInstructions(method))
                {
                    var call=instruction.operand as MethodInfo;
                    if(call!=null && ((call.DeclaringType==typeof(WebRequest) && call.Name=="Create") ||
                       (call.Name=="Enqueue" && call.DeclaringType.IsGenericType && call.DeclaringType.GetGenericArguments()[0].Name=="RemoteEvent"))) { needed=true;break; }
                }
                if(needed) harmony.Patch(method,transpiler:new HarmonyMethod(typeof(LoggingPrivacyRuntime),"TrackRequests"));
            }
            foreach(var nested in type.GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic)) PatchRequests(harmony,nested);
        }
        static IEnumerable<CodeInstruction> TrackRequests(IEnumerable<CodeInstruction> instructions)
        {
            var original=AccessTools.Method(typeof(WebRequest),"Create",new[]{typeof(string)});
            foreach(var instruction in instructions)
            {
                if(Equals(instruction.operand,original)) instruction.operand=AccessTools.Method(typeof(LoggingPrivacyRuntime),"CreateRequest");
                var method=instruction.operand as MethodInfo;
                if(method!=null && method.Name=="Enqueue" && method.DeclaringType.IsGenericType && method.DeclaringType.GetGenericTypeDefinition()==typeof(Queue<>) && method.DeclaringType.GetGenericArguments()[0].Name=="RemoteEvent")
                {
                    instruction.opcode=OpCodes.Call;
                    instruction.operand=AccessTools.Method(typeof(LoggingPrivacyRuntime),"EnqueueChecked").MakeGenericMethod(method.DeclaringType.GetGenericArguments());
                }
                yield return instruction;
            }
        }
        public static void EnqueueChecked<T>(Queue<T> queue,T item)
        {
            string code=EventScope<T>.Campaign==null?campaign:Convert.ToString(EventScope<T>.Campaign.GetValue(item));
            if(string.Equals(code,"test1",StringComparison.OrdinalIgnoreCase)) { queue.Enqueue(item);return; }
            lock(gate) { if(!Block(code)) queue.Enqueue(item); }
        }
        static class EventScope<T> { internal static readonly FieldInfo Campaign=AccessTools.Field(typeof(T),"CampaignId"); }
        public static WebRequest CreateRequest(string url)
        {
            var request=WebRequest.Create(url);
            if(new Uri(url).AbsolutePath!="/events" || string.IsNullOrEmpty(sending)) return request;
            string code=sending;
            if(string.Equals(code,"test1",StringComparison.OrdinalIgnoreCase)) return request;
            lock(gate)
            {
                if(failed || string.IsNullOrEmpty(code) || store.IsStopped(code)) throw new OperationCanceledException("Campaign logging is stopped.");
                if(requests.Count>=32) throw new IOException("Too many simultaneous game requests.");
                requests[request]=code;
                if(threadRequests==null) threadRequests=new List<WebRequest>(); threadRequests.Add(request);
            }
            return request;
        }
        static string RemoteCode(object instance)
        {
            var settings=Get(instance,"settings");return settings==null?"":Convert.ToString(Get(settings,"CampaignId"));
        }
        static bool Block(string code) { if(string.Equals(code,"test1",StringComparison.OrdinalIgnoreCase)) return false;return failed || (!string.IsNullOrEmpty(code) && store!=null && store.IsStopped(code)); }
        static bool ParameterGate(ref bool __result) { if(!ParameterWrite()) { __result=false;return false; } return true; }
        static bool ParameterWrite() { RefreshContext();return !Block(campaign); }
        static bool RemoteUpdate(object __instance)
        {
            if(string.Equals(RemoteCode(__instance),"test1",StringComparison.OrdinalIgnoreCase)) return true;
            lock(gate) if(!remotes.Contains(__instance)) remotes.Add(__instance);
            if(Block(RemoteCode(__instance))) { Clear(__instance);return false; }return true;
        }
        static bool RemoteWork(object __instance,MethodBase __originalMethod)
        {
            bool allowed=!Block(RemoteCode(__instance));
            if(!allowed && __originalMethod.Name=="ScanLoggerCsv") { var field=Field(__instance.GetType(),"scanInProgress");if(field!=null) field.SetValue(__instance,false); }
            return allowed;
        }
        static bool BeforePost(object __instance)
        {
            sending=RemoteCode(__instance);if(!Block(sending)) return true;
            var field=Field(__instance.GetType(),"uploadInProgress");if(field!=null) field.SetValue(__instance,false);return false;
        }
        static void AfterPost(object __instance)
        {
            lock(gate) { if(threadRequests!=null) { foreach(var request in threadRequests) requests.Remove(request);threadRequests.Clear(); } }
            sending=null;if(Block(RemoteCode(__instance))) Clear(__instance);
        }
        static void Clear(object instance)
        {
            object sync=Get(instance,"sync");if(sync==null) return;
            lock(sync)
            {
                object queue=Get(instance,"pending");if(queue!=null) queue.GetType().GetMethod("Clear").Invoke(queue,null);
                foreach(string name in new[]{"raceLoggerEnabled","downloadAfterUpload","standingsAfterUpload","yearEndUploadPending"})
                { var field=Field(instance.GetType(),name);if(field!=null && field.FieldType==typeof(bool)) field.SetValue(instance,false); }
            }
        }
        static void OnStopped(string code)
        {
            var abort=new List<WebRequest>();var instances=new List<object>();
            lock(gate) { foreach(var pair in requests) if(string.Equals(pair.Value,code,StringComparison.OrdinalIgnoreCase)) abort.Add(pair.Key);instances.AddRange(remotes); }
            foreach(var request in abort) try { request.Abort(); } catch { }
            foreach(var instance in instances) if(string.Equals(RemoteCode(instance),code,StringComparison.OrdinalIgnoreCase)) Clear(instance);
            pendingKnown=true;
        }
        static string SnapshotSave(object snapshot) { return Convert.ToString(Get(snapshot,"Save")); }
        static bool SaveAllowed(string save)
        {
            string code;lock(gate) { if(!saves.TryGetValue(save,out code)) return false; }return !Block(code);
        }
        static bool BeforeEnqueue(object snapshot) { RefreshContext();return SaveAllowed(SnapshotSave(snapshot)); }
        static bool BeforeCsv(object snapshot) { return SaveAllowed(SnapshotSave(snapshot)); }
        static bool PathAllowed(string path)
        {
            string full=Path.GetFullPath(path);
            foreach(string root in new[]{Path.Combine(gameRoot,"saves")+Path.DirectorySeparatorChar,
                Path.Combine(gameRoot,"GameData","KSRParameterLogger","PluginData")+Path.DirectorySeparatorChar})
            {
                if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase)) continue;
                string rest=full.Substring(root.Length);int end=rest.IndexOfAny(new[]{'\\','/'});
                return end>0 && SaveAllowed(rest.Substring(0,end));
            }
            return false;
        }
        static IEnumerable<CodeInstruction> GuardCsvPublish(IEnumerable<CodeInstruction> instructions)
        {
            foreach(var instruction in instructions)
            {
                if(Equals(instruction.operand,AccessTools.Method(typeof(File),"Move",new[]{typeof(string),typeof(string)}))) instruction.operand=AccessTools.Method(typeof(LoggingPrivacyRuntime),"MoveCsv");
                if(Equals(instruction.operand,AccessTools.Method(typeof(File),"Replace",new[]{typeof(string),typeof(string),typeof(string)}))) instruction.operand=AccessTools.Method(typeof(LoggingPrivacyRuntime),"ReplaceCsv");
                yield return instruction;
            }
        }
        public static void MoveCsv(string source,string target) { lock(gate) { if(!PathAllowed(target)) throw new OperationCanceledException("Logging stopped before CSV publication.");File.Move(source,target); } }
        public static void ReplaceCsv(string source,string target,string backup) { lock(gate) { if(!PathAllowed(target)) throw new OperationCanceledException("Logging stopped before CSV publication.");File.Replace(source,target,backup); } }
        static string Value(string text,string key)
        {
            var match=Regex.Match(text??"",@"(?im)^\s*"+Regex.Escape(key)+@"\s*=\s*([^\r\n]*)");
            if(!match.Success) return "";
            string value=match.Groups[1].Value;int comment=value.IndexOf("//",StringComparison.Ordinal);
            return (comment<0?value:value.Substring(0,comment)).Trim();
        }
        static string Read(string path)
        {
            // Called by the existing once-per-second context refresh, never by
            // worker threads. Unchanged files are metadata checks, not rereads.
            try
            {
                var info=new FileInfo(path);if(!info.Exists) { configs.Remove(path);return ""; }
                if(info.Length>65536) throw new IOException("Configuration is too large.");
                TextConfig cached;
                if(configs.TryGetValue(path,out cached) && cached.Length==info.Length && cached.Ticks==info.LastWriteTimeUtc.Ticks) return cached.Text;
                string text=File.ReadAllText(path);configs[path]=new TextConfig {Length=info.Length,Ticks=info.LastWriteTimeUtc.Ticks,Text=text};return text;
            }
            catch(IOException) { failed=true;status="Campaign configuration is unavailable. Close KSP and repair the installation.";return ""; }
            catch(UnauthorizedAccessException) { failed=true;status="Campaign configuration cannot be read. Close KSP and check permissions.";return ""; }
        }
        static void RefreshContext()
        {
            if(Thread.CurrentThread.ManagedThreadId!=mainThread) return;
            string save=HighLogic.SaveFolder??"";
            if(save==saveName && Time.realtimeSinceStartup<nextConfig) return;
            saveName=save;nextConfig=Time.realtimeSinceStartup+1f;
            string root=gameRoot;
            string text=Read(Path.Combine(root,"GameData","KerbalSpaceRace","PluginData","LegacyCampaign.cfg"));
            legacy=string.Equals(Value(text,"enabled"),"true",StringComparison.OrdinalIgnoreCase);
            campaign=legacy?Value(text,"campaignId"):Value(Read(Path.Combine(root,"saves",save,"KSR","KSRRaceState.cfg")),"campaignId");
            if(legacy && string.IsNullOrEmpty(campaign)) campaign="test1";
            string tickets=Read(Path.Combine(root,"GameData","KSRLite","PluginData","GameTickets.cfg"));
            long.TryParse(Value(tickets,"userId"),out userId);ticket="";
            foreach(Match block in Regex.Matches(tickets,@"TICKET\s*\{([^{}]*)\}"))
                if(string.Equals(Value(block.Groups[1].Value,"campaignId"),campaign,StringComparison.OrdinalIgnoreCase)) ticket=Value(block.Groups[1].Value,"token");
            if(!string.IsNullOrEmpty(save) && !string.IsNullOrEmpty(campaign)) lock(gate) saves[save]=campaign;
        }
        public void Update()
        {
            if(store==null) return;
            RefreshContext();
            if(pendingKnown && !working && Time.realtimeSinceStartup>=nextRetry)
            {
                nextRetry=Time.realtimeSinceStartup+30f;working=true;
                ThreadPool.QueueUserWorkItem(delegate {
                    try { string[] codes=store.PendingCodes();if(codes.Length>0) store.Deliver(codes[(retryIndex++)%codes.Length],0,null);pendingKnown=store.HasPending; }
                    catch { status="Logging stopped locally. Server exit pending; it will retry when connected."; }
                    finally { working=false; }
                });
            }
        }
        public void OnGUI()
        {
            if(legacy || string.Equals(campaign,"test1",StringComparison.OrdinalIgnoreCase)) return;
            if(failed) { GUILayout.BeginArea(new Rect(18,80,560,100),GUI.skin.box);GUILayout.Label("KSR PRIVACY CONTROL FAILED — CLOSE KSP\n"+status);GUILayout.EndArea();return; }
            if(store==null || string.IsNullOrEmpty(campaign) || HighLogic.CurrentGame==null) return;
            GUILayout.BeginArea(new Rect(18,80,460,confirmation?220:105),GUI.skin.box);
            if(Block(campaign))
            {
                GUILayout.Label("KSR: LOGGING STOPPED — "+campaign+"\n"+(store.IsConfirmed(campaign,userId)?"Server exit confirmed":"Server exit pending"));
                if(!pendingKnown && !store.IsConfirmed(campaign,userId) && GUILayout.Button("RETRY SAVING SERVER EXIT"))
                    try { store.Stop(campaign,userId,"https://play.kerbalspacerace.net",ticket);pendingKnown=true;nextRetry=0; } catch { status="Exit could not be saved. Contact support."; }
            }
            else if(!confirmation)
            {
                if(GUILayout.Button("STOP LOGGING AND LEAVE CAMPAIGN")) confirmation=true;
            }
            else
            {
                GUILayout.Label("Stop collection and queued game uploads, and leave "+campaign+" as a player? The campaign stays open. Administrators keep their administration role. Previously submitted data keeps its recorded end-of-campaign choice.");
                if(GUILayout.Button("STOP AND LEAVE"))
                {
                    try { store.Stop(campaign,userId,"https://play.kerbalspacerace.net",ticket);confirmation=false;pendingKnown=true;nextRetry=0; }
                    catch { status="Logging blocked, but the exit could not be saved. Keep the game closed and contact support."; }
                }
                if(GUILayout.Button("CANCEL")) confirmation=false;
            }
            if(!string.IsNullOrEmpty(status)) GUILayout.Label(status);
            GUILayout.EndArea();
        }
        public void OnDestroy() { if(store!=null) store.Dispose(); }
    }
}
