# License Plate Bot 🚗

A Telegram bot for playing the license plate game on road trips. Track which US states you've spotted, start fresh for each trip, and play together in a shared Telegram chat.

## Features

- `/saw CA` or `/saw California` — log a state you spotted; the bot credits you by name and notes if someone already got it
- `/status` — see your progress with a visual progress bar and a per-player leaderboard
- `/missing` — see which states you still need
- `/undo` — remove the last logged state
- `/newtrip` — start fresh for a new trip (previous trip is saved automatically)
- `/history` — view results from all previous trips, including the top spotter for each

State is stored per Telegram chat, so any member of a group chat can log plates and everyone sees the updates in real time.

---

## Architecture

- **Azure Functions** (.NET 8 isolated worker) — HTTP trigger receives Telegram webhook POSTs
- **Azure Table Storage** — stores trip state (seen states, trip name) keyed by Telegram chat ID
- **Telegram Bot API** — delivers messages and receives commands via webhook

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local)
- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli)
- [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite) (local Storage emulator, for development)
- [ngrok](https://ngrok.com) (for local webhook testing)
- A Telegram bot token from [@BotFather](https://t.me/BotFather)

---

## Local Development

### 1. Create a Telegram bot

Message [@BotFather](https://t.me/BotFather) on Telegram:

```
/newbot
```

Follow the prompts, then copy the bot token it gives you.

### 2. Configure local settings

Edit `local.settings.json` and fill in your bot token:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "TelegramBotToken": "<YOUR_BOT_TOKEN>",
    "StorageConnectionString": "UseDevelopmentStorage=true"
  }
}
```

> `local.settings.json` is gitignored and never committed.

### 3. Start the local Storage emulator

```bash
azurite --silent
```

### 4. Start the Function

```bash
dotnet restore
func start
```

The function starts on `http://localhost:7071`. You should see:

```
TelegramWebhook: [POST] http://localhost:7071/api/telegram
```

### 5. Expose localhost via ngrok

In a separate terminal:

```bash
ngrok http 7071
```

Copy the HTTPS forwarding URL (e.g. `https://a1b2c3d4.ngrok-free.app`).

> The ngrok URL changes each time you restart it. Re-register the webhook each new dev session.

### 6. Register the Telegram webhook

```bash
curl -X POST "https://api.telegram.org/bot<YOUR_TOKEN>/setWebhook" \
  -d "url=https://a1b2c3d4.ngrok-free.app/api/telegram"
```

Verify it worked:

```bash
curl "https://api.telegram.org/bot<YOUR_TOKEN>/getWebhookInfo"
```

You're now ready to test. Open your Telegram bot and send `/newtrip Test`.

---

## Deploying to Azure

### 1. Create Azure resources

```bash
# Create a resource group
az group create --name rg-licenseplate-bot --location eastus

# Create a Storage Account
az storage account create \
  --name stlicenseplatebot \
  --resource-group rg-licenseplate-bot \
  --sku Standard_LRS

# Create the Function App
az functionapp create \
  --resource-group rg-licenseplate-bot \
  --consumption-plan-location eastus \
  --runtime dotnet-isolated \
  --runtime-version 8 \
  --functions-version 4 \
  --name func-licenseplate-bot \
  --storage-account stlicenseplatebot
```

### 2. Get the Storage connection string

```bash
az storage account show-connection-string \
  --name stlicenseplatebot \
  --resource-group rg-licenseplate-bot \
  --query connectionString \
  --output tsv
```

### 3. Set production app settings

```bash
az functionapp config appsettings set \
  --name func-licenseplate-bot \
  --resource-group rg-licenseplate-bot \
  --settings \
    TelegramBotToken="<YOUR_BOT_TOKEN>" \
    StorageConnectionString="<YOUR_CONNECTION_STRING>"
```

### 4. Deploy

Deployment happens automatically via GitHub Actions when you push to `main` (see [CI/CD](#cicd) below). To deploy manually:

```bash
func azure functionapp publish func-licenseplate-bot
```

### 5. Get the function key

```bash
az functionapp function keys list \
  --name func-licenseplate-bot \
  --resource-group rg-licenseplate-bot \
  --function-name TelegramWebhook
```

Copy the `default` key value.

### 6. Register the production webhook

```bash
curl -X POST "https://api.telegram.org/bot<YOUR_TOKEN>/setWebhook" \
  -d "url=https://func-licenseplate-bot.azurewebsites.net/api/telegram?code=<FUNCTION_KEY>"
```

---

## CI/CD

A GitHub Actions workflow (`.github/workflows/ci-cd.yml`) runs on every pull request to `main` and every push to `main`.

| Event | Jobs run |
|---|---|
| Pull request to `main` | Build only |
| Push to `main` | Build + deploy to Azure Functions |

### Required secret

Add the Azure Function publish profile as a repository secret named `AZURE_FUNCTIONAPP_PUBLISH_PROFILE`:

1. Azure Portal → Function App `func-licenseplate-bot` → **Overview** → **Get publish profile** — download the file
2. GitHub repo → **Settings** → **Secrets and variables** → **Actions** → **New repository secret**
3. Name: `AZURE_FUNCTIONAPP_PUBLISH_PROFILE`, value: paste the contents of the downloaded file

---

## Command Menu

The bot registers its command list with BotFather automatically on every startup, so the `/` command popup in Telegram stays in sync without any manual steps.

To add a new command, add an entry to the `Commands` array in `BotCommandHandler.cs` — it will be registered on the next deploy.

---

## Adding the Bot to a Telegram Chat

1. Open the group chat in Telegram
2. Tap the group name → **Add Members**
3. Search for your bot by its username (e.g. `@YourLicensePlateBot`)
4. Add it to the chat

Both you and your chat partner can now send commands and see each other's updates in real time.

---

## Bot Commands Reference

| Command | Description | Example |
|---|---|---|
| `/saw [state]` | Log a state you spotted by abbreviation or full name; credits you by name; omit the state and the bot will prompt you | `/saw CA` or `/saw California` |
| `/status` | Show progress, states found, and a per-player leaderboard | `/status` |
| `/missing` | List states not yet found | `/missing` |
| `/undo` | Remove the last logged state | `/undo` |
| `/newtrip [name]` | Start a fresh trip; current trip is saved to history if any states were logged | `/newtrip Colorado 2026` |
| `/history` | Show results from all previous trips in this chat | `/history` |
| `/help` | Show command reference | `/help` |

---

## Project Structure

```
LicensePlateBot/
├── .github/
│   └── workflows/
│       └── ci-cd.yml        # GitHub Actions CI/CD pipeline
├── LicensePlateBot.csproj   # Project file and NuGet dependencies
├── host.json                # Azure Functions host configuration
├── local.settings.json      # Local dev secrets (gitignored)
├── Program.cs               # Host builder and DI wiring
├── TelegramFunction.cs      # HTTP trigger — receives webhook POSTs
├── BotCommandHandler.cs     # Command routing and response logic
├── TripStateService.cs      # Azure Table Storage read/write
└── Models/
    └── TripState.cs         # Table Storage entity model
```
