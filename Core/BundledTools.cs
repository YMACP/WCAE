using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace WCAE
{
    // The manifest is small. ZIP resources are opened only when an export needs them.
    internal static class BundledTools
    {
        private sealed class Manifest
        {
            public int Format { get; set; }
            public string BundleId { get; set; }
            public List<PayloadFile> Files { get; set; }
        }
        private sealed class PayloadFile
        {
            public string Name { get; set; }
            public string Group { get; set; }
            public long Length { get; set; }
            public string Sha256 { get; set; }
        }
        private static readonly Lazy<Manifest> payload = new Lazy<Manifest>(ReadManifest);
        private static readonly SemaphoreSlim extraction = new SemaphoreSlim(1, 1);
        private static readonly ConcurrentDictionary<string, DateTime> verified = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        internal static string DirectoryPath => Path.Combine(AppPaths.DataDirectory, "components", payload.Value.BundleId.Substring(0, 20));
        internal static async Task EnsurePdfAsync(CancellationToken token)
        {
            await EnsureGroupAsync("runtime", token).ConfigureAwait(false);
            await EnsureGroupAsync("pdf", token).ConfigureAwait(false);
        }
        internal static async Task EnsureMediaAsync(CancellationToken token)
        {
            await EnsureGroupAsync("runtime", token).ConfigureAwait(false);
            await EnsureGroupAsync("media", token).ConfigureAwait(false);
        }
        internal static async Task EnsureAllAsync(CancellationToken token)
        {
            await EnsurePdfAsync(token).ConfigureAwait(false);
            await EnsureMediaAsync(token).ConfigureAwait(false);
            await EnsureIngressAsync(token).ConfigureAwait(false);
        }
        internal static async Task EnsureIngressAsync(CancellationToken token)
        {
            await EnsureGroupAsync("runtime", token).ConfigureAwait(false);
            await EnsureGroupAsync("ingress", token).ConfigureAwait(false);
        }
        private static Manifest ReadManifest()
        {
            using (var stream = OpenResource("manifest.json"))
            using (var reader = new StreamReader(stream))
            {
                var manifest = JsonConvert.DeserializeObject<Manifest>(reader.ReadToEnd());
                if (manifest?.Format != 1 || manifest.BundleId?.Length != 64 || manifest.Files == null || manifest.Files.Count == 0)
                    throw new InvalidDataException("导出组件清单无效。");
                if (!manifest.BundleId.All(Uri.IsHexDigit) || manifest.Files.Any(file =>
                    string.IsNullOrWhiteSpace(file.Name) || Path.GetFileName(file.Name) != file.Name || file.Name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                    file.Length < 0 || file.Sha256?.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) ||
                    !new[] { "runtime", "pdf", "media", "ingress" }.Contains(file.Group)))
                    throw new InvalidDataException("导出组件清单包含无效文件。");
                return manifest;
            }
        }
        private static Stream OpenResource(string name) => typeof(BundledTools).Assembly.GetManifestResourceStream("WCAE.Tools." + name)
            ?? throw new InvalidDataException("内置导出组件缺失：" + name);

        private static async Task EnsureGroupAsync(string group, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string directory = DirectoryPath;
            var files = payload.Value.Files.Where(file => file.Group == group).ToArray();
            await extraction.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(directory);
                // A file lock works across both normal instances and offline preview/self-test instances.
                using (await AcquireLockAsync(Path.Combine(directory, ".extract.lock"), token).ConfigureAwait(false))
                {
                    var needed = new List<PayloadFile>();
                    foreach (var file in files)
                        if (!await IsValidAsync(Path.Combine(directory, file.Name), file, token).ConfigureAwait(false)) needed.Add(file);
                    if (needed.Count == 0) return;
                    using (var stream = OpenResource(group + ".zip"))
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                    {
                        foreach (var file in needed)
                        {
                            token.ThrowIfCancellationRequested();
                            var entry = archive.GetEntry(file.Name);
                            if (entry == null || entry.Length != file.Length) throw new InvalidDataException("导出组件压缩包无效：" + file.Name);
                            string output = Path.Combine(directory, file.Name);
                            string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            try
                            {
                                using (var input = entry.Open())
                                using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                                    await input.CopyToAsync(destination, 131072, token).ConfigureAwait(false);
                                if (!await HasExpectedHashAsync(temporary, file, token).ConfigureAwait(false))
                                    throw new InvalidDataException("导出组件校验失败：" + file.Name);
                                token.ThrowIfCancellationRequested();
                                File.Move(temporary, output, true);
                                verified[output] = File.GetLastWriteTimeUtc(output);
                            }
                            finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        }
                    }
                }
            }
            finally { extraction.Release(); }
        }
        private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { await Task.Delay(100, token).ConfigureAwait(false); }
            }
        }
        private static async Task<bool> IsValidAsync(string path, PayloadFile file, CancellationToken token)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Length) return false;
            if (verified.TryGetValue(path, out var stamp) && stamp == info.LastWriteTimeUtc) return true;
            if (!await HasExpectedHashAsync(path, file, token).ConfigureAwait(false)) return false;
            verified[path] = info.LastWriteTimeUtc;
            return true;
        }
        private static async Task<bool> HasExpectedHashAsync(string path, PayloadFile file, CancellationToken token)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            {
                if (stream.Length != file.Length) return false;
                var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                return Convert.ToHexString(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
