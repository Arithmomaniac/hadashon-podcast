using Azure.Data.Tables;
using Azure.Storage.Blobs;
using HadashonPodcast.Functions.Models;
using HadashonPodcast.Functions.Scrapers;
using HadashonPodcast.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace HadashonPodcast.Functions;

public class ScrapeAndPublishFunction(
    HadashonScraper scraper,
    PodcastFeedGenerator feedGenerator,
    TableServiceClient tableService,
    BlobServiceClient blobService,
    ILogger<ScrapeAndPublishFunction> logger)
{
    private const string EpisodesTable = "episodes";
    private const string FeedContainer = "$web";
    private const string FeedBlobName = "feed.xml";

    [Function("ScrapeAndPublish")]
    public async Task Run([TimerTrigger("0 0 */3 * * *")] TimerInfo timerInfo)
    {
        logger.LogInformation("ScrapeAndPublish triggered at {Time}", DateTimeOffset.UtcNow);

        var table = tableService.GetTableClient(EpisodesTable);
        await table.CreateIfNotExistsAsync();

        // 1. Scrape homepage (3 daily segments)
        var homepageEpisodes = await scraper.ScrapeHomepageAsync();

        // 2. Scrape latest articles (page 1 only for regular runs)
        var articleEpisodes = await scraper.ScrapeArticlesAsync(maxPages: 1);

        // 3. Upsert all new episodes to Table Storage
        var allNew = homepageEpisodes.Concat(articleEpisodes).ToList();
        var upserted = 0;
        foreach (var episode in allNew)
        {
            await scraper.PopulateAudioMetadataAsync(episode);
            // Re-derive RowKey from the authoritative publish date
            var slug = episode.RowKey.Contains('_') ? episode.RowKey[(episode.RowKey.IndexOf('_') + 1)..] : episode.RowKey;
            episode.RowKey = $"{episode.PublishDate:yyyy-MM-dd}_{slug}";

            await table.UpsertEntityAsync(episode, TableUpdateMode.Merge);
            upserted++;
        }
        logger.LogInformation("Upserted {Count} episodes to Table Storage", upserted);

        // 4. Read all episodes from Table Storage and generate RSS
        var allEpisodes = new List<EpisodeEntity>();
        await foreach (var entity in table.QueryAsync<EpisodeEntity>())
        {
            allEpisodes.Add(entity);
        }
        logger.LogInformation("Total episodes in store: {Count}", allEpisodes.Count);

        // 5. Generate RSS feed
        var feedXml = feedGenerator.GenerateFeed(allEpisodes, selfUrl: GetFeedUrl());

        // 6. Upload to Blob Storage ($web container for static website)
        var container = blobService.GetBlobContainerClient(FeedContainer);
        await container.CreateIfNotExistsAsync();
        var blob = container.GetBlobClient(FeedBlobName);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(feedXml));
        await blob.UploadAsync(stream, new Azure.Storage.Blobs.Models.BlobHttpHeaders
        {
            ContentType = "application/rss+xml; charset=utf-8"
        });

        logger.LogInformation("Published feed.xml with {Count} episodes", allEpisodes.Count);
    }

    private static string GetFeedUrl()
    {
        var staticWebsiteUrl = Environment.GetEnvironmentVariable("StaticWebsiteUrl");
        if (!string.IsNullOrWhiteSpace(staticWebsiteUrl))
            return $"{staticWebsiteUrl.TrimEnd('/')}/{FeedBlobName}";

        var storageAccount = Environment.GetEnvironmentVariable("StorageAccountName") ?? "hadashonst";
        return $"https://{storageAccount}.z6.web.core.windows.net/{FeedBlobName}";
    }
}
