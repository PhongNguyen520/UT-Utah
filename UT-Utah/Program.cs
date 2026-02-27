using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UT_Utah.Models;
using UT_Utah.Services;

var isApify = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APIFY_CONTAINER_PORT"));

Console.WriteLine("Installing Chromium (if needed)...");
Microsoft.Playwright.Program.Main(["install", "chromium"]);

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<UtUtahScraperService>();
    })
    .Build();

// Local: apify_storage/key_value_stores/default/INPUT.json or input.json. Apify: APIFY_INPUT_VALUE (Actor.GetInputAsync equivalent).
var config = await ApifyHelper.GetInputAsync<InputConfig>();
var service = host.Services.GetRequiredService<UtUtahScraperService>();

try
{
    Console.WriteLine("Launching Utah scraper with input config...");
    await service.RunAsync(config);
    Console.WriteLine("Done.");
    if (!isApify)
    {
        Console.WriteLine("Press Enter to exit...");
        Console.ReadLine();
    }
}
finally
{
    await service.StopAsync();
}
