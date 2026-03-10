using System.Net;
using System.Web;
using HtmlAgilityPack;
using HadashonPodcast.Functions.Models;
using Microsoft.Extensions.Logging;

namespace HadashonPodcast.Functions.Scrapers;

public class HadashonScraper(HttpClient httpClient, ILogger<HadashonScraper> logger)
{
    private const string BaseUrl = "https://hadshon.education.gov.il";

    /// <summary>
    /// Scrapes the homepage for the 3 daily audio segments:
    /// daily broadcast, weather forecast, parasha/shabbat.
    /// </summary>
    public async Task<List<EpisodeEntity>> ScrapeHomepageAsync()
    {
        var episodes = new List<EpisodeEntity>();
        var html = await httpClient.GetStringAsync(BaseUrl + "/");
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var audioContainers = doc.DocumentNode.SelectNodes("//div[contains(@class, 'audio-player-container')]");
        if (audioContainers is null) return episodes;

        var today = DateTimeOffset.UtcNow;
        var dateKey = today.ToString("yyyy-MM-dd");

        for (int i = 0; i < audioContainers.Count; i++)
        {
            var source = audioContainers[i].SelectSingleNode(".//source[@src]");
            if (source is null) continue;

            var audioUrl = source.GetAttributeValue("src", "");
            if (string.IsNullOrEmpty(audioUrl)) continue;

            // Determine content type from filename pattern
            var (contentType, title) = ClassifyHomepageAudio(audioUrl, i);

            // Extract the text content that follows this audio player
            var textContent = ExtractFollowingText(audioContainers[i]);

            var episode = new EpisodeEntity
            {
                PartitionKey = contentType,
                RowKey = $"{dateKey}_{contentType}",
                Title = title,
                PublishDate = today,
                AudioUrl = audioUrl,
                ArticleUrl = BaseUrl + "/",
                FullText = textContent,
                ContentType = contentType,
                AudioContentType = audioUrl.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                    ? "audio/mpeg" : "audio/mp4",
            };
            episodes.Add(episode);
        }

        logger.LogInformation("Scraped {Count} homepage audio segments", episodes.Count);
        return episodes;
    }

    /// <summary>
    /// Scrapes the articles listing page and individual article pages.
    /// </summary>
    public async Task<List<EpisodeEntity>> ScrapeArticlesAsync(int maxPages = 1)
    {
        var episodes = new List<EpisodeEntity>();

        for (int page = 1; page <= maxPages; page++)
        {
            var listUrl = $"{BaseUrl}/articles/?page={page}";
            var listHtml = await httpClient.GetStringAsync(listUrl);
            var listDoc = new HtmlDocument();
            listDoc.LoadHtml(listHtml);

            var articleLinks = listDoc.DocumentNode
                .SelectNodes("//a[contains(@href, '/articles/') and not(contains(@href, '/articles/?'))]")
                ?.Select(a => a.GetAttributeValue("href", ""))
                .Where(href => href.StartsWith("/articles/") && href.Count(c => c == '/') >= 3)
                .Distinct()
                .ToList() ?? [];

            foreach (var href in articleLinks)
            {
                try
                {
                    var episode = await ScrapeArticlePageAsync(href);
                    if (episode is not null)
                        episodes.Add(episode);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to scrape article {Href}", href);
                }
            }
        }

        logger.LogInformation("Scraped {Count} articles", episodes.Count);
        return episodes;
    }

    private async Task<EpisodeEntity?> ScrapeArticlePageAsync(string href)
    {
        var url = href.StartsWith("http") ? href : BaseUrl + href;
        var html = await httpClient.GetStringAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Extract audio URL
        var source = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'audio-player-container')]//source[@src]");
        if (source is null) return null; // Skip articles without audio

        var audioUrl = source.GetAttributeValue("src", "");
        if (string.IsNullOrEmpty(audioUrl)) return null;

        // Extract title
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        var title = WebUtility.HtmlDecode(titleNode?.InnerText?.Trim() ?? "");

        // Extract publication date from the page
        var dateText = ExtractDateFromPage(doc);
        var publishDate = ParseHebrewDate(dateText) ?? DateTimeOffset.UtcNow;

        // Extract full article body
        var bodyNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'introBlock')]");
        var fullText = CleanHtmlToText(bodyNode);

        // Extract glossary
        var glossaryNode = doc.DocumentNode.SelectSingleNode(
            "//div[contains(@class, 'textBlock')][.//strong[contains(text(), 'ביאורי מילים')]]");
        var glossary = CleanHtmlToText(glossaryNode);

        // Build slug from URL
        var slug = href.Trim('/').Split('/').LastOrDefault() ?? "unknown";

        return new EpisodeEntity
        {
            PartitionKey = ContentTypes.Article,
            RowKey = $"{publishDate:yyyy-MM-dd}_{slug}",
            Title = title,
            PublishDate = publishDate,
            AudioUrl = audioUrl,
            ArticleUrl = url,
            FullText = fullText,
            Glossary = glossary,
            ContentType = ContentTypes.Article,
            AudioContentType = audioUrl.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                ? "audio/mpeg" : "audio/mp4",
        };
    }

    private static (string contentType, string title) ClassifyHomepageAudio(string audioUrl, int index)
    {
        var lower = audioUrl.ToLowerInvariant();
        if (lower.Contains("tachazit"))
            return (ContentTypes.Weather, "תחזית מזג האוויר");
        if (lower.Contains("shabbat") || lower.Contains("parasha"))
            return (ContentTypes.Parasha, "פָּרָשַׁת הַשָבוּעַ");
        // Default: first audio is typically the daily broadcast
        return (ContentTypes.Daily, "חדשון יומי – חדשות היום");
    }

    private static string ExtractFollowingText(HtmlNode audioContainer)
    {
        // Walk siblings after the audio container to get associated text
        var sibling = audioContainer.NextSibling;
        var texts = new List<string>();
        while (sibling is not null)
        {
            if (sibling.NodeType == HtmlNodeType.Element &&
                sibling.HasClass("audio-player-container"))
                break; // Stop at next audio player

            var text = CleanHtmlToText(sibling);
            if (!string.IsNullOrWhiteSpace(text))
                texts.Add(text);
            sibling = sibling.NextSibling;
        }
        return string.Join("\n\n", texts);
    }

    private static string ExtractDateFromPage(HtmlDocument doc)
    {
        // Look for date in the page content
        var dateNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'date')]")
            ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class, 'date')]");
        return dateNode?.InnerText?.Trim() ?? "";
    }

    private static DateTimeOffset? ParseHebrewDate(string dateText)
    {
        if (string.IsNullOrWhiteSpace(dateText)) return null;

        // Try to extract a date like "10 במרץ 2026" or "10/03/2026"
        var match = System.Text.RegularExpressions.Regex.Match(dateText, @"(\d{1,2})/(\d{1,2})/(\d{4})");
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var day) &&
            int.TryParse(match.Groups[2].Value, out var month) &&
            int.TryParse(match.Groups[3].Value, out var year))
        {
            return new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.FromHours(2));
        }
        return null;
    }

    private static string CleanHtmlToText(HtmlNode? node)
    {
        if (node is null) return "";
        // Remove script/style elements
        foreach (var script in node.SelectNodes(".//script|.//style") ?? Enumerable.Empty<HtmlNode>())
            script.Remove();
        // Get inner text, decode HTML entities, normalize whitespace
        var text = WebUtility.HtmlDecode(node.InnerText);
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Fetches audio file metadata (Content-Length) via HTTP HEAD.
    /// </summary>
    public async Task PopulateAudioMetadataAsync(EpisodeEntity episode)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, episode.AudioUrl);
            using var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength.HasValue)
            {
                episode.AudioContentLength = response.Content.Headers.ContentLength.Value;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get audio metadata for {Url}", episode.AudioUrl);
        }
    }
}
