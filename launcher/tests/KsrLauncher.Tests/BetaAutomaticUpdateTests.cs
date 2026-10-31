using KsrLauncher.Core;

internal static class BetaAutomaticUpdateTests
{
    private static KsrBetaModule Module(string id) => new(id,id,"",0,"",id=="ksr-full"?"full-mode":"installation");
    private static string Fixture()
    {
        var root=Path.Combine(Path.GetTempPath(),"ksr-auto-beta-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root,"GameData"));File.WriteAllText(Path.Combine(root,"KSP_x64.exe"),"fixture");return root;
    }
    public static async Task MissingFolderNeverInvokesDownload()
    {
        var partial=Fixture();Directory.Delete(Path.Combine(partial,"GameData"));int requests=0;
        try {
            await BetaInstallationUpdates.RunAsync(new[]{Module("probalism"),Module("ksr-full"),Module("future-beta")},
                id=>id=="probalism"?null:id=="ksr-full"?"":partial,()=>true,
                (_,_)=>{requests++;return Task.CompletedTask;},(_,_)=>throw new Exception("Invalid folder is a silent skip"));
            if(requests!=0)throw new Exception("Download requested before selecting a valid folder");
        } finally {Directory.Delete(partial,true);}
    }
    public static async Task AuthorizedBetasSequentialAndIsolated()
    {
        var full=Fixture();var probalism=Fixture();var future=Fixture();int active=0;
        var updated=new List<string>();var failures=new List<string>();
        try {
            await BetaInstallationUpdates.RunAsync(new[]{Module("ksr-full"),Module("probalism"),Module("future-beta")},
                id=>id=="ksr-full"?full:id=="probalism"?probalism:future,()=>true,
                async (m,r)=>{if(++active!=1)throw new Exception("Concurrent beta operations");try{await Task.Yield();if(m.Id=="probalism")throw new IOException("offline");updated.Add(m.Id+":"+r);}finally{active--; }},
                (m,_)=>failures.Add(m.Id));
            if(updated.Count!=2 || failures.Single()!="probalism" || !updated[0].EndsWith(full) || !updated[1].EndsWith(future))throw new Exception("Failure or destination isolation regressed");
        } finally {foreach(var r in new[]{full,probalism,future})Directory.Delete(r,true);}
    }
    public static async Task RevocationAndDestinationChangesStopNewDownloads()
    {
        var first=Fixture();var second=Fixture();bool authorized=true;int calls=0;
        try {
            await BetaInstallationUpdates.RunAsync(new[]{Module("ksr-full"),Module("probalism")},id=>id=="ksr-full"?first:second,
                ()=>authorized,(_,_)=>{calls++;authorized=false;return Task.CompletedTask;},(_,_)=>{});
            if(calls!=1)throw new Exception("Another beta started after session revocation");
            calls=0;string? current=second;
            await BetaInstallationUpdates.RunAsync(new[]{Module("ksr-full"),Module("probalism")},id=>id=="ksr-full"?first:current,
                ()=>true,(_,_)=>{calls++;current=null;return Task.CompletedTask;},(_,_)=>{});
            if(calls!=1)throw new Exception("A cleared destination was reused");
        } finally {Directory.Delete(first,true);Directory.Delete(second,true);}
    }
    public static async Task SharedFoldersNeverMixAutomaticPackages()
    {
        var root=Fixture();int calls=0;int rejected=0;
        try {
            await BetaInstallationUpdates.RunAsync(new[]{Module("ksr-full"),Module("probalism")},_=>root,()=>true,
                (_,_)=>{calls++;return Task.CompletedTask;},(_,_)=>rejected++);
            if(calls!=0||rejected!=2)throw new Exception("Two beta packages shared an automatic destination");
            await BetaInstallationUpdates.RunAsync(new[]{Module("probalism")},_=>root,()=>true,
                (_,_)=>{calls++;return Task.CompletedTask;},(_,_)=>rejected++,root);
            if(calls!=0||rejected!=3)throw new Exception("Beta package targeted main installation");
        } finally {Directory.Delete(root,true);}
    }
    public static Task StatePathsMatchExistingManualBetaUpdates()
    {
        var game=Path.Combine(Path.GetTempPath(),"beta-game");var data=Path.Combine(Path.GetTempPath(),"launcher-data");
        var full=BetaInstallationUpdates.GetLauncherDataRoot("ksr-full",game,null,data);
        if(full!=TestInstallationPaths.GetLauncherDataRoot(game,null,data))throw new Exception("Legacy Full state moved");
        var pathKey=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(game).ToUpperInvariant())))[..16];
        var beta=BetaInstallationUpdates.GetLauncherDataRoot("probalism",game,null,data);
        if(beta!=Path.Combine(data,"beta","probalism",pathKey)||beta==BetaInstallationUpdates.GetLauncherDataRoot("future-beta",game,null,data))throw new Exception("Manual and automatic states differ");
        return Task.CompletedTask;
    }
}
