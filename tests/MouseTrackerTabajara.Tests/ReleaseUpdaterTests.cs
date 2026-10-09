using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MouseTrackerTabajara.Application;
using Xunit;
using Xunit.Abstractions;
namespace MouseTrackerTabajara.Tests;

public class ReleaseUpdaterTests(ITestOutputHelper output)
{
    private const string Version="0.1.0-dev.13", Repo="https://github.com/pcarvalho75/RatoObservatorTabajara/releases/";
    private string Root(){var root=Path.Combine(Path.GetTempPath(),"tabajara-s44-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"outside-sentinel.txt"),"original untouched");output.WriteLine("retained update evidence="+root);return root;}
    private static UpdateRelease Release(byte[] zip,string version=Version)=>new(version,version,new(Repo+"tag/"+version),new(Repo+"download/"+version+"/RatoObservatorTabajara-"+version+"-win-x64.zip"),zip.Length,Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant());
    private static byte[] Package(string? extra=null,string defect="",int entries=0)
    {
        var files=new Dictionary<string,byte[]>{["build-identity.json"]=JsonSerializer.SerializeToUtf8Bytes(new{version=defect=="version"?"0.1.0-dev.12":Version,rid=defect=="rid"?"linux-x64":"win-x64",self_contained=defect!="runtime"}),["Start Mouse Tracker Tabajara.cmd"]=Encoding.UTF8.GetBytes("@echo synthetic"),["desktop/MouseTrackerTabajara.exe"]=[1,2,3], ["desktop/MouseTrackerTabajara.dll"]=[4,5], ["desktop/coreclr.dll"]=[6], ["desktop/hostfxr.dll"]=[7], ["desktop/hostpolicy.dll"]=[8]};
        foreach(var name in new[]{"MouseTrackerTabajara.runtimeconfig.json","MouseTrackerTabajara.deps.json","MouseTrackerTabajara.Application.dll","MouseTrackerTabajara.Core.dll","MouseTrackerTabajara.Storage.dll","MouseTrackerTabajara.Tracking.dll","PresentationFramework.dll"})files["desktop/"+name]=[1,2];
        if(defect=="required")files.Remove("desktop/hostfxr.dll");
        if(extra is not null)files[extra]=[9];
        if(defect=="large"){var data=new byte[150000];new Random(7).NextBytes(data);files["docs/random.bin"]=data;}
        for(int i=0;i<entries;i++)files[$"extra/{i}.txt"]=[1];
        var manifest=files.Select(p=>new{path=defect=="case"&&p.Key=="desktop/coreclr.dll"?"Desktop/coreclr.dll":p.Key,bytes=(long)p.Value.Length+(defect=="bytes"?1:0),sha256=defect=="hash"?new string('0',64):Convert.ToHexString(SHA256.HashData(p.Value)).ToLowerInvariant()}).ToArray();
        files["files.sha256.json"]=JsonSerializer.SerializeToUtf8Bytes(defect=="incomplete"?manifest.Skip(1).ToArray():manifest);
        using var memory=new MemoryStream();using(var archive=new ZipArchive(memory,ZipArchiveMode.Create,true))foreach(var file in files){var e=archive.CreateEntry(file.Key);if(defect=="symlink"&&file.Key==extra)e.ExternalAttributes=unchecked((int)0xa1ff0000);if(defect=="reparse"&&file.Key==extra)e.ExternalAttributes=(int)FileAttributes.ReparsePoint;using var stream=e.Open();stream.Write(file.Value);}
        return memory.ToArray();
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> call):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>call(request,token);}
    private static HttpClient Client(byte[] bytes,HttpStatusCode code=HttpStatusCode.OK)=>new(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(code){Content=new ByteArrayContent(bytes)})));
    private static object Candidate(string version,string? digest="sha256:"+"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",bool draft=false,bool? prerelease=null,string? download=null,long size=100)
        =>new{tag_name=version,html_url=Repo+"tag/"+version,draft,prerelease=prerelease??version.Contains("-dev."),assets=new[]{new{name="RatoObservatorTabajara-"+version+"-win-x64.zip",state="uploaded",size,digest,browser_download_url=download??Repo+"download/"+version+"/RatoObservatorTabajara-"+version+"-win-x64.zip"}}};
    [Fact] public async Task NumericVersionSelectionStablePrecedenceAndChannelsUsePinnedRequest()
    {
        var json=JsonSerializer.SerializeToUtf8Bytes(new[]{Candidate("0.1.0-dev.9"),Candidate("0.1.0-dev.12"),Candidate("0.1.0-dev.13"),Candidate("0.1.0-dev.200",draft:true),Candidate("0.1.0-dev.14",digest:null),Candidate("0.1.0-dev.15",download:"https://evil.example/update.zip")});
        using var http=new HttpClient(new Handler((request,_)=>{Assert.Equal("https://api.github.com/repos/pcarvalho75/RatoObservatorTabajara/releases?per_page=30",request.RequestUri!.AbsoluteUri);Assert.NotEmpty(request.Headers.UserAgent);Assert.Contains(request.Headers.Accept,x=>x.MediaType=="application/vnd.github+json");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(json)});}));
        var updater=new ReleaseUpdater(http,Root());Assert.Equal("0.1.0-dev.13",(await updater.CheckAsync("0.1.0-dev.12",true))!.Version);Assert.Null(await updater.CheckAsync("0.1.0-dev.13",true));Assert.Null(await updater.CheckAsync("0.1.0-dev.12",false));
        using var stable=Client(JsonSerializer.SerializeToUtf8Bytes(new[]{Candidate("0.1.0"),Candidate("0.1.0-dev.999")}));Assert.Equal("0.1.0",(await new ReleaseUpdater(stable,Root()).CheckAsync("0.1.0-dev.12",false))!.Version);
    }
    [Theory][InlineData("0.1.0-dev.013")][InlineData("0.1.0-preview.13")][InlineData("0.1.0-dev.x")][InlineData("1.0")]
    public async Task InvalidVersionsAreNotOffered(string bad)
    {using var http=Client(JsonSerializer.SerializeToUtf8Bytes(new[]{Candidate(bad)}));Assert.Null(await new ReleaseUpdater(http,Root()).CheckAsync("0.1.0-dev.12",true));await Assert.ThrowsAsync<InvalidDataException>(()=>new ReleaseUpdater(http,Root()).CheckAsync(bad,true));}
    [Fact] public async Task SuccessInstallsOnlyVerifiedImmutableVersionAndRevalidatesBeforeLaunch()
    {
        var root=Root();var old=Path.Combine(root,"old-version");Directory.CreateDirectory(old);File.WriteAllText(Path.Combine(old,"retained.tabajara"),"old study untouched");var zip=Package();using var http=Client(zip);var updater=new ReleaseUpdater(http,root);
        var first=await updater.PrepareAsync(Release(zip));ReleaseUpdater.ValidatePrepared(first);var second=await updater.PrepareAsync(Release(zip));Assert.NotEqual(first.InstallDirectory,second.InstallDirectory);Assert.StartsWith(root+Path.DirectorySeparatorChar,first.InstallDirectory);Assert.Equal("original untouched",File.ReadAllText(Path.Combine(root,"outside-sentinel.txt")));Assert.Equal("old study untouched",File.ReadAllText(Path.Combine(old,"retained.tabajara")));
        Assert.Equal(Version,ReleaseUpdater.ReadCurrentVersion(Path.GetDirectoryName(first.ExecutablePath)!));Assert.Equal(Version,ReleaseUpdater.ReadCurrentVersion(Path.GetDirectoryName(first.ExecutablePath)!+Path.DirectorySeparatorChar,"0.0.0"));Assert.Equal("0.0.0",ReleaseUpdater.ReadCurrentVersion(Path.Combine(root,"uninstalled")));
        File.AppendAllText(first.ExecutablePath,"tamper");Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ValidatePrepared(first));Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ValidatePrepared(second with{ExecutablePath=Path.Combine(root,"outside-sentinel.txt")}));
    }
    [Theory][InlineData("../escaped.txt")][InlineData("/absolute.txt")][InlineData("C:/escape.txt")][InlineData("desktop/CON.txt")][InlineData("desktop/x:stream")][InlineData("desktop/trailing.")][InlineData("desktop/x ")][InlineData("desktop/../escape")][InlineData("desktop\\escape")][InlineData("study.tabajara")][InlineData("docs/study.db")][InlineData("desktop/study.db-wal")][InlineData("desktop/study.db-shm")]
    public async Task HostileWindowsAndStudyPathsNeverPublish(string path)
    {var root=Root();var zip=Package(path);using var http=Client(zip);await Assert.ThrowsAnyAsync<Exception>(()=>new ReleaseUpdater(http,root).PrepareAsync(Release(zip)));Assert.Empty(Directory.GetDirectories(root,Version+"-*"));Assert.Equal("original untouched",File.ReadAllText(Path.Combine(root,"outside-sentinel.txt")));Assert.Single(Directory.GetFiles(root,"failed.json",SearchOption.AllDirectories));}
    [Theory][InlineData("version")][InlineData("rid")][InlineData("runtime")][InlineData("required")][InlineData("hash")][InlineData("bytes")][InlineData("incomplete")][InlineData("case")][InlineData("symlink")][InlineData("reparse")]
    public async Task WrongIdentityManifestOrLinksNeverPublish(string defect)
    {var root=Root();var zip=Package("docs/extra.txt",defect);using var http=Client(zip);await Assert.ThrowsAnyAsync<Exception>(()=>new ReleaseUpdater(http,root).PrepareAsync(Release(zip)));Assert.Empty(Directory.GetDirectories(root,Version+"-*"));}
    [Fact] public async Task DuplicateAndParentCaseAliasesRejectedBeforeReady()
    {
        foreach(var name in new[]{"desktop/coreclr.dll","desktop/CORECLR.dll","Desktop/extra.dll"})
        {var zip=Package();using(var memory=new MemoryStream()){memory.Write(zip);memory.Position=0;using(var z=new ZipArchive(memory,ZipArchiveMode.Update,true)){using var s=z.CreateEntry(name).Open();s.WriteByte(1);}zip=memory.ToArray();}var root=Root();using var http=Client(zip);await Assert.ThrowsAsync<InvalidDataException>(()=>new ReleaseUpdater(http,root).PrepareAsync(Release(zip)));Assert.Empty(Directory.GetDirectories(root,Version+"-*"));}
    }
    [Fact] public async Task TransferSizeHashMetadataBoundAndFileCountFailuresKeepOldInstall()
    {
        var zip=Package();var root=Root();using var http=Client(zip);var updater=new ReleaseUpdater(http,root);
        await Assert.ThrowsAsync<InvalidDataException>(()=>updater.PrepareAsync(Release(zip) with{Sha256=new string('0',64)}));await Assert.ThrowsAsync<InvalidDataException>(()=>updater.PrepareAsync(Release(zip) with{Bytes=zip.Length+1}));await Assert.ThrowsAsync<InvalidDataException>(()=>updater.PrepareAsync(Release(zip) with{Bytes=512L*1024*1024}));
        using var metadata=Client(new byte[2*1024*1024+1]);await Assert.ThrowsAsync<InvalidDataException>(()=>new ReleaseUpdater(metadata,root).CheckAsync("0.1.0-dev.12",true));
        var many=Package(entries:5001);using var excess=Client(many);await Assert.ThrowsAsync<InvalidDataException>(()=>new ReleaseUpdater(excess,root).PrepareAsync(Release(many)));Assert.Empty(Directory.GetDirectories(root,Version+"-*"));
    }
    [Fact] public async Task CancellationAndHttpFailureRetainStageAndCurrentSentinel()
    {
        var root=Root();var zip=Package();using var cts=new CancellationTokenSource();using var http=new HttpClient(new Handler(async(_,token)=>{cts.Cancel();await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();}));await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ReleaseUpdater(http,root).PrepareAsync(Release(zip),token:cts.Token));
        using var failure=Client(zip,HttpStatusCode.ServiceUnavailable);await Assert.ThrowsAsync<HttpRequestException>(()=>new ReleaseUpdater(failure,root).PrepareAsync(Release(zip)));Assert.Equal("original untouched",File.ReadAllText(Path.Combine(root,"outside-sentinel.txt")));Assert.Empty(Directory.GetDirectories(root,Version+"-*"));Assert.Equal(2,Directory.GetFiles(root,"failed.json",SearchOption.AllDirectories).Length);
    }
    private sealed class UnknownLengthContent(byte[] bytes):HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream,System.Net.TransportContext? context)=>stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length){length=0;return false;}
    }
    private sealed class InlineProgress(Action<UpdateProgress> report):IProgress<UpdateProgress>{public void Report(UpdateProgress value)=>report(value);}
    [Fact] public async Task StreamingWithoutLengthRejectsShortAndOverlongAndCancelsAfterPartialArchive()
    {
        var root=Root();var zip=Package();
        foreach(var bytes in new[]{zip[..^1],zip.Concat(new byte[]{0}).ToArray()})
        {using var http=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new UnknownLengthContent(bytes)})));await Assert.ThrowsAsync<InvalidDataException>(()=>new ReleaseUpdater(http,root).PrepareAsync(Release(zip)));}
        var large=Package(defect:"large");using var cts=new CancellationTokenSource();using var client=Client(large);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ReleaseUpdater(client,root).PrepareAsync(Release(large),new InlineProgress(p=>{if(p.BytesReceived>0)cts.Cancel();}),cts.Token));
        Assert.Contains(Directory.GetFiles(root,"download.zip",SearchOption.AllDirectories),p=>new FileInfo(p).Length>0&&new FileInfo(p).Length<large.Length);Assert.Empty(Directory.GetDirectories(root,Version+"-*"));
    }
    [Fact] public async Task DeclaredExpansionAtLimitRejectedWithoutAllocatingGigabytes()
    {
        var zip=Package();var changed=false;
        for(int i=0;i<zip.Length-46;i++)if(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i,4))==0x02014b50){System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i+24,4),0x80000000);changed=true;break;}
        Assert.True(changed);using var http=Client(zip);var root=Root();await Assert.ThrowsAsync<InvalidDataException>(()=>new ReleaseUpdater(http,root).PrepareAsync(Release(zip)));Assert.Empty(Directory.GetDirectories(root,Version+"-*"));
    }
    [Fact] public async Task ValidatePreparedCancellationAndInvalidVersionIdentityRemainFailClosed()
    {
        var root=Root();var zip=Package();using var http=Client(zip);var ready=await new ReleaseUpdater(http,root).PrepareAsync(Release(zip));using var cts=new CancellationTokenSource();cts.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>ReleaseUpdater.ValidatePrepared(ready,cts.Token));
        File.WriteAllText(Path.Combine(ready.InstallDirectory,"build-identity.json"),"{broken");Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ReadCurrentVersion(Path.GetDirectoryName(ready.ExecutablePath)!,"0.0.0"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>new ReleaseUpdater(http,root).PrepareAsync(Release(zip) with{DownloadUrl=new("https://github.com/other/repo/releases/download/x/update.zip")}));
    }    [Theory][InlineData("count")][InlineData("disk")][InlineData("metadata")]
    public async Task CentralIndexIsBoundedBeforeZipEntryMaterialization(string defect)
    {
        var zip=Package();var footer=zip.Length-22;Assert.Equal(0x06054b50u,System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(footer,4)));
        if(defect=="count")System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(footer+10,2),1);
        if(defect=="disk")System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(footer+4,2),1);
        if(defect=="metadata")System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(footer+12,4),2*1024*1024+1);
        var root=Root();using var http=Client(zip);await Assert.ThrowsAsync<InvalidDataException>(()=>new ReleaseUpdater(http,root).PrepareAsync(Release(zip)));Assert.Empty(Directory.GetDirectories(root,Version+"-*"));
    }    [Fact] public void CurrentVersionHandlesTrailingSeparatorBomAndRejectsMalformedVersions()
    {
        var root=Root();var app=Path.Combine(root,"desktop");Directory.CreateDirectory(app);var identity=Path.Combine(root,"build-identity.json");
        File.WriteAllText(identity,JsonSerializer.Serialize(new{version=Version}),new UTF8Encoding(true));
        Assert.Equal(Version,ReleaseUpdater.ReadCurrentVersion(app));Assert.Equal(Version,ReleaseUpdater.ReadCurrentVersion(app+Path.DirectorySeparatorChar,"0.0.0"));
        File.WriteAllText(identity,JsonSerializer.Serialize(new{version="0.1.0-dev.013"}),new UTF8Encoding(true));Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ReadCurrentVersion(app+Path.DirectorySeparatorChar,"0.0.0"));
        File.WriteAllText(identity,"\uFEFF{broken",new UTF8Encoding(false));Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ReadCurrentVersion(app+Path.DirectorySeparatorChar,"0.0.0"));
    }}
