using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlatformLevelTechempower
{
    public partial class Program
    {
        public static Task Main(string[] args)
        {
            var parsedArgs = Args.Parse(args);

            //IServerApplication app;

            //if (parsedArgs.Raw)
            //{
            //    app = new PlainTextRawApplication();
            //}
            //else
            //{
            //    app = new PlainTextApplication();
            //}

            //return app.RunAsync(parsedArgs.Port);

            var server = new HttpServer(new BenchmarkHandler());
            return server.RunAsync(parsedArgs.Port);
        }
    }
}
