using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Threading;

namespace WCAE
{
    internal static class ExportFileSystem
    {
        static readonly AsyncLocal<ExportDirectoryRegistry> directoryRegistry=new AsyncLocal<ExportDirectoryRegistry>();
        internal static string Hash(string value, int length = 12)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").Substring(0, length).ToLowerInvariant();
        }

        internal static string ArticleDirectory(string root, ArticleRecord article)
            => ArticleDirectory(root,article,ExportFormat.Html);

        internal static string ArticleDirectory(string root,ArticleRecord article,ExportFormat format,CancellationToken token=default)
            => (directoryRegistry.Value??new ExportDirectoryRegistry(AppPaths.DataDirectory)).Allocate(root,article,format,token);

        internal static IDisposable UseDirectoryRegistry(ExportDirectoryRegistry registry)
        {
            if(registry==null)throw new ArgumentNullException(nameof(registry));
            var prior=directoryRegistry.Value; directoryRegistry.Value=registry; return new RegistryScope(prior);
        }
        sealed class RegistryScope : IDisposable
        {
            readonly ExportDirectoryRegistry prior; bool disposed;
            public RegistryScope(ExportDirectoryRegistry prior) { this.prior=prior; }
            public void Dispose() { if(disposed)return; disposed=true; directoryRegistry.Value=prior; }
        }

        internal static string TypeName(ExportFormat format) => format switch
        {
            ExportFormat.Html => "正文HTML", ExportFormat.Markdown => "正文Markdown", ExportFormat.Text => "正文TXT", ExportFormat.Pdf => "正文PDF",
            ExportFormat.Images => "图片", ExportFormat.Audio => "音频", ExportFormat.Video => "视频", ExportFormat.AllMedia => "全部媒体", ExportFormat.Full => "全文",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

        internal static string SafeName(string value, int maxLength)
        {
            if(maxLength<1)throw new ArgumentOutOfRangeException(nameof(maxLength));
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string((value ?? "").Where(c => c >= 32 && !invalid.Contains(c) && !"<>:\"/\\|?*".Contains(c)).ToArray()).Trim().TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "未命名文章";
            string stem=cleaned.Split('.')[0].ToUpperInvariant();
            if(stem=="CON" || stem=="PRN" || stem=="AUX" || stem=="NUL" || stem=="CLOCK$"
                || (stem.Length==4 && (stem.StartsWith("COM",StringComparison.Ordinal) || stem.StartsWith("LPT",StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3])))cleaned="_"+cleaned;
            var result=new StringBuilder(); var elements=StringInfo.GetTextElementEnumerator(cleaned);
            while(elements.MoveNext())
            {
                string element=elements.GetTextElement();
                if(result.Length+element.Length>maxLength)break;
                // Invalid UTF-16 from an old imported title must not become a broken path.
                if(element.Length==1 && char.IsSurrogate(element[0]))continue;
                result.Append(element);
            }
            string name=result.ToString().TrimEnd('.', ' ');
            return name.Length>0?name:"文章".Substring(0,Math.Min(2,maxLength));
        }

        internal static string TemporaryFile(string destination)
        {
            // HttpTransport adds its own atomic-download suffix; leave room below MAX_PATH.
            return Path.Combine(Path.GetDirectoryName(destination), ".t-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part");
        }

        // Both names are on the same volume. The final file is never partially written.
        internal static bool Commit(string temporary, string destination, bool overwrite)
        {
            if (File.Exists(destination))
            {
                if (!overwrite) return false;
                File.Replace(temporary, destination, null);
            }
            else
            {
                try { File.Move(temporary, destination); }
                catch (IOException)
                {
                    if (!File.Exists(destination)) throw;
                    if (!overwrite) return false;
                    File.Replace(temporary, destination, null);
                }
            }
            return true;
        }

        internal static void DeleteTemporaryFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        internal static string CreateWorkDirectory(string articleDirectory)
        {
            var path = Path.Combine(articleDirectory, ".work-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(path);
            return path;
        }

        internal static void DeleteWorkDirectory(string articleDirectory, string workDirectory)
        {
            var parent = Path.GetFullPath(articleDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(workDirectory);
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(target), parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).StartsWith(".work-", StringComparison.Ordinal))
                throw new IOException("临时目录不在本次文章导出目录内。");
            try
            {
                if (!Directory.Exists(target)) return;
                // Never traverse a replacement junction while cleaning temporary data.
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) Directory.Delete(target, false);
                else Directory.Delete(target, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
