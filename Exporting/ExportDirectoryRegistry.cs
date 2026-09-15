using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;

namespace WCAE
{
    // Ownership lives in WCAE's local data directory, never alongside exported content.
    // A directory's Windows identity also prevents an old index from claiming a user's
    // replacement directory after they move or delete an earlier export.
    public sealed class ExportDirectoryRegistry
    {
        readonly string directory;
        readonly Func<string,string> readIdentity;
        public string IndexPath { get; }
        public ExportDirectoryRegistry(string dataDirectory,Func<string,string> identityReader=null)
        {
            if(string.IsNullOrWhiteSpace(dataDirectory))throw new ArgumentException("缺少导出目录索引位置。",nameof(dataDirectory));
            directory=Path.GetFullPath(dataDirectory);
            IndexPath=Path.Combine(directory,"export-directories.json");
            readIdentity=identityReader??DirectoryIdentity;
        }
        public string Allocate(string destination,ArticleRecord article,ExportFormat format,CancellationToken token=default)
        {
            if(article==null)throw new ArgumentNullException(nameof(article));
            if(string.IsNullOrWhiteSpace(destination))throw new ArgumentException("请选择导出位置。",nameof(destination));
            token.ThrowIfCancellationRequested();
            string root=NormalizeRoot(destination), type=ExportFileSystem.TypeName(format);
            string articleId=string.IsNullOrWhiteSpace(article.Id)
                ? ArticleIdentity.Create(article.Biz,article.Mid,article.Idx,article.Url,article.SourceMessageId) : article.Id;
            string owner=ExportFileSystem.Hash((article.Biz??"")+"\n"+articleId+"\n"+((int)format).ToString(System.Globalization.CultureInfo.InvariantCulture),64);
            // Validate the budget before creating even an empty destination directory.
            Leaf(root,article.Title,type,1);
            Directory.CreateDirectory(directory);
            using(var held=AcquireLock(token))
            {
                token.ThrowIfCancellationRequested();
                var index=ReadIndex();
                token.ThrowIfCancellationRequested();
                var owned=index.Entries.FirstOrDefault(e=>e.Owner==owner && string.Equals(e.Root,root,StringComparison.OrdinalIgnoreCase));
                if(owned!=null && SafeLeaf(owned.Leaf))
                {
                    string existing=Path.Combine(root,owned.Leaf);
                    if(Directory.Exists(existing) && Identity(existing)==owned.DirectoryId)
                    { token.ThrowIfCancellationRequested(); return existing; }
                }
                Directory.CreateDirectory(root);
                for(int number=1;number<=100000;number++)
                {
                    token.ThrowIfCancellationRequested();
                    string leaf=Leaf(root,article.Title,type,number), path=Path.Combine(root,leaf);
                    // Even an empty pre-existing directory is unowned. CreateDirectoryW
                    // fails atomically if another app/user created it after our last check.
                    if(!NativeCreateDirectory(path,IntPtr.Zero))
                    {
                        int error=Marshal.GetLastWin32Error();
                        if(error==80 || error==183)continue;
                        throw new IOException("无法建立文章导出目录。",new Win32Exception(error));
                    }
                    string identity="";
                    try
                    {
                        identity=Identity(path);
                        token.ThrowIfCancellationRequested();
                        // Some network/removable filesystems have no reliable directory ID.
                        // The exclusive creation still makes this export safe; avoid recording
                        // reusable ownership so a later export chooses a fresh numbered folder.
                        if(identity.Length==0)return path;
                        if(owned!=null)index.Entries.Remove(owned);
                        index.Entries.Add(new Entry { Root=root,Owner=owner,Leaf=leaf,DirectoryId=identity });
                        WriteIndex(index,token);
                        return path;
                    }
                    catch
                    {
                        // Only this newly and exclusively created empty directory is ours.
                        // An unregistered directory left by a crash is treated as unknown next time.
                        try { if(identity.Length>0 && Identity(path)==identity && !Directory.EnumerateFileSystemEntries(path).Any())Directory.Delete(path,false); }
                        catch(IOException) { } catch(UnauthorizedAccessException) { }
                        throw;
                    }
                }
                throw new IOException("同名导出目录过多，请选择其他保存位置。");
            }
        }
        static string NormalizeRoot(string path)
        {
            string full=Path.GetFullPath(path), volume=Path.GetPathRoot(full);
            return full.Length>volume.Length?full.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar):full;
        }
        static string Leaf(string root,string title,string type,int number)
        {
            string suffix="("+type+")"+(number==1?"":"("+number.ToString(System.Globalization.CultureInfo.InvariantCulture)+")");
            int available=Math.Min(120,195-root.TrimEnd(Path.DirectorySeparatorChar).Length-1-suffix.Length);
            if(available<8)throw new IOException("导出目录过长，请选择更短的目标路径。");
            return ExportFileSystem.SafeName(title,available)+suffix;
        }
        static bool SafeLeaf(string leaf)=>!string.IsNullOrWhiteSpace(leaf) && leaf!="." && leaf!=".."
            && leaf==Path.GetFileName(leaf) && leaf.IndexOfAny(Path.GetInvalidFileNameChars())<0
            && !leaf.EndsWith(".",StringComparison.Ordinal) && !leaf.EndsWith(" ",StringComparison.Ordinal);
        FileStream AcquireLock(CancellationToken token)
        {
            var waiting=Stopwatch.StartNew();
            while(true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(IndexPath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
                catch(IOException ex) when((ex.HResult&0xffff)==32 || (ex.HResult&0xffff)==33)
                {
                    if(waiting.Elapsed>TimeSpan.FromSeconds(10))throw new IOException("其他导出任务仍在分配目录，请稍后重试。",ex);
                    if(token.WaitHandle.WaitOne(50))token.ThrowIfCancellationRequested();
                }
            }
        }
        Index ReadIndex()
        {
            if(!File.Exists(IndexPath))return new Index();
            try
            {
                if(new FileInfo(IndexPath).Length>32L*1024*1024)throw new JsonException("Directory index size limit.");
                var value=JsonConvert.DeserializeObject<Index>(File.ReadAllText(IndexPath,Encoding.UTF8),new JsonSerializerSettings { MaxDepth=16 });
                if(value?.Version!=1 || value.Entries==null || value.Entries.Count>100000)throw new JsonException("Directory index format.");
                value.Entries.RemoveAll(e=>e==null || string.IsNullOrWhiteSpace(e.Root) || string.IsNullOrWhiteSpace(e.Owner) || !SafeLeaf(e.Leaf) || string.IsNullOrWhiteSpace(e.DirectoryId));
                return value;
            }
            catch(JsonException)
            {
                // Preserve the unreadable local index; do not infer ownership from names.
                File.Copy(IndexPath,IndexPath+".invalid-"+Guid.NewGuid().ToString("N"));
                return new Index();
            }
        }
        void WriteIndex(Index value,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string temporary=Path.Combine(directory,".export-directories-"+Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                byte[] bytes=new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(value));
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
                { stream.Write(bytes,0,bytes.Length); stream.Flush(true); }
                token.ThrowIfCancellationRequested();
                if(File.Exists(IndexPath))File.Replace(temporary,IndexPath,null);
                else File.Move(temporary,IndexPath);
            }
            finally { if(File.Exists(temporary))File.Delete(temporary); }
        }
        static string DirectoryIdentity(string path)
        {
            using(var handle=CreateFile(path,0,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero))
            {
                if(handle.IsInvalid || !GetFileInformationByHandle(handle,out var info)
                    || (info.Attributes&(uint)FileAttributes.Directory)==0 || (info.Attributes&(uint)FileAttributes.ReparsePoint)!=0
                    || (info.IndexHigh==0 && info.IndexLow==0))return "";
                return info.Volume.ToString("x8")+info.IndexHigh.ToString("x8")+info.IndexLow.ToString("x8")
                    +info.CreationHigh.ToString("x8")+info.CreationLow.ToString("x8");
            }
        }
        string Identity(string path)
        {
            try { return readIdentity(path)??""; }
            catch(IOException) { return ""; }
            catch(UnauthorizedAccessException) { return ""; }
        }
        sealed class Index { public int Version { get; set; }=1; public List<Entry> Entries { get; set; }=new List<Entry>(); }
        sealed class Entry { public string Root { get; set; }=""; public string Owner { get; set; }=""; public string Leaf { get; set; }=""; public string DirectoryId { get; set; }=""; }
        [StructLayout(LayoutKind.Sequential)]
        struct FileInformation
        {
            public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
        }
        [DllImport("kernel32.dll",EntryPoint="CreateDirectoryW",CharSet=CharSet.Unicode,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)] static extern bool NativeCreateDirectory(string path,IntPtr security);
        [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern SafeFileHandle CreateFile(string path,uint desiredAccess,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetFileInformationByHandle(SafeFileHandle file,out FileInformation info);
    }
}
