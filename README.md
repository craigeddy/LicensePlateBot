# License Plate Bot 🚗

A Telegram bot for playing the license plate game on road trips. Track which US states you've spotted, start fresh for each trip, and play together in a shared Telegram chat. Supports two game modes: collaborative and race.

## Game Modes

### Collaborative (default)
All players in the chat share one list of seen states and work together to collect all 51 plates (50 states + DC). The group wins when the target is reached.

### Race Mode 🏁
Each player builds their own independent collection of all 51 plates. The first player to finish wins, with 2nd and 3rd place announcements as others complete their sets. Races can span weeks — the bot tracks progress across sessions so there's no pressure to finish in one sitting.

Start a race with `/newrace [name]`. Before the first plate is logged by anyone, any player can `/skip` states that are unlikely to appear (e.g. HI or AK on a continental trip) — skips apply to everyone's target. Once any player logs their first sighting, skips are locked for everyone.

## Features

- `/saw CA` or `/saw California` or `/saw DC` — log a state you spotted; in collaborative mode the bot credits the first spotter; in race mode it tracks your personal collection and announces when you finish
- `/skip HI` — in collaborative mode, removes a state from the group's required list; in race mode, removes it from your personal required list (only available before your first sighting)
- `/status` — in collaborative mode, shows group progress and a per-player leaderboard; in race mode, shows a race standings table with each player's count and finish placement
- `/missing` — shows which states you still need (collaborative: shared list; race: your personal list)
- `/undo` — removes your last logged state; in race mode, also removes you from the finisher ledger if you had finished
- `/newtrip [name]` — start a fresh collaborative trip; if no name is given, the bot asks for one (default: `Road Trip MM/DD/YYYY`)
- `/newrace [name]` — start a race; if no name is given, the bot asks for one (default: `Race MM/DD/YYYY`)
- `/history` — view results from all previous trips as a table; race trips show the winner and finisher count
- **Admin broadcast** — send a plain-text message to all active game chats, or to a specific chat, via an HTTP trigger or the `/broadcast` Telegram command (admin-only)

State is stored per Telegram chat, so any member of a group chat can log plates and everyone sees the updates in real time.

Replies that benefit from structure (`/status`, `/history`, `/help`, and the all-plates-found celebration) use Telegram's [rich message formatting](https://core.telegram.org/bots/api#rich-message-formatting-options) (Bot API 10.1) — headings, lists, and tables — for a clearer layout.

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
    "StorageConnectionString": "UseDevelopmentStorage=true",
    "AdminTelegramUserId": "<YOUR_TELEGRAM_USER_ID>"
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
    StorageConnectionString="<YOUR_CONNECTION_STRING>" \
    AdminTelegramUserId="<YOUR_TELEGRAM_USER_ID>"
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

## Admin Broadcast

As an admin you can push a plain-text message to all active game chats, or to a specific chat, via two mechanisms.

### 1. HTTP trigger

`POST /api/broadcast` — secured by the Azure Function host key.

```bash
# Broadcast to all active chats
curl -X POST "https://func-licenseplate-bot.azurewebsites.net/api/broadcast?code=<FUNCTION_KEY>" \
  -H "Content-Type: application/json" \
  -d '{"message": "We are doing maintenance at midnight — save your progress!"}'

# Broadcast to a specific chat
curl -X POST "https://func-licenseplate-bot.azurewebsites.net/api/broadcast?code=<FUNCTION_KEY>" \
  -H "Content-Type: application/json" \
  -d '{"message": "Hey, your trip data was migrated.", "chatId": -1001234567890}'
```

Response: `{ "sent": 3, "failed": 0, "total": 3 }`

For local testing replace the URL with `http://localhost:7071/api/broadcast` (no `?code=` needed in dev).

### 2. Telegram `/broadcast` command

Send the command from your own Telegram account (the one whose user ID is set in `AdminTelegramUserId`).

```
/broadcast Hello everyone, enjoy the trip!
/broadcast -1001234567890 Hey, just this one chat.
```

The bot replies with a delivery summary visible only in your chat. Non-admins get no response.

To find your Telegram user ID, message [@userinfobot](https://t.me/userinfobot).

---

## Bot Commands Reference

| Command | Description | Example |
|---|---|---|
| `/saw [state]` | Log a state you spotted by abbreviation or full name (including DC); in collaborative mode credits the first spotter; in race mode tracks your personal count and announces when you finish | `/saw CA` or `/saw California` or `/saw DC` |
| `/skip [state]` | Remove a state from the required list; affects the whole group in both modes; in race mode only allowed before any player logs their first sighting | `/skip HI` or `/skip Hawaii` |
| `/status` | Show progress; in collaborative mode shows group progress and leaderboard; in race mode shows a standings table with each player's count and finish placement | `/status` |
| `/missing` | List states not yet found; in race mode shows your personal missing states | `/missing` |
| `/undo` | Remove your last logged state; in race mode removes you from the finisher ledger if you had finished | `/undo` |
| `/newtrip [name]` | Start a fresh collaborative trip; if no name is given, the bot asks for one (default: `Road Trip MM/DD/YYYY`); current trip is saved to history if any states were logged | `/newtrip Colorado 2026` |
| `/newrace [name]` | Start a race where each player collects all 51 plates independently — first to finish wins; skips can be set before your first plate | `/newrace Summer 2026` |
| `/history` | Show results from all previous trips in this chat; race trips show the winner and finisher count | `/history` |
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
├── BroadcastFunction.cs     # HTTP trigger — admin broadcast endpoint
├── BotCommandHandler.cs     # Command routing and response logic
├── TripStateService.cs      # Azure Table Storage read/write
└── Models/
    └── TripState.cs         # Table Storage entity model
```
