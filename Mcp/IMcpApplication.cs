using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WCAE
{
    internal interface IMcpApplication
    {
        Task<object> InvokeAsync(string tool, JsonElement arguments, CancellationToken cancellation);
    }

    internal sealed class McpApplicationException : Exception
    {
        internal string Code { get; }
        internal McpApplicationException(string code, string message) : base(message) { Code = code; }
    }
}
