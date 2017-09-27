using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Internal.System.IO.Pipelines;

namespace PlatformLevelTechempower
{
    public class BenchmarkHandler : Handler
    {
        private static readonly byte[] _plainTextBody = Encoding.UTF8.GetBytes("Hello, World!");

        private static class Paths
        {
            public static readonly byte[] Plaintext = Encoding.ASCII.GetBytes("/plaintext");
            public static readonly byte[] Json = Encoding.ASCII.GetBytes("/json");
        }

        public override Task<HttpStatus> ProcessAsync(HttpMethod method, byte[] path, byte[] query, bool keepAlive, WritableBuffer output)
        {
            if (method == HttpMethod.Get)
            {
                if (PathMatch(path, Paths.Plaintext))
                {
                    return Task.FromResult(Ok(output, keepAlive, _plainTextBody, MediaType.TextPlain));
                }
                else if (PathMatch(path, Paths.Json))
                {
                    return Task.FromResult(Json(output, keepAlive, new { message = "Hello, World!" }));
                }
            }

            return Task.FromResult(NotFound(output, keepAlive));
        }
    }
}
