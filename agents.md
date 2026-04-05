# License Plate Bot — Claude Code Guide

## Project Overview

A Telegram bot for playing the US license plate game on road trips. Built as an Azure Function (.NET 8 isolated worker) with Azure Table Storage for state persistence.

## Architecture

- **TelegramFunction.cs** — HTTP trigger entry point, receives webhook POSTs from Telegram
- **BotCommandHandler.cs** — routes commands and builds reply messages
- **TripStateService.cs** — all Table Storage reads and writes
- **Models/TripState.cs** — Table Storage entity (one row per chat)

State is keyed by Telegram chat ID. One active trip per chat at a time.

## Build & Run

```bash
# Restore dependencies
dotnet restore

# Build
dotnet build

# Run locally (requires Azurite running separately)
func start

# Start local Storage emulator
azurite --silent
```

## Local Dev Workflow

Three terminals are needed:
1. `azurite --silent` — local Storage emulator
2. `func start` — the Function on port 7071
3. `ngrok http 7071` — public tunnel for Telegram webhook

After starting ngrok, re-register the webhook:
```bash
curl -X POST "https://api.telegram.org/bot<TOKEN>/setWebhook" \
  -d "url=https://<ngrok-id>.ngrok-free.app/api/telegram"
```

## Deploy

```bash
func azure functionapp publish func-licenseplate-bot
```

## Environment Variables

| Variable | Description |
|---|---|
| `TelegramBotToken` | Bot token from BotFather |
| `StorageConnectionString` | Azure Storage connection string |
| `AzureWebJobsStorage` | Required by Functions runtime (same value as StorageConnectionString in prod) |

Local values live in `local.settings.json` (gitignored). Production values are set via Azure app settings.

## Key Conventions

- **Always return HTTP 200 to Telegram** even on errors — Telegram will retry on non-200 responses and flood the function. Errors are logged, not surfaced as HTTP errors.
- **State is stored as JSON in a single Table Storage row** per chat. `SeenStatesJson` is a serialized `List<string>` of 2-letter state abbreviations.
- **State abbreviations are always uppercased** before storage and comparison. Validation uses the `AllStates` HashSet in `BotCommandHandler`.
- **Bot replies use HTML parse mode** — use `<b>` for bold, not markdown. Avoid other HTML tags.
- **`PendingCommand` on `TripState`** tracks conversational state (e.g. waiting for a state abbreviation after `/saw` with no argument). Always clear it when a new slash command arrives.

## Adding a New Command

1. Add the handler method to `BotCommandHandler.cs` following the existing `Handle*` pattern
2. Add the case to the command switch in `HandleUpdateAsync`
3. Add the command to the BotFather command list (see README)
4. If the command needs conversational input, set `PendingCommand` and handle the follow-up in the non-slash branch of `HandleUpdateAsync`

## Branching

Always create a feature branch before making any code changes. Never commit directly to `main`. Create the branch first, then make changes:

```bash
git checkout -b <feature-branch-name>
```

## Commits

Always attribute commits to Claude using the `--author` flag:

```bash
git commit --author="Claude Sonnet 4.6 <noreply@anthropic.com>" -m "..."
```

Always include a `Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>` trailer in the commit message body as well.

## Before Declaring Done

Always run `dotnet build` and confirm it succeeds (0 errors) before declaring a code change complete.

Always update `README.md` if the change affects user-facing behavior, commands, or examples.

## Testing

No automated tests currently. Test manually via Telegram with ngrok running locally. Key scenarios to verify after any change:

- `/newtrip` resets state correctly
- `/saw CA` adds a state and reports the correct count
- `/saw CA` a second time reports "already got"
- `/saw` with no argument prompts for input (if conversational flow is implemented)
- `/undo` removes the last state
- `/status` and `/missing` reflect current state accurately
- Invalid abbreviation (e.g. `/saw XX`) returns a helpful error


Do not attempt to save feedback memory or call any memory persistence tools; use agents.md for all persistent instructions.
