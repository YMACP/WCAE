using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WCAE
{
    internal sealed class ConnectionSettingsForm : Form
    {
        private readonly ComboBox mode = Dropdown(), ingress = Dropdown(), protocol = Dropdown();
        private readonly TextBox host = new TextBox { Text = "127.0.0.1", Dock = DockStyle.Fill };
        private readonly NumericUpDown port = new NumericUpDown { Minimum = 0, Maximum = 65535, Dock = DockStyle.Fill };
        private readonly ProxyEndpoint preservedManualEndpoint;
        private readonly Label result = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(84,104,93), AutoEllipsis = true, Visible = false, Margin = new Padding(0,4,0,8) };
        private readonly RowStyle resultRow = new RowStyle(SizeType.Absolute,0);
        private readonly Button test = new Button { Text = "测试连接", AutoSize = true }, save = new Button { Text = "保存并应用", AutoSize = true };
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly int capturePort;
        private bool testing;
        internal ProxyConfiguration SelectedConfiguration { get; private set; }

        internal static void RenderPreview(string path, bool manual)
        {
            var configuration=new ProxyConfiguration();
            if(manual)configuration=new ProxyConfiguration { Mode=ProxyMode.Manual, ManualEndpoint=new ProxyEndpoint { Host="127.0.0.1", Port=10809, Protocol=ProxyProtocol.Socks5, Username="example", Password="preview" } };
            using(var form=new ConnectionSettingsForm(configuration))
            {
                form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-32000,-32000);
                form.Show(); Application.DoEvents();
                using(var bitmap=new Bitmap(form.Width,form.Height))
                {
                    form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));
                    bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
                }
                form.Close(); Application.DoEvents();
            }
        }

        internal ConnectionSettingsForm(ProxyConfiguration initial, int capturePort = 8879, string currentConnection = "")
        {
            this.capturePort = capturePort;
            initial = initial?.Clone() ?? new ProxyConfiguration();
            preservedManualEndpoint=initial.ManualEndpoint?.Clone();
            Text = "连接设置"; Icon = AppBranding.CreateApplicationIcon(); Font = new Font("Microsoft YaHei UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; BackColor = Color.White;
            ClientSize = new Size(330, 241);
            mode.Items.AddRange(new object[] { "自动检测", "手动配置", "直接连接" });
            ingress.Items.AddRange(new object[] { "自动选择", "按进程接入", "系统代理兼容" });
            protocol.Items.AddRange(new object[] { "HTTP", "SOCKS5" });
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16,16,16,14), ColumnCount = 2, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,66)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for (int i = 0; i < 5; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,35));
            AddRow(layout, 0, "网络出口", mode); AddRow(layout, 1, "微信接入", ingress); AddRow(layout, 2, "代理协议", protocol);
            AddRow(layout, 3, "代理地址", host); AddRow(layout, 4, "代理端口", port);
            layout.RowStyles.Add(resultRow); layout.Controls.Add(result,0,5); layout.SetColumnSpan(result,2);
            var buttons = new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.RightToLeft, WrapContents=false, Margin=Padding.Empty };
            var cancel = new Button { Text="取消", AutoSize=true, DialogResult=DialogResult.Cancel };
            buttons.Controls.Add(cancel); buttons.Controls.Add(save); buttons.Controls.Add(test);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); layout.Controls.Add(buttons,0,6); layout.SetColumnSpan(buttons,2);
            Controls.Add(layout); AcceptButton=save; CancelButton=cancel;
            mode.SelectedIndex=(int)initial.Mode; ingress.SelectedIndex=(int)initial.IngressMode;
            protocol.SelectedIndex=(int)(initial.ManualEndpoint?.Protocol ?? ProxyProtocol.Http);
            if(initial.ManualEndpoint != null)
            {
                host.Text=initial.ManualEndpoint.Host; port.Value=Math.Clamp(initial.ManualEndpoint.Port,0,65535);
            }
            mode.SelectedIndexChanged+=(s,e)=>UpdateMode(); UpdateMode();
            test.Click+=async(s,e)=>await TestConnectionAsync();
            save.Click+=(s,e)=>
            {
                try { SelectedConfiguration=ReadConfiguration(); DialogResult=DialogResult.OK; Close(); }
                catch(ArgumentException ex) { ShowResult(ex.Message,Color.Firebrick); }
            };
            FormClosing+=(s,e)=>lifetime.Cancel();
            FormClosed+=(s,e)=>lifetime.Dispose();
        }
        private static ComboBox Dropdown() => new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, Dock=DockStyle.Fill };
        private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
        {
            control.AccessibleName=label;
            layout.Controls.Add(new Label { Text=label, Anchor=AnchorStyles.Left, AutoSize=true, Margin=Padding.Empty },0,row);
            control.Anchor=AnchorStyles.Left|AnchorStyles.Right; control.Dock=DockStyle.None; control.Margin=new Padding(0,3,0,3);
            layout.Controls.Add(control,1,row);
        }
        private void UpdateMode()
        {
            bool manual=mode.SelectedIndex==(int)ProxyMode.Manual && !testing;
            foreach(var control in new Control[] { host,port,protocol })control.Enabled=manual;
            test.Text=mode.SelectedIndex==(int)ProxyMode.Auto?"重新检测":"测试连接";
        }
        private ProxyConfiguration ReadConfiguration()
        {
            // Removing credential fields is a presentation change. Keep existing protected
            // credentials, including when a user temporarily selects automatic or direct mode.
            var configuration=new ProxyConfiguration { Mode=(ProxyMode)mode.SelectedIndex, IngressMode=(CaptureIngressMode)ingress.SelectedIndex, ManualEndpoint=preservedManualEndpoint?.Clone() };
            if(configuration.Mode==ProxyMode.Manual)
            {
                configuration.ManualEndpoint ??= new ProxyEndpoint();
                configuration.ManualEndpoint.Host=host.Text.Trim();
                configuration.ManualEndpoint.Port=(int)port.Value;
                configuration.ManualEndpoint.Protocol=(ProxyProtocol)protocol.SelectedIndex;
            }
            configuration.Validate(capturePort); return configuration;
        }
        private void ShowResult(string message, Color color)
        {
            float previousHeight=resultRow.Height;
            int width=Math.Max(100,ClientSize.Width-(int)Math.Round(32*DeviceDpi/96f));
            int textHeight=TextRenderer.MeasureText(message??"",result.Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak).Height;
            int minimum=(int)Math.Round(44*DeviceDpi/96f), maximum=(int)Math.Round(110*DeviceDpi/96f);
            int nextHeight=string.IsNullOrEmpty(message)?0:Math.Clamp(textHeight+(int)Math.Round(12*DeviceDpi/96f),minimum,maximum);
            SuspendLayout();
            try
            {
                result.Text=message??""; result.ForeColor=color; result.Visible=nextHeight>0;
                resultRow.Height=nextHeight;
                ClientSize=new Size(ClientSize.Width,ClientSize.Height+nextHeight-(int)Math.Round(previousHeight));
            }
            finally { ResumeLayout(true); }
        }
        private async Task TestConnectionAsync()
        {
            if(testing)return;
            try
            {
                var config=ReadConfiguration(); testing=true; test.Enabled=false; save.Enabled=false; mode.Enabled=false; ingress.Enabled=false; UpdateMode();
                ShowResult("正在测试连接……",Color.FromArgb(84,104,93));
                var found=config.Mode==ProxyMode.Direct ? await ProxyDiscovery.ProbeDirectAsync(lifetime.Token)
                    : await ProxyDiscovery.ResolveAsync(config,capturePort,lifetime.Token);
                if(IsDisposed||Disposing)return;
                var message=found.Success && config.Mode==ProxyMode.Auto && found.Endpoint!=null
                    ? "已自动识别并验证代理："+found.Endpoint.DisplayName : found.Message;
                ShowResult(message,found.Success?Color.FromArgb(43,130,78):Color.Firebrick);
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) { if(!IsDisposed&&!Disposing)ShowResult(ex is ArgumentException?ex.Message:"连接测试失败："+ex.GetType().Name,Color.Firebrick); }
            finally
            {
                testing=false;
                if(!IsDisposed&&!Disposing) { test.Enabled=true; save.Enabled=true; mode.Enabled=true; ingress.Enabled=true; UpdateMode(); }
            }
        }
    }
}
