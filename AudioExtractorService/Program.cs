using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var isConsole = args.Contains("--console", StringComparer.OrdinalIgnoreCase);

var builder = Host.CreateDefaultBuilder(args);

if (!isConsole)
{
    builder = builder.UseWindowsService();
}

builder
    .ConfigureLogging((context, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole(); // Optional: keep for debugging when running as console
        logging.AddEventLog(settings =>
        {
            settings.SourceName = "AudioExtractorService";
            settings.LogName = "Application";
        });
    })
    .ConfigureServices((hostContext, services) =>
    {
        services.AddHostedService<AudioExtractorService.AudioExtractorWorker>();
        // Add other services/configuration as needed
    })
    .Build()
    .Run();
