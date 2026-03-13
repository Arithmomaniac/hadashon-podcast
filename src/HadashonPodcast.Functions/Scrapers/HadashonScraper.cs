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

        // Extract the displayed Gregorian date from the homepage text
        var homepageDate = ExtractGregorianDateFromText(doc.DocumentNode.InnerText);

        for (int i = 0; i < audioContainers.Count; i++)
        {
            var source = audioContainers[i].SelectSingleNode(".//source[@src]");
            if (source is null) continue;

            var audioUrl = source.GetAttributeValue("src", "");
            if (string.IsNullOrEmpty(audioUrl)) continue;

            var (contentType, title) = ClassifyHomepageAudio(audioUrl, i);
            var textContent = ExtractFollowingText(audioContainers[i]);

            var publishDate = ParseDate(homepageDate)
                ?? ParseDateFromAudioUrl(audioUrl)
                ?? DateTimeOffset.UtcNow;
            var dateKey = publishDate.ToString("yyyy-MM-dd");

            var episode = new EpisodeEntity
            {
                PartitionKey = contentType,
                RowKey = $"{dateKey}_{contentType}",
                Title = title,
                PublishDate = publishDate,
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

            // Only look for links in the main content area (leftSide), not the nav menu
            var articleLinks = listDoc.DocumentNode
                .SelectNodes("//div[contains(@class, 'leftSide')]//a[contains(@href, '/articles/')]")
                ?.Select(a => a.GetAttributeValue("href", ""))
                .Where(href => href.StartsWith("/articles/") && href.Count(c => c == '/') >= 3)
                .Distinct()
                .Take(15) // Limit per page to avoid excessive scraping
                .ToList() ?? [];

            logger.LogInformation("Found {Count} article links on page {Page}", articleLinks.Count, page);

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

        // Extract publication date from the page, audio URL, or fall back to now
        var dateText = ExtractDateFromPage(doc);
        var publishDate = ParseDate(dateText)
            ?? ParseDateFromAudioUrl(audioUrl)
            ?? DateTimeOffset.UtcNow;

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
        // Try machine-readable date first
        var timeNode = doc.DocumentNode.SelectSingleNode("//time[@datetime]");
        if (timeNode is not null)
        {
            var dt = timeNode.GetAttributeValue("datetime", "");
            if (!string.IsNullOrWhiteSpace(dt)) return dt.Trim();
        }

        // Try meta tags
        var metaNode = doc.DocumentNode.SelectSingleNode("//meta[@property='article:published_time']")
            ?? doc.DocumentNode.SelectSingleNode("//meta[@name='publish_date']");
        if (metaNode is not null)
        {
            var content = metaNode.GetAttributeValue("content", "");
            if (!string.IsNullOrWhiteSpace(content)) return content.Trim();
        }

        // Try visible date elements
        var dateNode = doc.DocumentNode.SelectSingleNode(
            "//*[contains(@class,'date') or contains(@class,'publish') or contains(@class,'posted')]");
        if (dateNode is not null)
        {
            var text = WebUtility.HtmlDecode(dateNode.InnerText ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        // Fall back to regex search in page text
        return ExtractGregorianDateFromText(doc.DocumentNode.InnerText);
    }

    private static readonly Dictionary<string, int> HebrewMonths = new()
    {
        ["ינואר"] = 1, ["פברואר"] = 2, ["מרץ"] = 3,
        ["אפריל"] = 4, ["מאי"] = 5, ["יוני"] = 6,
        ["יולי"] = 7, ["אוגוסט"] = 8, ["ספטמבר"] = 9,
        ["אוקטובר"] = 10, ["נובמבר"] = 11, ["דצמבר"] = 12,
    };

    private static readonly string HebrewMonthPattern =
        string.Join("|", HebrewMonths.Keys);

    /// <summary>
    /// Searches raw text for a Gregorian date like "12 במרץ 2026" or "10/03/2026".
    /// </summary>
    private static string ExtractGregorianDateFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = WebUtility.HtmlDecode(text);

        // Try "12 במרץ 2026"
        var hebrewMatch = System.Text.RegularExpressions.Regex.Match(
            text, $@"\b(\d{{1,2}})\s+ב({HebrewMonthPattern})\s+(\d{{4}})\b");
        if (hebrewMatch.Success) return hebrewMatch.Value;

        // Try dd/MM/yyyy
        var numericMatch = System.Text.RegularExpressions.Regex.Match(text, @"\b\d{1,2}/\d{1,2}/\d{4}\b");
        if (numericMatch.Success) return numericMatch.Value;

        return "";
    }

    /// <summary>
    /// Parses multiple date formats: ISO, dd/MM/yyyy, and "12 במרץ 2026".
    /// </summary>
    private static DateTimeOffset? ParseDate(string? dateText)
    {
        if (string.IsNullOrWhiteSpace(dateText)) return null;
        dateText = WebUtility.HtmlDecode(dateText).Trim();

        // ISO / machine-readable
        if (DateTimeOffset.TryParse(dateText, out var isoDate))
            return isoDate;

        // dd/MM/yyyy
        var numericMatch = System.Text.RegularExpressions.Regex.Match(dateText, @"\b(\d{1,2})/(\d{1,2})/(\d{4})\b");
        if (numericMatch.Success &&
            int.TryParse(numericMatch.Groups[1].Value, out var day) &&
            int.TryParse(numericMatch.Groups[2].Value, out var month) &&
            int.TryParse(numericMatch.Groups[3].Value, out var year))
        {
            return new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.FromHours(2));
        }

        // Hebrew month names: "12 במרץ 2026"
        var hebrewMatch = System.Text.RegularExpressions.Regex.Match(
            dateText, $@"\b(\d{{1,2}})\s+ב({HebrewMonthPattern})\s+(\d{{4}})\b");
        if (hebrewMatch.Success &&
            int.TryParse(hebrewMatch.Groups[1].Value, out day) &&
            int.TryParse(hebrewMatch.Groups[3].Value, out year) &&
            HebrewMonths.TryGetValue(hebrewMatch.Groups[2].Value, out month))
        {
            return new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.FromHours(2));
        }

        return null;
    }

    /// <summary>
    /// Extracts a date from audio URLs like /hadshon/2026/3/8.3hadshon.mp3
    /// </summary>
    private static DateTimeOffset? ParseDateFromAudioUrl(string? audioUrl)
    {
        if (string.IsNullOrWhiteSpace(audioUrl)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            audioUrl, @"/hadshon/(\d{4})/(\d{1,2})/(\d{1,2})\.\d{1,2}");
        if (!match.Success) return null;

        if (int.TryParse(match.Groups[1].Value, out var year) &&
            int.TryParse(match.Groups[2].Value, out var month) &&
            int.TryParse(match.Groups[3].Value, out var day))
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

        // Insert newlines for block elements to preserve paragraph structure
        foreach (var block in node.SelectNodes(".//p|.//br|.//div|.//li|.//tr|.//h1|.//h2|.//h3|.//h4|.//h5|.//h6") ?? Enumerable.Empty<HtmlNode>())
            block.InnerHtml = "\n" + block.InnerHtml;

        var text = WebUtility.HtmlDecode(node.InnerText);
        // Normalize runs of spaces/tabs within lines, but preserve newlines
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[^\S\n]+", " ");
        // Collapse 3+ consecutive newlines to 2
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    /// <summary>
    /// Fetches audio file metadata (Content-Length, Last-Modified) via HTTP HEAD.
    /// Updates PublishDate from Last-Modified if it was previously set to a fallback value.
    /// </summary>
    public async Task PopulateAudioMetadataAsync(EpisodeEntity episode)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, episode.AudioUrl);
            using var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                if (response.Content.Headers.ContentLength.HasValue)
                    episode.AudioContentLength = response.Content.Headers.ContentLength.Value;

                // Use Last-Modified as the authoritative publish timestamp
                if (response.Content.Headers.LastModified.HasValue)
                    episode.PublishDate = response.Content.Headers.LastModified.Value;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get audio metadata for {Url}", episode.AudioUrl);
        }
    }
}
