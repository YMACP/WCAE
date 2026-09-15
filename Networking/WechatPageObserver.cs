using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Web;
using System.Windows;
using System.Windows.Automation;

namespace WCAE
{
    public sealed class WechatObservedPage
    {
        // Contains session-bearing query parameters. Keep in memory; never log or serialize it.
        public string Url { get; internal set; } = "";
        public string AccountName { get; internal set; } = "";
        internal long ObservationGeneration { get; set; }
        public override string ToString() => "可见公众号文档（地址已省略）";
    }

    /// <summary>
    /// Reads only visible Document ValuePattern URLs under Weixin/WeChat article windows, and an
    /// optional account button in the confirmed document's profileBt group. No input, navigation,
    /// TextPattern, chat names, window titles, browser history or injection. Visibility does not
    /// establish foreground focus. Event handlers must be short and must not wait on network/UI.
    /// </summary>
    public sealed class WechatPageObserver : IDisposable
    {
        private readonly object gate = new object();
        private ObserverRun run;
        private bool disposed;
        private string lastStatus = "";
        private long publicationGeneration;
        private long observationGeneration;
        private int historySuspended;
        private const int PollMilliseconds = 2000;
        private const int ScanTimeoutSeconds = 8;
        private const int MaximumDocuments = 64;
        public event Action<WechatObservedPage> DocumentObserved;
        public event Action<string> StatusChanged;

        public void InvalidatePendingObservations() => Interlocked.Increment(ref observationGeneration);
        internal long ObservationGeneration => Interlocked.Read(ref observationGeneration);
        internal bool IsCurrentObservation(long generation) => generation == Interlocked.Read(ref observationGeneration);
        internal void SetHistorySuspended(bool suspended) => Volatile.Write(ref historySuspended, suspended ? 1 : 0);

        public void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(WechatPageObserver));
                if (run != null && !run.Exited)
                {
                    if (!run.Active) StatusLocked("上次页面读取尚未退出，未重复启动观察线程；代理识别仍可使用。");
                    return;
                }
                var next = new ObserverRun();
                run = next;
                next.Worker = new Thread(() => ObserveLoop(next)) { IsBackground = true, Name = "WCAE visible page observer" };
                next.Worker.SetApartmentState(ApartmentState.STA);
                next.Watchdog = new Timer(_ => CheckTimeout(next), null, 1000, 1000);
                StatusLocked("可见公众号页面观察已启动；可见状态不代表前台标签页。");
                try { next.Worker.Start(); }
                catch
                {
                    next.Active = false; next.Exited = true;
                    next.Watchdog.Dispose(); next.Cancellation.Dispose();
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                publicationGeneration++;
                InvalidatePendingObservations();
                if (run == null || run.Exited) return;
                run.Active = false;
                run.Cancellation.Cancel();
                run.Watchdog?.Dispose();
                // Do not join: an external UIA provider may be blocked indefinitely.
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                Stop();
                DocumentObserved = null;
                StatusChanged = null;
            }
        }

        private void ObserveLoop(ObserverRun observation)
        {
            try
            {
                while (!observation.Cancellation.IsCancellationRequested)
                {
                    long generation = Interlocked.Read(ref observationGeneration);
                    Interlocked.Exchange(ref observation.ScanStarted, Stopwatch.GetTimestamp());
                    try
                    {
                        var scan = ReadVisibleDocuments(observation.Cancellation.Token);
                        Interlocked.Exchange(ref observation.ScanStarted, 0);
                        Publish(observation, scan.Selection, scan.Status, generation);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception)
                    {
                        Interlocked.Exchange(ref observation.ScanStarted, 0);
                        Publish(observation, null, "暂时无法读取微信可见页面；可在微信中刷新文章，继续使用代理识别。", generation);
                    }
                    if (observation.Cancellation.Token.WaitHandle.WaitOne(PollMilliseconds)) break;
                }
            }
            finally
            {
                lock (gate)
                {
                    observation.Active = false;
                    observation.Exited = true;
                    observation.Watchdog?.Dispose();
                    observation.Cancellation.Dispose();
                }
            }
        }

        private void CheckTimeout(ObserverRun observation)
        {
            long started = Interlocked.Read(ref observation.ScanStarted);
            if (started == 0 || (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency < ScanTimeoutSeconds) return;
            lock (gate)
            {
                if (disposed || run != observation || !observation.Active || observation.Exited) return;
                // Mark inactive before cancellation so even a late UIA return cannot publish a URL.
                observation.Active = false;
                observation.Cancellation.Cancel();
                observation.Watchdog?.Dispose();
                StatusLocked("微信页面读取超时，已停止本次页面观察；可继续使用代理识别，稍后点击重新接入重试。");
            }
        }

        private void Publish(ObserverRun observation, Selection selection, string status, long generation)
        {
            lock (gate)
            {
                if (disposed || run != observation || !observation.Active || observation.Cancellation.IsCancellationRequested
                    || !IsCurrentObservation(generation) || Volatile.Read(ref historySuspended) != 0) return;
                StatusLocked(status);
                // Re-check after subscriber callbacks, which may themselves call Stop/Dispose.
                if (disposed || !observation.Active || observation.Cancellation.IsCancellationRequested
                    || !IsCurrentObservation(generation) || Volatile.Read(ref historySuspended) != 0) return;
                if (selection?.Page == null) { observation.LastUrl = ""; return; }
                if (selection.Page.Url == observation.LastUrl) return;
                observation.LastUrl = selection.Page.Url;
                var listeners = DocumentObserved;
                if (listeners == null) return;
                foreach (Action<WechatObservedPage> listener in listeners.GetInvocationList())
                {
                    if (disposed || !observation.Active || !IsCurrentObservation(generation) || Volatile.Read(ref historySuspended) != 0) return;
                    try { listener(new WechatObservedPage { Url = selection.Page.Url, AccountName = selection.Page.AccountName, ObservationGeneration = generation }); }
                    catch { /* A UI consumer must not interrupt passive observation. */ }
                }
            }
        }

        private void StatusLocked(string message)
        {
            if (disposed || message == lastStatus) return;
            lastStatus = message;
            var listeners = StatusChanged;
            if (listeners == null) return;
            long generation = publicationGeneration;
            foreach (Action<string> listener in listeners.GetInvocationList())
            {
                if (disposed || generation != publicationGeneration) return;
                try { listener(message); } catch { }
            }
        }

        private static Scan ReadVisibleDocuments(CancellationToken token)
        {
            var candidates = new List<VisibleDocument>();
            var elements = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
            bool sawWindow = false, hadReadFailure = false;
            var processes = new List<Process>();
            try
            {
                processes.AddRange(Process.GetProcessesByName("Weixin"));
                processes.AddRange(Process.GetProcessesByName("WeChat"));
                processes.AddRange(Process.GetProcessesByName("WeChatAppEx"));
                foreach (var process in processes)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        IntPtr handle = process.MainWindowHandle;
                        if (handle == IntPtr.Zero) continue;
                        var window = AutomationElement.FromHandle(handle);
                        if (window == null || window.Current.IsOffscreen || !VisibleBounds(window.Current.BoundingRectangle)) continue;
                        sawWindow = true;
                        Rect windowBounds = window.Current.BoundingRectangle;
                        var documents = window.FindAll(TreeScope.Descendants, new AndCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                            new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
                        if (documents.Count > MaximumDocuments) return new Scan { Status = "微信可见文档数量超出观察上限，未猜测目标页面。" };
                        foreach (AutomationElement document in documents)
                        {
                            token.ThrowIfCancellationRequested();
                            var info = document.Current;
                            if (info.IsOffscreen || !VisibleBounds(info.BoundingRectangle) || !windowBounds.IntersectsWith(info.BoundingRectangle)
                                || IsNestedOrUnconfirmedDocument(document, window)) continue;
                            if (!document.TryGetCurrentPattern(ValuePattern.Pattern, out object valueObject)) continue;
                            string value = ((ValuePattern)valueObject).Current.Value;
                            string normalized;
                            if (!TryDocumentUrl(value, out normalized)) continue;
                            candidates.Add(new VisibleDocument { Url = normalized, IsVisible = true });
                            if (!elements.ContainsKey(normalized)) elements[normalized] = document;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { hadReadFailure = true; }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            token.ThrowIfCancellationRequested();
            // A failed window scan may hide a second account. Never select only the successful subset.
            if (hadReadFailure) return new Scan { Status = "部分微信窗口暂时无法读取，未猜测当前公众号；代理识别仍可使用。" };
            var selected = SelectVisibleDocuments(candidates);
            if (selected.Page != null && elements.TryGetValue(selected.Page.Url, out AutomationElement selectedDocument))
            {
                selected.Page.AccountName = ReadProfileName(selectedDocument, token);
                token.ThrowIfCancellationRequested();
                string stillVisibleUrl;
                if (selectedDocument.Current.IsOffscreen || !selectedDocument.TryGetCurrentPattern(ValuePattern.Pattern, out object currentPattern)
                    || !TryDocumentUrl(((ValuePattern)currentPattern).Current.Value, out stillVisibleUrl) || stillVisibleUrl != selected.Page.Url)
                    return new Scan { Status = "微信文档在读取期间发生变化，等待下一次稳定页面观察。" };
            }
            string status = selected.Reason == SelectionReason.Selected ? "已观察到可见公众号文档；可见状态不代表前台标签页。"
                : selected.Reason == SelectionReason.ConflictingAccounts ? "发现多个可见公众号，无法确定目标；请保留一个公众号页面后重试。"
                : selected.Reason == SelectionReason.AmbiguousDocuments ? "存在多个不同的可见文档，未猜测目标页面。"
                : !sawWindow ? "尚未发现可见的微信主窗口；页面观察保持等待。"
                : "当前未发现可读取地址的可见公众号文档；可在微信中打开文章或刷新以使用代理识别。";
            return new Scan { Selection = selected, Status = status };
        }

        private static bool IsNestedOrUnconfirmedDocument(AutomationElement document, AutomationElement window)
        {
            var parent = TreeWalker.RawViewWalker.GetParent(document);
            for (int depth = 0; parent != null && depth < 64; depth++)
            {
                if (Automation.Compare(parent, window)) return false;
                if (parent.Current.ControlType == ControlType.Document) return true;
                parent = TreeWalker.RawViewWalker.GetParent(parent);
            }
            return true;
        }

        private static string ReadProfileName(AutomationElement document, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var groups = document.FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
                    new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "profileBt"),
                        new PropertyCondition(AutomationElement.NameProperty, "profileBt"))));
                if (groups.Count != 1) return "";
                var group = groups[0];
                if (group.Current.IsOffscreen || IsNestedOrUnconfirmedDocument(group, document)) return "";
                var buttons = group.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                if (buttons.Count > 8) return "";
                var names = new List<string>();
                foreach (AutomationElement button in buttons)
                {
                    token.ThrowIfCancellationRequested();
                    if (button.Current.IsOffscreen) continue;
                    string name = CleanAccountName(button.Current.Name);
                    if (name.Length > 0) names.Add(name);
                }
                var unique = names.Distinct(StringComparer.Ordinal).ToArray();
                return unique.Length == 1 ? unique[0] : "";
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
        }

        private static bool VisibleBounds(Rect bounds) => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
            && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);

        internal static bool TryDocumentUrl(string value, out string normalized)
        {
            normalized = "";
            if (string.IsNullOrWhiteSpace(value) || value.Length > 32768 || value.Any(char.IsControl)) return false;
            Uri uri;
            if (!Uri.TryCreate(HttpUtility.HtmlDecode(value.Trim()), UriKind.Absolute, out uri)
                || !uri.IsDefaultPort || !WechatCaptureParser.IsDocumentUri(uri)) return false;
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (string.Equals(query["action"], "getmsg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(query["f"], "json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(query["format"], "json", StringComparison.OrdinalIgnoreCase) || query["ajax"] == "1") return false;
            normalized = uri.GetLeftPart(UriPartial.Path) + uri.Query;
            return true;
        }

        internal static Selection SelectVisibleDocuments(IEnumerable<VisibleDocument> documents)
        {
            var eligible = new Dictionary<string, VisibleDocument>(StringComparer.Ordinal);
            foreach (var candidate in documents ?? Enumerable.Empty<VisibleDocument>())
            {
                if (candidate == null || !candidate.IsVisible || candidate.IsNestedDocument) continue;
                string normalized;
                if (!TryDocumentUrl(candidate.Url, out normalized)) continue;
                if (!eligible.ContainsKey(normalized)) eligible[normalized] = candidate;
                if (eligible.Count > MaximumDocuments) return new Selection { Reason = SelectionReason.AmbiguousDocuments };
            }
            if (eligible.Count == 0) return new Selection { Reason = SelectionReason.NoDocument };
            var identities = eligible.Keys.Select(url => HttpUtility.ParseQueryString(new Uri(url).Query))
                .Select(q => q["__biz"] ?? q["biz"]).Where(biz => !string.IsNullOrWhiteSpace(biz)).Distinct(StringComparer.Ordinal).ToArray();
            if (identities.Length > 1) return new Selection { Reason = SelectionReason.ConflictingAccounts };
            if (eligible.Count != 1) return new Selection { Reason = SelectionReason.AmbiguousDocuments };
            var chosen = eligible.Single();
            return new Selection { Reason = SelectionReason.Selected, Page = new WechatObservedPage { Url = chosen.Key, AccountName = CleanAccountName(chosen.Value.AccountName) } };
        }

        private static string CleanAccountName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)) return "";
            return value.Trim();
        }
        internal enum SelectionReason { NoDocument, Selected, ConflictingAccounts, AmbiguousDocuments }
        internal sealed class VisibleDocument
        {
            public string Url { get; set; }
            public string AccountName { get; set; }
            public bool IsVisible { get; set; } = true;
            public bool IsNestedDocument { get; set; }
        }
        internal sealed class Selection
        {
            public SelectionReason Reason { get; set; }
            public WechatObservedPage Page { get; set; }
        }
        private sealed class Scan { public Selection Selection; public string Status; }
        private sealed class ObserverRun
        {
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public Thread Worker;
            public Timer Watchdog;
            public long ScanStarted;
            public bool Active = true, Exited;
            public string LastUrl = "";
        }
    }
}
