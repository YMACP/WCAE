using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using HtmlAgilityPack;

namespace WCAE
{
    // Reads data literals only. No JavaScript engine, evaluation, or network access is used.
    internal static class ArticleColumnParser
    {
        internal const int CurrentVersion = 1;
        static readonly HashSet<string> Containers = new HashSet<string>(StringComparer.Ordinal)
        { "appmsgalbuminfo", "album_info", "albumInfo", "album_info_list", "album_list", "albumList", "album_data", "albumData" };
        static readonly HashSet<string> Children = new HashSet<string>(StringComparer.Ordinal)
        { "list", "data", "albums", "items", "album_info", "albumInfo", "album_info_list", "album_list", "albumList" };

        internal static void Apply(ArticleRecord article, string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return;
            var doc = new HtmlDocument(); doc.LoadHtml(html); Apply(article, doc, html);
        }

        internal static void Apply(ArticleRecord article, HtmlDocument doc, string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return;
            var found = new List<ColumnInfo>();
            var legacyIds = new HashSet<string>(StringComparer.Ordinal);
            var legacyNames = new HashSet<string>(StringComparer.Ordinal);
            var scripts = doc.DocumentNode.Descendants("script").Select(n => n.InnerHtml).ToList();
            if (html.TrimStart().StartsWith("{", StringComparison.Ordinal)) scripts.Add(html);
            foreach (string script in scripts) ReadScript(article, script, found, legacyIds, legacyNames);
            foreach (string id in legacyIds) Add(article, found, id, legacyIds.Count == 1 && legacyNames.Count == 1 ? legacyNames.Single() : "", "");
            foreach (var node in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element))
            {
                if (node.AncestorsAndSelf().Any(n => Excluded(n.Id) || Excluded(n.GetAttributeValue("class", "")))) continue;
                string explicitId = First(node.GetAttributeValue("data-album-id", ""), node.GetAttributeValue("data-albumid", ""), node.GetAttributeValue("album_id", ""));
                string url = node.GetAttributeValue("href", ""), linkedId = UrlId(url, article.Url);
                string id = First(explicitId, linkedId);
                // Ordinary body/recommendation links are not evidence of this article's membership.
                bool albumWidget = node.AncestorsAndSelf().Any(n => (n.Id + " " + n.GetAttributeValue("class", "")).IndexOf("album", StringComparison.OrdinalIgnoreCase) >= 0);
                if (explicitId.Length == 0 && !albumWidget && !found.Any(c => c.Id == id)) continue;
                Add(article, found, id, First(node.GetAttributeValue("data-album-name", ""), node.GetAttributeValue("album_name", ""), node.GetAttributeValue("title", ""), node.InnerText), url);
            }
            article.Columns = Merge(found, article.Columns);
            article.ColumnMetadataVersion = CurrentVersion;
        }

        internal static List<ColumnInfo> Merge(IEnumerable<ColumnInfo> preferred, IEnumerable<ColumnInfo> saved)
        {
            var result = new List<ColumnInfo>();
            foreach (var input in (preferred ?? Enumerable.Empty<ColumnInfo>()).Concat(saved ?? Enumerable.Empty<ColumnInfo>()))
            {
                if (input == null) continue;
                var column = new ColumnInfo { Id = Clean(input.Id), Name = Clean(input.Name), Url = Clean(input.Url) };
                if (column.Id.Length == 0) column.Id = UrlId(column.Url, "");
                if (column.Id.Length == 0 && column.Url.Length == 0 && column.Name.Length == 0) continue;
                if (column.Id == "0") continue;
                if (column.Name.Length == 0) column.Name = "合集 " + column.Id;
                var existing = result.FirstOrDefault(c => column.Id.Length > 0 ? c.Id == column.Id
                    : column.Url.Length > 0 ? c.Id.Length == 0 && c.Url == column.Url : c.Id.Length == 0 && c.Url.Length == 0 && c.Name == column.Name);
                if (existing == null) result.Add(column);
                else
                {
                    if (IsPlaceholder(existing) && !IsPlaceholder(column)) existing.Name = column.Name;
                    if (existing.Url.Length == 0) existing.Url = column.Url;
                }
            }
            return result;
        }

        internal static bool IsPlaceholder(ColumnInfo column)
        {
            if (column == null) return true;
            string name = Clean(column.Name), id = Clean(column.Id);
            if (name.Length == 0 || name == "合集" || name == "未识别" || name == "未分类" || name == "未识别专栏") return true;
            return id.Length > 0 && string.Concat(name.Where(c => !char.IsWhiteSpace(c))) == "合集" + id;
        }

        static void ReadScript(ArticleRecord article, string script, List<ColumnInfo> result, HashSet<string> legacyIds, HashSet<string> legacyNames)
        {
            if (string.IsNullOrWhiteSpace(script) || script.Length > 16000000) return;
            List<Token> tokens = Lexer.Read(script);
            var contexts = new Stack<(string Close, bool Blocked)>();
            for (int i = 0; i < tokens.Count; i++)
            {
                Token token = tokens[i];
                bool blocked = contexts.Count > 0 && contexts.Peek().Blocked;
                if (!blocked && token.IsKey && i + 2 < tokens.Count && (tokens[i + 1].Is(":") || tokens[i + 1].Is("=")))
                {
                    if (Containers.Contains(token.Text))
                    {
                        int start = i + 2;
                        try
                        {
                            var value = new LiteralReader(tokens).Read(ref start, 0);
                            if (value?.Scalar != null && (value.Scalar.TrimStart().StartsWith("{", StringComparison.Ordinal) || value.Scalar.TrimStart().StartsWith("[", StringComparison.Ordinal)))
                            {
                                var inner = Lexer.Read(value.Scalar); int at = 0; value = new LiteralReader(inner).Read(ref at, 0);
                            }
                            Collect(article, value, result, 0);
                        }
                        catch (FormatException) { /* Keep other independently readable containers. */ }
                    }
                    else if (contexts.Count == 0 && tokens[i + 1].Is("=") && LegacyGlobal(tokens, i) && (token.Text == "album_id" || token.Text == "album_name"))
                    {
                        int at = i + 2;
                        try
                        {
                            string value = new LiteralReader(tokens).Read(ref at, 0)?.Scalar ?? "";
                            if (token.Text == "album_id") { if (ValidId(value)) legacyIds.Add(value); }
                            else if (!string.IsNullOrWhiteSpace(value)) legacyNames.Add(Clean(value));
                        }
                        catch (FormatException) { }
                    }
                }
                if (token.Is("{") || token.Is("[") || token.Is("("))
                {
                    string owner = i >= 2 && (tokens[i - 1].Is(":") || tokens[i - 1].Is("=")) ? tokens[i - 2].Text : "";
                    contexts.Push((token.Text == "{" ? "}" : token.Text == "[" ? "]" : ")", blocked || Excluded(owner)));
                }
                else if (contexts.Count > 0 && token.Is(contexts.Peek().Close)) contexts.Pop();
            }
        }

        static void Collect(ArticleRecord article, Data value, List<ColumnInfo> result, int depth)
        {
            if (value == null || depth > 32) return;
            if (value.Items != null) { foreach (var item in value.Items) Collect(article, item, result, depth + 1); return; }
            if (value.Fields == null) return;
            string id = Field(value, "albumIdStr", "album_id_str", "album_id", "albumId", "id");
            string url = Field(value, "album_url", "url", "link");
            if (!ValidId(id)) id = UrlId(url, article.Url);
            Add(article, result, id, Field(value, "album_name", "albumName", "title", "name"), url);
            // Do not descend into article_titles, article_list, arbitrary tags, or recommendations.
            foreach (var field in value.Fields) if (Children.Contains(field.Key)) Collect(article, field.Value, result, depth + 1);
        }

        static void Add(ArticleRecord article, List<ColumnInfo> result, string id, string name, string url)
        {
            id = Clean(id); if (!ValidId(id)) return;
            url = ArticleHtmlParser.NormalizeUrl(url, article.Url);
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri albumUrl) && albumUrl.Host.Equals("mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase)
                && albumUrl.AbsolutePath.Equals("/mp/appmsgalbum", StringComparison.OrdinalIgnoreCase))
            {
                var query = HttpUtility.ParseQueryString(albumUrl.Query);
                string biz = query["__biz"] ?? "", linkedId = query["album_id"] ?? "";
                if (biz.Length > 0 && !string.IsNullOrEmpty(article.Biz) && biz != article.Biz) return;
                if (ValidId(linkedId) && linkedId != id) return;
            }
            if (url.Length == 0 && !string.IsNullOrEmpty(article.Biz)) url = "https://mp.weixin.qq.com/mp/appmsgalbum?__biz=" + Uri.EscapeDataString(article.Biz) + "&album_id=" + Uri.EscapeDataString(id);
            result.Add(new ColumnInfo { Id = id, Name = Clean(name), Url = url });
        }
        static string Field(Data data, params string[] names) => First(names.Select(n => data.Fields.TryGetValue(n, out Data value) ? value?.Scalar : "").ToArray());
        static bool LegacyGlobal(List<Token> tokens, int at) => at == 0 || !tokens[at - 1].Is(".")
            || at >= 2 && tokens[at - 2].Kind == 'i' && tokens[at - 2].Text == "window" && (at < 3 || !tokens[at - 3].Is("."));
        static string First(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
        static string Clean(string value) => HttpUtility.HtmlDecode(HttpUtility.HtmlDecode(value ?? "")).Trim();
        static bool ValidId(string id) => !string.IsNullOrEmpty(id) && id.Length <= 100 && id.Any(c => c != '0') && id.All(char.IsAsciiDigit);
        static string UrlId(string url, string baseUrl)
        {
            if (!Uri.TryCreate(ArticleHtmlParser.NormalizeUrl(url, baseUrl), UriKind.Absolute, out Uri parsed)
                || !parsed.Host.Equals("mp.weixin.qq.com", StringComparison.OrdinalIgnoreCase)
                || !parsed.AbsolutePath.Equals("/mp/appmsgalbum", StringComparison.OrdinalIgnoreCase)) return "";
            string id = HttpUtility.ParseQueryString(parsed.Query)["album_id"] ?? "";
            return ValidId(id) ? id : "";
        }
        static bool Excluded(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            return n.Contains("recommend") || n.Contains("related") || n.Contains("suggest") || n.Contains("advert")
                || n.Contains("article_list") || n.Contains("article_titles") || n.Contains("appmsg_list") || n.Contains("app_msg_list")
                || n.Contains("app_msg_item") || n.Contains("news_item") || n == "articles" || n == "next_article" || n == "prev_article";
        }

        sealed class Data
        {
            internal string Scalar;
            internal Dictionary<string, Data> Fields;
            internal List<Data> Items;
        }
        sealed class Token
        {
            internal string Text;
            internal char Kind;
            internal bool IsKey => Kind == 's' || Kind == 'i';
            internal bool Is(string punctuation) => Kind == 'p' && Text == punctuation;
            internal Token(string text, char kind) { Text = text; Kind = kind; }
        }

        // Tokenization treats quoted strings, comments and regular expressions as indivisible.
        // Numeric tokens stay strings, including integers larger than JavaScript's safe range.
        static class Lexer
        {
            internal static List<Token> Read(string text)
            {
                var tokens = new List<Token>(); int at = 0;
                while (at < text.Length && tokens.Count < 500000)
                {
                    char c = text[at]; if (char.IsWhiteSpace(c)) { at++; continue; }
                    if (c == '/' && at + 1 < text.Length && text[at + 1] == '/') { at += 2; while (at < text.Length && text[at] != '\n') at++; continue; }
                    if (c == '/' && at + 1 < text.Length && text[at + 1] == '*') { int end = text.IndexOf("*/", at + 2, StringComparison.Ordinal); at = end < 0 ? text.Length : end + 2; continue; }
                    if (c == '\'' || c == '"' || c == '`')
                    {
                        char quote = c; at++; var value = new StringBuilder(); bool closed = false, valid = true;
                        while (at < text.Length)
                        {
                            c = text[at++]; if (c == quote) { closed = true; break; }
                            if (c != '\\') { value.Append(c); continue; }
                            if (at >= text.Length) break; c = text[at++];
                            if (c == 'u' || c == 'x')
                            {
                                int width = c == 'u' ? 4 : 2;
                                if (at + width <= text.Length && int.TryParse(text.Substring(at, width), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)) { value.Append((char)code); at += width; }
                                else valid = false;
                            }
                            else if (c == '\n') { }
                            else if (c == '\r') { if (at < text.Length && text[at] == '\n') at++; }
                            else value.Append(c == 'n' ? '\n' : c == 'r' ? '\r' : c == 't' ? '\t' : c == 'b' ? '\b' : c == 'f' ? '\f' : c);
                        }
                        tokens.Add(new Token(value.ToString(), closed && valid && quote != '`' ? 's' : 'x')); continue;
                    }
                    if (c == '/' && (tokens.Count == 0 || RegexCanStart(tokens[tokens.Count - 1])))
                    {
                        int start = at++; bool escaped = false, inClass = false;
                        while (at < text.Length)
                        {
                            c = text[at++]; if (escaped) { escaped = false; continue; } if (c == '\\') { escaped = true; continue; }
                            if (c == '[') inClass = true; else if (c == ']') inClass = false; else if (c == '/' && !inClass) break;
                            if (c == '\r' || c == '\n') break;
                        }
                        while (at < text.Length && char.IsAsciiLetter(text[at])) at++;
                        tokens.Add(new Token(text.Substring(start, at - start), 'x')); continue;
                    }
                    if (char.IsAsciiLetter(c) || c == '_' || c == '$')
                    { int start = at++; while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '_' || text[at] == '$')) at++; tokens.Add(new Token(text.Substring(start, at - start), 'i')); continue; }
                    if (char.IsAsciiDigit(c))
                    { int start = at++; while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '.')) at++; tokens.Add(new Token(text.Substring(start, at - start), 'n')); continue; }
                    tokens.Add(new Token(c.ToString(), 'p')); at++;
                }
                return tokens;
            }
            static bool RegexCanStart(Token previous) => previous.Kind == 'p' && "(:,=[{!?;*+-|&".Contains(previous.Text, StringComparison.Ordinal)
                || previous.Kind == 'i' && (previous.Text == "return" || previous.Text == "case" || previous.Text == "throw");
        }

        sealed class LiteralReader
        {
            readonly List<Token> tokens;
            int nodes;
            internal LiteralReader(List<Token> tokens) { this.tokens = tokens; }
            internal Data Read(ref int at, int depth)
            {
                if (at >= tokens.Count || depth > 48 || ++nodes > 20000) throw new FormatException("Album literal exceeds supported limits");
                int start = at; Token token = tokens[at++]; Data value = null;
                if (token.Is("{"))
                {
                    value = new Data { Fields = new Dictionary<string, Data>(StringComparer.Ordinal) };
                    while (at < tokens.Count && !tokens[at].Is("}"))
                    {
                        Token key = tokens[at++];
                        if (!key.IsKey || at >= tokens.Count || !tokens[at++].Is(":")) throw new FormatException("Invalid album member");
                        Data member = Read(ref at, depth + 1);
                        if (!value.Fields.ContainsKey(key.Text)) value.Fields.Add(key.Text, member);
                        else value.Fields[key.Text] = null; // Ambiguous duplicate keys are not trusted.
                        if (at < tokens.Count && tokens[at].Is(",")) at++;
                        else if (at >= tokens.Count || !tokens[at].Is("}")) throw new FormatException("Unterminated album object");
                    }
                    if (at >= tokens.Count) throw new FormatException("Unterminated album object"); at++;
                }
                else if (token.Is("["))
                {
                    value = new Data { Items = new List<Data>() };
                    while (at < tokens.Count && !tokens[at].Is("]"))
                    {
                        value.Items.Add(Read(ref at, depth + 1));
                        if (at < tokens.Count && tokens[at].Is(",")) at++;
                        else if (at >= tokens.Count || !tokens[at].Is("]")) throw new FormatException("Unterminated album array");
                    }
                    if (at >= tokens.Count) throw new FormatException("Unterminated album array"); at++;
                }
                else if (token.Kind == 's' || token.Kind == 'n') value = new Data { Scalar = token.Text };
                // A literal followed by operators is an expression, not an exact field value.
                if (at < tokens.Count && !Delimiter(tokens[at])) { at = start; Skip(ref at); return null; }
                return value;
            }
            void Skip(ref int at)
            {
                var closing = new Stack<string>();
                while (at < tokens.Count)
                {
                    Token token = tokens[at];
                    if (closing.Count == 0 && Delimiter(token)) return;
                    if (token.Is("{") || token.Is("[") || token.Is("(")) closing.Push(token.Is("{") ? "}" : token.Is("[") ? "]" : ")");
                    else if (closing.Count > 0 && token.Is(closing.Peek())) closing.Pop();
                    at++;
                }
            }
            static bool Delimiter(Token token) => token.Is(",") || token.Is("}") || token.Is("]") || token.Is(";");
        }
    }
}
