using Azure.Data.Tables;
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

// Storage clients (use connection string from AzureWebJobsStorage)
var storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
    ?? "UseDevelopmentStorage=true";

builder.Services.AddSingleton(new TableServiceClient(storageConnectionString));
builder.Services.AddSingleton(new BlobServiceClient(storageConnectionString));

// HTTP client for scraping
builder.Services.AddHttpClient<HadashonScraper>();

// Services
builder.Services.AddSingleton<PodcastFeedGenerator>();

builder.Build().Run();
