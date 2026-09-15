using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    // Offline download-boundary tests: no WeChat, browser, external tool, or network is used.
    public static class AudioDownloadSelfTests
    {
        public static void Run() => RunAsync().GetAwaiter().GetResult();

        public static async Task RunAsync()
        {
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string root = Path.Combine(temp, "WCAE-audio-download-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(root);
            using var registryScope = ExportFileSystem.UseDirectoryRegistry(new ExportDirectoryRegistry(Path.Combine(root, "registry")));
            bool completed = false;
            try
            {
                await ActualBytesChooseExtension(root);
                await MissingAudioAndUnresolvedAddressAreSeparate(root);
                await FailedDownloadsAreClassified(root);
                await InvalidContentNeverCommits(root);
                await ExistingFilesAndOverwriteFailureStayAtomic(root);
                await CancellationLeavesNoAudioOrTemporaryFile(root);
                AssertNoReportFiles(root);
                completed = true;
            }
            finally
            {
                string resolved = Path.GetFullPath(root);
                if (completed && Path.GetDirectoryName(resolved) == temp
                    && Path.GetFileName(resolved).StartsWith("WCAE-audio-download-", StringComparison.Ordinal))
                    Directory.Delete(resolved, true);
            }
        }

        static async Task ActualBytesChooseExtension(string root)
        {
            var formats = new[]
            {
                (Name:"mp3-id3", Data:WithId3(Mp3(),0), Url:"https://res.wx.qq.com/voice/getvoice?mediaid=fixture&voice_type=1", Extension:".mp3"),
                (Name:"mp3-frame", Data:Mp3(), Url:"https://cdn.example.test/download.aac", Extension:".mp3"),
                (Name:"aac-adts", Data:Aac(), Url:"https://cdn.example.test/download.mp3", Extension:".aac"),
                (Name:"aac-tagged", Data:WithId3(Aac(),1024), Url:"https://cdn.example.test/download", Extension:".aac"),
                (Name:"m4a", Data:M4a(), Url:"https://cdn.example.test/download.mp3", Extension:".m4a")
            };
            foreach (var item in formats)
            {
                var transport = new FakeTransport { Data = item.Data };
                var result = await ExportOne(root, item.Name, Audio(item.Url), transport);
                Check(result.State == "Success" && Path.GetExtension(result.OutputPath) == item.Extension,
                    item.Name + " did not use the format identified from its bytes");
                Check(File.ReadAllBytes(result.OutputPath).SequenceEqual(item.Data), item.Name + " bytes were altered");
                Check(transport.Downloads == 1 && transport.Sessions.All(s => s.Cookie == "" && s.Key == ""),
                    "CDN audio received a WeChat session or caused redundant downloads");
            }
        }

        static async Task MissingAudioAndUnresolvedAddressAreSeparate(string root)
        {
            var transport = new FakeTransport { Data = Mp3() };
            var empty = Audio(""); empty.Media.Clear();
            var result = await ExportOne(root, "no-audio", empty, transport);
            Check(result.State == "Skipped" && result.Reason.Contains("未识别到音频") && transport.Downloads == 0,
                "No audio was reported as a failed network download");
            result = await ExportOne(root, "unresolved", Audio(""), transport);
            Check(result.State == "Failed" && result.Reason.Contains("尚未解析到可下载地址") && transport.Downloads == 0,
                "An identified audio without a URL was silently skipped");
            var unavailable = Audio(""); unavailable.Media[0].UnavailableReason = "需要有效微信会话";
            result = await ExportOne(root, "unavailable", unavailable, transport);
            Check(result.State == "Failed" && result.Reason.Contains("需要有效微信会话") && transport.Downloads == 0,
                "The resolver's unavailable reason was lost or triggered an invented URL");
        }

        static async Task FailedDownloadsAreClassified(string root)
        {
            foreach (int status in new[] { 401, 403, 429 })
            {
                var transport = new FakeTransport { Error = new HttpRequestFailureException((HttpStatusCode)status,
                    "do not log https://example.test/?key=secret-value") };
                var result = await ExportOne(root, "restricted-" + status, Audio("https://cdn.example.test/audio"), transport);
                Check(result.State == "Failed" && result.Reason.Contains("下载受限") && result.Reason.Contains(status.ToString())
                    && !result.Reason.Contains("secret-value"), "HTTP access restriction was not separated from missing audio");
            }
            var failed = await ExportOne(root, "network-failure", Audio("https://cdn.example.test/audio"),
                new FakeTransport { Error = new IOException("secret-value https://example.test/?key=hidden") });
            Check(failed.State == "Failed" && failed.Reason.Contains("音频下载失败") && !failed.Reason.Contains("secret-value"),
                "Download error classification exposed a raw exception");
            var timeout = await ExportOne(root, "timeout", Audio("https://cdn.example.test/audio"),
                new FakeTransport { Error = new OperationCanceledException("transport timeout") });
            Check(timeout.State == "Failed" && timeout.Reason.Contains("超时"), "Transport timeout became user cancellation");
        }

        static async Task InvalidContentNeverCommits(string root)
        {
            var invalid = new[]
            {
                Array.Empty<byte>(),
                Encoding.UTF8.GetBytes("<!DOCTYPE html><html>登录页面</html>"),
                Encoding.UTF8.GetBytes("{\"ret\":-3}"),
                Encoding.UTF8.GetBytes("#EXTM3U\nhttps://cdn.example.test/a.aac"),
                new byte[] { 0xff, 0xff, 0xff, 0xff, 0, 0, 0, 0 },
                new byte[] { 0xff, 0xf1, 0x50, 0x80, 0xff, 0xff, 0xfc },
                Encoding.ASCII.GetBytes("ID3\u0004\0\0\0\0\0\0"),
                WithId3(Encoding.UTF8.GetBytes("<html>metadata cannot certify audio</html>"), 0),
                WithId3(Encoding.ASCII.GetBytes("fixture-audio-without-any-audio-frame"), 0)
            };
            for (int i = 0; i < invalid.Length; i++)
            {
                string name = "invalid-" + i;
                var result = await ExportOne(root, name, Audio("https://cdn.example.test/audio.mp3"), new FakeTransport { Data = invalid[i] });
                Check(result.State == "Failed" && result.OutputPath == "", "Text, empty, or malformed audio was reported as success");
                Check(!Directory.GetFiles(Path.Combine(root, name), "audio_*", SearchOption.AllDirectories).Any()
                    && !Directory.GetFiles(Path.Combine(root, name), "*.part*", SearchOption.AllDirectories).Any(),
                    "A failed audio download retained a final or temporary file");
            }
        }

        static async Task ExistingFilesAndOverwriteFailureStayAtomic(string root)
        {
            string destination = Path.Combine(root, "overwrite");
            var transport = new FakeTransport { Data = Aac() };
            var service = new ExportService(transport);
            var article = Audio("https://cdn.example.test/audio.mp3");
            var options = new ExportOptions { Destination = destination, Format = ExportFormat.Audio };
            await service.ExportAsync(new[] { article }, Session(), options, null, CancellationToken.None);
            string output = Directory.GetFiles(destination, "*.aac", SearchOption.AllDirectories).Single();
            byte[] valid = File.ReadAllBytes(output);
            await service.ExportAsync(new[] { article }, Session(), options, null, CancellationToken.None);
            Check(transport.Downloads == 1 && ReadManifest(service, destination).Skipped == 1,
                "No-overwrite mode ignored the previously detected AAC extension");
            transport.Data = Encoding.UTF8.GetBytes("<html>expired</html>"); options.Overwrite = true;
            await service.ExportAsync(new[] { article }, Session(), options, null, CancellationToken.None);
            Check(File.ReadAllBytes(output).SequenceEqual(valid) && ReadManifest(service, destination).Failed == 1
                && !Directory.GetFiles(destination, "*.part*", SearchOption.AllDirectories).Any(),
                "Failed overwrite damaged previously saved audio or left temporary output");
        }

        static async Task CancellationLeavesNoAudioOrTemporaryFile(string root)
        {
            var transport = new FakeTransport { WaitForCancellation = true };
            string destination = Path.Combine(root, "cancel");
            using var cts = new CancellationTokenSource();
            var service = new ExportService(transport);
            Task exporting = service.ExportAsync(new[] { Audio("https://cdn.example.test/audio") }, Session(),
                new ExportOptions { Destination = destination, Format = ExportFormat.Audio }, null, cts.Token);
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cts.Cancel();
            bool cancelled = false;
            try { await exporting; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && ReadManifest(service, destination).Cancelled
                && !Directory.GetFiles(destination, "audio_*", SearchOption.AllDirectories).Any()
                && !Directory.GetFiles(destination, "*.part*", SearchOption.AllDirectories).Any(),
                "Cancellation committed partial audio or failed to propagate");
        }

        static async Task<ExportResult> ExportOne(string root, string name, ArticleRecord article, FakeTransport transport)
        {
            string destination = Path.Combine(root, name);
            var service = new ExportService(transport);
            await service.ExportAsync(new[] { article }, Session(),
                new ExportOptions { Destination = destination, Format = ExportFormat.Audio }, null, CancellationToken.None);
            return ReadManifest(service, destination).Results.Single();
        }
        static ExportManifest ReadManifest(ExportService service, string destination)
        {
            AssertNoReportFiles(destination);
            Check(service.LastManifest != null && service.LastManifest.FinishedUtc.HasValue, "Completed audio export has no in-memory result");
            return service.LastManifest;
        }
        static void AssertNoReportFiles(string destination)
            => Check(!Directory.GetFiles(destination, "export-results-*", SearchOption.AllDirectories).Any(), "An export report was written to the user's destination");
        static ArticleRecord Audio(string url) => new()
        {
            Id = "audio-fixture:1:1", Biz = "audio-fixture", Title = "独立音频下载", Status = ArticleStatus.Available,
            Url = "https://mp.weixin.qq.com/s/audio-fixture",
            Media = new List<MediaAsset> { new MediaAsset { Kind = MediaKind.Audio, Id = "voice-fixture", Url = url } }
        };
        static AccountSession Session() => new() { Cookie = "fixture-cookie", Key = "fixture-key", Uin = "fixture-uin" };
        static byte[] Mp3()
        {
            var data = new byte[417]; data[0] = 0xff; data[1] = 0xfb; data[2] = 0x90; data[3] = 0x00; return data;
        }
        static byte[] Aac()
        {
            var data = new byte[32];
            byte[] header = { 0xff, 0xf1, 0x50, 0x80, 0x04, 0x1f, 0xfc }; Array.Copy(header, data, header.Length); return data;
        }
        static byte[] WithId3(byte[] audio, int tagLength)
        {
            var data = new byte[10 + tagLength + audio.Length];
            Array.Copy(Encoding.ASCII.GetBytes("ID3"), data, 3); data[3] = 4;
            data[6] = (byte)((tagLength >> 21) & 127); data[7] = (byte)((tagLength >> 14) & 127);
            data[8] = (byte)((tagLength >> 7) & 127); data[9] = (byte)(tagLength & 127);
            Array.Copy(audio, 0, data, 10 + tagLength, audio.Length); return data;
        }
        static byte[] M4a()
        {
            var data = new byte[64]; data[3] = 24;
            Array.Copy(Encoding.ASCII.GetBytes("ftypM4A "), 0, data, 4, 8);
            Array.Copy(Encoding.ASCII.GetBytes("isommp42"), 0, data, 16, 8); return data;
        }
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Audio download: " + message); }

        sealed class FakeTransport : IHttpTransport
        {
            public byte[] Data { get; set; } = Array.Empty<byte>();
            public Exception Error { get; set; }
            public bool WaitForCancellation { get; set; }
            public int Downloads { get; private set; }
            public List<AccountSession> Sessions { get; } = new();
            public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<string> GetStringAsync(string url, AccountSession session, CancellationToken token)
                => throw new InvalidOperationException("Audio export must use its resolved download URL");
            public Task<string> PostFormAsync(string url, IDictionary<string, string> values, AccountSession session, CancellationToken token)
                => throw new InvalidOperationException("The download test must not request an API");
            public async Task DownloadAsync(string url, string destination, AccountSession session, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Downloads++; Sessions.Add(session.Clone());
                if (Error != null) throw Error;
                if (WaitForCancellation)
                {
                    await File.WriteAllBytesAsync(destination, Aac(), token); Started.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token); return;
                }
                await File.WriteAllBytesAsync(destination, Data, token); Started.TrySetResult(true);
            }
        }
    }
}
