using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WCAE
{
    // Modeless on purpose: MCP cancellation and the main window's shutdown remain responsive.
    internal static class McpDestinationPicker
    {
        internal static Task<string> SelectAsync(Form owner, CancellationToken token)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (token.IsCancellationRequested || owner.IsDisposed || owner.Disposing) return Task.FromResult<string>(null);
            if (owner.InvokeRequired) throw new InvalidOperationException("导出目录选择器必须在界面线程打开。");
            var picker = new Picker(owner);
            try
            {
                picker.Show(owner);
                picker.AttachCancellation(token);
                return picker.Result;
            }
            catch
            {
                picker.Dispose();
                throw;
            }
        }

        sealed class DirectoryNode
        {
            internal readonly string Path;
            internal bool Loading, Loaded;
            internal DirectoryNode(string path) { Path = path; }
        }

        sealed class Listing
        {
            internal readonly List<string> Paths = new List<string>();
            internal bool Truncated;
            internal string Error;
        }

        sealed class Picker : Form
        {
            readonly Form owner;
            readonly TaskCompletionSource<string> completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            readonly CancellationTokenSource lifetime = new CancellationTokenSource();
            CancellationTokenRegistration registration;
            CancellationToken requestCancellation;
            readonly TreeView tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.FixedSingle, ShowNodeToolTips = true };
            readonly TextBox path = new TextBox { Dock = DockStyle.Fill, AccessibleName = "导出保存路径" };
            readonly Label message = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(93, 111, 102) };
            readonly Button select = new Button { Text = "选择此目录", AutoSize = true, Height = 32, BackColor = Color.FromArgb(226, 243, 233), FlatStyle = FlatStyle.Flat };
            readonly Button cancel = new Button { Text = "取消", AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat };
            bool finished, cleaned;
            internal Task<string> Result => completion.Task;

            internal Picker(Form owner)
            {
                this.owner = owner;
                Text = "WCAE · 选择导出目录";
                Font = new Font("Microsoft YaHei UI", 9F);
                BackColor = Color.White;
                ClientSize = new Size(660, 480);
                MinimumSize = new Size(540, 380);
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = false;
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                layout.Controls.Add(new Label { Text = "请选择保存位置，也可以直接输入新的目录路径。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
                layout.Controls.Add(tree, 0, 1);
                var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 10, 0, 0) };
                pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                pathRow.Controls.Add(new Label { Text = "路径", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, 0, 0);
                pathRow.Controls.Add(path, 1, 0); layout.Controls.Add(pathRow, 0, 2);
                layout.Controls.Add(message, 0, 3);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
                cancel.Margin = new Padding(8, 3, 0, 0); select.Margin = new Padding(8, 3, 0, 0);
                cancel.FlatAppearance.BorderColor = Color.FromArgb(203, 214, 208); select.FlatAppearance.BorderColor = Color.FromArgb(184, 216, 198);
                buttons.Controls.Add(cancel); buttons.Controls.Add(select); layout.Controls.Add(buttons, 0, 4);
                Controls.Add(layout);
                AcceptButton = select; CancelButton = cancel;
                select.Click += (_, __) => Choose(); cancel.Click += (_, __) => Finish(null);
                tree.AfterSelect += (_, e) =>
                {
                    if (e.Node.Tag is DirectoryNode directory) { path.Text = directory.Path; message.Text = "可展开目录浏览，或直接点击“选择此目录”。"; }
                };
                tree.BeforeExpand += (_, e) => { if (e.Node.Tag is DirectoryNode directory && !directory.Loaded && !directory.Loading) _ = LoadDirectoriesAsync(e.Node, directory); };
                tree.NodeMouseDoubleClick += (_, e) => { if (e.Node.Tag is DirectoryNode directory) path.Text = directory.Path; };
                path.TextChanged += (_, __) => { select.Enabled = !string.IsNullOrWhiteSpace(path.Text); };
                owner.FormClosing += OwnerClosing;
                AddRoot("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                AddRoot("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                var drives = new TreeNode("此电脑") { ToolTipText = "展开查看驱动器" };
                // GetLogicalDrives only lists drive letters; no IsReady/VolumeLabel calls that may wait for remote/removable media.
                try { foreach (string drive in Directory.GetLogicalDrives()) drives.Nodes.Add(MakeNode(drive, drive)); }
                catch (IOException) { drives.Nodes.Add(new TreeNode("驱动器列表暂不可用，请直接输入路径。")); }
                catch (UnauthorizedAccessException) { drives.Nodes.Add(new TreeNode("驱动器列表暂不可用，请直接输入路径。")); }
                tree.Nodes.Add(drives); drives.Expand();
                if (tree.Nodes.Count > 1 && tree.Nodes[0].Tag is DirectoryNode) tree.SelectedNode = tree.Nodes[0];
                select.Enabled = path.Text.Length > 0;
            }

            void AddRoot(string title, string directory)
            { if (!string.IsNullOrWhiteSpace(directory)) tree.Nodes.Add(MakeNode(title, directory)); }
            static TreeNode MakeNode(string title, string directory)
            {
                var node = new TreeNode(title) { Tag = new DirectoryNode(directory), ToolTipText = directory };
                node.Nodes.Add(new TreeNode("展开加载目录…")); return node;
            }

            internal void AttachCancellation(CancellationToken token)
            {
                if (finished || IsDisposed) return;
                requestCancellation = token;
                registration = token.Register(() =>
                {
                    if (IsDisposed || !IsHandleCreated) { completion.TrySetResult(null); return; }
                    try { BeginInvoke(new Action(() => Finish(null))); }
                    catch (InvalidOperationException) { completion.TrySetResult(null); }
                });
            }

            async Task LoadDirectoriesAsync(TreeNode node, DirectoryNode directory)
            {
                if (finished || cleaned) return;
                directory.Loading = true;
                CancellationToken token = lifetime.Token;
                node.Nodes.Clear(); node.Nodes.Add(new TreeNode("正在读取目录…"));
                try
                {
                    Listing listing = await Task.Run(() => ReadDirectories(directory.Path, token), token);
                    if (finished || cleaned || IsDisposed || token.IsCancellationRequested || node.TreeView != tree) return;
                    tree.BeginUpdate();
                    try
                    {
                        node.Nodes.Clear();
                        foreach (string child in listing.Paths)
                        {
                            string title = Path.GetFileName(child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                            node.Nodes.Add(MakeNode(title, child));
                        }
                        directory.Loaded = listing.Error == null;
                        if (listing.Error != null) node.Nodes.Add(new TreeNode(listing.Error + "；收起后展开可重试。"));
                        else if (listing.Truncated) node.Nodes.Add(new TreeNode("仅显示前 500 个目录，可在路径框输入其他目录。"));
                        else if (listing.Paths.Count == 0) node.Nodes.Add(new TreeNode("没有子目录，可选择当前目录。"));
                        if (listing.Error != null) message.Text = "目录暂不可读取；仍可直接输入保存路径。";
                    }
                    finally { tree.EndUpdate(); }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException)
                {
                    if (!finished && !cleaned && !IsDisposed && node.TreeView == tree)
                    { node.Nodes.Clear(); node.Nodes.Add(new TreeNode("目录暂不可读取，收起后展开可重试。")); message.Text = "请直接输入保存路径，或选择其他目录。"; }
                }
                finally { directory.Loading = false; }
            }

            static Listing ReadDirectories(string directory, CancellationToken token)
            {
                var result = new Listing(); token.ThrowIfCancellationRequested();
                try
                {
                    using var entries = Directory.EnumerateDirectories(directory, "*", new EnumerationOptions
                    { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.System, ReturnSpecialDirectories = false }).GetEnumerator();
                    while (entries.MoveNext())
                    {
                        token.ThrowIfCancellationRequested();
                        if (result.Paths.Count == 500) { result.Truncated = true; break; }
                        result.Paths.Add(entries.Current);
                    }
                    result.Paths.Sort(StringComparer.CurrentCultureIgnoreCase);
                }
                catch (UnauthorizedAccessException) { result.Error = "没有读取此目录的权限"; }
                catch (System.Security.SecurityException) { result.Error = "没有读取此目录的权限"; }
                catch (IOException) { result.Error = "目录暂不可用"; }
                return result;
            }

            void Choose()
            {
                if (finished) return;
                try
                {
                    string candidate = path.Text.Trim();
                    if (candidate.Length >= 2 && candidate[0] == '"' && candidate[candidate.Length - 1] == '"') candidate = candidate.Substring(1, candidate.Length - 2);
                    if (!Path.IsPathFullyQualified(candidate) || candidate.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*', '\0' }) >= 0
                        || candidate.IndexOf(':', candidate.Length >= 2 && candidate[1] == ':' ? 2 : 0) >= 0)
                        throw new ArgumentException("请输入完整的目录路径，例如 E:\\文章导出。");
                    string absolute = Path.GetFullPath(candidate);
                    // Do not create or probe the destination here. Exporting owns creation and detailed IO errors.
                    Finish(absolute);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
                { message.Text = "路径无效，请输入完整的目录路径，例如 E:\\文章导出。"; path.Focus(); }
            }

            void OwnerClosing(object sender, FormClosingEventArgs e) => Finish(null);
            void Finish(string result)
            {
                if (finished) return;
                finished = true;
                if (requestCancellation.IsCancellationRequested) result = null;
                Cleanup();
                completion.TrySetResult(result);
                if (!IsDisposed && !Disposing) Close();
            }
            void Cleanup()
            {
                if (cleaned) return;
                cleaned = true;
                owner.FormClosing -= OwnerClosing;
                registration.Unregister(); // Non-blocking: a cancellation callback may currently be posting to this UI thread.
                lifetime.Cancel(); lifetime.Dispose();
            }
            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                finished = true; Cleanup(); completion.TrySetResult(null); base.OnFormClosed(e);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { finished = true; Cleanup(); completion.TrySetResult(null); }
                base.Dispose(disposing);
            }
        }
    }
}
