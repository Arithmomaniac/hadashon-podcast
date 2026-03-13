using Azure.Data.Tables;
using Azure.Storage.Blobs;
using HadashonPodcast.Functions.Models;
using HadashonPodcast.Functions.Scrapers;
using HadashonPodcast.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace HadashonPodcast.Functions;

/// <summary>
/// HTTP-triggered version for local testing and manual runs.
/// GET /api/scrape — runs the full scrape-and-publish pipeline on demand.
/// </summary>
public class ManualTriggerFunction(
    HadashonScraper scraper,
    PodcastFeedGenerator feedGenerator,
    TableServiceClient tableService,
    BlobServiceClient blobService,
    ILogger<ManualTriggerFunction> logger)
{
    private const string EpisodesTable = "episodes";

    [Function("ManualScrape")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "scrape")] HttpRequestData req)
    {
        logger.LogInformation("Manual scrape triggered");

        try
        {
            var table = tableService.GetTableClient(EpisodesTable);
            await table.CreateIfNotExistsAsync();

            var homepageEpisodes = await scraper.ScrapeHomepageAsync();
            var articleEpisodes = await scraper.ScrapeArticlesAsync(maxPages: 1);

            var allNew = homepageEpisodes.Concat(articleEpisodes).ToList();
            foreach (var episode in allNew)
            {
                await scraper.PopulateAudioMetadataAsync(episode);
                // Re-derive RowKey from the authoritative publish date
                var slug = episode.RowKey.Contains('_') ? episode.RowKey[(episode.RowKey.IndexOf('_') + 1)..] : episode.RowKey;
                episode.RowKey = $"{episode.PublishDate:yyyy-MM-dd}_{slug}";
                await table.UpsertEntityAsync(episode, TableUpdateMode.Merge);
            }

            // Read all and generate feed
            var allEpisodes = new List<EpisodeEntity>();
            await foreach (var entity in table.QueryAsync<EpisodeEntity>())
                allEpisodes.Add(entity);

            var staticWebsiteUrl = Environment.GetEnvironmentVariable("StaticWebsiteUrl");
            var storageAccount = Environment.GetEnvironmentVariable("StorageAccountName") ?? "hadashonst";
            var baseUrl = string.IsNullOrWhiteSpace(staticWebsiteUrl)
                ? $"https://{storageAccount}.z6.web.core.windows.net"
                : staticWebsiteUrl.TrimEnd('/');
            var selfUrl = $"{baseUrl}/feed.xml";
            var feedXml = feedGenerator.GenerateFeed(allEpisodes, selfUrl: selfUrl);

            // Write to blob
            var container = blobService.GetBlobContainerClient("$web");
            await container.CreateIfNotExistsAsync();
            var blob = container.GetBlobClient("feed.xml");
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(feedXml));
            await blob.UploadAsync(stream, new Azure.Storage.Blobs.Models.BlobHttpHeaders
            {
                ContentType = "application/rss+xml; charset=utf-8"
            });

            // Return the feed as response for easy inspection
            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/rss+xml; charset=utf-8");
            await response.WriteStringAsync(feedXml);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ManualScrape failed");
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            errorResponse.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            await errorResponse.WriteStringAsync("Manual scrape failed. Check Application Insights logs for details.");
            return errorResponse;
        }
    }
}
