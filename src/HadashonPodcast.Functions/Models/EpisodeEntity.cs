using Azure;
using Azure.Data.Tables;

namespace HadashonPodcast.Functions.Models;

/// <summary>
/// Represents a podcast episode stored in Azure Table Storage.
/// PartitionKey = content type (daily, weather, parasha, article, lexicon-word, lexicon-proverb, lexicon-person)
/// RowKey = {yyyy-MM-dd}_{slug}
/// </summary>
public class EpisodeEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Title { get; set; } = string.Empty;
    public DateTimeOffset PublishDate { get; set; }
    public string AudioUrl { get; set; } = string.Empty;
    public string? AudioContentType { get; set; }
    public long? AudioContentLength { get; set; }
    public string ArticleUrl { get; set; } = string.Empty;

    /// <summary>Full article body text with nikud for episode description.</summary>
    public string FullText { get; set; } = string.Empty;

    /// <summary>Glossary section ("ביאורי מילים") if present.</summary>
    public string? Glossary { get; set; }

    /// <summary>Content type for categorization within the single combined feed.</summary>
    public string ContentType { get; set; } = string.Empty;
}

public static class ContentTypes
{
    public const string Daily = "daily";
    public const string Weather = "weather";
    public const string Parasha = "parasha";
    public const string Article = "article";
    public const string LexiconWord = "lexicon-word";
    public const string LexiconProverb = "lexicon-proverb";
    public const string LexiconPerson = "lexicon-person";
}
