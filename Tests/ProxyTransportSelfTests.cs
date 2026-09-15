using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    public static class ProxyTransportSelfTests
    {
        public static async Task RunAsync()
        {
            foreach (var protocol in new[] { ProxyProtocol.Http, ProxyProtocol.Socks5 }) await CheckTransportAsync(protocol);
            using var transport = new HttpTransport();
            transport.UseProxy(new ProxyEndpoint { Host="127.0.0.1", Port=19876, Protocol=ProxyProtocol.Socks5, Username="test", Password="p@ss" });
            var environment = new ExportService(transport).ProxyEnvironment();
            Check(environment["https_proxy"] == "socks5://test:p%40ss@127.0.0.1:19876", "媒体工具未跟随手动代理或认证编码不正确");
            transport.UseProxy(null);
            Check(new ExportService(transport).ProxyEnvironment().Values.All(x=>x==""), "直连媒体工具仍继承外部代理环境");
            Check(CaptureService.CreateExternalProxy(null)==null, "直连监听错误使用代理");
            var socks=CaptureService.CreateExternalProxy(new ProxyEndpoint { Host="example.invalid", Port=54321, Protocol=ProxyProtocol.Socks5, Username="user", Password="secret" });
            Check(socks.HostName=="example.invalid" && socks.Port==54321 && socks.ProxyType==Titanium.Web.Proxy.Models.ExternalProxyType.Socks5 && socks.ProxyDnsRequests && socks.UserName=="user", "监听上游协议/地址/认证没有正确传递");
        }
        static async Task CheckTransportAsync(ProxyProtocol protocol)
        {
            var listener = new TcpListener(IPAddress.Loopback,0); listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                int port=((IPEndPoint)listener.LocalEndpoint).Port;
                var serve=Task.Run(async()=>
                {
                    using var client=await listener.AcceptTcpClientAsync(timeout.Token);
                    var stream=client.GetStream();
                    if(protocol==ProxyProtocol.Socks5)
                    {
                        var hello=await ReadAsync(stream,2,timeout.Token);
                        Check(hello[0]==5,"SOCKS5版本错误");
                        await ReadAsync(stream,hello[1],timeout.Token);
                        await stream.WriteAsync(new byte[]{5,0},timeout.Token);
                        var connect=await ReadAsync(stream,4,timeout.Token);
                        Check(connect[0]==5&&connect[1]==1&&connect[3]==3,"SOCKS5没有使用代理端域名解析");
                        int length=(await ReadAsync(stream,1,timeout.Token))[0];
                        Check(Encoding.ASCII.GetString(await ReadAsync(stream,length,timeout.Token))=="wcae-target.invalid","SOCKS5转发目标错误");
                        var remotePort=await ReadAsync(stream,2,timeout.Token); Check(remotePort[0]*256+remotePort[1]==80,"SOCKS5目标端口错误");
                        await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,80},timeout.Token);
                    }
                    var request=new StringBuilder();
                    while(!request.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal))
                    { request.Append((char)(await ReadAsync(stream,1,timeout.Token))[0]); Check(request.Length<16384,"HTTP请求头过长"); }
                    Check(request.ToString().Contains(protocol==ProxyProtocol.Http?"GET http://wcae-target.invalid/article":"GET /article"),"HTTP未通过选择的代理发出");
                    byte[] body=Encoding.ASCII.GetBytes("proxy-ok");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\nproxy-ok"),timeout.Token);
                },timeout.Token);
                using var transport=new HttpTransport();
                var selected=new ProxyEndpoint { Host="127.0.0.1",Port=port,Protocol=protocol };
                transport.UseProxy(selected); selected.Port=1;
                Check(await transport.GetStringAsync("http://wcae-target.invalid/article",null,timeout.Token)=="proxy-ok", "HTTP/SOCKS5手动端口未实际用于文章请求");
                await serve;
            }
            finally { listener.Stop(); }
        }
        static async Task<byte[]> ReadAsync(Stream stream,int count,CancellationToken token)
        { var data=new byte[count]; await stream.ReadExactlyAsync(data,token); return data; }
        static void Check(bool condition,string message) { if(!condition)throw new InvalidOperationException(message); }
    }
}
