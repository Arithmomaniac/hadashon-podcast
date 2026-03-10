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
- Azure Functions Core Tools v4 (`npm install -g azure-functions-core-tools@4`)
- [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite) (local Storage emulator)

### Local Development

```bash
# 1. Start Azurite (in a separate terminal)
azurite --silent

# 2. Run the function app
cd src/HadashonPodcast.Functions
func start
```

The function uses `UseDevelopmentStorage=true` by default, so it connects to Azurite automatically.

**Manual test** — trigger a scrape via HTTP:
```bash
curl http://localhost:7071/api/scrape
```
This runs the full pipeline (scrape → Table Storage → RSS) and returns the generated `feed.xml` in the response.

The timer trigger (`ScrapeAndPublish`) also works locally — it fires every 3 hours, or you can invoke it manually via the Azure Functions Core Tools admin API:
```bash
curl -X POST http://localhost:7071/admin/functions/ScrapeAndPublish -H "Content-Type: application/json" -d "{}"
```

### Project Structure

```
hadashon-podcast/
├── infra/                           # Bicep IaC
│   ├── main.bicep                   # Subscription-scoped entry point
│   └── modules/
│       ├── storage.bicep            # Storage Account (Blob + Table + static website)
│       ├── monitoring.bicep         # Log Analytics + App Insights
│       └── functionapp.bicep        # Flex Consumption Function App + RBAC
├── src/HadashonPodcast.Functions/
│   ├── Models/EpisodeEntity.cs      # Table Storage entity
│   ├── Scrapers/HadashonScraper.cs  # HTML scraper (homepage + articles)
│   ├── Services/PodcastFeedGenerator.cs  # RSS 2.0 + iTunes feed builder
│   ├── ScrapeAndPublishFunction.cs  # Timer trigger (every 3h)
│   ├── ManualTriggerFunction.cs     # HTTP trigger for testing
│   └── Program.cs                   # DI setup
├── .github/workflows/
│   ├── deploy.yml                   # Build + deploy Function App
│   └── deploy-infra.yml             # Deploy Bicep infrastructure
└── README.md
```

### Deployment

Deployed via GitHub Actions on push to `master`. See `.github/workflows/deploy.yml`.

**First-time setup** (one-time):
1. `az login`
2. Deploy infra: `az deployment sub create --template-file infra/main.bicep --parameters environmentName=hadashon location=westeurope --location westeurope`
3. Create OIDC service principal for GitHub Actions
4. Set GitHub secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`
5. Set GitHub variable: `AZURE_FUNCTIONAPP_NAME`

## License

MIT
