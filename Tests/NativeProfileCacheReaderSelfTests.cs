using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public static class NativeProfileCacheReaderSelfTests
    {
        const string Biz = "MzIzMzQyMzUzNw==", User = "gh_fixture_cache";
        public static async Task RunAsync()
        {
            ObservedTargetShapesRemainIndependent();
            AccountAndCanonicalEvidenceAreRequired();
            UnknownAndMalformedStructuresRemainPartial();
            SnappyBoundsAndCopiesAreChecked();
            await CurrentManifestWalAndBlobAreReadWithoutWrites();
            await TombstonesAndMissingSourcesDoNotResurrectData();
            await CorruptAndOversizedFilesDoNotBlockCollection();
            await CanceledReadsDoNotReturnLateResults();
        }
        static AccountSession Session() => new AccountSession { Biz = Biz, UserName = User, Name = "缓存测试号" };
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Native profile cache self-test: " + message); }
        static string Url(string mid, string biz = Biz) => "https://mp.weixin.qq.com/s?__biz=" + Uri.EscapeDataString(biz) + "&mid=" + mid + "&idx=1";
        static JObject Detail(string mid, string title, int show = 0) => new JObject
        { ["ItemIndex"] = 1, ["Title"] = title, ["ContentUrl"] = Url(mid), ["ItemShowType"] = show };
        static JObject Message(string mid, long date, int type, params JObject[] details) => new JObject
        {
            ["BaseInfo"] = new JObject { ["MsgId"] = long.Parse(mid), ["MsgType"] = 49, ["DateTime"] = date },
            ["AppMsg"] = new JObject { ["BaseInfo"] = new JObject { ["AppMsgId"] = long.Parse(mid), ["Type"] = type },
                ["DetailInfo"] = new JArray(details.Cast<object>().ToArray()) }
        };
        static JObject Payload(params JObject[] messages) => new JObject
        {
            ["BaseResponse"] = new JObject { ["Ret"] = 0 }, ["AccountInfo"] = new JObject { ["UserName"] = User },
            ["MsgList"] = new JObject { ["Msg"] = new JArray(messages.Cast<object>().ToArray()), ["FeaturedList"] = new JArray() },
            ["PagingInfo"] = new JObject { ["Offset"] = "fixture-native-offset", ["IsEnd"] = 0 }, ["updateTime"] = 1789445811418L
        };
        static ArticleSupplementSnapshot Parse(JObject payload) => NativeProfileCacheReader.ParseProfileJson(payload.ToString(Formatting.None), Session());

        static void ObservedTargetShapesRemainIndependent()
        {
            // Field names, IDs, timestamps and titles of two previously missed articles are
            // from the saved, sanitized first-screen evidence. All profile fields are synthetic.
            var first = Message("2247520031", 1789388361, 10002, Detail("2247520031", "Al lnfra！Agent 时代的基础设施搭建"));
            var second = Message("2247520021", 1789345721, 10002, Detail("2247520021", "Grok Bot连续补了两个非常实用的能力！"));
            var shortPost = Message("2247520023", 1789383895, 9, Detail("2247520023", "短帖第一段。\n不能省略的完整第二段。", 10));
            shortPost["AppMsg"]["DetailInfo"][0]["ori_content"] = "不是已下载正文的证据";
            var payload = Payload(first, second, shortPost);
            payload["MsgList"]["FeaturedList"] = new JArray(Message("2247517707", 1780000000, 10002,
                Detail("2247517707", "置顶文章一"), Detail("2247516056", "置顶文章二")));
            var result = Parse(payload);
            Check(result.Articles.Select(x => x.Mid).SequenceEqual(new[] { "2247517707", "2247516056", "2247520031", "2247520021", "2247520023" }),
                "遗漏 Type10002/Type9 或把同 ItemIndex 的不同置顶 MID 合并");
            Check(result.Articles.All(x => x.Idx == 1 && x.Status == ArticleStatus.Pending && string.IsNullOrEmpty(x.Html)),
                "缓存被当成已下载/已删除文章");
            var article = result.Articles.Single(x => x.Mid == "2247520031");
            Check(article.PublishedAt == DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeSeconds(1789388361).UtcDateTime.AddHours(8), DateTimeKind.Unspecified)
                && article.PublishedAt?.Kind == DateTimeKind.Unspecified && article.HistoryTitle.Contains("Al lnfra")
                && article.PublishedAtSource == "native-cache:message-time", "时间未统一东八区或丢失标题来源");
            Check(result.Articles[1].PublishedAt == null && result.Articles[1].HistoryPublishedAt == null,
                "不同 MID 的置顶文章套用了同组第一篇的时间");
            Check(result.CapturedAtUtc == DateTimeOffset.FromUnixTimeMilliseconds(1789445811418).UtcDateTime
                && result.CapturedAtUtc?.Kind == DateTimeKind.Utc, "缓存采样时间未保持 UTC");
        }
        static void AccountAndCanonicalEvidenceAreRequired()
        {
            var detail = Detail("2247520031", "准确链接");
            detail["ContentUrl"] = Url("2247520031").Replace("&", "&amp;")
                + "&amp;sn=fixture-signature&amp;key=do-not-retain&amp;uin=123&amp;pass_ticket=do-not-retain";
            var result = Parse(Payload(Message("2247520031", 1789388361, 10002, detail)));
            Check(result.Articles.Count == 1, "HTML 实体编码的真实链接未识别");
            var query = HttpUtility.ParseQueryString(new Uri(result.Articles[0].Url).Query);
            Check(query.AllKeys.OrderBy(x => x).SequenceEqual(new[] { "__biz", "mid", "idx", "sn" }.OrderBy(x => x))
                && query["sn"] == "fixture-signature" && !result.Articles[0].Url.Contains("do-not-retain"), "会话字段进入返回 URL");
            var wrong = Payload(Message("2247520031", 1789388361, 10002, Detail("2247520031", "测试")));
            wrong["AccountInfo"]["UserName"] = "gh_someone_else";
            Check(Parse(wrong).Articles.Count == 0, "用户名不一致仍合入");
            foreach (string url in new[] { Url("2247520031", "another-biz"), Url("2247520031") + "&mid=999",
                Url("2247520031") + "&appmsgid=999", Url("2247520031").Replace("idx=1", "idx=101"),
                "https://example.invalid/s?__biz=" + Biz + "&mid=1&idx=1", "https://mp.weixin.qq.com/s/short-opaque" })
            {
                detail["ContentUrl"] = url;
                Check(Parse(Payload(Message("2247520031", 1789388361, 10002, detail))).Articles.Count == 0,
                    "不明确或跨号身份仍合入");
            }
        }
        static void UnknownAndMalformedStructuresRemainPartial()
        {
            var invalid = Detail("2247520031", "没有自己的链接"); invalid.Remove("ContentUrl");
            var payload = Payload(Message("2247520031", 1789388361, 10002, invalid),
                Message("2247520021", 1789345721, 10002, Detail("2247520021", "可保留的条目")));
            var partial = Parse(payload);
            Check(partial.Articles.Count == 1 && partial.Message.Contains("未合入") && partial.Message.Contains("不代表完整历史"),
                "未知条目伪造身份或把部分缓存声明完整");
            foreach (string field in new[] { "AccountInfo", "BaseResponse", "MsgList" })
            {
                var unknown = (JObject)payload.DeepClone(); unknown[field] = "unknown";
                Check(Parse(unknown).Articles.Count == 0, "未知父结构未安全拒绝");
            }
            var unknownMessage = Payload(); unknownMessage["MsgList"]["Msg"] = new JArray(new JObject { ["AppMsg"] = "unknown" });
            Check(Parse(unknownMessage).Articles.Count == 0, "未知 AppMsg 结构未安全拒绝");
            Check(NativeProfileCacheReader.ParseProfileJson("{bad", Session()).Articles.Count == 0, "损坏 JSON 未安全拒绝");
            Check(NativeProfileCacheReader.ParseProfileJson("{\"AccountInfo\":{},\"AccountInfo\":{}}", Session()).Articles.Count == 0,
                "重复身份属性未拒绝");
        }
        static void SnappyBoundsAndCopiesAreChecked()
        {
            // Literal abc followed by a six-byte offset-3 overlapping copy.
            Check(Encoding.ASCII.GetString(NativeProfileCacheReader.DecodeSnappy(new byte[] { 9, 8, 97, 98, 99, 22, 3, 0 }, 9)) == "abcabcabc",
                "Snappy overlapping copy 解析错误");
            foreach (byte[] bad in new[] { new byte[] { 5, 2, 0, 0 }, new byte[] { 3, 8, 97 },
                new byte[] { 128, 128, 128, 128, 128, 128, 128, 128, 128, 2 }, new byte[] { 100, 0, 97 } })
                ThrowsIo(() => NativeProfileCacheReader.DecodeSnappy(bad, 32));
            ThrowsIo(() => NativeProfileCacheReader.DecodeSnappy(new byte[] { 9, 8, 97, 98, 99, 22, 3, 0 }, 8));
        }
        static void ThrowsIo(Action action)
        { try { action(); } catch (IOException) { return; } throw new InvalidOperationException("Native profile cache self-test: corrupt input accepted"); }

        static async Task CurrentManifestWalAndBlobAreReadWithoutWrites()
        {
            using var fixture = new CacheFixture();
            fixture.Table(5, Key(User), Inline(Payload(Message("101", 1780000000, 10002, Detail("101", "SST 旧值")))), 10);
            fixture.Table(6, Key(User), Inline(Payload(Message("999", 1780000000, 10002, Detail("999", "已移除表中的旧值")))), 999);
            var expected = Payload(Message("2247520031", 1789388361, 10002, Detail("2247520031", "当前外部值")),
                Message("2247520021", 1789345721, 10002, Detail("2247520021", "第二篇")));
            byte[] blob = Join(new byte[] { 255, 17, 2 }, Literal(V8(expected)));
            fixture.Blob(blob);
            fixture.Manifest(4, 5);
            fixture.Log(4, Batch(20, (Key(User), External(blob.Length)), (Key(User, 3), BlobMetadata(blob.Length)),
                (Key("gh_else"), Inline(Payload(Message("888", 1780000000, 10002, Detail("888", "另一账号")))))));
            var before = fixture.Hashes();
            using var shared = new FileStream(Path.Combine(fixture.Origin, "000004.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var result = await new NativeProfileCacheReader(fixture.Root).ReadAsync(Session(), CancellationToken.None);
            Check(result.Articles.Select(x => x.Mid).SequenceEqual(new[] { "2247520031", "2247520021" }),
                "未按当前 manifest、sequence、外部 blob 选择目标缓存");
            Check(before.OrderBy(x => x.Key).SequenceEqual(fixture.Hashes().OrderBy(x => x.Key)), "共享读取修改了源文件");
            // Current value moves entirely into a compressed SST; stale WAL is not read.
            fixture.Table(7, Key(User), Inline(expected), 30, true); fixture.Manifest(8, 7); fixture.Log(8);
            result = await new NativeProfileCacheReader(fixture.Root).ReadAsync(Session(), CancellationToken.None);
            Check(result.Articles.Count == 2 && result.Articles[0].Mid == "2247520031", "仅 SST 的压缩当前值未读取");
        }
        static async Task TombstonesAndMissingSourcesDoNotResurrectData()
        {
            using var fixture = new CacheFixture();
            fixture.Table(5, Key(User), Inline(Payload(Message("101", 1780000000, 10002, Detail("101", "旧值")))), 10);
            fixture.Manifest(4, 5); fixture.Log(4, Batch(20, (Key(User), (byte[])null)));
            var reader = new NativeProfileCacheReader(fixture.Root);
            Check((await reader.ReadAsync(Session(), CancellationToken.None)).Articles.Count == 0, "墓碑之后恢复了旧缓存");
            fixture.Log(4, Batch(21, (Key(User), External(100)), (Key(User, 3), BlobMetadata(100))));
            Check((await reader.ReadAsync(Session(), CancellationToken.None)).Articles.Count == 0, "缺少外部 blob 时退回失效旧值");
            Check((await new NativeProfileCacheReader(Path.Combine(fixture.Root, "absent")).ReadAsync(Session(), CancellationToken.None)).Articles.Count == 0,
                "缺少缓存目录未安全返回");
            Check((await reader.ReadAsync(new AccountSession { Biz = Biz }, CancellationToken.None)).Articles.Count == 0, "没有 username 仍猜缓存");
        }
        static async Task CorruptAndOversizedFilesDoNotBlockCollection()
        {
            using var fixture = new CacheFixture(); fixture.Manifest(4);
            byte[] log = Physical(Batch(1, (Key(User), Inline(Payload(Message("101", 1780000000, 10002, Detail("101", "值")))))));
            log[0] ^= 1; File.WriteAllBytes(Path.Combine(fixture.Origin, "000004.log"), log);
            var reader = new NativeProfileCacheReader(fixture.Root);
            Check((await reader.ReadAsync(Session(), CancellationToken.None)).Articles.Count == 0, "错误 CRC 仍返回文章");
            using (var stream = new FileStream(Path.Combine(fixture.Origin, "000004.log"), FileMode.Create, FileAccess.Write)) stream.SetLength(16L * 1024 * 1024 + 1);
            Check((await reader.ReadAsync(Session(), CancellationToken.None)).Articles.Count == 0, "超出文件读取限额仍读取");
        }
        static async Task CanceledReadsDoNotReturnLateResults()
        {
            using var fixture = new CacheFixture(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await new NativeProfileCacheReader(fixture.Root).ReadAsync(Session(), canceled.Token); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("Native profile cache self-test: cancellation swallowed");
        }

        // Small, valid LevelDB fixtures exercise physical framing and selection instead
        // of mocking the reader. No live profile is copied or opened by these tests.
        sealed class CacheFixture : IDisposable
        {
            public string Root { get; } = Path.Combine(Path.GetTempPath(), "WCAE-native-cache-" + Guid.NewGuid().ToString("N"));
            public string Origin { get; }
            public CacheFixture() { Origin = Path.Combine(Root, "fixture-profile", "IndexedDB", "weixin_resourceid_0.indexeddb.leveldb"); Directory.CreateDirectory(Origin); }
            public void Manifest(ulong log, params ulong[] tables)
            {
                var bytes = new List<byte>(); AddVar(bytes, 2); AddVar(bytes, log);
                foreach (ulong table in tables) { AddVar(bytes, 7); AddVar(bytes, 0); AddVar(bytes, table); AddVar(bytes, 100); AddSized(bytes, Array.Empty<byte>()); AddSized(bytes, Array.Empty<byte>()); }
                File.WriteAllBytes(Path.Combine(Origin, "MANIFEST-000001"), Physical(bytes.ToArray()));
                File.WriteAllText(Path.Combine(Origin, "CURRENT"), "MANIFEST-000001\n", Encoding.ASCII);
            }
            public void Log(ulong number, params byte[][] batches)
            { File.WriteAllBytes(Path.Combine(Origin, number.ToString("D6") + ".log"), Join(batches.Select(Physical).ToArray())); }
            public void Table(ulong number, byte[] key, byte[] value, ulong sequence, bool compressed = false)
            {
                byte[] internalKey = Join(key, BitConverter.GetBytes((sequence << 8) | 1));
                byte[] raw = Block(internalKey, value), data = compressed ? Literal(raw) : raw;
                byte[] dataWithTrailer = Trailer(data, compressed ? (byte)1 : (byte)0);
                var handle = new List<byte>(); AddVar(handle, 0); AddVar(handle, (ulong)data.Length);
                byte[] index = Block(internalKey, handle.ToArray()); byte[] indexTrailer = Trailer(index, 0);
                var footer = new List<byte>(); AddVar(footer, 0); AddVar(footer, 0); AddVar(footer, (ulong)dataWithTrailer.Length); AddVar(footer, (ulong)index.Length);
                while (footer.Count < 40) footer.Add(0); footer.AddRange(BitConverter.GetBytes(0xdb4775248b80fb57UL));
                File.WriteAllBytes(Path.Combine(Origin, number.ToString("D6") + ".ldb"), Join(dataWithTrailer, indexTrailer, footer.ToArray()));
            }
            public void Blob(byte[] value)
            { string path = Path.Combine(Origin.Replace(".leveldb", ".blob"), "1", "00"); Directory.CreateDirectory(path); File.WriteAllBytes(Path.Combine(path, "16"), value); }
            public Dictionary<string, string> Hashes() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
                .ToDictionary(x => x.Substring(Root.Length), x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))));
            public void Dispose()
            {
                string target = Path.GetFullPath(Root), prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).StartsWith("WCAE-native-cache-", StringComparison.Ordinal)) Directory.Delete(target, true);
            }
        }
        static byte[] Key(string user, byte index = 1)
        { var b = new List<byte> { 0, 1, 1, index, 1 }; AddVar(b, (ulong)user.Length); b.AddRange(Encoding.BigEndianUnicode.GetBytes(user)); return b.ToArray(); }
        static byte[] Inline(JObject payload) => Join(new byte[] { 1 }, V8(payload));
        static byte[] V8(JObject payload)
        { byte[] text = Encoding.Unicode.GetBytes(payload.ToString(Formatting.None)); var b = new List<byte> { 255, 17, 255, 15, 99 }; AddVar(b, (ulong)text.Length); b.AddRange(text); return b.ToArray(); }
        static byte[] External(int size)
        { var b = new List<byte> { 31, 255, 17, 1 }; AddVar(b, (ulong)size); AddVar(b, 0); return b.ToArray(); }
        static byte[] BlobMetadata(int size)
        { const string mime = "application/vnd.blink-idb-value-wrapper"; var b = new List<byte> { 0, 22 }; AddVar(b, (ulong)mime.Length); b.AddRange(Encoding.BigEndianUnicode.GetBytes(mime)); AddVar(b, (ulong)size); return b.ToArray(); }
        static byte[] Batch(ulong sequence, params (byte[] Key, byte[] Value)[] values)
        {
            var b = new List<byte>(BitConverter.GetBytes(sequence)); b.AddRange(BitConverter.GetBytes((uint)values.Length));
            foreach (var value in values) { b.Add(value.Value == null ? (byte)0 : (byte)1); AddSized(b, value.Key); if (value.Value != null) AddSized(b, value.Value); }
            return b.ToArray();
        }
        static byte[] Physical(byte[] value)
        {
            if (value.Length > 32761) throw new InvalidOperationException("Fixture record too large");
            var b = new List<byte>(BitConverter.GetBytes(MaskedCrc(Join(new byte[] { 1 }, value))));
            b.AddRange(BitConverter.GetBytes((ushort)value.Length)); b.Add(1); b.AddRange(value); return b.ToArray();
        }
        static byte[] Block(byte[] key, byte[] value)
        { var b = new List<byte>(); AddVar(b, 0); AddVar(b, (ulong)key.Length); AddVar(b, (ulong)value.Length); b.AddRange(key); b.AddRange(value); b.AddRange(BitConverter.GetBytes(0)); b.AddRange(BitConverter.GetBytes(1)); return b.ToArray(); }
        static byte[] Trailer(byte[] data, byte type) => Join(data, new[] { type }, BitConverter.GetBytes(MaskedCrc(Join(data, new[] { type }))));
        static byte[] Literal(byte[] data)
        {
            var b = new List<byte>(); AddVar(b, (ulong)data.Length); int n = data.Length - 1;
            if (data.Length < 1) return b.ToArray();
            if (n < 60) b.Add((byte)(n << 2));
            else { int width = n < 256 ? 1 : n < 65536 ? 2 : n < 16777216 ? 3 : 4; b.Add((byte)((59 + width) << 2)); for (int i = 0; i < width; i++) b.Add((byte)(n >> (8 * i))); }
            b.AddRange(data); return b.ToArray();
        }
        static byte[] Join(params byte[][] values) => values.SelectMany(x => x).ToArray();
        static void AddSized(List<byte> bytes, byte[] value) { AddVar(bytes, (ulong)value.Length); bytes.AddRange(value); }
        static void AddVar(List<byte> bytes, ulong value) { do { byte b = (byte)(value & 127); value >>= 7; bytes.Add(value == 0 ? b : (byte)(b | 128)); } while (value != 0); }
        static uint MaskedCrc(byte[] bytes)
        {
            uint crc = 0xffffffffU;
            foreach (byte b in bytes) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82f63b78U : 0); }
            crc ^= 0xffffffffU; return unchecked(((crc >> 15) | (crc << 17)) + 0xa282ead8U);
        }
    }
}
