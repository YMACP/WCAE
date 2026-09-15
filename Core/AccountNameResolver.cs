using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using HtmlAgilityPack;

namespace WCAE
{
    /// <summary>Public-account display names, kept separate from identifiers and page-component field mappings.</summary>
    public static class AccountNameResolver
    {
        private const int MaximumSourceLength = 16 * 1024 * 1024;
        private const int MaximumTokens = 400000;
        private static readonly HashSet<string> FieldMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "data-miniprogram-nickname", "data-miniprogram-appid", "data-miniprogram-path",
            "data-nickname", "data-nick-name"
        };

        public static string Normalize(string name, string biz = "")
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string value = HttpUtility.HtmlDecode(name).Trim();
            if (value.Length == 0 || value.Length > 1024 || value.Any(char.IsControl) || FieldMarkers.Contains(value)) return "";
            if (!string.IsNullOrWhiteSpace(biz) && string.Equals(value, HttpUtility.HtmlDecode(biz).Trim(), StringComparison.Ordinal)) return "";
            return value;
        }

        public static string Choose(string biz, params string[] candidates)
        {
            foreach (string candidate in candidates ?? Array.Empty<string>())
            {
                string value = Normalize(candidate, biz);
                if (value.Length != 0) return value;
            }
            return "";
        }

        public static string Extract(string html, string biz = "")
        {
            if (string.IsNullOrWhiteSpace(html) || html.Length > MaximumSourceLength) return "";
            HtmlDocument document = null;
            if (LooksLikeHtml(html)) { document = new HtmlDocument(); document.LoadHtml(html); }
            return Extract(document, html, biz);
        }

        public static string Extract(HtmlDocument document, string html, string biz = "")
        {
            if (string.IsNullOrWhiteSpace(html) || html.Length > MaximumSourceLength) return "";
            var names = new List<string>();
            var identifiers = new List<string>();
            if (LooksLikeHtml(html))
            {
                if (document == null) { document = new HtmlDocument(); document.LoadHtml(html); }
                foreach (var script in document.DocumentNode.Descendants("script"))
                {
                    string type = script.GetAttributeValue("type", "").Trim();
                    if (script.GetAttributeValue("src", "").Length != 0 || (type.Length != 0 && type != "module"
                        && !type.Equals("text/javascript", StringComparison.OrdinalIgnoreCase) && !type.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
                        && !type.Equals("text/ecmascript", StringComparison.OrdinalIgnoreCase) && !type.Equals("application/ecmascript", StringComparison.OrdinalIgnoreCase))) continue;
                    ReadDeclarations(script.InnerHtml, names, identifiers);
                }
            }
            else ReadDeclarations(html, names, identifiers); // Existing parser callers also supply standalone page scripts.

            string expected = (biz ?? "").Trim();
            if (expected.Length != 0 && identifiers.Any(value => value.Length != 0 && value != expected)) return "";
            if (expected.Length == 0)
            {
                var documentIds = identifiers.Where(value => value.Length != 0).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                if (documentIds.Length > 1) return "";
                if (documentIds.Length == 1) expected = documentIds[0];
            }
            if (document != null && LooksLikeHtml(html))
            {
                foreach (var node in document.DocumentNode.Descendants().Where(n => n.GetAttributeValue("id", "") == "js_name"))
                {
                    if (node.AncestorsAndSelf().Any(n => n.Name == "script" || n.Name == "style" || n.Name == "template" || n.Name == "noscript"
                        || n.GetAttributeValue("id", "") == "js_content" || n.GetAttributeValue("id", "") == "js_comment")) continue;
                    string name = Normalize(node.InnerText, expected);
                    if (name.Length != 0) return name;
                }
            }
            return Choose(expected, names.ToArray());
        }

        private static bool LooksLikeHtml(string text) => text.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal);
        private static bool NameVariable(string name) => name == "nickname" || name == "nick_name";
        private static bool BizVariable(string name) => name == "biz" || name == "__biz";
        private static bool ProfileVariable(string name) => name == "cgiDataNew" || name == "cgiData";

        private static void ReadDeclarations(string script, List<string> names, List<string> identifiers)
            => ReadTopLevelAssignments(script, (tokens, name, start) => ReadAssignment(tokens, name, start, names, identifiers));

        // Shared, non-executing lexer for explicit page metadata. It ignores local variables,
        // strings, comments, template literals and arbitrary nested objects.
        internal static Dictionary<string, List<string>> ReadPageScalars(HtmlDocument document, string html)
        {
            var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(html) || html.Length > MaximumSourceLength) return values;
            Action<List<Token>, string, int> scalar = (tokens, name, start) =>
            {
                string value; int next;
                if (!TryStringExpression(tokens, start, out value, out next))
                {
                    if (start >= tokens.Count || tokens[start].Kind != TokenKind.Number) return;
                    value = tokens[start].Text; next = start + 1;
                }
                if (next < tokens.Count && !At(tokens, next, ";") && !At(tokens, next, ",") && !At(tokens, next, "}")) return;
                if (!values.TryGetValue(name, out var entries)) values[name] = entries = new List<string>();
                entries.Add(value);
            };
            Action<List<Token>, string, int> assignment = (tokens, name, start) =>
            {
                if (!ProfileVariable(name) || !At(tokens, start, "{")) { scalar(tokens, name, start); return; }
                int depth = 1;
                for (int i = start + 1; i < tokens.Count && depth != 0; i++)
                {
                    if (depth == 1 && (tokens[i].Kind == TokenKind.Identifier || tokens[i].Kind == TokenKind.String)
                        && (At(tokens, i - 1, "{") || At(tokens, i - 1, ",")) && At(tokens, i + 1, ":"))
                        scalar(tokens, tokens[i].Text, i + 2);
                    if (tokens[i].Kind != TokenKind.Punctuation) continue;
                    if (tokens[i].Text == "{" || tokens[i].Text == "[" || tokens[i].Text == "(") depth++;
                    if (tokens[i].Text == "}" || tokens[i].Text == "]" || tokens[i].Text == ")") depth--;
                }
            };
            if (!LooksLikeHtml(html)) ReadTopLevelAssignments(html, assignment);
            else
            {
                if (document == null) { document = new HtmlDocument(); document.LoadHtml(html); }
                foreach (var script in document.DocumentNode.Descendants("script"))
                {
                    string type = script.GetAttributeValue("type", "").Trim();
                    if (script.GetAttributeValue("src", "").Length != 0 || (type.Length != 0 && type != "module"
                        && !type.Equals("text/javascript", StringComparison.OrdinalIgnoreCase) && !type.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
                        && !type.Equals("text/ecmascript", StringComparison.OrdinalIgnoreCase) && !type.Equals("application/ecmascript", StringComparison.OrdinalIgnoreCase))) continue;
                    ReadTopLevelAssignments(script.InnerHtml, assignment);
                }
            }
            return values;
        }

        private static void ReadTopLevelAssignments(string script, Action<List<Token>, string, int> assignment)
        {
            var tokens = Tokenize(script);
            int braces = 0, parentheses = 0, brackets = 0;
            bool declaration = false;
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (braces == 0 && parentheses == 0 && brackets == 0)
                {
                    if (token.Is(";")) declaration = false;
                    if (token.Kind == TokenKind.Identifier && (token.Text == "var" || token.Text == "let" || token.Text == "const")) declaration = true;
                    bool declared = i > 0 && declaration && (tokens[i - 1].Is("var") || tokens[i - 1].Is("let") || tokens[i - 1].Is("const") || tokens[i - 1].Is(","));
                    if (declared && token.Kind == TokenKind.Identifier && At(tokens, i + 1, "="))
                        assignment(tokens, token.Text, i + 2);
                    if (token.Is("window") && (i == 0 || !tokens[i - 1].Is(".")) && At(tokens, i + 1, ".")
                        && i + 3 < tokens.Count && tokens[i + 2].Kind == TokenKind.Identifier && At(tokens, i + 3, "="))
                        assignment(tokens, tokens[i + 2].Text, i + 4);
                }
                if (token.Kind != TokenKind.Punctuation) continue;
                switch (token.Text)
                {
                    case "{": braces++; break; case "}": braces = Math.Max(0, braces - 1); break;
                    case "(": parentheses++; break; case ")": parentheses = Math.Max(0, parentheses - 1); break;
                    case "[": brackets++; break; case "]": brackets = Math.Max(0, brackets - 1); break;
                }
            }
        }

        private static void ReadAssignment(List<Token> tokens, string name, int start, List<string> names, List<string> identifiers)
        {
            if (ProfileVariable(name) && At(tokens, start, "{"))
            {
                // Only direct fields of a named page-data object; nested comments and component objects are excluded.
                int depth = 1;
                for (int i = start + 1; i < tokens.Count && depth != 0; i++)
                {
                    if (depth == 1 && (tokens[i].Kind == TokenKind.Identifier || tokens[i].Kind == TokenKind.String)
                        && (At(tokens, i - 1, "{") || At(tokens, i - 1, ",")) && At(tokens, i + 1, ":"))
                        ReadScalar(tokens, tokens[i].Text, i + 2, names, identifiers);
                    if (tokens[i].Kind == TokenKind.Punctuation)
                    {
                        if (tokens[i].Text == "{" || tokens[i].Text == "[") depth++;
                        if (tokens[i].Text == "}" || tokens[i].Text == "]") depth--;
                    }
                }
                return;
            }
            ReadScalar(tokens, name, start, names, identifiers);
        }

        private static void ReadScalar(List<Token> tokens, string name, int start, List<string> names, List<string> identifiers)
        {
            if (!NameVariable(name) && !BizVariable(name)) return;
            if (!TryStringExpression(tokens, start, out string value, out int next)) return;
            // Refuse a literal prefix of a concatenation, ternary, property access or another computed expression.
            if (next < tokens.Count && !At(tokens, next, ";") && !At(tokens, next, ",") && !At(tokens, next, "}")) return;
            if (NameVariable(name)) names.Add(value);
            else
            {
                string identifier = HttpUtility.HtmlDecode(value).Trim();
                if (identifier.Length != 0) identifiers.Add(identifier);
            }
        }

        private static bool TryStringExpression(List<Token> tokens, int start, out string value, out int next)
        {
            value = ""; next = start;
            if (!TryStringLiteral(tokens, start, out string current, out next)) return false;
            value = current;
            while (At(tokens, next, "||"))
            {
                if (!TryStringLiteral(tokens, next + 1, out string fallback, out next)) return false;
                if (value.Length == 0) value = fallback;
            }
            return true;
        }
        private static bool TryStringLiteral(List<Token> tokens, int start, out string value, out int next)
        {
            value = ""; next = start;
            if (start >= tokens.Count) return false;
            if (tokens[start].Kind == TokenKind.String) { value = tokens[start].Text; next = start + 1; return true; }
            int function = start;
            if (At(tokens, start, "window") && At(tokens, start + 1, ".")) function += 2;
            if (At(tokens, function, "htmlDecode") && At(tokens, function + 1, "(") && function + 3 < tokens.Count
                && tokens[function + 2].Kind == TokenKind.String && At(tokens, function + 3, ")"))
            { value = tokens[function + 2].Text; next = function + 4; return true; }
            return false;
        }
        private static bool At(List<Token> tokens, int index, string text) => index >= 0 && index < tokens.Count && tokens[index].Is(text);
        private enum TokenKind { Identifier, String, Number, Punctuation, Opaque }
        private sealed record Token(TokenKind Kind, string Text)
        {
            public bool Is(string text) => (Kind == TokenKind.Identifier || Kind == TokenKind.Punctuation) && Text == text;
        }

        private static List<Token> Tokenize(string text)
        {
            var result = new List<Token>();
            int offset = 0;
            while (offset < text.Length && result.Count < MaximumTokens)
            {
                char c = text[offset];
                if (char.IsWhiteSpace(c)) { offset++; continue; }
                if (SkipComment(text, ref offset)) continue;
                if (c == '\'' || c == '"')
                {
                    bool complete = ReadQuoted(text, ref offset, out string value);
                    if (!complete) return new List<Token>();
                    result.Add(new Token(TokenKind.String, value)); continue;
                }
                if (c == '`') { SkipTemplate(text, ref offset, 0); result.Add(new Token(TokenKind.Opaque, "")); continue; }
                if (c == '/' && RegexMayStart(result.LastOrDefault()))
                { SkipRegex(text, ref offset); result.Add(new Token(TokenKind.Opaque, "")); continue; }
                if (c >= '0' && c <= '9')
                {
                    int start = offset++;
                    while (offset < text.Length && text[offset] >= '0' && text[offset] <= '9') offset++;
                    result.Add(new Token(TokenKind.Number, text.Substring(start, offset - start))); continue;
                }
                if (char.IsLetter(c) || c == '_' || c == '$')
                {
                    int start = offset++;
                    while (offset < text.Length && (char.IsLetterOrDigit(text[offset]) || text[offset] == '_' || text[offset] == '$')) offset++;
                    result.Add(new Token(TokenKind.Identifier, text.Substring(start, offset - start))); continue;
                }
                if (c == '|' && offset + 1 < text.Length && text[offset + 1] == '|') { result.Add(new Token(TokenKind.Punctuation, "||")); offset += 2; continue; }
                result.Add(new Token(TokenKind.Punctuation, c.ToString())); offset++;
            }
            return result;
        }
        private static void SkipQuoted(string text, ref int offset)
        {
            char quote = text[offset++];
            while (offset < text.Length)
            {
                char c = text[offset++];
                if (c == '\\') { if (offset < text.Length) offset++; }
                else if (c == quote) return;
            }
        }
        private static bool RegexMayStart(Token prior)
            => prior == null || (prior.Kind == TokenKind.Identifier && (prior.Text == "return" || prior.Text == "throw" || prior.Text == "case"))
                || (prior.Kind == TokenKind.Punctuation && new[] { "=", "(", "[", "{", ",", ":", ";", "!", "?", "|", "||", "&" }.Contains(prior.Text));

        private static bool SkipComment(string text, ref int offset)
        {
            if (offset + 1 >= text.Length || text[offset] != '/') return false;
            if (text[offset + 1] == '/')
            { offset += 2; while (offset < text.Length && text[offset] != '\r' && text[offset] != '\n') offset++; return true; }
            if (text[offset + 1] != '*') return false;
            int end = text.IndexOf("*/", offset + 2, StringComparison.Ordinal); offset = end < 0 ? text.Length : end + 2; return true;
        }
        private static void SkipRegex(string text, ref int offset)
        {
            offset++; bool bracket = false;
            while (offset < text.Length)
            {
                char c = text[offset++];
                if (c == '\\') { if (offset < text.Length) offset++; continue; }
                if (c == '[') bracket = true;
                else if (c == ']') bracket = false;
                else if (c == '/' && !bracket) { while (offset < text.Length && char.IsLetter(text[offset])) offset++; return; }
                else if (c == '\r' || c == '\n') return;
            }
        }
        private static void SkipTemplate(string text, ref int offset, int recursion)
        {
            if (recursion > 32) { offset = text.Length; return; }
            offset++;
            while (offset < text.Length)
            {
                char c = text[offset++];
                if (c == '\\') { if (offset < text.Length) offset++; continue; }
                if (c == '`') return;
                if (c != '$' || offset >= text.Length || text[offset] != '{') continue;
                offset++; int braces = 1;
                while (offset < text.Length && braces != 0)
                {
                    if (SkipComment(text, ref offset)) continue;
                    c = text[offset];
                    if (c == '\'' || c == '"') { SkipQuoted(text, ref offset); continue; }
                    if (c == '`') { SkipTemplate(text, ref offset, recursion + 1); continue; }
                    offset++;
                    if (c == '{') braces++; else if (c == '}') braces--;
                }
            }
        }
        private static bool ReadQuoted(string text, ref int offset, out string value)
        {
            char quote = text[offset++]; var output = new StringBuilder(); value = "";
            while (offset < text.Length)
            {
                char c = text[offset++];
                if (c == quote) { value = output.ToString(); return true; }
                if (c == '\r' || c == '\n') return false;
                if (c != '\\') { output.Append(c); continue; }
                if (offset >= text.Length) return false;
                c = text[offset++];
                if (c == '\n') continue;
                if (c == '\r') { if (offset < text.Length && text[offset] == '\n') offset++; continue; }
                if (c == 'u' || c == 'x')
                {
                    int digits = c == 'u' ? 4 : 2;
                    if (offset + digits > text.Length || !int.TryParse(text.AsSpan(offset, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)) return false;
                    output.Append((char)code); offset += digits; continue;
                }
                output.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 'v' => '\v', '0' => '\0', _ => c });
            }
            return false;
        }
    }
}
