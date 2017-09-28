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

        public override Task ProcessAsync(HandlerContext context)
        {
            if (context.Method == HttpMethod.Get)
            {
                if (PathMatch(context.Path, Paths.Plaintext))
                {
                    Ok(context, _plainTextBody, MediaType.TextPlain);

                    return Task.CompletedTask;
                }
                else if (PathMatch(context.Path, Paths.Json))
                {
                    Json(context, new { message = "Hello, World!" });
                    return Task.CompletedTask;
                }
            }

            NotFound(context);
            return Task.CompletedTask;
        }
    }
}
