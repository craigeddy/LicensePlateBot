using LicensePlateBot.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

public class BotCommandHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly TripStateService _stateService;

    // All 50 US states plus DC
    private static readonly HashSet<string> AllStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA",
        "HI","ID","IL","IN","IA","KS","KY","LA","ME","MD",
        "MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ",
        "NM","NY","NC","ND","OH","OK","OR","PA","RI","SC",
        "SD","TN","TX","UT","VT","VA","WA","WV","WI","WY",
        "DC"
    };

    private static readonly Dictionary<string, string> StateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        {"AL","Alabama"},{"AK","Alaska"},{"AZ","Arizona"},{"AR","Arkansas"},{"CA","California"},
        {"CO","Colorado"},{"CT","Connecticut"},{"DE","Delaware"},{"FL","Florida"},{"GA","Georgia"},
        {"HI","Hawaii"},{"ID","Idaho"},{"IL","Illinois"},{"IN","Indiana"},{"IA","Iowa"},
        {"KS","Kansas"},{"KY","Kentucky"},{"LA","Louisiana"},{"ME","Maine"},{"MD","Maryland"},
        {"MA","Massachusetts"},{"MI","Michigan"},{"MN","Minnesota"},{"MS","Mississippi"},{"MO","Missouri"},
        {"MT","Montana"},{"NE","Nebraska"},{"NV","Nevada"},{"NH","New Hampshire"},{"NJ","New Jersey"},
        {"NM","New Mexico"},{"NY","New York"},{"NC","North Carolina"},{"ND","North Dakota"},{"OH","Ohio"},
        {"OK","Oklahoma"},{"OR","Oregon"},{"PA","Pennsylvania"},{"RI","Rhode Island"},{"SC","South Carolina"},
        {"SD","South Dakota"},{"TN","Tennessee"},{"TX","Texas"},{"UT","Utah"},{"VT","Vermont"},
        {"VA","Virginia"},{"WA","Washington"},{"WV","West Virginia"},{"WI","Wisconsin"},{"WY","Wyoming"},
        {"DC","Washington DC"}
    };

    private static readonly Dictionary<string, string> StateNameToAbbr =
        StateNames.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    // Canonical command list registered with BotFather on startup.
    // /start is intentionally omitted — it is an internal alias for /newtrip
    // required by Telegram for new users, not a user-facing command.
    public static readonly Telegram.Bot.Types.BotCommand[] Commands =
    [
        new() { Command = "saw",      Description = "Log a state you spotted (e.g. /saw CA)" },
        new() { Command = "status",   Description = "See your current progress" },
        new() { Command = "missing",  Description = "See which states are still needed" },
        new() { Command = "undo",     Description = "Remove the last logged state" },
        new() { Command = "newtrip",  Description = "Start a fresh trip" },
        new() { Command = "history",  Description = "View results from previous trips" },
        new() { Command = "help",     Description = "Show available commands" },
    ];

    public BotCommandHandler(ITelegramBotClient bot, TripStateService stateService)
    {
        _bot = bot;
        _stateService = stateService;
    }

    public async Task HandleUpdateAsync(Update update)
    {
        if (update.Message is not { } message) return;
        if (message.Text is not { } text) return;

        var chatId = message.Chat.Id;
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var isCommand = parts[0].StartsWith('/');
        var command = parts[0].ToLower().Split('@')[0]; // handle /cmd@botname format
        var args = parts.Skip(1).ToArray();

        string? reply;

        if (isCommand)
        {
            // Clear any pending conversational state when a new command arrives
            if (command != "/saw")
            {
                var s = await _stateService.GetOrCreateAsync(chatId);
                if (s.PendingCommand is not null)
                {
                    s.PendingCommand = null;
                    await _stateService.SaveAsync(s);
                }
            }

            reply = command switch
            {
                "/newtrip" or "/start" => await HandleNewTrip(chatId, args),
                "/saw"                 => await HandleSaw(chatId, args, message.From),
                "/status"              => await HandleStatus(chatId),
                "/missing"             => await HandleMissing(chatId),
                "/undo"                => await HandleUndo(chatId),
                "/history"             => await HandleHistory(chatId),
                "/help"                => GetHelp(),
                _                      => null
            };
        }
        else
        {
            // Non-command message — check for pending conversational state
            var state = await _stateService.GetOrCreateAsync(chatId);
            if (state.PendingCommand == "saw")
            {
                state.PendingCommand = null;
                await _stateService.SaveAsync(state);
                reply = await HandleSaw(chatId, parts, message.From);
            }
            else if (state.PendingCommand is { } pc && pc.StartsWith("newtrip:", StringComparison.Ordinal))
            {
                var pendingDefault = pc["newtrip:".Length..];
                state.PendingCommand = null;
                await _stateService.SaveAsync(state);
                reply = await HandleNewTrip(chatId, parts, pendingDefault);
            }
            else
            {
                reply = null;
            }
        }

        if (reply is not null)
            await _bot.SendMessage(chatId, reply, parseMode: ParseMode.Html);
    }

    private async Task<string?> HandleNewTrip(long chatId, string[] args, string? pendingDefault = null)
    {
        if (args.Length == 0)
        {
            var defaultName = $"Road Trip {DateTime.UtcNow.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture)}";
            var pending = await _stateService.GetOrCreateAsync(chatId);
            pending.PendingCommand = $"newtrip:{defaultName}";
            await _stateService.SaveAsync(pending);
            await _bot.SendMessage(chatId,
                $"What would you like to name this trip? Reply with a name, or send <b>skip</b> to use \"{defaultName}\".",
                parseMode: ParseMode.Html,
                replyMarkup: new ForceReplyMarkup());
            return null;
        }

        var tripName = string.Join(" ", args).Equals("skip", StringComparison.OrdinalIgnoreCase)
            ? (pendingDefault ?? $"Road Trip {DateTime.UtcNow:MM/dd/yyyy}")
            : string.Join(" ", args);
        await _stateService.ResetAsync(chatId, tripName);
        return $"🚗 <b>New trip started: {System.Net.WebUtility.HtmlEncode(tripName)}</b>\n\nReady to collect all 51 states! Use /saw CA to log a plate.";
    }

    private async Task<string?> HandleSaw(long chatId, string[] args, Telegram.Bot.Types.User? from)
    {
        if (args.Length == 0)
        {
            var pending = await _stateService.GetOrCreateAsync(chatId);
            pending.PendingCommand = "saw";
            await _stateService.SaveAsync(pending);
            await _bot.SendMessage(chatId,
                "Which state did you spot? Reply with the 2-letter abbreviation or full name (e.g. CA, TX, California).",
                replyMarkup: new ForceReplyMarkup());
            return null;
        }

        var input = string.Join(" ", args);
        string abbr;
        if (AllStates.Contains(input))
            abbr = input.ToUpper();
        else if (StateNameToAbbr.TryGetValue(input, out var matched))
            abbr = matched;
        else
            return $"❓ <b>{input}</b> isn't a recognized US state. Use a 2-letter abbreviation (CA) or full name (California).";

        var state = await _stateService.GetOrCreateAsync(chatId);
        state.PendingCommand = null;
        var sightings = _stateService.DeserializeSightings(state.SeenStatesJson);

        var existing = sightings.FirstOrDefault(s => s.State.Equals(abbr, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var spotter = existing.UserName is { Length: > 0 } name ? $" — spotted by {System.Net.WebUtility.HtmlEncode(name)}" : "";
            return $"👀 Already got <b>{StateNames[abbr]}</b> ({abbr}){spotter}! That's {sightings.Count}/51.";
        }

        var displayName = DisplayName(from);
        sightings.Add(new SightingRecord(abbr, from?.Id ?? 0, displayName));
        state.SeenStatesJson = _stateService.SerializeSightings(sightings);
        await _stateService.SaveAsync(state);

        var remaining = 51 - sightings.Count;
        var credit = displayName is { Length: > 0 } ? $" by {System.Net.WebUtility.HtmlEncode(displayName)}" : "";

        if (sightings.Count == 51)
        {
            await SendAllStatesFoundCelebrationAsync(chatId, state, sightings);
            return $"✅ <b>{StateNames[abbr]}</b> ({abbr}) spotted{credit}!\n🏁 <b>51/51 — COMPLETE!</b> 🎆";
        }

        return $"✅ <b>{StateNames[abbr]}</b> ({abbr}) spotted{credit}!\n{sightings.Count}/51 states found — {remaining} to go.";
    }

    private async Task SendAllStatesFoundCelebrationAsync(long chatId, TripState state, List<SightingRecord> sightings)
    {
        var duration = DateTimeOffset.UtcNow - state.StartedAt;
        string durationText;
        if (duration.TotalDays >= 1)
        {
            var days = (int)duration.TotalDays;
            var hours = duration.Hours;
            durationText = hours > 0 ? $"{days} day{(days == 1 ? "" : "s")} and {hours} hour{(hours == 1 ? "" : "s")}" : $"{days} day{(days == 1 ? "" : "s")}";
        }
        else if (duration.TotalHours >= 1)
        {
            var hrs = (int)duration.TotalHours;
            durationText = $"{hrs} hour{(hrs == 1 ? "" : "s")}";
        }
        else
        {
            var mins = Math.Max(1, (int)duration.TotalMinutes);
            durationText = $"{mins} minute{(mins == 1 ? "" : "s")}";
        }

        var leaderboard = sightings
            .Where(s => s.UserId != 0)
            .GroupBy(s => s.UserId)
            .OrderByDescending(g => g.Count())
            .Select((g, i) =>
            {
                var spotterName = g.Select(s => s.UserName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Unknown";
                var medal = i switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => $"   {i + 1}." };
                return $"{medal} {System.Net.WebUtility.HtmlEncode(spotterName)} — {g.Count()} state{(g.Count() == 1 ? "" : "s")}";
            })
            .ToList();

        var allStatesList = string.Join(", ", AllStates.OrderBy(s => s));
        var startedAt = state.StartedAt.ToString("MMM d, yyyy");
        var tripName = System.Net.WebUtility.HtmlEncode(state.TripName);

        var msg =
            "🎆🎇✨🎆🎇✨🎆🎇✨🎆🎇✨🎆🎇✨\n\n" +
            "🏆 <b>ALL 51 STATES COLLECTED!</b> 🏆\n\n" +
            "🌟🌟🌟 <b>LEGENDARY ACHIEVEMENT UNLOCKED!</b> 🌟🌟🌟\n\n" +
            "You and your crew have spotted license plates from every single US state — " +
            "from the frozen tundra of <b>Alaska</b> to the tropical shores of <b>Hawaii</b>, " +
            "from the mighty coasts of <b>California</b> to the historic streets of <b>Maine</b>! " +
            "You've conquered all 51! 🇺🇸\n\n" +
            "━━━━━━━━━━━━━━━━━━━━━━\n" +
            $"🗺️ <b>TRIP: {tripName}</b>\n" +
            $"📅 Started: {startedAt}\n" +
            $"⏱️ Duration: {durationText}\n" +
            "🏁 Final score: <b>51 / 51 states</b>\n" +
            "━━━━━━━━━━━━━━━━━━━━━━";

        if (leaderboard.Count > 0)
        {
            msg += "\n\n🏆 <b>FINAL LEADERBOARD:</b>\n" + string.Join("\n", leaderboard);
        }

        msg +=
            $"\n\n🗺️ <b>ALL 51 STATES SPOTTED:</b>\n{allStatesList}\n\n" +
            "🎉🥳🎊 <b>CONGRATULATIONS, ROAD TRIP LEGENDS!</b> 🎊🥳🎉\n\n" +
            "🎆🎇✨🎆🎇✨🎆🎇✨🎆🎇✨🎆🎇✨";

        await _bot.SendMessage(chatId, msg, parseMode: ParseMode.Html);
    }

    private static string DisplayName(Telegram.Bot.Types.User? user)
    {
        if (user is null) return string.Empty;
        var name = $"{user.FirstName} {user.LastName}".Trim();
        return name.Length > 0 ? name : user.Username ?? string.Empty;
    }

    private async Task<string> HandleStatus(long chatId)
    {
        var state = await _stateService.GetOrCreateAsync(chatId);
        var sightings = _stateService.DeserializeSightings(state.SeenStatesJson);

        if (sightings.Count == 0)
            return "No plates logged yet! Use /saw CA to log your first one.";

        var stateList = string.Join(", ", sightings.Select(s => s.State).OrderBy(s => s));
        var bar = BuildProgressBar(sightings.Count, 51);

        var startedAt = state.StartedAt.ToString("MMM d, yyyy 'at' h:mm tt UTC");
        var result = $"🗺 <b>{System.Net.WebUtility.HtmlEncode(state.TripName)}</b>\n" +
                     $"Started: {startedAt}\n" +
                     $"{bar} {sightings.Count}/51\n\n" +
                     $"<b>Found:</b> {stateList}";

        var leaderboard = sightings
            .Where(s => s.UserId != 0)
            .GroupBy(s => s.UserId)
            .OrderByDescending(g => g.Count())
            .Select(g =>
            {
                var spotterName = g.Select(s => s.UserName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Unknown";
                return $"{System.Net.WebUtility.HtmlEncode(spotterName)} — {g.Count()}";
            })
            .ToList();

        if (leaderboard.Count > 0)
            result += "\n\n<b>Leaderboard:</b>\n" +
                      string.Join("\n", leaderboard.Select((line, i) => $"{i + 1}. {line}"));

        return result;
    }

    private async Task<string> HandleMissing(long chatId)
    {
        var state = await _stateService.GetOrCreateAsync(chatId);
        var sightings = _stateService.DeserializeSightings(state.SeenStatesJson);
        var seenAbbrs = sightings.Select(s => s.State).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = AllStates
            .Where(s => !seenAbbrs.Contains(s))
            .OrderBy(s => s)
            .ToList();

        if (missing.Count == 0)
            return "🎉 You've found all 51 states! Nothing missing!";

        var list = string.Join(", ", missing);
        return $"🔍 <b>{missing.Count} states still needed:</b>\n{list}";
    }

    private async Task<string> HandleUndo(long chatId)
    {
        var state = await _stateService.GetOrCreateAsync(chatId);
        var sightings = _stateService.DeserializeSightings(state.SeenStatesJson);

        if (sightings.Count == 0)
            return "Nothing to undo — no states logged yet.";

        var removed = sightings[^1];
        sightings.RemoveAt(sightings.Count - 1);
        state.SeenStatesJson = _stateService.SerializeSightings(sightings);
        await _stateService.SaveAsync(state);

        return $"↩️ Removed <b>{StateNames[removed.State]}</b> ({removed.State}). Back to {sightings.Count}/51.";
    }

    private async Task<string> HandleHistory(long chatId)
    {
        var history = await _stateService.GetHistoryAsync(chatId);
        if (history.Count == 0)
            return "No previous trips yet. Start a new trip with /newtrip!";

        var lines = history.Take(25).Select((t, i) =>
        {
            var sightings = _stateService.DeserializeSightings(t.SeenStatesJson);
            var date = t.StartedAt.ToString("MMM d, yyyy");
            var name = System.Net.WebUtility.HtmlEncode(t.TripName);
            var topSpotter = sightings
                .Where(s => s.UserId != 0)
                .GroupBy(s => s.UserId)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Select(s => s.UserName).FirstOrDefault(userName => !string.IsNullOrEmpty(userName)) ?? "Unknown")
                .FirstOrDefault();
            var mvp = topSpotter is not null ? $" 🏆 {System.Net.WebUtility.HtmlEncode(topSpotter)}" : "";
            return $"{i + 1}. <b>{name}</b> ({date}) — {sightings.Count}/51 states{mvp}";
        });

        return "📋 <b>Trip History</b>\n\n" + string.Join("\n", lines);
    }

    private static string GetHelp() =>
        "<b>License Plate Game 🚗</b>\n\n" +
        "/saw CA — log a state you spotted (abbreviation or full name)\n" +
        "/status — see your progress\n" +
        "/missing — see what's left\n" +
        "/undo — remove the last logged state\n" +
        "/newtrip [name] — start a fresh trip\n" +
        "/history — view results from previous trips\n" +
        "/help — show this message";

    private static string BuildProgressBar(int current, int total)
    {
        var filled = (int)Math.Round((double)current / total * 10);
        return "[" + new string('█', filled) + new string('░', 10 - filled) + "]";
    }
}
