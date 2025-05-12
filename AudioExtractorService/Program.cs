using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AudioExtractorService;

public class Program
{
    public static void Main(string[] args)
    {
        IHost host = Host.CreateDefaultBuilder(args)
            .UseWindowsService(options =>
            {
                options.ServiceName = "Audio Extractor Service";
            })
            .ConfigureServices(services =>
            {
                services.AddHostedService<AudioExtractorWorker>();
            })
            .Build();

        host.Run();
    }
}