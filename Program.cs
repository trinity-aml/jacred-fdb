using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Globalization;
using System.Text;
using System.Threading;
using JacRed.Engine;

namespace JacRed
{
    public class Program
    {
        public static void Main(string[] args)
        {
            ThreadPool.QueueUserWorkItem(async _ => await SyncCron.Run());
            ThreadPool.QueueUserWorkItem(async _ => await StatsCron.Run());

            ThreadPool.QueueUserWorkItem(async _ => await TracksCron.Run(1));
            ThreadPool.QueueUserWorkItem(async _ => await TracksCron.Run(2));
            ThreadPool.QueueUserWorkItem(async _ => await TracksCron.Run(3));
            ThreadPool.QueueUserWorkItem(async _ => await TracksCron.Run(4));

            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseKestrel(op => op.Listen((AppInit.conf.listenip == "any" ? IPAddress.Any : IPAddress.Parse(AppInit.conf.listenip)), AppInit.conf.listenport))
                    .UseStartup<Startup>();
                });
    }
}
