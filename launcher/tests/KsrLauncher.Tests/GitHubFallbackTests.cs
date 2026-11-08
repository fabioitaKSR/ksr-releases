using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KsrLauncher.Core;

internal static class GitHubFallbackTests
{
    const string Prefix = "https://github.com/fabioitaKSR/ksr-releases/releases/download/v0.7.33/";
    static HttpResponseMessage Reply(HttpRequestMessage request, byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { RequestMessage=request, Content=new ByteArrayContent(bytes) };
    static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
    static byte[] Lookup(string name, string? browser=null, string tag="v0.7.33", bool draft=false) =>
        Json(new {tag_name=tag,draft,assets=new[]{new{id=7,name,browser_download_url=browser??Prefix+name}}});
    static async Task Reject(Func<Task> action)
    { try { await action(); } catch(Exception e) when(e is InvalidDataException or HttpRequestException) { return; } throw new Exception("Unsafe fallback accepted."); }

    public static async Task SignedLauncher()
    {
        using var key = new TestSignatures();
        var exe=Encoding.UTF8.GetBytes("synthetic executable fixture");
        var name="KSR-Launcher-v0.7.33-win-x64.exe";
        var sha=Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant();
        var metadata=Json(new SignedLauncherMetadata(1,"KSRLauncher","0.7.33",name,Prefix+name,sha,exe.Length));
        var proof=key.Sign(metadata);
        var names=new[]{"ksr-launcher-update.json","ksr-launcher-update.json.sig",name,"ksr-release.json"};
        var release=new{tag_name="v0.7.33",draft=false,prerelease=false,assets=names.Select((n,i)=>new{id=i+1,name=n,browser_download_url=Prefix+n,size=n==name?exe.Length:0}).ToArray()};
        bool tampered=false,badProof=false; int apiDownloads=0;
        using var http=new HttpClient(new Handler((request,_)=>
        {
            if(request.Headers.Authorization is not null)throw new Exception("Public fallback sent credentials.");
            if(request.RequestUri!.Host=="github.com")return Task.FromResult(Reply(request,[],HttpStatusCode.GatewayTimeout));
            if(request.RequestUri.Host!="api.github.com")throw new Exception("Foreign fallback host.");
            var path=request.RequestUri.AbsolutePath;
            if(path.EndsWith("/releases"))return Task.FromResult(Reply(request,Json(new[]{release})));
            if(path.EndsWith("/tags/v0.7.33"))return Task.FromResult(Reply(request,Json(release)));
            if(request.Headers.Accept.Single().MediaType!="application/octet-stream")throw new Exception("Binary API header missing.");
            apiDownloads++;
            return Task.FromResult(Reply(request,path.EndsWith("/1")?metadata:path.EndsWith("/2")?(badProof?Encoding.UTF8.GetBytes("invalid proof"):proof):tampered?Enumerable.Repeat((byte)1,exe.Length).ToArray():exe));
        }));
        var service=new LauncherUpdateService(http,key.Verifier);
        var update=await service.CheckAsync("fabioitaKSR/ksr-releases",new Version(0,7,32))??throw new Exception("Fallback discovery absent.");
        var folder=Path.Combine(Path.GetTempPath(),"ksr-api-fallback-"+Guid.NewGuid().ToString("N"));
        var downloaded=await service.DownloadAsync(update,folder);
        if(!exe.SequenceEqual(await File.ReadAllBytesAsync(downloaded))||apiDownloads!=3)throw new Exception("Fallback download failed.");
        tampered=true; await Reject(()=>service.DownloadAsync(update,folder));
        if(!exe.SequenceEqual(await File.ReadAllBytesAsync(downloaded))||File.Exists(downloaded+".part"))throw new Exception("Bad hash replaced verified executable.");
        badProof=true; await Reject(()=>service.CheckAsync("fabioitaKSR/ksr-releases",new Version(0,7,32)));
    }

    public static async Task ExactAssetBoundary()
    {
        foreach(var kind in new[]{"url","tag","draft","duplicate"})
        {
            int calls=0;
            using var http=new HttpClient(new Handler((request,_)=>
            {
                calls++;
                if(request.RequestUri!.Host=="github.com")return Task.FromResult(Reply(request,[],HttpStatusCode.BadGateway));
                if(calls!=2)throw new Exception("Unmatched asset was fetched.");
                var bytes=kind=="duplicate"?Json(new{tag_name="v0.7.33",draft=false,assets=new[]{new{id=7,name="proof.sig",browser_download_url=Prefix+"proof.sig"},new{id=8,name="proof.sig",browser_download_url=Prefix+"proof.sig"}}}):
                    Lookup("proof.sig",kind=="url"?"https://evil.test/proof.sig":null,kind=="tag"?"other":"v0.7.33",kind=="draft");
                return Task.FromResult(Reply(request,bytes));
            }));
            await Reject(()=>ReleaseSignatures.DownloadBoundedAsync(http,Prefix+"proof.sig",100,default));
            if(calls!=2)throw new Exception("Unexpected fallback requests.");
        }
    }

    public static async Task Cancellation()
    {
        int calls=0;using var cancel=new CancellationTokenSource();
        using var http=new HttpClient(new Handler(async (request,token)=>{calls++;await Task.Delay(Timeout.Infinite,token);return Reply(request,[]);}));
        cancel.CancelAfter(30);
        try{await ReleaseSignatures.DownloadBoundedAsync(http,Prefix+"proof.sig",100,cancel.Token);throw new Exception("Cancellation ignored.");}
        catch(OperationCanceledException){}
        if(calls!=1)throw new Exception("Cancellation started API fallback.");
    }

    public static async Task PackageHashAndSize()
    {
        var bytes=Encoding.UTF8.GetBytes("package fixture"); int calls=0;
        using var http=new HttpClient(new Handler((request,_)=>
        {
            calls++;
            return Task.FromResult(Reply(request,request.RequestUri!.Host=="github.com"?[]:request.RequestUri.AbsolutePath.Contains("/tags/")?Lookup("package.zip"):bytes,
                request.RequestUri.Host=="github.com"?HttpStatusCode.GatewayTimeout:HttpStatusCode.OK));
        }));
        var component=new ComponentManifest{Id="fixture",Asset="package.zip",AssetUrl=Prefix+"package.zip",Size=bytes.Length,Sha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()};
        var folder=Path.Combine(Path.GetTempPath(),"ksr-fallback-package-"+Guid.NewGuid().ToString("N"));
        var packages=new PackageService(http);var result=await packages.AcquireAsync("",component,folder,default);
        if(!bytes.SequenceEqual(await File.ReadAllBytesAsync(result))||calls!=3)throw new Exception("Package fallback failed.");
        component.Sha256=new string('0',64);await Reject(()=>packages.AcquireAsync("",component,folder,default));
        component.Size=bytes.Length-1;await Reject(()=>packages.AcquireAsync("",component,folder,default));
    }

    public static async Task AuthAndHostBoundaries()
    {
        foreach(var code in new[]{401,403,404,429})
        {
            int calls=0;using var http=new HttpClient(new Handler((r,_)=>{calls++;return Task.FromResult(Reply(r,[],(HttpStatusCode)code));}));
            await Reject(()=>ReleaseSignatures.DownloadBoundedAsync(http,Prefix+"proof.sig",100,default));
            if(calls!=1)throw new Exception("Authentication/rate limit response caused extra requests.");
        }
        foreach(var url in new[]{Prefix+"proof.sig","https://play.kerbalspacerace.net/api/v1/betas/proof.sig"})
        {
            int calls=0;using var http=new HttpClient(new Handler((r,_)=>{calls++;return Task.FromResult(Reply(r,[],HttpStatusCode.GatewayTimeout));}));
            http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","synthetic-private-credential");
            await Reject(()=>ReleaseSignatures.DownloadBoundedAsync(http,url,100,default));
            if(calls!=1)throw new Exception("Private credentials crossed into fallback.");
        }
    }

    public static async Task HealthyDirect()
    {
        int calls=0;var bytes=Encoding.UTF8.GetBytes("healthy direct download");
        using var http=new HttpClient(new Handler((r,_)=>{calls++;return Task.FromResult(Reply(r,bytes));}));
        var result=await ReleaseSignatures.DownloadBoundedAsync(http,Prefix+"proof.sig",100,default);
        if(calls!=1||!bytes.SequenceEqual(result))throw new Exception("Healthy download used extra API requests.");
    }

    public static async Task BudgetAndBoundedFailure()
    {
        foreach(var kind in new[]{"large","api-failure","bad-json"})
        {
            int calls=0;using var http=new HttpClient(new Handler((r,_)=>
            {
                calls++;if(calls==1)return Task.FromResult(Reply(r,[],HttpStatusCode.GatewayTimeout));
                if(calls!=2)throw new Exception("Fallback retried indefinitely.");
                return Task.FromResult(Reply(r,kind=="large"?new byte[2*1024*1024+1]:Encoding.UTF8.GetBytes("invalid json"),kind=="api-failure"?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK));
            }));
            await Reject(()=>ReleaseSignatures.DownloadBoundedAsync(http,Prefix+"proof.sig",100,default));
            if(calls!=2)throw new Exception("Unexpected API fallback count.");
        }
    }
    sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> response):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>response(request,token);}
}
