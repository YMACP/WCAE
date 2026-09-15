using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WCAE
{
    public interface IArticleSupplementSource
    {
        Task<ArticleSupplementSnapshot> ReadAsync(AccountSession session, CancellationToken token);
    }
    public sealed class ArticleSupplementSnapshot
    {
        public IReadOnlyList<ArticleRecord> Articles { get; set; } = Array.Empty<ArticleRecord>();
        public string Message { get; set; } = "";
        public DateTime? CapturedAtUtc { get; set; }
    }

    // This is a passive first-screen supplement. It never opens a database engine,
    // starts WeChat, invokes the page, requests a URL, or changes the source's cursor.
    public sealed class NativeProfileCacheReader : IArticleSupplementSource
    {
        const int MaxFile = 16 * 1024 * 1024, MaxDecoded = 8 * 1024 * 1024;
        readonly string profilesRoot;
        public NativeProfileCacheReader() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Tencent", "xwechat", "radium", "web", "profiles")) { }
        public NativeProfileCacheReader(string profilesRoot)
        { this.profilesRoot = Path.GetFullPath(profilesRoot ?? throw new ArgumentNullException(nameof(profilesRoot))); }

        public Task<ArticleSupplementSnapshot> ReadAsync(AccountSession session, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var copy = session?.Clone();
            return Task.Run(() => Read(copy, token), token);
        }
        ArticleSupplementSnapshot Read(AccountSession session, CancellationToken token)
        {
            var budget = new ReadBudget(token);
            if (session == null || string.IsNullOrWhiteSpace(session.Biz) || string.IsNullOrWhiteSpace(session.UserName))
                return Empty("缺少公众号主页用户名，跳过缓存补漏。");
            try
            {
                budget.Check();
                if (!Directory.Exists(profilesRoot)) return Empty("未找到微信主页缓存，继续历史接口收集。");
                ArticleSupplementSnapshot best = null;
                bool unavailable = false;
                // Limit discovery to immediate profiles and the one known native origin.
                var profiles = Directory.EnumerateDirectories(profilesRoot, "*", SearchOption.TopDirectoryOnly)
                    .Where(p => !IsLink(p)).OrderByDescending(Directory.GetLastWriteTimeUtc).Take(8).ToArray();
                foreach (string profile in profiles)
                {
                    budget.Check();
                    string indexed = Path.Combine(profile, "IndexedDB");
                    string origin = Path.Combine(indexed, "weixin_resourceid_0.indexeddb.leveldb");
                    if (!Directory.Exists(origin) || IsLink(indexed) || IsLink(origin)) continue;
                    try
                    {
                        var snapshot = ReadOrigin(origin, session, budget);
                        if (snapshot != null && snapshot.Articles.Count > 0 && (best == null
                            || (snapshot.CapturedAtUtc ?? DateTime.MinValue) > (best.CapturedAtUtc ?? DateTime.MinValue))) best = snapshot;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (Expected(ex)) { unavailable = true; }
                }
                budget.Check();
                return best ?? Empty(unavailable ? "部分微信主页缓存正在变动或格式暂不支持，继续历史接口收集。"
                    : "当前公众号没有可用的原生首屏缓存，继续历史接口收集。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (Expected(ex))
            { return Empty("主页缓存补漏暂不可用（" + ex.GetType().Name + "），继续历史接口收集。"); }
        }
        static bool Expected(Exception ex) => ex is IOException || ex is UnauthorizedAccessException || ex is JsonException
            || ex is ArgumentException || ex is OverflowException || ex is TimeoutException || ex is NotSupportedException;
        static bool IsLink(string path) => Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        static ArticleSupplementSnapshot Empty(string message) => new ArticleSupplementSnapshot { Message = message };

        static ArticleSupplementSnapshot ReadOrigin(string origin, AccountSession session, ReadBudget budget)
        {
            string manifestName = Encoding.ASCII.GetString(ReadFile(Path.Combine(origin, "CURRENT"), budget, 256)).Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(manifestName, @"^MANIFEST-[0-9]+$")) throw Bad("manifest name");
            var manifest = new Manifest();
            string manifestPath = Path.Combine(origin, manifestName);
            byte[] manifestBytes = ReadFile(manifestPath, budget);
            foreach (byte[] edit in LogRecords(manifestBytes, budget)) manifest.Apply(edit);
            if (manifest.Files.Count > 32) throw Bad("too many tables");
            var records = new Dictionary<string, CacheRecord>(StringComparer.Ordinal);
            void Accept(byte[] key, byte[] value, ulong sequence)
            {
                budget.Entry();
                if (!TargetKey(key, session.UserName, out ulong db, out ulong store, out ulong index)) return;
                string mapKey = db + ":" + store + ":" + index;
                if (!records.TryGetValue(mapKey, out var previous) || sequence > previous.Sequence)
                    records[mapKey] = new CacheRecord { Database = db, Store = store, Index = index, Value = value, Sequence = sequence };
            }
            foreach (ulong number in manifest.Files.OrderBy(x => x))
            {
                budget.Check();
                string path = Path.Combine(origin, number.ToString("D6", CultureInfo.InvariantCulture) + ".ldb");
                if (!File.Exists(path)) path = Path.ChangeExtension(path, ".sst");
                ReadTable(ReadFile(path, budget), Accept, budget);
            }
            foreach (ulong number in new[] { manifest.PreviousLog, manifest.Log }.Where(x => x > 0).Distinct())
            {
                foreach (byte[] batch in LogRecords(ReadFile(Path.Combine(origin, number.ToString("D6", CultureInfo.InvariantCulture) + ".log"), budget), budget))
                {
                    if (batch.Length < 12) throw Bad("write batch");
                    ulong sequence = U64(batch, 0); uint count = U32(batch, 8); int pos = 12;
                    if (count > 100000) throw Bad("batch count");
                    for (uint i = 0; i < count; i++)
                    {
                        byte kind = Byte(batch, ref pos); byte[] key = Sized(batch, ref pos);
                        byte[] value = kind == 1 ? Sized(batch, ref pos) : kind == 0 ? null : throw Bad("write kind");
                        Accept(key, value, checked(sequence + i));
                    }
                    if (pos != batch.Length) throw Bad("write batch trailing bytes");
                }
            }
            // If the manifest changed, a removed SST or old WAL must not masquerade as current data.
            string after = Encoding.ASCII.GetString(ReadFile(Path.Combine(origin, "CURRENT"), budget, 256)).Trim();
            if (after != manifestName || !ReadFile(manifestPath, budget).AsSpan().SequenceEqual(manifestBytes)) throw Bad("manifest changed");
            ArticleSupplementSnapshot best = null;
            foreach (var entry in records.Values.Where(x => x.Index == 1 && x.Value != null))
            {
                budget.Check(); int pos = 0; Var(entry.Value, ref pos); // IndexedDB record version.
                byte[] value = entry.Value.AsSpan(pos).ToArray();
                if (Starts(value, 0xff, 0x11, 0x01))
                {
                    int at = 3; ulong expectedSize = Var(value, ref at), externalIndex = Var(value, ref at);
                    if (at != value.Length || externalIndex != 0 || expectedSize > MaxFile) throw Bad("external wrapper");
                    if (!records.TryGetValue(entry.Database + ":" + entry.Store + ":3", out var external) || external.Value == null)
                        throw Bad("missing external object");
                    int ep = 0;
                    if (Byte(external.Value, ref ep) != 0) throw Bad("unsupported external object type");
                    ulong blob = Var(external.Value, ref ep), chars = Var(external.Value, ref ep);
                    if (chars > 256 || chars * 2 > (ulong)(external.Value.Length - ep)) throw Bad("blob mime");
                    string mime = Encoding.BigEndianUnicode.GetString(external.Value, ep, (int)chars * 2); ep += (int)chars * 2;
                    ulong size = Var(external.Value, ref ep);
                    if (ep != external.Value.Length || size != expectedSize || mime != "application/vnd.blink-idb-value-wrapper") throw Bad("blob metadata");
                    string blobRoot = origin.Substring(0, origin.Length - ".leveldb".Length) + ".blob";
                    if (IsLink(blobRoot)) throw Bad("blob link");
                    string dbPath = Path.Combine(blobRoot, entry.Database.ToString(CultureInfo.InvariantCulture));
                    string bucket = Path.Combine(dbPath, (blob >> 8).ToString("x2", CultureInfo.InvariantCulture));
                    if (IsLink(dbPath) || IsLink(bucket)) throw Bad("blob link");
                    value = ReadFile(Path.Combine(bucket, blob.ToString("x", CultureInfo.InvariantCulture)), budget);
                    if ((ulong)value.Length != size) throw Bad("blob length");
                }
                string json = ProfileJson(value, budget);
                var parsed = ParseProfileJson(json, session, budget.Token);
                budget.Check();
                if (parsed.Articles.Count > 0 && (best == null || (parsed.CapturedAtUtc ?? DateTime.MinValue)
                    > (best.CapturedAtUtc ?? DateTime.MinValue))) best = parsed;
            }
            return best;
        }

        internal static ArticleSupplementSnapshot ParseProfileJson(string json, AccountSession session, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (session == null || string.IsNullOrWhiteSpace(session.Biz) || string.IsNullOrWhiteSpace(session.UserName)) return Empty("缺少公众号主页用户名。");
            if (json == null || json.Length > MaxDecoded) return Empty("主页缓存内容超出读取限制。");
            JObject payload;
            try
            {
                using var text = new StringReader(json);
                using var reader = new JsonTextReader(text) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
                payload = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            }
            catch (JsonException) { return Empty("主页缓存结构暂不支持，跳过补漏。"); }
            token.ThrowIfCancellationRequested();
            var account = payload["AccountInfo"] as JObject;
            var response = payload["BaseResponse"] as JObject;
            var list = payload["MsgList"] as JObject;
            if (account?["UserName"]?.Type != JTokenType.String || (string)account["UserName"] != session.UserName)
                return Empty("缓存公众号不匹配，未合入文章。");
            if (Integer(response?["Ret"]) != 0 || !(list?["Msg"] is JArray messages))
                return Empty("缓存没有有效的原生列表响应。");
            var featured = list["FeaturedList"];
            if (featured != null && featured.Type != JTokenType.Null && !(featured is JArray)) return Empty("缓存置顶列表结构未知。");
            if (messages.Count + ((featured as JArray)?.Count ?? 0) > 1024) return Empty("缓存列表超过条数限制。");
            var result = new List<ArticleRecord>(); int skipped = 0, count = 0;
            var source = (featured as JArray ?? new JArray()).Concat(messages);
            foreach (var message in source)
            {
                token.ThrowIfCancellationRequested();
                if (!(message is JObject item) || !(item["AppMsg"] is JObject app)
                    || !(app["DetailInfo"] is JArray details)) { skipped++; continue; }
                var info = item["BaseInfo"] as JObject;
                string sourceMid = Digits(info?["MsgId"]);
                foreach (var value in details)
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > 2048) return Empty("缓存文章超过条数限制。");
                    if (!(value is JObject detail) || detail["Title"]?.Type != JTokenType.String
                        || detail["ContentUrl"]?.Type != JTokenType.String
                        || !CanonicalUrl((string)detail["ContentUrl"], session.Biz, out string url, out string mid, out int idx)) { skipped++; continue; }
                    string title = (string)detail["Title"];
                    if (string.IsNullOrWhiteSpace(title) || title.Length > 32768) { skipped++; continue; }
                    var record = new ArticleRecord { Biz = session.Biz, AccountName = session.Name ?? "", Mid = mid, Idx = idx,
                        Id = session.Biz + ":" + mid + ":" + idx.ToString(CultureInfo.InvariantCulture), Url = url,
                        SourceMessageId = sourceMid, Status = ArticleStatus.Pending,
                        HistoryItemShowType = Integer(detail["ItemShowType"]), HistoryMessageType = Integer(info?["MsgType"]) };
                    // Featured groups can contain articles from different messages. The group's
                    // timestamp only belongs to an article whose URL confirms that message ID.
                    DateTime? utc = sourceMid == mid ? UnixDate(info?["DateTime"], false) : null;
                    DateTime? published = utc.HasValue ? DateTime.SpecifyKind(utc.Value.AddHours(8), DateTimeKind.Unspecified) : null;
                    ArticleMetadata.ApplyHistory(record, title, published);
                    if (record.Title.Length == 0) { skipped++; continue; }
                    // Keep native time as source evidence. A cached page's precise ct remains stronger.
                    if (published.HasValue) record.PublishedAtSource = "native-cache:message-time";
                    if (detail["Digest"]?.Type == JTokenType.String) record.Digest = (string)detail["Digest"];
                    if (detail["Author"]?.Type == JTokenType.String) record.Author = (string)detail["Author"];
                    // Neither ori_content nor a title is evidence of a downloaded article body.
                    result.Add(record);
                }
            }
            token.ThrowIfCancellationRequested();
            return new ArticleSupplementSnapshot { Articles = result, CapturedAtUtc = UnixDate(payload["updateTime"], true),
                Message = "原生首屏缓存补漏：读取 " + result.Count + " 条文章记录；缓存不代表完整历史。"
                    + (skipped > 0 ? " " + skipped + " 条缺少可验证链接或结构，未合入。" : "") };
        }
        static int? Integer(JToken value) => value?.Type == JTokenType.Integer && int.TryParse(value.ToString(), out int n) ? n : (int?)null;
        static string Digits(JToken value)
        {
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.String)) return "";
            string s = value.ToString(); return s.Length > 0 && s.Length <= 20 && s.All(c => c >= '0' && c <= '9') ? s : "";
        }
        static DateTime? UnixDate(JToken value, bool milliseconds)
        {
            if (value?.Type != JTokenType.Integer || !long.TryParse(value.ToString(), out long n) || n <= 0) return null;
            try { return (milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n)).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        static bool CanonicalUrl(string raw, string biz, out string url, out string mid, out int idx)
        {
            url = mid = ""; idx = 0;
            if (!Uri.TryCreate(HttpUtility.HtmlDecode(raw ?? ""), UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
                || !uri.Host.Equals("mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase)
                || (uri.AbsolutePath != "/s" && uri.AbsolutePath != "/mp/appmsg/show")) return false;
            var q = HttpUtility.ParseQueryString(uri.Query);
            foreach (string key in new[] { "__biz", "mid", "appmsgid", "idx", "sn" })
                if ((q.GetValues(key)?.Length ?? 0) > 1) return false;
            mid = q["mid"] ?? q["appmsgid"] ?? "";
            if (q["__biz"] != biz || mid.Length == 0 || mid.Length > 20 || !mid.All(c => c >= '0' && c <= '9')
                || (q["mid"] != null && q["appmsgid"] != null && q["mid"] != q["appmsgid"])
                || !int.TryParse(q["idx"], NumberStyles.None, CultureInfo.InvariantCulture, out idx) || idx < 1 || idx > 100) return false;
            url = "https://mp.weixin.qq.com/s?__biz=" + Uri.EscapeDataString(biz) + "&mid=" + mid + "&idx=" + idx.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(q["sn"]) && q["sn"].Length <= 256) url += "&sn=" + Uri.EscapeDataString(q["sn"]);
            return true;
        }

        static bool TargetKey(byte[] key, string name, out ulong db, out ulong store, out ulong index)
        {
            db = store = index = 0;
            if (key.Length < 6) return false;
            int p = 1, d = (key[0] >> 5) + 1, s = ((key[0] >> 2) & 7) + 1, i = (key[0] & 3) + 1;
            if (1 + d + s + i >= key.Length) return false;
            db = Little(key, ref p, d); store = Little(key, ref p, s); index = Little(key, ref p, i);
            if (db == 0 || store == 0 || (index != 1 && index != 3) || Byte(key, ref p) != 1) return false;
            ulong chars = Var(key, ref p);
            return chars == (ulong)name.Length && chars * 2 == (ulong)(key.Length - p)
                && Encoding.BigEndianUnicode.GetString(key, p, key.Length - p) == name;
        }
        sealed class CacheRecord { public ulong Database, Store, Index, Sequence; public byte[] Value; }
        sealed class Manifest
        {
            public ulong Log, PreviousLog;
            public HashSet<ulong> Files = new HashSet<ulong>();
            public void Apply(byte[] data)
            {
                int p = 0;
                while (p < data.Length)
                    switch (Var(data, ref p))
                    {
                        case 1: Sized(data, ref p); break;
                        case 2: Log = Var(data, ref p); break;
                        case 3: case 4: Var(data, ref p); break;
                        case 5: Var(data, ref p); Sized(data, ref p); break;
                        case 6: Var(data, ref p); Files.Remove(Var(data, ref p)); break;
                        case 7: Var(data, ref p); ulong n = Var(data, ref p); Var(data, ref p); Sized(data, ref p); Sized(data, ref p); Files.Add(n); break;
                        case 9: PreviousLog = Var(data, ref p); break;
                        default: throw Bad("unsupported manifest tag");
                    }
            }
        }
        sealed class ReadBudget
        {
            readonly Stopwatch watch = Stopwatch.StartNew();
            int entries; long bytes;
            public CancellationToken Token { get; }
            public ReadBudget(CancellationToken token) { Token = token; }
            public void Check() { Token.ThrowIfCancellationRequested(); if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(); }
            public void Entry() { if (++entries > 100000) throw Bad("entry limit"); if ((entries & 127) == 0) Check(); }
            public void Read(int count) { bytes += count; if (bytes > 64L * 1024 * 1024) throw Bad("read limit"); Check(); }
        }
        static byte[] ReadFile(string path, ReadBudget budget, int maximum = MaxFile)
        {
            budget.Check();
            var before = new FileInfo(path);
            if ((before.Attributes & FileAttributes.ReparsePoint) != 0) throw Bad("file link");
            long stamp = before.LastWriteTimeUtc.Ticks, length = before.Length;
            if (length < 0 || length > maximum) throw Bad("file size");
            byte[] bytes = new byte[(int)length]; int offset = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length != length) throw Bad("file changed");
                while (offset < bytes.Length)
                { int n = stream.Read(bytes, offset, Math.Min(65536, bytes.Length - offset)); if (n == 0) throw Bad("short read"); offset += n; budget.Read(n); }
                if (stream.Length != length) throw Bad("file grew");
            }
            before.Refresh();
            if (!before.Exists || before.Length != length || before.LastWriteTimeUtc.Ticks != stamp) throw Bad("file changed");
            return bytes;
        }

        static IEnumerable<byte[]> LogRecords(byte[] data, ReadBudget budget)
        {
            int p = 0; MemoryStream fragments = null;
            try
            {
                while (p + 7 <= data.Length)
                {
                    budget.Check(); int remaining = 32768 - p % 32768;
                    if (remaining < 7) { p += remaining; continue; }
                    uint crc = U32(data, p); int length = data[p + 4] | data[p + 5] << 8; byte type = data[p + 6];
                    if (length == 0 && type == 0) { p += remaining; continue; }
                    if (length > remaining - 7 || p + 7 + length > data.Length) throw Bad("log fragment");
                    var fragment = data.AsSpan(p + 7, length).ToArray();
                    if (crc != Mask(Crc(type, fragment, true))) throw Bad("log crc"); p += 7 + length;
                    if (type == 1) { if (fragments != null) throw Bad("log order"); yield return fragment; }
                    else if (type == 2) { if (fragments != null) throw Bad("log order"); fragments = new MemoryStream(); fragments.Write(fragment); }
                    else if (type == 3 || type == 4)
                    {
                        if (fragments == null || fragments.Length + length > MaxFile) throw Bad("log sequence");
                        fragments.Write(fragment);
                        if (type == 4) { byte[] result = fragments.ToArray(); fragments.Dispose(); fragments = null; yield return result; }
                    }
                    else throw Bad("log type");
                }
                if (fragments != null) throw Bad("partial log");
            }
            finally { fragments?.Dispose(); }
        }
        static void ReadTable(byte[] file, Action<byte[], byte[], ulong> accept, ReadBudget budget)
        {
            if (file.Length < 48 || U64(file, file.Length - 8) != 0xdb4775248b80fb57UL) throw Bad("table footer");
            int p = file.Length - 48; Var(file, ref p); Var(file, ref p);
            ulong indexOffset = Var(file, ref p), indexSize = Var(file, ref p);
            foreach (var pair in BlockEntries(TableBlock(file, indexOffset, indexSize, budget), budget))
            {
                int at = 0; ulong offset = Var(pair.Value, ref at), length = Var(pair.Value, ref at);
                if (at != pair.Value.Length) throw Bad("block handle");
                foreach (var row in BlockEntries(TableBlock(file, offset, length, budget), budget))
                {
                    if (row.Key.Length < 8) throw Bad("internal key");
                    ulong sequence = U64(row.Key, row.Key.Length - 8); byte kind = (byte)(sequence & 255);
                    if (kind != 0 && kind != 1) throw Bad("table value type");
                    accept(row.Key.AsSpan(0, row.Key.Length - 8).ToArray(), kind == 0 ? null : row.Value, sequence >> 8);
                }
            }
        }
        static byte[] TableBlock(byte[] file, ulong offset, ulong length, ReadBudget budget)
        {
            budget.Check();
            if (length > MaxFile || offset > (ulong)file.Length || length + 5 > (ulong)file.Length - offset) throw Bad("table block bounds");
            byte[] data = file.AsSpan((int)offset, (int)length).ToArray(); byte type = file[(int)(offset + length)];
            if (U32(file, (int)(offset + length + 1)) != Mask(Crc(type, data, false))) throw Bad("block crc");
            return type == 0 ? data : type == 1 ? Snappy(data, MaxDecoded, budget) : throw Bad("block compression");
        }
        static IEnumerable<KeyValuePair<byte[], byte[]>> BlockEntries(byte[] block, ReadBudget budget)
        {
            if (block.Length < 4) throw Bad("block restart count");
            uint restarts = U32(block, block.Length - 4);
            if (restarts > (block.Length - 4) / 4) throw Bad("block restarts");
            int end = block.Length - 4 - (int)restarts * 4, p = 0; byte[] previous = Array.Empty<byte>();
            while (p < end)
            {
                budget.Entry(); ulong shared = Var(block, ref p), unshared = Var(block, ref p), valueLength = Var(block, ref p);
                if (shared > (ulong)previous.Length || unshared > MaxDecoded || valueLength > MaxDecoded
                    || p > end || unshared + valueLength > (ulong)(end - p)) throw Bad("block entry bounds");
                byte[] key = new byte[checked((int)(shared + unshared))];
                Buffer.BlockCopy(previous, 0, key, 0, (int)shared); Buffer.BlockCopy(block, p, key, (int)shared, (int)unshared); p += (int)unshared;
                byte[] value = block.AsSpan(p, (int)valueLength).ToArray(); p += (int)valueLength; previous = key;
                yield return new KeyValuePair<byte[], byte[]>(key, value);
            }
            if (p != end) throw Bad("block end");
        }

        static string ProfileJson(byte[] value, ReadBudget budget)
        {
            byte[] data = Starts(value, 0xff, 0x11, 0x02) ? Snappy(value.AsSpan(3).ToArray(), MaxDecoded, budget) : value;
            if (data.Length > MaxDecoded) throw Bad("value size");
            for (int p = 0; p < data.Length; p++)
            {
                if ((p & 1023) == 0) budget.Check();
                if (data[p] != 0x63 && data[p] != 0x22) continue;
                int start = p + 1; ulong length;
                try { length = Var(data, ref start); } catch (IOException) { continue; }
                bool two = data[p] == 0x63;
                if (length < 4 || length > (ulong)(data.Length - start) || (two && length % 2 != 0)) continue;
                if (data[start] != '{' || (two ? data[start + 1] != 0 || data[start + 2] != '"' || data[start + 3] != 0 : data[start + 1] != '"')) continue;
                string json = (two ? Encoding.Unicode : Encoding.Latin1).GetString(data, start, (int)length);
                if (json.Contains("\"AccountInfo\"", StringComparison.Ordinal) && json.Contains("\"MsgList\"", StringComparison.Ordinal)) return json;
            }
            throw Bad("profile value format");
        }
        internal static byte[] DecodeSnappy(byte[] data, int maximumOutput, CancellationToken token = default)
            => Snappy(data, Math.Min(maximumOutput, MaxDecoded), new ReadBudget(token));
        static byte[] Snappy(byte[] data, int maximum, ReadBudget budget)
        {
            budget.Check(); int p = 0; ulong size = Var(data, ref p);
            if (maximum < 0 || size > (ulong)maximum) throw Bad("decompression limit");
            byte[] output = new byte[(int)size]; int written = 0;
            while (p < data.Length)
            {
                budget.Check(); byte tag = Byte(data, ref p); int kind = tag & 3, length; ulong offset = 0;
                if (kind == 0)
                {
                    ulong literal = (ulong)(tag >> 2);
                    if (literal >= 60) literal = Little(data, ref p, (int)literal - 59);
                    if (literal >= int.MaxValue) throw Bad("literal size"); length = (int)literal + 1;
                    if (length > data.Length - p || length > output.Length - written) throw Bad("literal bounds");
                    Buffer.BlockCopy(data, p, output, written, length); p += length; written += length;
                }
                else
                {
                    if (kind == 1) { length = 4 + ((tag >> 2) & 7); offset = ((ulong)(tag & 224) << 3) | (ulong)Byte(data, ref p); }
                    else { length = 1 + (tag >> 2); offset = Little(data, ref p, kind == 2 ? 2 : 4); }
                    if (offset == 0 || offset > (ulong)written || length > output.Length - written) throw Bad("copy bounds");
                    for (int i = 0; i < length; i++) { output[written] = output[written - (int)offset]; written++; }
                }
            }
            if (written != output.Length) throw Bad("decompression length"); return output;
        }
        static bool Starts(byte[] data, params byte[] prefix) => data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);
        static IOException Bad(string detail) => new IOException("Unsupported or inconsistent native profile cache: " + detail);
        static byte Byte(byte[] data, ref int pos) { if ((uint)pos >= data.Length) throw Bad("truncated value"); return data[pos++]; }
        static byte[] Sized(byte[] data, ref int pos)
        {
            ulong length = Var(data, ref pos);
            if (length > MaxFile || length > (ulong)(data.Length - pos)) throw Bad("length-delimited value");
            byte[] result = data.AsSpan(pos, (int)length).ToArray(); pos += (int)length; return result;
        }
        static ulong Var(byte[] data, ref int pos)
        {
            ulong result = 0;
            for (int shift = 0; shift < 70; shift += 7)
            { byte value = Byte(data, ref pos); if (shift == 63 && value > 1) throw Bad("varint overflow"); result |= (ulong)(value & 127) << shift; if (value < 128) return result; }
            throw Bad("varint overflow");
        }
        static ulong Little(byte[] data, ref int pos, int length)
        { if (length < 1 || length > 8) throw Bad("integer size"); ulong n = 0; for (int i = 0; i < length; i++) n |= (ulong)Byte(data, ref pos) << (8 * i); return n; }
        static uint U32(byte[] data, int pos) { int p = pos; return (uint)Little(data, ref p, 4); }
        static ulong U64(byte[] data, int pos) { int p = pos; return Little(data, ref p, 8); }
        static uint Mask(uint crc) => unchecked(((crc >> 15) | (crc << 17)) + 0xa282ead8U);
        static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(x =>
        { uint c = (uint)x; for (int i = 0; i < 8; i++) c = (c >> 1) ^ ((c & 1) != 0 ? 0x82f63b78U : 0); return c; }).ToArray();
        static uint Crc(byte type, byte[] data, bool typeFirst)
        {
            uint crc = 0xffffffffU;
            if (typeFirst) crc = CrcTable[(crc ^ type) & 255] ^ (crc >> 8);
            foreach (byte b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
            if (!typeFirst) crc = CrcTable[(crc ^ type) & 255] ^ (crc >> 8);
            return crc ^ 0xffffffffU;
        }
    }
}
