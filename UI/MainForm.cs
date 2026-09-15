using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WCAE
{
    public sealed partial class MainForm : Form, IMcpApplication
    {
        readonly bool preview;
        readonly string dataDirectory;
        readonly Func<CancellationToken,Task> isolatedConnectionInitializer;
        ArticleRepository repository;
        SessionVault vault;
        Dictionary<string,AccountSession> sessions = new Dictionary<string,AccountSession>();
        readonly Dictionary<string,ArticleRecord> records = new Dictionary<string,ArticleRecord>();
        readonly HashSet<string> selected = new HashSet<string>();
        Dictionary<(string,string),string> representativeIds = new Dictionary<(string,string),string>();
        HttpTransport http;
        CaptureService capture;
        ArticleEnricher enricher;
        ExportService exporter;
        CollectionController controller;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        ProxyConfigurationStore connectionStore;
        ProxyConfiguration connectionConfiguration = new ProxyConfiguration();
        ProxyEndpoint activeProxy;
        string activeConnection = "";
        bool coreReady, coreDisposed, uiResourcesDisposed;
        Task initializationTask, settingsTask, shutdownTask;
        internal Task InitializationTask => initializationTask ?? Task.CompletedTask;
        internal Task ShutdownTask => shutdownTask ?? Task.CompletedTask;
        internal bool IsClosing => closing;
        internal event EventHandler ShutdownStarted;
        readonly ComboBox accounts = Combo(235), columns = Combo(100), status = Combo(100), sort = Combo(98), direction = Combo(64), format = Combo(145);
        readonly TextBox search = new TextBox { Width = 180, AccessibleName = "文章标题关键词" };
        readonly DateFilterPicker from = new DateFilterPicker { AccessibleName="起始日期" }, through = new DateFilterPicker { AccessibleName="结束日期" };
        readonly IconActionButton start = new IconActionButton(ActionIcon.Start, "开始收集"), stop = new IconActionButton(ActionIcon.Stop, "停止收集"), reconnect = new IconActionButton(ActionIcon.Reconnect, "重新接入");
        readonly IconActionButton searchButton = new IconActionButton(ActionIcon.Search, "搜索标题");
        readonly IconActionButton clearCache = new IconActionButton(ActionIcon.ClearCache, "清空缓存");
        readonly IconActionButton connectionSettings = new IconActionButton(ActionIcon.Settings, "连接设置");
        readonly IconActionButton mcpSettings = new IconActionButton(ActionIcon.Mcp, "MCP接入");
        readonly Button exportSelected = Button("导出", 60);
        readonly ToolTip actionTips = new ToolTip { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 5000, ShowAlways = true };
        readonly CheckBox localImages = new CheckBox { Text = "保存正文图片", AutoSize = true, Checked = false }, overwrite = new CheckBox { Text = "覆盖已有文件", AutoSize = true };
        readonly Label summary = new Label { Dock=DockStyle.Fill, TextAlign=ContentAlignment.MiddleRight, Margin=Padding.Empty, Text = "0 篇文章", ForeColor = Color.FromArgb(84,96,104) };
        readonly CaptureStageProgress captureProgress = new CaptureStageProgress { Dock = DockStyle.Fill, Margin = Padding.Empty };
        const string CaptureNotice = "注意：需打开一篇公众号文章后刷新，方可成功识别公众号";
        readonly Label captureNotice = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Text = CaptureNotice, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(96,116,105), Margin = Padding.Empty };
        readonly DataGridView grid = new WholeRowDataGridView();
        readonly TextBox log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(248,250,251) };
        readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 600 };
        readonly System.Windows.Forms.Timer healthTimer = new System.Windows.Forms.Timer { Interval = 15000 };
        List<ArticleRecord> filtered = new List<ArticleRecord>();
        AccountSession current;
        Task listenerTask, collectionTask, exportTask, healthTask, reconnectTask, clearTask;
        CancellationTokenSource exportCancellation;
        string displayedBiz = "";
        string titleKeyword = "";
        int historyEpoch;
        bool clearingCache;
        bool binding, dirty, closing, allowClose;
        bool healthChecking, reconnecting, lastRouteActive;
        long lastDiagnosticVersion = -1, lastDiagnosticSequence;
        string lastDiagnosticLog = "";
        DateTime lastDiagnosticLogAt = DateTime.MinValue;

        public MainForm(bool preview = false) : this(preview,null) { }
        internal MainForm(bool preview, Func<CancellationToken,Task> isolatedConnectionInitializer)
        {
            this.preview = preview;
            dataDirectory=AppPaths.DataDirectory;
            this.isolatedConnectionInitializer=isolatedConnectionInitializer;
            Text = "WCAE";
            Icon = AppBranding.CreateApplicationIcon();
            Font = new Font("Microsoft YaHei UI", 9F);
            Size = new Size(1360,880); MinimumSize = new Size(1180,740); StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            if(preview) { InitializeCore(CancellationToken.None); coreReady=true; }
            BuildUi(); WireEvents();
            if(preview) { LoadAccounts(); FillPreview(); } else RefreshTable();
            refreshTimer.Tick += (s,e) => { if(closing || clearingCache)return; if (dirty) { dirty = false; RefreshTable(); } if(!preview && coreReady)RenderCaptureDiagnostics(); }; refreshTimer.Start();
            healthTimer.Tick += async (s,e) => await CheckCaptureHealthAsync();
        }
        void InitializeCore(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            repository = new ArticleRepository(dataDirectory); vault = new SessionVault(dataDirectory); sessions = vault.Load();
            AccountNameMaintenance.Repair(repository,vault,sessions,token);
            try
            {
                var repaired=ArticleMetadataMaintenance.Repair(repository,token);
                if(repaired.Examined>0)Ui(()=>AppendLog($"历史文章检查完成：更新 {repaired.Changed} 篇元数据，保留 {repaired.Unchanged} 篇。"));
            }
            catch(OperationCanceledException) { throw; }
            catch(Exception ex) { Ui(()=>AppendLog("历史文章修复未完成，原记录已保留（"+ex.GetType().Name+"）；下次启动将继续检查。")); }
            try
            {
                var progress=new Progress<ArticleColumnRepairReport>(p=>
                {
                    if(p.Examined%50==0)Ui(()=>AppendLog($"正在检查历史合集：已检查 {p.Examined} 篇，补齐 {p.Changed} 篇。"));
                });
                var repaired=ArticleColumnMaintenance.Repair(repository,token,progress);
                if(repaired.Examined>0)Ui(()=>AppendLog($"历史合集检查完成：补齐 {repaired.Changed} 篇；{repaired.Unresolved} 篇暂缺名称，{repaired.Unreadable} 篇正文读取失败。未补齐的记录已保留，后续获取页面时继续补齐。"));
            }
            catch(OperationCanceledException) { throw; }
            catch(Exception ex) { Ui(()=>AppendLog("历史合集修复未完成（"+ex.GetType().Name+"）；已保存的文章保留，下次启动将继续检查。")); }
            token.ThrowIfCancellationRequested();
            http = new HttpTransport(); capture = new CaptureService(); enricher = new ArticleEnricher(http); exporter = new ExportService(http);
            capture.SeedKnownSessions(sessions.Values);
            var collector = new HistoryCollector(http);
            controller = new CollectionController(collector, enricher.EnrichAsync, repository,
                supplementSource: new NativeProfileCacheReader());
            controller.ArticleSaved += article => QueueHistoryUpdate(() => OnArticleSaved(article));
            controller.SessionUpdated += session => QueueHistoryUpdate(() => OnSessionUpdated(session));
            capture.AccountDetected += s => QueueHistoryUpdate(() => OnRecognized(s));
            capture.SessionUpdated += session => QueueHistoryUpdate(() => OnSessionUpdated(session));
            capture.StatusChanged += message => Ui(() => { if(closing)return; AppendLog(message); RenderCaptureDiagnostics(true); });
            token.ThrowIfCancellationRequested();
            connectionStore = new ProxyConfigurationStore(dataDirectory);
            if(!preview)
            {
                try { connectionConfiguration=connectionStore.Load(); }
                catch(InvalidDataException ex) { Ui(()=>AppendLog(ex.Message)); }
            }
            InitializeMcpCore();
        }
        async Task InitializeApplicationAsync()
        {
            try
            {
                AppendLog("正在后台加载历史记录并检测网络出口……");
                mcpInitializationTask=InitializeMcpConfigurationAsync(lifetime.Token);
                await Task.Run(()=>InitializeCore(lifetime.Token),lifetime.Token);
                if(closing)return;
                coreReady=true;
                var history=await Task.Run(()=>
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    var known=repository.Accounts();
                    var first=known.Keys.FirstOrDefault()??"";
                    return (Known:known, Biz:first, Articles:repository.Load(first));
                },lifetime.Token);
                if(closing)return;
                binding=true;
                try
                {
                    accounts.Items.Clear(); foreach(var pair in history.Known)accounts.Items.Add(new AccountItem(pair.Key,pair.Value));
                    accounts.SelectedIndex=accounts.Items.Count>0?0:-1; displayedBiz=history.Biz;
                }
                finally { binding=false; }
                for(int i=0;i<history.Articles.Count;i++)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    var article=history.Articles[i]; records[article.Id]=article;
                    if(i==499)RefreshTable();
                    if(i%500==499)await Task.Yield();
                }
                RefreshTable(); accounts.Enabled=true;
                await ConnectConfiguredAsync(false);
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) { if(!closing)AppendLog("初始化未完成："+ex.GetType().Name+"；请查看连接设置或重新启动。"); }
            finally { if(!closing) { connectionSettings.Enabled=true; reconnect.Enabled=coreReady; clearCache.Enabled=coreReady; start.Enabled=coreReady && !preview; } }
        }
        async Task ConnectConfiguredAsync(bool reconfigure)
        {
            if(!coreReady || closing)return;
            if(isolatedConnectionInitializer!=null)
            {
                await isolatedConnectionInitializer(lifetime.Token); return;
            }
            int port=capture.ListeningPort>0?capture.ListeningPort:capture.PreferredPort;
            var configuration=connectionConfiguration.Clone();
            if(reconfigure && capture.Diagnostics.Snapshot.Listening)
            {
                // Restore the original network settings before auto-discovery so we never discover our own proxy.
                await Task.Run(()=>capture.Stop(),lifetime.Token);
                if(closing)return;
            }
            await Task.Run(()=>SystemProxyCaptureRoute.RecoverOrphanedAsync(message=>Ui(()=>AppendLog(message)),lifetime.Token),lifetime.Token);
            if(closing)return;
            if(configuration.Mode==ProxyMode.Manual)
            {
                activeProxy=configuration.ManualEndpoint.Clone();
                activeConnection=activeProxy.DisplayName;
                http.UseProxy(activeProxy);
            }
            var found=await ProxyDiscovery.ResolveAsync(configuration,port,lifetime.Token);
            if(closing)return;
            AppendLog(found.Message);
            if(!found.Success)
            {
                if(configuration.Mode==ProxyMode.Manual)
                {
                    activeConnection+="（连接未通过验证）";
                    AppendLog("手动代理配置已保存，但当前连接未通过验证；请检查地址、端口和认证信息，保存后或点击「重新接入」重试。");
                }
                else AppendLog("未找到可用的代理出口，请点击右上角「连接设置」，手动填写地址和端口并保存。");
                RenderCaptureDiagnostics(true); return;
            }
            activeProxy=found.Endpoint?.Clone(); activeConnection=found.Endpoint?.DisplayName??"直接连接";
            http.UseProxy(activeProxy);
            if(reconfigure)
                listenerTask=Task.Run(()=>capture.ReconfigureAsync(activeProxy,configuration.IngressMode,lifetime.Token),lifetime.Token);
            else
                listenerTask=Task.Run(()=> { lifetime.Token.ThrowIfCancellationRequested(); capture.Configure(activeProxy,configuration.IngressMode); capture.Start(); },lifetime.Token);
            await listenerTask;
            if(closing)return;
            RenderCaptureDiagnostics(true); healthTimer.Start();
        }
        async Task OpenConnectionSettingsAsync()
        {
            if(closing)return;
            using(var dialog=new ConnectionSettingsForm(connectionConfiguration,capture?.PreferredPort??8879,activeConnection))
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK || closing)return;
                try
                {
                    connectionStore ??= new ProxyConfigurationStore(dataDirectory);
                    connectionStore.Save(dialog.SelectedConfiguration,capture?.PreferredPort??8879);
                    connectionConfiguration=dialog.SelectedConfiguration.Clone();
                    AppendLog(connectionConfiguration.Mode==ProxyMode.Manual?"已保存手动代理配置，将使用填写的地址和端口。":"连接设置已保存。");
                    if(preview) { AppendLog("离线预览仅保存测试设置，未更改网络接入。"); return; }
                    if(!coreReady || initializationTask?.IsCompleted==false) { AppendLog("设置将在初始化完成后的接入中使用。"); return; }
                    if(controller.IsRunning || exportCancellation!=null || HasActiveMcpTask("collection") || HasActiveMcpTask("export") || clearingCache)
                    { AppendLog("当前任务继续使用原连接；任务结束后点击「重新接入」应用新设置。"); return; }
                    await ReconnectWechatAsync();
                }
                catch(Exception ex) { if(!closing)AppendLog("连接设置应用失败："+(ex is ArgumentException?ex.Message:ex.GetType().Name)); }
            }
        }
        static ComboBox Combo(int width) => new ComboBox { Width = width, DropDownStyle = ComboBoxStyle.DropDownList, IntegralHeight = false, DropDownHeight = 300 };
        static Button Button(string text, int width) => new Button { Text = text, Width = width, Height = 25, FlatStyle = FlatStyle.Flat, BackColor = Color.White, Cursor = Cursors.Hand, Padding=Padding.Empty };
        static Label Caption(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(0,9,6,0) };
        static FlowLayoutPanel Row(params Control[] controls)
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12,6,4,4), WrapContents = true, BackColor = Color.White };
            foreach (var c in controls) { c.Margin = new Padding(c.Margin.Left, c is Label ? c.Margin.Top : 3, 10, 3); p.Controls.Add(c); }
            return p;
        }
        static TableLayoutPanel CenteredRow(params Control[] controls)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(12,0,12,0), RowCount = 1, ColumnCount = controls.Length + 1, BackColor = Color.White };
            row.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            for(var i=0;i<controls.Length;i++)
            {
                var control=controls[i];
                control.Anchor=AnchorStyles.Left;
                control.Margin=new Padding(0,0,control is Label?6:10,0);
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                row.Controls.Add(control,i,0);
            }
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            return row;
        }
        void BuildUi()
        {
            start.Enabled = coreReady && !preview; stop.Enabled = false; reconnect.Enabled = false;
            clearCache.Enabled=coreReady; accounts.Enabled=coreReady; connectionSettings.Enabled=preview;
            actionTips.SetToolTip(start, "开始收集"); actionTips.SetToolTip(stop, "停止收集"); actionTips.SetToolTip(reconnect, "重新接入");
            actionTips.SetToolTip(searchButton, "搜索标题"); accounts.AccessibleName="已收集的公众号";
            actionTips.SetToolTip(clearCache,"清空缓存");
            actionTips.SetToolTip(connectionSettings,"连接设置");
            actionTips.SetToolTip(mcpSettings,"MCP接入");
            columns.Items.AddRange(new object[] { "全部", "未分类" }); columns.SelectedIndex = 0;
            status.Items.AddRange(new object[] { "全部", "可访问", "已删除" }); status.SelectedIndex = 0;
            sort.Items.AddRange(new object[] { "发布时间", "浏览量", "点赞数", "喜欢数", "转发量" }); sort.SelectedIndex = 0;
            direction.Items.AddRange(new object[] { "降序", "升序" }); direction.SelectedIndex = 0;
            format.Items.AddRange(new object[] { "正文HTML", "正文Markdown", "正文TXT", "正文PDF", "仅图片", "仅音频", "仅视频", "全部媒体", "全文" }); format.SelectedIndex = 0;
            var title = new WcaeWordmark { Text = "WCAE", AutoSize = true, Font = new Font("Microsoft YaHei UI", 20, FontStyle.Bold), ForeColor = Color.Black, Margin = new Padding(0,2,0,0) };
            var subtitle = new Label { Text = "公众号文章收集平台", AutoSize = true, Font = new Font(Font.FontFamily, 10.5F, Font.Style), ForeColor = Color.FromArgb(105,120,112), Margin = new Padding(0,14,0,0) };
            var all = Button("全选", 60); var none = Button("清除", 60); var reset = Button("重置", 60);
            all.Click += (s,e) => { foreach (var a in filtered) selected.Add(a.Id); RefreshTable(false); };
            none.Click += (s,e) => { selected.Clear(); RefreshTable(false); };
            reset.Click += (s,e) => ResetFilters();
            grid.Dock = DockStyle.Fill; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.RowHeadersVisible = false; grid.AutoGenerateColumns = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = true; grid.AllowUserToResizeRows = false;
            grid.RowTemplate.Height = 35; grid.ColumnHeadersHeight = 38; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(238,244,241);
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor=grid.ColumnHeadersDefaultCellStyle.BackColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor=Color.FromArgb(45,65,55);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45,65,55); grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225,242,233);
            grid.DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleCenter;
            grid.DefaultCellStyle.SelectionForeColor = Color.Black; grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249,251,250);
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Pick", HeaderText = "", DataPropertyName = "Pick", Width = 40, SortMode = DataGridViewColumnSortMode.NotSortable });
            AddColumn("Title", "文章标题", 300, true); AddColumn("Columns", "专栏", 135); AddColumn("Status", "状态", 90);
            AddColumn("ReadCount", "浏览量", 82); AddColumn("LikeCount", "点赞数", 82); AddColumn("FavoriteCount", "喜欢数", 82); AddColumn("ShareCount", "转发量", 82);
            AddColumn("PublishedAt", "发布时间", 155); AddColumn("Author", "作者", 110);
            foreach (DataGridViewColumn c in grid.Columns) if (c.Name != "Pick") c.ReadOnly = true;
            var middle = new TableLayoutPanel { Dock=DockStyle.Fill, Margin=Padding.Empty, Padding=new Padding(12,0,12,0), ColumnCount=1, RowCount=2 };
            middle.RowStyles.Add(new RowStyle(SizeType.Absolute,22)); middle.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            grid.Margin=Padding.Empty; middle.Controls.Add(summary,0,0); middle.Controls.Add(grid,0,1);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, Margin=Padding.Empty, ColumnCount = 1, RowCount = 2, Padding = new Padding(12,4,12,10) };
            var logTitle=new Label { Text="日志", Dock=DockStyle.Fill, TextAlign=ContentAlignment.MiddleLeft, Margin=Padding.Empty, ForeColor=Color.FromArgb(84,96,104) };
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute,24)); bottom.RowStyles.Add(new RowStyle(SizeType.Percent,100)); bottom.Controls.Add(logTitle,0,0); bottom.Controls.Add(log,0,1);
            var capturePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(16,7,16,9), Margin = new Padding(12,0,12,6), BackColor = Color.FromArgb(238,247,242) };
            capturePanel.RowStyles.Add(new RowStyle(SizeType.Percent,100)); capturePanel.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
            capturePanel.Controls.Add(captureProgress,0,0); capturePanel.Controls.Add(captureNotice,0,1);
            var actionToolbar = CenteredRow(start,stop,reconnect,accounts);
            foreach(var button in new[] { start,stop,reconnect })
            {
                button.Margin = Padding.Empty;
                button.MinimumSize = new Size(24,32);
                button.Width = 24;
            }
            accounts.Margin = new Padding(8,0,0,0);
            actionToolbar.ColumnCount=8;
            for(int i=0;i<3;i++)actionToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            mcpSettings.Anchor=AnchorStyles.Right; mcpSettings.Margin=Padding.Empty; actionToolbar.Controls.Add(mcpSettings,5,0);
            connectionSettings.Anchor=AnchorStyles.Right; connectionSettings.Margin=Padding.Empty; actionToolbar.Controls.Add(connectionSettings,6,0);
            clearCache.Anchor=AnchorStyles.Right; clearCache.Margin=Padding.Empty; actionToolbar.Controls.Add(clearCache,7,0);
            // Disabled actions still explain their purpose when hovered.
            IconActionButton disabledTip = null;
            actionToolbar.MouseMove += (s,e) =>
            {
                var target = new[] { start,stop,reconnect,clearCache,mcpSettings,connectionSettings }.FirstOrDefault(button => !button.Enabled && button.Bounds.Contains(e.Location));
                if (target == disabledTip) return;
                actionTips.Hide(actionToolbar); disabledTip = target;
                if (target != null) actionTips.Show(target.AccessibleName,actionToolbar,target.Left,target.Bottom+4,4000);
            };
            actionToolbar.MouseLeave += (s,e) => { actionTips.Hide(actionToolbar); disabledTip = null; };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 8, ColumnCount = 1, Margin = Padding.Empty };
            foreach (var h in new float[] { 54,42,126,36,36,36 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,100));
            var heading=Row(title,subtitle);
            title.Margin=new Padding(0,2,4,3);
            layout.Controls.Add(heading,0,0);
            layout.Controls.Add(actionToolbar,0,1);
            layout.Controls.Add(capturePanel,0,2);
            var filters = new TableLayoutPanel { Dock=DockStyle.Fill, Margin=Padding.Empty, RowCount=1, ColumnCount=2 };
            filters.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,282));
            var filterControls=CenteredRow(Caption("专栏"),columns,Caption("状态"),status,Caption("日期"),from,Caption("至"),through);
            from.Margin=new Padding(0,0,4,0); filterControls.Controls[6].Margin=new Padding(0,0,4,0);
            filters.Controls.Add(filterControls,0,0);
            var titleSearch = CenteredRow(Caption("标题"),search,searchButton);
            titleSearch.Padding=new Padding(0,0,12,0);
            titleSearch.ColumnStyles[1].SizeType=SizeType.Percent; titleSearch.ColumnStyles[1].Width=100;
            titleSearch.ColumnStyles[3].SizeType=SizeType.Absolute; titleSearch.ColumnStyles[3].Width=0;
            search.Anchor=AnchorStyles.Left|AnchorStyles.Right; search.Margin=new Padding(0,0,4,0); searchButton.Margin=Padding.Empty;
            filters.Controls.Add(titleSearch,1,0);
            layout.Controls.Add(filters,0,3);
            layout.Controls.Add(CenteredRow(Caption("排序"),sort,direction,reset,all,none),0,4);
            layout.Controls.Add(CenteredRow(Caption("导出"),format,localImages,overwrite,exportSelected),0,5);
            layout.Controls.Add(middle,0,6); layout.Controls.Add(bottom,0,7); Controls.Add(layout);
            void MatchButtonHeights() { foreach(var button in new[] { reset,all,none,exportSelected }) button.Height=sort.Height; }
            sort.SizeChanged+=(s,e)=>MatchButtonHeights(); MatchButtonHeights();
            var menu = new ContextMenuStrip(); menu.Items.Add("打开原文",null,(s,e) => OpenCurrentArticle()); menu.Items.Add("复制文章链接",null,(s,e) => { var a=CurrentArticle(); if(a!=null) Clipboard.SetText(a.Url); }); grid.ContextMenuStrip = menu;
        }
        void AddColumn(string property, string title, int width, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { Name = property, DataPropertyName = property, HeaderText = title, Width = width, MinimumWidth = width, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (fill) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; c.MinimumWidth = 220; }
            c.DefaultCellStyle.Alignment=property=="Title"?DataGridViewContentAlignment.MiddleLeft:DataGridViewContentAlignment.MiddleCenter;
            if (property.EndsWith("Count")) { c.DefaultCellStyle.Format = "N0"; c.DefaultCellStyle.NullValue = "—"; }
            if (property == "PublishedAt") { c.DefaultCellStyle.Format = "yyyy-MM-dd HH:mm"; c.DefaultCellStyle.NullValue = "—"; }
            grid.Columns.Add(c);
        }
        void WireEvents()
        {
            Shown += async (s,e) =>
            {
                if(preview) return;
                initializationTask=InitializeApplicationAsync(); await initializationTask;
            };
            start.Click += async (s,e) => await StartCollection();
            reconnect.Click += async (s,e) => await ReconnectWechatAsync();
            stop.Click += (s,e) => { StopSharedCollection(); stop.Enabled = false; AppendLog("已发出停止指令，正在取消请求。"); };
            connectionSettings.Click += async (s,e) => { if(settingsTask==null||settingsTask.IsCompleted) { settingsTask=OpenConnectionSettingsAsync(); await settingsTask; } };
            mcpSettings.Click += (s,e) => OpenMcpSettings(this);
            clearCache.Click += async (s,e) => { if(!clearingCache) { clearTask=ClearCacheAsync(); await clearTask; } };
            accounts.SelectedIndexChanged += (s,e) => { if(!binding) SwitchAccount(); };
            foreach(var c in new[] { columns,status }) c.SelectedIndexChanged += (s,e) => ApplyLeftFilters();
            foreach(var c in new[] { sort,direction }) c.SelectedIndexChanged += (s,e) => { if(!binding) RefreshTable(); };
            searchButton.Click += (s,e) => ApplyTitleSearch();
            search.KeyDown += (s,e) => { if(e.KeyCode==Keys.Enter) { e.SuppressKeyPress=true; ApplyTitleSearch(); } };
            from.SelectedDateChanged += (s,e) => ApplyLeftFilters(); through.SelectedDateChanged += (s,e) => ApplyLeftFilters();
            grid.CurrentCellDirtyStateChanged += (s,e) => { if(grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s,e) =>
            {
                if(binding || e.RowIndex<0 || e.ColumnIndex!=0) return;
                var row=grid.Rows[e.RowIndex].DataBoundItem as DataRowView; if(row==null) return;
                var id=(string)row["Id"]; if(Convert.ToBoolean(row["Pick"])) selected.Add(id); else selected.Remove(id); UpdateSummary();
            };
            grid.CellDoubleClick += (s,e) => { if(e.RowIndex>=0 && e.ColumnIndex!=0) OpenCurrentArticle(); };
            grid.ColumnHeaderMouseClick += (s,e) =>
            {
                var names=new[] { "PublishedAt","ReadCount","LikeCount","FavoriteCount","ShareCount" }; var i=Array.IndexOf(names,grid.Columns[e.ColumnIndex].Name);
                if(i<0)return; if(sort.SelectedIndex==i)direction.SelectedIndex=direction.SelectedIndex==0?1:0; else sort.SelectedIndex=i;
            };
            exportSelected.Click += async (s,e) => await BeginExport();
            FormClosing += async (s,e) =>
            {
                if(allowClose)return; e.Cancel=true; if(closing)return; closing=true;
                Hide(); Enabled=false;
                ShutdownStarted?.Invoke(this,EventArgs.Empty);
                healthTimer.Stop(); refreshTimer.Stop();
                lifetime.Cancel(); controller?.Stop(); exportCancellation?.Cancel(); capture?.CancelPendingOperations();
                if(preview) { DisposeCore(); allowClose=true; e.Cancel=false; return; }
                shutdownTask=ShutdownAsync();
                try { await shutdownTask; }
                catch(Exception ex) { System.Diagnostics.Trace.TraceError("WCAE shutdown cleanup: "+ex.GetType().Name); }
                allowClose=true; if(!IsDisposed)Close();
            };
            FormClosed += (s,e) => DisposeUiResources();
        }
        async Task ShutdownAsync()
        {
            var stopMcp=StopMcpAsync();
            var stopTasks=mcpTasks?.ShutdownAsync()??Task.CompletedTask;
            try { await Task.WhenAll(new[] { initializationTask,mcpInitializationTask,stopMcp,stopTasks,listenerTask,collectionTask,exportTask,healthTask,reconnectTask,clearTask,settingsTask }.Where(t=>t!=null).Distinct()); } catch { }
            // Initialization may have created the services after the close click canceled the lifetime.
            if(mcpTasks!=null)try { await mcpTasks.ShutdownAsync(); } catch { }
            controller?.Stop(); capture?.CancelPendingOperations();
            await Task.Run(DisposeCore);
        }
        void DisposeCore()
        {
            if(coreDisposed)return; coreDisposed=true;
            try { controller?.Dispose(); }
            finally
            {
                try { capture?.Dispose(); }
                finally { http?.Dispose(); }
            }
        }
        void DisposeUiResources()
        {
            if(uiResourcesDisposed)return; uiResourcesDisposed=true;
            refreshTimer.Dispose(); healthTimer.Dispose(); actionTips.Dispose(); lifetime.Dispose();
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing && !uiResourcesDisposed)
            {
                lifetime.Cancel();
                if(initializationTask==null || initializationTask.IsCompleted)
                {
                    if(!coreDisposed)DisposeCore();
                    DisposeUiResources();
                }
            }
            base.Dispose(disposing);
        }
        internal void ValidateDeferredConstruction()
        {
            if(preview || coreReady || repository!=null || vault!=null || http!=null || capture!=null || controller!=null || initializationTask!=null)
                throw new InvalidOperationException("主界面显示前提前加载了历史或网络服务。");
            if(start.Enabled || stop.Enabled || clearCache.Enabled || exportSelected.Enabled || grid.RowCount!=0)
                throw new InvalidOperationException("初始化前的操作没有正确隔离。");
        }
        internal void ValidateIsolatedStartupHistory(string articleId)
        {
            if(isolatedConnectionInitializer==null || !coreReady || !records.ContainsKey(articleId) || accounts.Items.Count!=1 || grid.RowCount!=1)
                throw new InvalidOperationException("后台初始化没有完整读取隔离历史。");
            if(capture.Diagnostics.Snapshot.Listening || controller.IsRunning)
                throw new InvalidOperationException("生命周期测试触发了真实监听或自动采集。");
        }
        internal void ValidateAccountNameSynchronization()
        {
            if(isolatedConnectionInitializer==null)throw new InvalidOperationException("名称同步检查仅用于隔离生命周期测试。");
            const string biz="lifecycle-fixture";
            var incoming=sessions[biz].Clone(); var previousName=incoming.Name;
            incoming.Name="data-miniprogram-nickname"; incoming.CapturedAt=DateTime.Now;
            OnRecognized(incoming);
            if(current.Name!=previousName || repository.Accounts()[biz]!=previousName || captureProgress.AccountName!=previousName)
                throw new InvalidOperationException("字段标记覆盖了有效的公众号名称。");
            selected.Add("lifecycle-article");
            const string correctedName="WCAE研究2026";
            var corrected=new ArticleRecord { Id="lifecycle-article",Biz=biz,AccountName=correctedName,Title="后台载入测试",Status=ArticleStatus.Available,
                Html="<script>var biz='lifecycle-fixture';</script><a id='js_name'>WCAE研究2026</a>" };
            repository.Save(corrected);
            var queuedSnapshot=incoming.Clone(); queuedSnapshot.Name=previousName;
            OnSessionUpdated(queuedSnapshot); // A detail save can precede its UI callback.
            OnArticleSaved(corrected);
            if(current.Name!=correctedName || sessions[biz].Name!=correctedName || vault.Load()[biz].Name!=correctedName
                || captureProgress.AccountName!=correctedName || ((AccountItem)accounts.SelectedItem).Name!=correctedName
                || !selected.Contains(corrected.Id))throw new InvalidOperationException("正文名称没有同步界面/缓存，或更新清除了选择。");
            var oldSnapshot=incoming.Clone(); oldSnapshot.Name=previousName;
            OnSessionUpdated(oldSnapshot);
            var oldArticle=new ArticleRecord { Id="lifecycle-article",Biz=biz,AccountName=previousName,Title="后台载入测试",Status=ArticleStatus.Pending };
            repository.Save(oldArticle); OnArticleSaved(oldArticle);
            if(current.Name!=correctedName || repository.AccountName(biz)!=correctedName || captureProgress.AccountName!=correctedName)
                throw new InvalidOperationException("旧会话或无正文旧文章覆盖了已修正的名称。");
            var unnamed=current.Clone(); unnamed.Name=""; unnamed.CapturedAt=DateTime.Now;
            OnSessionUpdated(unnamed);
            OnArticleSaved(new ArticleRecord { Id="other-article",Biz="other-biz",AccountName="Other Account 2026",Status=ArticleStatus.Available });
            if(current.Biz!=biz || current.Name!=correctedName || displayedBiz!=biz || records.Count!=1 || !selected.Contains(corrected.Id))
                throw new InvalidOperationException("缺名刷新或其他公众号的正文更新改变了当前公众号。");
            if(DisplayAccountName("unresolved","data-miniprogram-nickname")!="名称待识别")
                throw new InvalidOperationException("无可信名称时未使用等待提示。");
        }
        void LoadAccounts(string selectBiz = null)
        {
            binding=true;
            var known=repository.Accounts(); accounts.Items.Clear(); foreach(var pair in known) accounts.Items.Add(new AccountItem(pair.Key,pair.Value));
            var index=accounts.Items.Cast<AccountItem>().ToList().FindIndex(x=>x.Biz==(selectBiz??displayedBiz));
            accounts.SelectedIndex=index>=0?index:accounts.Items.Count>0?0:-1; binding=false; SwitchAccount();
        }
        void SwitchAccount()
        {
            if(!coreReady || closing)return;
            var account=accounts.SelectedItem as AccountItem; displayedBiz=account?.Biz??""; records.Clear(); selected.Clear();
            foreach(var a in repository.Load(displayedBiz)) records[a.Id]=a; RefreshTable();
        }
        void OnRecognized(AccountSession s)
        {
            if(!coreReady || closing || clearingCache || s==null || string.IsNullOrEmpty(s.Biz))return;
            current=SaveSession(s); controller.Recognize(current);
            repository.SaveAccount(current.Biz,current.Name);
            start.Enabled=!preview&&!controller.IsRunning&&!HasActiveMcpTask("collection"); RenderCaptureDiagnostics(true);
            if(!controller.IsRunning&&!HasActiveMcpTask("collection")) { LoadAccounts(s.Biz); AppendLog("已识别 "+DisplayAccountName(current.Biz,current.Name)+"，等待点击开始收集。"); }
            else { UpdateAccountItem(current.Biz,current.Name); AppendLog("识别到 "+DisplayAccountName(current.Biz,current.Name)+"；当前仍收集 "+controller.CollectingAccount+"，下次开始时使用新识别的公众号。"); }
        }
        async Task StartCollection()
        {
            if(!coreReady || closing || clearingCache || preview)return;
            if(reconnecting || initializationTask?.IsCompleted==false || listenerTask?.IsCompleted==false) { AppendLog("正在调整网络接入，请完成后再开始收集。"); return; }
            if(controller.IsRunning || HasActiveMcpTask("collection")) { AppendLog("正在收集；请先停止当前任务。"); return; }
            var missing=MissingSessionFields(current);
            if(missing.Count>0)
            {
                RenderCaptureDiagnostics(true);
                string message=current==null ? "尚未识别公众号。请在电脑微信中打开目标公众号的一篇文章并刷新。" : "会话不完整，缺少："+string.Join("、",missing)+"。请在电脑微信中刷新该公众号文章。";
                message+="\r\n"+CaptureReason(capture.Diagnostics.Snapshot)+"\r\n如仍无变化，可点击「重新接入」，然后再次刷新文章。";
                AppendLog(current==null?"尚未识别公众号，未开始收集。":"会话缺少："+string.Join("、",missing)+"，未开始收集。");
                MessageBox.Show(this,message,"开始收集",MessageBoxButtons.OK,MessageBoxIcon.Information);
                return;
            }
            try
            {
                var task=StartSharedCollection(current.Biz,null,"ui-"+Guid.NewGuid().ToString("N"),"ui");
                await mcpTasks.Completion(task.Id);
            }
            catch(McpApplicationException ex) { AppendLog(ex.Message); }
        }
        AccountSession SaveSession(AccountSession incoming, bool acceptNewName = true)
        {
            AccountSession previous;
            sessions.TryGetValue(incoming.Biz,out previous);
            bool older=previous!=null && previous.CapturedAt>incoming.CapturedAt;
            if(older && acceptNewName)return previous.Clone();
            var saved=older?previous.Clone():incoming.Clone();
            saved.Name=acceptNewName ? AccountNameResolver.Choose(saved.Biz,saved.Name,previous?.Name)
                : AccountNameResolver.Choose(saved.Biz,repository.AccountName(saved.Biz),previous?.Name,saved.Name);
            if(previous!=null)
            {
                if(string.IsNullOrWhiteSpace(saved.UserName))saved.UserName=previous.UserName;
                if(string.IsNullOrWhiteSpace(saved.Cookie))saved.Cookie=previous.Cookie;
                if(string.IsNullOrWhiteSpace(saved.Uin))saved.Uin=previous.Uin;
                if(string.IsNullOrWhiteSpace(saved.Key))saved.Key=previous.Key;
                if(string.IsNullOrWhiteSpace(saved.PassTicket))saved.PassTicket=previous.PassTicket;
                if(string.IsNullOrWhiteSpace(saved.AppMsgToken))saved.AppMsgToken=previous.AppMsgToken;
                if(string.IsNullOrWhiteSpace(saved.RequestUrl))saved.RequestUrl=previous.RequestUrl;
            }
            sessions[saved.Biz]=saved.Clone();
            capture.UpdateKnownAccountName(saved.Biz,saved.Name);
            try { vault.Save(sessions); } catch(Exception ex) { AppendLog("会话保存失败："+ex.GetType().Name); }
            return saved;
        }
        void OnSessionUpdated(AccountSession session)
        {
            if(!coreReady || IsDisposed || Disposing || closing || clearingCache || session==null || string.IsNullOrWhiteSpace(session.Biz))return;
            var saved=SaveSession(session,false);
            if(closing)return;
            repository.SaveAccount(saved.Biz,saved.Name);
            UpdateAccountItem(saved.Biz,saved.Name);
            // A background refresh must not select another account or alter the records being viewed.
            if(current?.Biz==saved.Biz && current.CapturedAt<=saved.CapturedAt)
            {
                current=saved.Clone(); controller.Recognize(current);
                RenderCaptureDiagnostics(true);
            }
        }
        void OnArticleSaved(ArticleRecord article)
        {
            if(!coreReady || closing || clearingCache || article==null)return;
            // Save may retain a newer account name than an old collection snapshot contains.
            var name=repository.AccountName(article.Biz);
            article.AccountName=AccountNameResolver.Choose(article.Biz,name,article.AccountName);
            if(name.Length>0)
            {
                if(sessions.TryGetValue(article.Biz,out var session) && session.Name!=name)
                {
                    var updated=session.Clone(); updated.Name=name;
                    // A name correction does not make old credentials newer.
                    var saved=SaveSession(updated);
                    if(current?.Biz==saved.Biz) { current=saved.Clone(); controller.Recognize(current); RenderCaptureDiagnostics(true); }
                }
                UpdateAccountItem(article.Biz,name);
                foreach(var cached in records.Values.Where(a=>a.Biz==article.Biz && AccountNameResolver.Normalize(a.AccountName,a.Biz).Length==0))cached.AccountName=name;
            }
            if(article.Status==ArticleStatus.Available && article.Columns?.Any(ArticleColumnParser.IsPlaceholder)==true
                && (!records.TryGetValue(article.Id,out var previous) || previous.Status!=ArticleStatus.Available || previous.ColumnMetadataVersion<article.ColumnMetadataVersion || previous.ColumnNames!=article.ColumnNames))
                AppendLog("合集名称暂未提取，已保留合集编号："+article.Title);
            if(article.Biz==displayedBiz) { records[article.Id]=article; dirty=true; }
        }
        void UpdateAccountItem(string biz,string name)
        {
            var normalized=AccountNameResolver.Normalize(name,biz);
            for(int i=0;i<accounts.Items.Count;i++)
            {
                if(accounts.Items[i] is not AccountItem item || item.Biz!=biz || item.Name==normalized)continue;
                bool wasBinding=binding; binding=true;
                try { accounts.Items[i]=new AccountItem(biz,normalized); }
                finally { binding=wasBinding; }
                return;
            }
        }
        static string DisplayAccountName(string biz,string name)
        {
            var normalized=AccountNameResolver.Normalize(name,biz);
            return normalized.Length>0?normalized:"名称待识别";
        }
        static List<string> MissingSessionFields(AccountSession session)
        {
            var missing=new List<string>();
            if(string.IsNullOrWhiteSpace(session?.Biz))missing.Add("公众号标识(Biz)");
            if(string.IsNullOrWhiteSpace(session?.Cookie))missing.Add("登录 Cookie");
            if(string.IsNullOrWhiteSpace(session?.Uin))missing.Add("Uin");
            if(string.IsNullOrWhiteSpace(session?.Key))missing.Add("Key");
            return missing;
        }
        async Task CheckCaptureHealthAsync()
        {
            if(!coreReady || preview || closing || healthChecking || reconnecting || listenerTask==null || !listenerTask.IsCompleted)return;
            healthChecking=true;
            try
            {
                capture.Diagnostics.CheckNoRequests(TimeSpan.FromSeconds(15));
                healthTask=Task.Run(() => capture.CheckHealthAsync());
                await healthTask;
                if(!closing)RenderCaptureDiagnostics(true);
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) { if(!closing)AppendLog("接入检测未完成："+ex.GetType().Name); }
            finally { healthChecking=false; }
        }
        async Task ReconnectWechatAsync()
        {
            if(!coreReady || preview || closing || clearingCache || reconnecting || (listenerTask!=null&&!listenerTask.IsCompleted))return;
            if(controller.IsRunning || exportCancellation!=null || HasActiveMcpTask("collection") || HasActiveMcpTask("export")) { AppendLog("请等待收集或导出任务停止后重新接入，已保存的配置将在届时应用。"); return; }
            reconnecting=true; reconnect.Enabled=false; start.Enabled=false; clearCache.Enabled=false; connectionSettings.Enabled=false;
            AppendLog("正在重新接入。"); RenderCaptureDiagnostics(true);
            try
            {
                // Serialize with the health probe; neither operation blocks the UI message loop.
                if(healthTask!=null && !healthTask.IsCompleted)try { await healthTask; } catch { }
                if(closing)return;
                reconnectTask=ConnectConfiguredAsync(true);
                await reconnectTask;
                if(closing)return;
                AppendLog(capture.RouteActive?"重新接入已完成，请在电脑微信中刷新公众号文章。":"重新接入尚未完成，请查看日志后重试。");
                healthTimer.Start();
            }
            catch(OperationCanceledException) { if(!closing)AppendLog("重新接入已取消。"); }
            catch(Exception ex)
            {
                if(!closing)AppendLog("重新接入失败："+ex.GetType().Name);
            }
            finally
            {
                reconnecting=false;
                if(!closing) { reconnect.Enabled=!clearingCache; start.Enabled=!clearingCache&&!preview; clearCache.Enabled=!clearingCache; connectionSettings.Enabled=true; RenderCaptureDiagnostics(true); }
            }
        }
        void RenderCaptureDiagnostics(bool force=false)
        {
            if(!coreReady || preview || closing || IsDisposed)return;
            var snapshot=capture.Diagnostics.Snapshot;
            bool route=capture.RouteActive;
            if(!force && snapshot.Version==lastDiagnosticVersion && route==lastRouteActive)return;
            lastDiagnosticVersion=snapshot.Version; lastRouteActive=route;
            bool connected = snapshot.Listening && route && !reconnecting && snapshot.UpstreamReachable != false;
            captureProgress.SetStages(connected,!string.IsNullOrWhiteSpace(current?.Biz),current?.Name ?? "");
            // The diagnostic service exposes only whitelisted messages and redacted endpoints.
            // Poll a bounded batch instead of dispatching one UI work item per network response.
            var recent=snapshot.RecentEntries.Where(entry=>entry.Sequence>lastDiagnosticSequence).ToArray();
            foreach(var entry in recent.Skip(Math.Max(0,recent.Length-6)))
            {
                string message=entry.Message;
                if(!string.IsNullOrEmpty(entry.Endpoint))message+=" ["+entry.Endpoint+(entry.StatusCode.HasValue?" · HTTP "+entry.StatusCode.Value:"")+"]";
                if(!string.IsNullOrEmpty(entry.ExceptionType))message+=" · "+entry.ExceptionType;
                if(message==lastDiagnosticLog && DateTime.UtcNow-lastDiagnosticLogAt<TimeSpan.FromSeconds(30))continue;
                AppendLog(message); lastDiagnosticLog=message; lastDiagnosticLogAt=DateTime.UtcNow;
            }
            if(recent.Length>0)lastDiagnosticSequence=recent.Max(entry=>entry.Sequence);
        }
        string CaptureReason(CaptureDiagnosticsSnapshot snapshot)
        {
            if(!snapshot.Listening)return "本地接入尚未开启；请等待启动完成，或点击「重新接入」。";
            if(!capture.RouteActive)return "本地监听已开启，但微信分流尚未接通；请点击「重新接入」。";
            if(snapshot.UpstreamReachable==false)return "上游连接不可达；请检查代理服务后重新接入。";
            if(current!=null && MissingSessionFields(current).Count>0)
                return "已识别微信页面中的公众号；会话缺少："+string.Join("、",MissingSessionFields(current))+"。请刷新文章以补全登录会话。";
            if(current!=null && !snapshot.HasObservedTraffic)
                return "已识别微信页面中的公众号；点击「开始收集」后验证会话并采集。";
            if(!snapshot.HasObservedTraffic)return "尚未收到请求；请在电脑微信中打开公众号文章并刷新。只打开微信主窗口不会触发识别。";
            if(snapshot.MpTunnels==0 && snapshot.DecryptedResponses==0)return "已收到代理请求，尚未收到公众号 HTTPS 隧道；请刷新公众号文章，仍无变化时点击「重新接入」。";
            if(snapshot.DecryptedResponses==0)return snapshot.LastFailure==CaptureFailureKind.TlsHandshakeFailed
                ?"公众号隧道已进入，但 TLS 握手失败，尚未取得页面；请查看诊断记录后重新接入。"
                :"公众号隧道已进入，尚未收到解密页面响应；请刷新文章并留意证书或连接提示。";
            if(current==null)
            {
                if(snapshot.LastFailure==CaptureFailureKind.MissingBiz)return "已经解密页面，但未找到公众号标识；请打开一篇公众号文章并刷新。";
                if(snapshot.LastFailure==CaptureFailureKind.FilteredStatus)return "已收到页面响应，但 HTTP 状态不符合识别条件；请确认文章可正常打开后刷新。";
                if(snapshot.LastFailure==CaptureFailureKind.EmptyBody)return "已收到响应，但正文为空且没有可靠缓存关联；请刷新公众号文章。";
                return "已收到解密响应，尚未识别到可用的公众号页面；后台统计请求不会切换公众号。";
            }
            var missing=MissingSessionFields(current);
            if(missing.Count>0)return "已识别公众号，但会话缺少："+string.Join("、",missing)+"；请在电脑微信中刷新该公众号文章。";
            return controller.IsRunning?"正在收集本次开始时的公众号；新识别或后台请求不会切换本轮任务。":"公众号与会话已就绪；点击「开始收集」才会采集。";
        }
        void ApplyLeftFilters()
        {
            if(binding)return;
            titleKeyword=""; search.Clear();
            RefreshTable();
        }
        void ApplyTitleSearch()
        {
            if(binding)return;
            binding=true;
            try
            {
                titleKeyword=search.Text.Trim();
                columns.SelectedIndex=0; status.SelectedIndex=0; from.SelectedDate=null; through.SelectedDate=null;
            }
            finally { binding=false; }
            RefreshTable();
        }
        void ResetFilters()
        {
            binding=true;
            try
            {
                titleKeyword=""; search.Clear(); columns.SelectedIndex=0; status.SelectedIndex=0;
                from.SelectedDate=null; through.SelectedDate=null; sort.SelectedIndex=0; direction.SelectedIndex=0;
            }
            finally { binding=false; }
            RefreshTable();
        }
        void RefreshTable(bool updateColumns = true)
        {
            if(binding || IsDisposed)return; binding=true;
            try
            {
                if(updateColumns)
                {
                    var prior=columns.SelectedItem as string; var names=records.Values.SelectMany(x=>x.Columns).Select(x=>x.Name).Where(x=>!string.IsNullOrEmpty(x)).Distinct().OrderBy(x=>x).ToArray();
                    columns.Items.Clear(); columns.Items.AddRange(new object[]{"全部","未分类"}); columns.Items.AddRange(names); columns.SelectedItem=prior; if(columns.SelectedIndex<0)columns.SelectedIndex=0;
                }
                var f=new ArticleFilter { Column=columns.SelectedIndex>1?(string)columns.SelectedItem:null,UnassignedColumn=columns.SelectedIndex==1,Status=status.SelectedIndex==1?ArticleStatus.Available:status.SelectedIndex==2?ArticleStatus.Deleted:(ArticleStatus?)null,From=from.SelectedDate,Through=through.SelectedDate,Search=titleKeyword,SortBy=new[]{"PublishedAt","ReadCount","LikeCount","FavoriteCount","ShareCount"}[Math.Max(0,sort.SelectedIndex)],Descending=direction.SelectedIndex==0 };
                if(f.From>f.Through)AppendLog("起始日期不能晚于结束日期，请调整日期范围。");
                NormalizeArticleSelection();
                filtered=ArticleQuery.Apply(records.Values,f);
                var table=new DataTable(); table.Columns.Add("Id",typeof(string)); table.Columns.Add("Pick",typeof(bool)); table.Columns.Add("Title",typeof(string)); table.Columns.Add("Columns",typeof(string)); table.Columns.Add("Status",typeof(string));
                foreach(var name in new[]{"ReadCount","LikeCount","FavoriteCount","ShareCount"})table.Columns.Add(name,typeof(long)); table.Columns.Add("PublishedAt",typeof(DateTime)); table.Columns.Add("Author",typeof(string));
                var displayedAccount=accounts.SelectedItem as AccountItem;
                foreach(var a in filtered)table.Rows.Add(a.Id,selected.Contains(a.Id),a.Title,string.IsNullOrEmpty(a.ColumnNames)?"未分类":a.ColumnNames,a.StatusText,(object)a.ReadCount??DBNull.Value,(object)a.LikeCount??DBNull.Value,(object)a.FavoriteCount??DBNull.Value,(object)a.ShareCount??DBNull.Value,(object)a.PublishedAt??DBNull.Value,DisplayArticleAuthor(a,displayedAccount));
                // New records and corrected timestamps may move rows. Keep the visible article,
                // rather than its former row number, anchored during background refreshes.
                var scroll=grid.FirstDisplayedScrollingRowIndex;
                var anchor=scroll>=0 && scroll<grid.RowCount ? (grid.Rows[scroll].DataBoundItem as DataRowView)?["Id"] as string : null;
                var currentId=(grid.CurrentRow?.DataBoundItem as DataRowView)?["Id"] as string;
                var currentColumn=grid.CurrentCell?.OwningColumn?.Name;
                grid.DataSource=table;
                currentId=RepresentativeArticleId(currentId);
                anchor=RepresentativeArticleId(anchor);
                var currentIndex=currentId==null?-1:filtered.FindIndex(a=>a.Id==currentId);
                if(currentIndex>=0 && currentColumn!=null && grid.Columns.Contains(currentColumn) && grid.Columns[currentColumn].Visible)
                    grid.CurrentCell=grid.Rows[currentIndex].Cells[currentColumn];
                var anchoredIndex=anchor==null?-1:filtered.FindIndex(a=>a.Id==anchor);
                if(anchoredIndex>=0)grid.FirstDisplayedScrollingRowIndex=anchoredIndex;
                else if(scroll>=0 && grid.RowCount>0)grid.FirstDisplayedScrollingRowIndex=Math.Min(scroll,grid.RowCount-1);
                UpdateSummary();
            }
            finally { binding=false; }
        }
        string RepresentativeArticleId(string id)
        {
            if(id==null || !records.TryGetValue(id,out var article))return id;
            return representativeIds.TryGetValue((article.Biz??"",article.Id??""),out var representative)?representative:id;
        }
        void NormalizeArticleSelection()
        {
            representativeIds=ArticleQuery.RepresentativeIds(records.Values);
            var mapped=selected.Where(records.ContainsKey).Select(RepresentativeArticleId).ToArray();
            selected.Clear(); selected.UnionWith(mapped);
        }
        void UpdateSummary() { if(IsDisposed||Disposing||closing)return; summary.Text=$"共 {ArticleQuery.ResolveReferences(records.Values).Count} 篇 · {(string.IsNullOrEmpty(titleKeyword)?"筛选":"检索")} {filtered.Count} 篇 · 已选 {selected.Count} 篇"; exportSelected.Enabled=coreReady&&!clearingCache&&exportCancellation==null&&!HasActiveMcpTask("export")&&selected.Count>0; }
        static string DisplayArticleAuthor(ArticleRecord article,AccountItem displayedAccount)
        {
            if(!string.IsNullOrWhiteSpace(article.Author))return article.Author;
            var name=AccountNameResolver.Choose(article.Biz,displayedAccount?.Biz==article.Biz?displayedAccount.Name:null,article.AccountName);
            return DisplayAccountName(article.Biz,name);
        }
        ArticleRecord CurrentArticle() { var row=grid.CurrentRow?.DataBoundItem as DataRowView; ArticleRecord a; return row!=null&&records.TryGetValue((string)row["Id"],out a)?a:null; }
        void OpenCurrentArticle() { var a=CurrentArticle(); Uri uri; if(a!=null && Uri.TryCreate(a.Url,UriKind.Absolute,out uri) && (uri.Scheme=="https"||uri.Scheme=="http"))try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true}); } catch(Exception ex){AppendLog(ex.Message);} }
        async Task BeginExport()
        {
            if(!coreReady || closing || clearingCache || exportCancellation!=null || HasActiveMcpTask("export"))return;
            if(!preview && (reconnecting || initializationTask?.IsCompleted==false || listenerTask?.IsCompleted==false)) { AppendLog("正在调整网络接入，请完成后再导出。"); return; }
            NormalizeArticleSelection();
            var articles=records.Values.Where(x=>selected.Contains(x.Id)).Select(x=>Newtonsoft.Json.JsonConvert.DeserializeObject<ArticleRecord>(Newtonsoft.Json.JsonConvert.SerializeObject(x))).ToList(); if(articles.Count==0)return;
            var exportBiz=articles[0].Biz;
            var options=new ExportOptions { Format=(ExportFormat)format.SelectedIndex,DownloadImages=localImages.Checked,Overwrite=overwrite.Checked };
            string destination;
            using(var dialog=new FolderBrowserDialog { Description="请选择这次导出的保存位置",ShowNewFolderButton=true }) { if(dialog.ShowDialog(this)!=DialogResult.OK)return; destination=dialog.SelectedPath; }
            if(closing)return;
            options.Destination=destination;
            try
            {
                var task=StartSharedExport(exportBiz,articles.Select(a=>a.Id).ToArray(),options,"ui-"+Guid.NewGuid().ToString("N"),"ui");
                await mcpTasks.Completion(task.Id);
            }
            catch(McpApplicationException ex) { AppendLog(ex.Message); }
        }
        async Task ClearCacheAsync()
        {
            if(!coreReady || closing || clearingCache)return;
            clearingCache=true; Interlocked.Increment(ref historyEpoch);
            clearCache.Enabled=false; start.Enabled=false; stop.Enabled=false; reconnect.Enabled=false; accounts.Enabled=false;
            UpdateSummary(); AppendLog("正在清空缓存，等待采集和导出停止写入……");
            IDisposable historySuspension=null;
            try
            {
                historySuspension=capture.SuspendHistory();
                StopSharedCollection(); CancelSharedExport();
                controller.Stop(); exportCancellation?.Cancel();
                try { await Task.WhenAll(new[] { collectionTask,exportTask }.Where(t=>t!=null)); } catch { }
                controller.ClearRecognition();
                await Task.Run(()=> { repository.ClearHistory(); vault.Clear(); });
                mcpQueries?.InvalidateSnapshots(); mcpTasks?.ClearCompleted();
                sessions.Clear(); current=null; records.Clear(); selected.Clear(); filtered.Clear(); displayedBiz=""; dirty=false;
                LoadAccounts(); ResetFilters();
                log.Clear(); AppendLog("缓存已清空：历史公众号、文章及正文记录已移除。");
                RenderCaptureDiagnostics(true);
            }
            catch(Exception ex) { AppendLog("清空缓存未完成："+ex.Message); }
            finally
            {
                Interlocked.Increment(ref historyEpoch); clearingCache=false;
                historySuspension?.Dispose();
                if(!closing)
                {
                    clearCache.Enabled=true; accounts.Enabled=true; start.Enabled=!preview;
                    stop.Enabled=controller.IsRunning;
                    reconnect.Enabled=!preview&&!reconnecting&&(listenerTask==null||listenerTask.IsCompleted);
                    UpdateSummary();
                }
            }
        }
        void QueueHistoryUpdate(Action update)
        {
            int epoch=Volatile.Read(ref historyEpoch);
            Ui(()=> { if(!clearingCache && !closing && epoch==Volatile.Read(ref historyEpoch))update(); });
        }
        void AppendLog(string message) { if(IsDisposed||Disposing||closing)return; if(log.TextLength>35000)log.Text=log.Text.Substring(log.TextLength-20000); log.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+message+Environment.NewLine); }
        void Ui(Action action) { if(IsDisposed||Disposing)return; if(InvokeRequired) { try { BeginInvoke(action); } catch(InvalidOperationException){} } else action(); }
        void FillPreview()
        {
            var s=new AccountSession { Biz="preview",Name="示例公众号" }; repository.SaveAccount(s.Biz,s.Name);
            for(var i=0;i<14;i++)repository.Save(new ArticleRecord { Id="preview:"+i,Biz=s.Biz,AccountName=s.Name,Title=new[]{"城市漫步：沿着河岸发现生活的另一面","本周阅读清单与创作笔记","夏日旅行指南：山野、海风与小城"}[i%3],Mid=i.ToString(),PublishedAt=DateTime.Today.AddDays(-i).AddHours(10),ReadCount=i==2?(long?)null:1680+i*913,LikeCount=26+i*8,FavoriteCount=13+i*2,ShareCount=8+i,Columns=new List<ColumnInfo>{new ColumnInfo{Name=i%2==0?"城市与生活":"阅读与思考"}},Author="编辑部",Status=i==4?ArticleStatus.Restricted:i==5?ArticleStatus.Deleted:ArticleStatus.Available });
            OnRecognized(s); AppendLog("离线预览，未启动监听和收集。"); start.Enabled=false; reconnect.Enabled=false;
            ShowCapturePreview(true,true,s.Name);
        }
        internal void ShowCapturePreview(bool connected, bool identified, string accountName)
        {
            if(!preview)throw new InvalidOperationException("只允许在预览中设置示例状态。");
            captureProgress.SetStages(connected,identified,accountName);
        }
        internal void ShowLastPreviewArticles()
        {
            if(!preview)throw new InvalidOperationException("只允许滚动预览数据。");
            if(grid.RowCount==0)return;
            grid.FirstDisplayedScrollingRowIndex=Math.Max(0,grid.RowCount-Math.Max(1,grid.DisplayedRowCount(false)));
            grid.Refresh();
        }
        public void ValidatePreviewControls()
        {
            if(!preview)throw new InvalidOperationException("只允许在预览中检查控件。");
            if(format.Text!="正文HTML"||status.Text!="全部"||sort.Text!="发布时间"||accounts.Text!="示例公众号")throw new Exception("筛选或导出控件缺少默认选项");
            if(format.Items.Count!=9 || (string)format.Items[(int)ExportFormat.Full]!="全文" || (string)format.Items[(int)ExportFormat.AllMedia]!="全部媒体")throw new Exception("全文导出选项缺失或顺序错误");
            if(grid.RowCount!=14)throw new Exception("示例文章未正确绑定");
            ShowLastPreviewArticles();
            var lastRow=grid.GetRowDisplayRectangle(grid.RowCount-1,false);
            if(!grid.Rows[grid.RowCount-1].Displayed || lastRow.Bottom>grid.ClientSize.Height)throw new Exception("滚动到列表底部未完整显示最后一篇文章");
            grid.FirstDisplayedScrollingRowIndex=0;
            status.SelectedIndex=1;
            if(grid.RowCount!=12 || filtered.Any(a=>a.Status!=ArticleStatus.Available))throw new Exception("可访问状态映射错误");
            status.SelectedIndex=2;
            if(grid.RowCount!=1 || filtered[0].Status!=ArticleStatus.Deleted)throw new Exception("已删除状态映射错误");
            status.SelectedIndex=0; columns.SelectedItem="城市与生活";
            if(grid.RowCount!=7)throw new Exception("专栏筛选未更新界面");
            from.SelectedDate=DateTime.Today.AddDays(1);
            if(grid.RowCount!=0)throw new Exception("日期测试条件未生效");
            search.Text="  城市漫步  ";
            if(grid.RowCount!=0)throw new Exception("输入标题未经提交就更改了筛选结果");
            searchButton.PerformClick();
            if(grid.RowCount!=5 || filtered.Any(a=>!a.Title.Contains("城市漫步")) || columns.SelectedIndex!=0 || status.SelectedIndex!=0 || from.SelectedDate.HasValue || through.SelectedDate.HasValue)throw new Exception("标题检索未独立于左侧筛选");
            sort.SelectedIndex=1;
            if(grid.RowCount!=5)throw new Exception("排序丢失标题检索结果");
            search.Text="不存在的关键词"; RefreshTable(false);
            if(grid.RowCount!=5)throw new Exception("未提交的关键词影响了列表刷新");
            searchButton.PerformClick();
            if(grid.RowCount!=0 || exportSelected.Enabled)throw new Exception("无匹配标题时结果或导出状态错误");
            search.Clear(); searchButton.PerformClick();
            if(grid.RowCount!=14)throw new Exception("空关键词未恢复全部文章");
            search.Text="城市漫步"; searchButton.PerformClick(); status.SelectedIndex=2;
            if(grid.RowCount!=1 || search.Text.Length!=0)throw new Exception("左侧筛选被标题检索限制");
            search.Text="城市漫步"; searchButton.PerformClick();
            from.SelectedDate=DateTime.Today.AddDays(1);
            if(grid.RowCount!=0 || search.Text.Length!=0)throw new Exception("日期未退出标题检索并应用日期范围");
            from.SelectedDate=DateTime.Today.AddDays(-2); through.SelectedDate=DateTime.Today.AddDays(-1);
            if(grid.RowCount!=2)throw new Exception("日期范围未包含两个边界日期");
            from.SelectedDate=null; through.SelectedDate=null;
            if(grid.RowCount!=14 || from.ShowCheckBox || through.ShowCheckBox)throw new Exception("无勾选框的空日期未恢复不限范围");
            ResetFilters();
            // Rebinding after discovering a new article must retain the visible article and focus.
            grid.CurrentCell=grid.Rows[3].Cells["Title"];
            grid.FirstDisplayedScrollingRowIndex=2;
            var visibleId=(string)((DataRowView)grid.Rows[2].DataBoundItem)["Id"];
            var focusedId=CurrentArticle().Id;
            records["preview-new-discovery"]=new ArticleRecord { Id="preview-new-discovery",Biz="preview",Title="新增文章",PublishedAt=DateTime.Today.AddDays(2) };
            RefreshTable(false);
            if((string)((DataRowView)grid.Rows[grid.FirstDisplayedScrollingRowIndex].DataBoundItem)["Id"]!=visibleId || CurrentArticle()?.Id!=focusedId)
                throw new Exception("新文章插入后列表未按文章标识保持滚动位置和焦点");
            records.Remove("preview-new-discovery"); RefreshTable(false);
            var originalPreview=new ArticleRecord { Id="preview:999991:1",Biz="preview",Mid="999991",Url="https://mp.weixin.qq.com/s?__biz=preview&mid=999991&idx=1",Title="可确认的原文",ContentKind=ArticleContentKind.Article,PublishedAt=DateTime.Today };
            var referencePreview=new ArticleRecord { Id="preview:999992:1",Biz="preview",Mid="999992",Title="分享的原文",PublishedAt=DateTime.Today };
            if(!ArticleMetadata.SetReference(referencePreview,"https://mp.weixin.qq.com/s?__biz=preview&mid=999991&idx=1","page:share-original-card"))
                throw new Exception("原文关联测试数据无效");
            records[referencePreview.Id]=referencePreview; selected.Add(referencePreview.Id); RefreshTable(false);
            if(!filtered.Any(a=>a.Id==referencePreview.Id))throw new Exception("原文未收集时分享记录被提前隐藏");
            records[originalPreview.Id]=originalPreview; RefreshTable(false);
            if(filtered.Count!=15 || filtered.Any(a=>a.Id==referencePreview.Id) || !selected.SetEquals(new[]{originalPreview.Id}))
                throw new Exception("确认原文后未合并展示或文章选择未转移到原文");
            var shortIdentityPreview=new ArticleRecord { Id="url:preview-short",Biz="preview",Mid="999991",ResolvedArticleId=originalPreview.Id,Title=originalPreview.Title,ContentKind=ArticleContentKind.Article };
            originalPreview.ResolvedArticleId=originalPreview.Id;
            records[shortIdentityPreview.Id]=shortIdentityPreview; selected.Clear(); selected.Add(shortIdentityPreview.Id); RefreshTable(false);
            if(filtered.Count!=15 || !selected.SetEquals(new[]{originalPreview.Id}))throw new Exception("短长链接身份确认后选择未转移到同一篇文章");
            records.Remove(shortIdentityPreview.Id); records.Remove(referencePreview.Id); records.Remove(originalPreview.Id); selected.Clear(); RefreshTable(false);
            columns.SelectedIndex=0; sort.SelectedIndex=1; direction.SelectedIndex=1;
            if(filtered.Last().ReadCount.HasValue)throw new Exception("缺失统计值未置后");
            grid.Rows[0].Cells[0].Value=true; grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            if(selected.Count!=1||!exportSelected.Enabled)throw new Exception("选择文章未启用导出");
            if(controller.IsRunning)throw new Exception("预览或筛选触发了收集");
            // Work posted before clearing must not recreate history afterward.
            var staleUpdate=new Thread(()=>QueueHistoryUpdate(()=>repository.SaveAccount("stale","旧识别")));
            staleUpdate.Start(); if(!staleUpdate.Join(2000))throw new Exception("历史回调排队超时");
            clearCache.PerformClick();
            var clearDeadline=DateTime.UtcNow.AddSeconds(4);
            while(clearingCache && DateTime.UtcNow<clearDeadline) { Application.DoEvents(); Thread.Sleep(5); }
            Application.DoEvents();
            if(clearingCache || repository.Accounts().Count!=0 || repository.Load("preview").Count!=0 || sessions.Count!=0 || vault.Load().Count!=0 || accounts.Items.Count!=0 || grid.RowCount!=0 || selected.Count!=0 || current!=null)throw new Exception("清空后历史记录或旧回调仍存在");
            if(capture.Diagnostics.Snapshot.Listening || controller.IsRunning)throw new Exception("预览清空触发了接入或收集");
        }
        sealed class AccountItem { public string Biz {get;} public string Name {get;} public AccountItem(string biz,string name){Biz=biz;Name=AccountNameResolver.Normalize(name,biz);} public override string ToString()=>DisplayAccountName(Biz,Name); }
    }
}
