using System.Xml.Linq;
using HadashonPodcast.Functions.Models;

namespace HadashonPodcast.Functions.Services;

public class PodcastFeedGenerator
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    private const string FeedTitle = "חדשון בעברית קלה";
    private const string FeedDescription = "חדשות בעברית קלה עם ניקוד – האתר של משרד החינוך. כולל חדשון יומי, תחזית מזג האוויר, פרשת השבוע, והרחבות למאמרים.";
    private const string FeedLink = "https://hadshon.education.gov.il/";
    private const string FeedLanguage = "he";

    public string GenerateFeed(IEnumerable<EpisodeEntity> episodes, string selfUrl)
    {
        var items = episodes
            .OrderByDescending(e => e.PublishDate)
            .Take(200) // Keep feed manageable
            .Select(BuildItem);

        var rss = new XElement("rss",
            new XAttribute("version", "2.0"),
            new XAttribute(XNamespace.Xmlns + "itunes", Itunes),
            new XAttribute(XNamespace.Xmlns + "content", Content),
            new XAttribute(XNamespace.Xmlns + "atom", Atom),
            new XElement("channel",
                new XElement("title", FeedTitle),
                new XElement("link", FeedLink),
                new XElement("description", FeedDescription),
                new XElement("language", FeedLanguage),
                new XElement("generator", "HadashonPodcast"),
                new XElement(Atom + "link",
                    new XAttribute("href", selfUrl),
                    new XAttribute("rel", "self"),
                    new XAttribute("type", "application/rss+xml")),
                new XElement(Itunes + "author", "משרד החינוך – חדשון"),
                new XElement(Itunes + "summary", FeedDescription),
                new XElement(Itunes + "category",
                    new XAttribute("text", "Education")),
                new XElement(Itunes + "explicit", "no"),
                items));

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            rss);

        return doc.ToString();
    }

    private static XElement BuildItem(EpisodeEntity episode)
    {
        // FullText already includes the glossary from the page scrape;
        // only append Glossary separately if FullText doesn't contain it
        var description = episode.FullText;
        if (!string.IsNullOrWhiteSpace(episode.Glossary)
            && !description.Contains(episode.Glossary.Trim()[..Math.Min(40, episode.Glossary.Trim().Length)]))
        {
            description += "\n\nביאורי מילים:\n" + episode.Glossary;
        }

        var categoryLabel = episode.ContentType switch
        {
            ContentTypes.Daily => "חדשון יומי",
            ContentTypes.Weather => "תחזית מזג האוויר",
            ContentTypes.Parasha => "פרשת השבוע",
            ContentTypes.Article => "הרחבה",
            ContentTypes.LexiconWord => "מילים ומושגים",
            ContentTypes.LexiconProverb => "פתגמים",
            ContentTypes.LexiconPerson => "אנשים",
            _ => "חדשון"
        };

        var enclosureAttrs = new List<XAttribute>
        {
            new("url", episode.AudioUrl),
            new("type", episode.AudioContentType ?? "audio/mp4"),
        };
        if (episode.AudioContentLength.HasValue)
            enclosureAttrs.Add(new XAttribute("length", episode.AudioContentLength.Value));

        return new XElement("item",
            new XElement("title", $"[{categoryLabel}] {episode.Title}"),
            new XElement("link", episode.ArticleUrl),
            new XElement("description", description),
            new XElement("enclosure", enclosureAttrs),
            new XElement("guid",
                new XAttribute("isPermaLink", "false"),
                $"{episode.PartitionKey}:{episode.RowKey}"),
            new XElement("pubDate", episode.PublishDate.ToString("R")),
            new XElement(Itunes + "summary",
                description.Length > 4000 ? description[..4000] : description),
            new XElement(Itunes + "explicit", "no"),
            new XElement("category", categoryLabel));
    }
}
