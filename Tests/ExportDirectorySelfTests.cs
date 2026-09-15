using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    public static class ExportDirectorySelfTests
    {
        public static void Run()
        {
            SameArticleReusesAndDifferentArticlesSeparate();
            EveryFormatHasReadableDirectoryName();
            UnknownFilesAndDirectoriesAreNeverClaimed();
            SanitizedAndTruncatedTitlesCannotCollide();
            UnicodeAndReservedNamesRemainValid();
            LongPathsKeepTheDownloadBudget();
            LostIndexAndReplacedDirectoryAreConservative();
            MissingDirectoryIdentityStillAllowsExport();
            ScopedRegistryKeepsExportsClean();
            CancellationAndConcurrentOwnership().GetAwaiter().GetResult();
        }
        static void SameArticleReusesAndDifferentArticlesSeparate()
        {
            using var fixture=new Fixture(); var first=Article("1","同标题"); var second=Article("2","同标题");
            string a=fixture.Registry.Allocate(fixture.Exports,first,ExportFormat.Audio);
            string b=fixture.Registry.Allocate(fixture.Exports,second,ExportFormat.Audio);
            File.WriteAllText(Path.Combine(a,"audio.mp3"),"fixture saved media");
            Check(Path.GetFileName(a)=="同标题(音频)" && Path.GetFileName(b)=="同标题(音频)(2)","不同文章同名目录没有可读编号");
            first.Title="之后补全的标题"; first.Url+="&key=synthetic-changed-session";
            var reopened=new ExportDirectoryRegistry(fixture.Data);
            Check(reopened.Allocate(fixture.Exports,first,ExportFormat.Audio)==a && File.ReadAllText(Path.Combine(a,"audio.mp3"))=="fixture saved media",
                "重复导出、标题补全或会话参数变化没有复用已归属目录");
            var other=Article("1","同标题"); other.Biz="another-account";
            Check(reopened.Allocate(fixture.Exports,other,ExportFormat.Audio)!=a,"不同公众号的同ID文章共用目录");
        }
        static void EveryFormatHasReadableDirectoryName()
        {
            using var fixture=new Fixture(); var article=Article("formats","标题");
            foreach(var pair in new[] {
                (ExportFormat.Html,"正文HTML"),(ExportFormat.Markdown,"正文Markdown"),(ExportFormat.Text,"正文TXT"),(ExportFormat.Pdf,"正文PDF"),
                (ExportFormat.Images,"图片"),(ExportFormat.Audio,"音频"),(ExportFormat.Video,"视频"),(ExportFormat.AllMedia,"全部媒体"),(ExportFormat.Full,"全文") })
            {
                string path=fixture.Registry.Allocate(fixture.Exports,article,pair.Item1);
                Check(Path.GetFileName(path)=="标题("+pair.Item2+")","类型目录名称或顺序错误："+pair.Item1);
            }
        }
        static void UnknownFilesAndDirectoriesAreNeverClaimed()
        {
            using var fixture=new Fixture(); Directory.CreateDirectory(fixture.Exports);
            string unknown=Path.Combine(fixture.Exports,"旧文章(全文)"); Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(unknown,"user.txt"),"user fixture");
            File.WriteAllText(Path.Combine(fixture.Exports,"旧文章(全文)(2)"),"existing file fixture");
            string allocated=fixture.Registry.Allocate(fixture.Exports,Article("unknown","旧文章"),ExportFormat.Full);
            Check(Path.GetFileName(allocated)=="旧文章(全文)(3)" && File.ReadAllText(Path.Combine(unknown,"user.txt"))=="user fixture"
                && File.ReadAllText(Path.Combine(fixture.Exports,"旧文章(全文)(2)"))=="existing file fixture","未知目录/同名文件被接管或覆盖");
            string empty=Path.Combine(fixture.Exports,"空目录(图片)"); Directory.CreateDirectory(empty);
            Check(fixture.Registry.Allocate(fixture.Exports,Article("empty","空目录"),ExportFormat.Images)!=empty,"未知空目录被推断为本工具已拥有");
        }
        static void SanitizedAndTruncatedTitlesCannotCollide()
        {
            using var fixture=new Fixture();
            string first=fixture.Registry.Allocate(fixture.Exports,Article("safe1","a:b"),ExportFormat.Text);
            string second=fixture.Registry.Allocate(fixture.Exports,Article("safe2","a?b"),ExportFormat.Text);
            Check(first!=second && Path.GetFileName(first)=="ab(正文TXT)","非法字符清理后的同名文章被合并");
            string prefix=new string('长',250);
            first=fixture.Registry.Allocate(fixture.Exports,Article("long1",prefix+"甲"),ExportFormat.Images);
            second=fixture.Registry.Allocate(fixture.Exports,Article("long2",prefix+"乙"),ExportFormat.Images);
            Check(first!=second && Path.GetFileName(second).EndsWith("(图片)(2)",StringComparison.Ordinal),"长标题截断后的同名文章被合并");
        }
        static void UnicodeAndReservedNamesRemainValid()
        {
            using var fixture=new Fixture();
            foreach(string title in new[]{"CON.txt","NUL","LPT1","COM²","../甲:乙*?\"<>|\\\0. ","👩🏽‍💻设计课😀",new string('文',115)+"👩🏽‍💻尾部","\ud83d坏UTF16"})
            {
                string path=fixture.Registry.Allocate(fixture.Exports,Article(Guid.NewGuid().ToString("N"),title),ExportFormat.Full);
                string leaf=Path.GetFileName(path);
                Check(leaf.IndexOfAny(Path.GetInvalidFileNameChars())<0 && Directory.Exists(path),"Windows非法字符/设备名导致无效目录");
                for(int i=0;i<leaf.Length;i++)
                {
                    if(char.IsHighSurrogate(leaf[i]))Check(i+1<leaf.Length && char.IsLowSurrogate(leaf[++i]),"目录截断了emoji代理对");
                    else Check(!char.IsLowSurrogate(leaf[i]),"目录包含孤立低代理项");
                }
            }
            Check(ExportFileSystem.SafeName("a👩🏽‍💻",2)=="a","标题截断拆开了组合emoji");
            Check(ExportFileSystem.SafeName("...  ",20)=="未命名文章","空白/点标题没有合法后备名称");
        }
        static void LongPathsKeepTheDownloadBudget()
        {
            using var fixture=new Fixture(); string root=fixture.Exports;
            const int desired=164;
            while(root.Length<desired)
            {
                int count=Math.Min(45,desired-root.Length-1);
                if(count==0) { root+="p"; continue; }
                root=Path.Combine(root,new string('p',count));
            }
            string path=fixture.Registry.Allocate(root,Article("budget",new string('题',200)),ExportFormat.Markdown);
            Check(path.Length<=195 && Directory.Exists(path),"文章目录未给下载临时文件预留长度");
            string excessive=Path.Combine(fixture.Exports,new string('x',190-fixture.Exports.Length-1)); bool rejected=false;
            try { fixture.Registry.Allocate(excessive,Article("too-long","长路径"),ExportFormat.Markdown); } catch(IOException) { rejected=true; }
            Check(rejected && !Directory.Exists(excessive),"过长目录没有在创建前拒绝");
        }
        static void LostIndexAndReplacedDirectoryAreConservative()
        {
            using var fixture=new Fixture(); var original=Article("replace","原文章");
            string old=fixture.Registry.Allocate(fixture.Exports,original,ExportFormat.Audio);
            string moved=Path.Combine(fixture.Exports,"moved-fixture-directory"); Directory.Move(old,moved);
            Directory.CreateDirectory(old); File.WriteAllText(Path.Combine(old,"unrelated.txt"),"replacement fixture");
            string current=fixture.Registry.Allocate(fixture.Exports,original,ExportFormat.Audio);
            Check(current!=old && File.ReadAllText(Path.Combine(old,"unrelated.txt"))=="replacement fixture","陈旧索引接管了用户替换的同名目录");
            File.WriteAllText(fixture.Registry.IndexPath,"{invalid fixture index");
            string afterLoss=new ExportDirectoryRegistry(fixture.Data).Allocate(fixture.Exports,original,ExportFormat.Audio);
            Check(afterLoss!=old && afterLoss!=current && Directory.GetFiles(fixture.Data,"*.invalid-*").Length==1,"损坏索引丢失后仍按标题猜测旧目录归属");
        }
        static void MissingDirectoryIdentityStillAllowsExport()
        {
            using var fixture=new Fixture(); var registry=new ExportDirectoryRegistry(fixture.Data,_=>""); var article=Article("portable","移动盘文章");
            string first=registry.Allocate(fixture.Exports,article,ExportFormat.Full);
            File.WriteAllText(Path.Combine(first,"article.html"),"portable fixture");
            string second=registry.Allocate(fixture.Exports,article,ExportFormat.Full);
            Check(Directory.Exists(first) && Directory.Exists(second) && first!=second && Path.GetFileName(second).EndsWith("(2)",StringComparison.Ordinal)
                && File.ReadAllText(Path.Combine(first,"article.html"))=="portable fixture" && !File.Exists(registry.IndexPath),
                "无可靠目录ID的保存位置被禁止导出或误登记复用归属");
        }
        static void ScopedRegistryKeepsExportsClean()
        {
            using var fixture=new Fixture();
            using(ExportFileSystem.UseDirectoryRegistry(fixture.Registry))
            {
                string first=ExportFileSystem.ArticleDirectory(fixture.Exports,Article("scoped","仅内容"),ExportFormat.Html);
                string second=ExportFileSystem.ArticleDirectory(fixture.Exports,Article("scoped","仅内容"));
                Check(first==second,"旧2参数目录接口没有复用HTML类型目录");
            }
            Check(File.Exists(fixture.Registry.IndexPath) && Directory.GetFiles(fixture.Exports,"*",SearchOption.AllDirectories).Length==0,
                "目录归属索引出现在导出内容目录中");
        }
        static async Task CancellationAndConcurrentOwnership()
        {
            using var fixture=new Fixture(); using(var canceled=new CancellationTokenSource())
            {
                canceled.Cancel(); bool observed=false;
                try { fixture.Registry.Allocate(fixture.Exports,Article("cancel","停止"),ExportFormat.Audio,canceled.Token); }
                catch(OperationCanceledException) { observed=true; }
                Check(observed && !Directory.Exists(fixture.Exports) && !Directory.Exists(fixture.Data),"预先取消仍创建导出目录或索引");
            }
            var tasks=Enumerable.Range(0,6).Select(_=>Task.Run(()=>new ExportDirectoryRegistry(fixture.Data)
                .Allocate(fixture.Exports,Article("parallel","并行"),ExportFormat.Audio))).ToArray();
            var paths=await Task.WhenAll(tasks); Check(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()==1,"同时导出同一文章创建了多份归属");
            using(var held=new FileStream(fixture.Registry.IndexPath+".lock",FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            using(var stop=new CancellationTokenSource())using(var entered=new ManualResetEventSlim())
            {
                Task pending=Task.Run(()=> { entered.Set(); fixture.Registry.Allocate(fixture.Exports,Article("locked","等待锁"),ExportFormat.Audio,stop.Token); });
                Check(entered.Wait(TimeSpan.FromSeconds(5)),"目录锁测试没有开始");
                await Task.Delay(100); Check(!pending.IsCompleted,"目录分配绕过了现有索引锁"); stop.Cancel();
                Check(await Task.WhenAny(pending,Task.Delay(3000))==pending,"等待目录锁时取消没有结束");
                bool observed=false; try { await pending; } catch(OperationCanceledException) { observed=true; }
                Check(observed && !Directory.GetDirectories(fixture.Exports).Any(p=>Path.GetFileName(p).StartsWith("等待锁",StringComparison.Ordinal)),"等待锁中取消仍创建新目录");
            }
        }
        static ArticleRecord Article(string id,string title)=>new ArticleRecord { Id=id,Biz="directory-fixture",Title=title,
            PublishedAt=new DateTime(2020,1,2),Url="https://mp.weixin.qq.com/s?__biz=directory-fixture&mid=1&idx=1" };
        static void Check(bool value,string message) { if(!value)throw new InvalidOperationException("Export directory self-test: "+message); }
        sealed class Fixture : IDisposable
        {
            public string Root { get; }=Path.Combine(Path.GetTempPath(),"WCAE-dir-"+Guid.NewGuid().ToString("N"));
            public string Data=>Path.Combine(Root,"registry");
            public string Exports=>Path.Combine(Root,"exports");
            public ExportDirectoryRegistry Registry { get; }
            public Fixture() { Directory.CreateDirectory(Root); Registry=new ExportDirectoryRegistry(Data); }
            public void Dispose() { if(Directory.Exists(Root))Directory.Delete(Root,true); }
        }
    }
}
