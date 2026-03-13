using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using HadashonPodcast.Functions.Scrapers;
using HadashonPodcast.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Storage clients: use managed identity in Azure, connection string locally
var storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
if (!string.IsNullOrEmpty(storageConnectionString))
{
    // Local dev with Azurite or explicit connection string
    builder.Services.AddSingleton(new TableServiceClient(storageConnectionString));
    builder.Services.AddSingleton(new BlobServiceClient(storageConnectionString));
}
else
{
    // Azure: use managed identity via DefaultAzureCredential
    var accountName = Environment.GetEnvironmentVariable("StorageAccountName")
        ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage__accountName")
        ?? "hadashonst";
    var credential = new DefaultAzureCredential();
    builder.Services.AddSingleton(new TableServiceClient(
        new Uri($"https://{accountName}.table.core.windows.net"), credential));
    builder.Services.AddSingleton(new BlobServiceClient(
        new Uri($"https://{accountName}.blob.core.windows.net"), credential));
}

// HTTP client for scraping
builder.Services.AddHttpClient<HadashonScraper>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0 Safari/537.36");
    client.DefaultRequestHeaders.Accept.ParseAdd(
        "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("he-IL,he;q=0.9,en-US;q=0.8,en;q=0.7");
});

// Services
builder.Services.AddSingleton<PodcastFeedGenerator>();

builder.Build().Run();
