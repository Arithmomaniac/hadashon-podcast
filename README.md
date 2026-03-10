# HaDashon Podcast

Turn [hadshon.education.gov.il](https://hadshon.education.gov.il/) — the Israeli Ministry of Education's simplified Hebrew news site (with nikud) — into a podcast feed, hosted on cheap serverless Azure resources.

## What it does

An Azure Function runs every 3 hours to:

1. **Scrape** the HaDashon website for new content and audio
2. **Store** episode data in Azure Table Storage
3. **Generate** a podcast RSS feed
4. **Publish** the feed to Azure Blob Storage (static website)

### Audio Sources

| Content Type | Source | Frequency |
|---|---|---|
| 📰 Daily news broadcast | Homepage | Daily |
| 🌤️ Weather forecast | Homepage | Daily |
| 📖 Weekly parasha | Homepage | Weekly |
| 📄 Article readings | `/articles/` pages | Per article |

Each episode's description contains the **full article text** (with nikud) and glossary — great for Hebrew learners reading along.

## Architecture

- **Azure Functions** (Flex Consumption, .NET 10 isolated worker)
- **Azure Table Storage** (episode metadata + full text)
- **Azure Blob Storage** (static website hosting for `feed.xml`)
- Audio links point directly to `tum-files.education.gov.il` (no re-hosting)
- **Cost**: ~$0/month (Azure free tier)

## Development

### Prerequisites

- .NET 10 SDK
- Azure Functions Core Tools v4
- Azure CLI (for deployment)

### Local Development

```bash
cd src/HadashonPodcast.Functions
func start
```

### Deployment

Deployed via GitHub Actions on push to `main`. See `.github/workflows/deploy.yml`.

## License

MIT
