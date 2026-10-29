using System.Net;
using System.Text;
using System.Text.Json;
using KsrLauncher.Core;
internal static class RegistrationTests {
 static string Response(string code,bool? open)=>JsonSerializer.Serialize(new {ok=true,campaign=new {campaignCode=code,name="Synthetic race",status="active",role="admin",registrationOpen=open}});
 public static async Task ExplicitSetting(){
  var calls=0;using var http=new HttpClient(new Handler(async request=>{
   calls++;if(request.Method!=HttpMethod.Put||request.Headers.Authorization?.Parameter!="synthetic-session"||request.RequestUri!.AbsolutePath!="/api/v1/campaigns/RACE-A/registration")throw new Exception("Wrong authenticated registration request");
   using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());if(body.RootElement.EnumerateObject().Count()!=1)throw new Exception("Unexpected mutation fields");
   var open=body.RootElement.GetProperty("open").GetBoolean();if(open!=(calls==2))throw new Exception("Registration was toggled instead of explicitly set");return Response("RACE-A",open);
  }));var client=new KsrPlatformClient(http);
  if((await client.SetCampaignRegistrationAsync("https://example.test","synthetic-session","RACE-A",false)).RegistrationOpen!=false)throw new Exception("Close not confirmed");
  if((await client.SetCampaignRegistrationAsync("https://example.test","synthetic-session","RACE-A",true)).RegistrationOpen!=true||calls!=2)throw new Exception("Reopen not confirmed");
 }
 public static async Task MismatchedConfirmation(){
  foreach(var response in new[]{Response("OTHER",false),Response("RACE-A",true),Response("RACE-A",null)}){
   using var http=new HttpClient(new Handler(_=>Task.FromResult(response)));try{await new KsrPlatformClient(http).SetCampaignRegistrationAsync("https://example.test","session","RACE-A",false);throw new Exception("Wrong or missing registration confirmation accepted");}catch(InvalidDataException){}
  }
 }
 public static async Task OlderServer(){
  using var http=new HttpClient(new Handler(request=>{
   if(request.Method!=HttpMethod.Get||request.RequestUri!.AbsolutePath!="/api/v1/campaigns/RACE-A")throw new Exception("Refresh mutated campaign");
   return Task.FromResult("""{"campaign":{"campaignCode":"RACE-A","name":"Synthetic race","status":"active","role":"admin"}}""");
  }));var result=await new KsrPlatformClient(http).GetCampaignAsync("https://example.test","session","RACE-A");if(result.RegistrationOpen is not null)throw new Exception("Old server fabricated registration state");
 }
 sealed class Handler(Func<HttpRequestMessage,Task<string>> fn):HttpMessageHandler{
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>new(HttpStatusCode.OK){Content=new StringContent(await fn(request),Encoding.UTF8,"application/json")};
 }
}
