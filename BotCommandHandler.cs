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
        new() { Command = "skip",     Description = "Skip a state so it's not required to complete (e.g. /skip HI)" },
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
            if (command != "/saw" && command != "/skip")
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
                "/skip"                => await HandleSkip(chatId, args),
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
        return $"🚗 <b>New trip started: {System.Net.WebUtility.HtmlEncode(tripName)}</b>\n\nReady to collect all 51 plates! Use /saw CA to log a plate.";
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
        var skipped = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);

        var existing = sightings.FirstOrDefault(s => s.State.Equals(abbr, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var spotter = existing.UserName is { Length: > 0 } name ? $" — spotted by {System.Net.WebUtility.HtmlEncode(name)}" : "";
            var currentTarget = 51 - skipped.Count;
            return $"👀 Already got <b>{StateNames[abbr]}</b> ({abbr}){spotter}! That's {sightings.Count}/{currentTarget}.";
        }

        // If the state was skipped, automatically un-skip it when the player logs it
        var skippedEntry = skipped.FirstOrDefault(s => s.Equals(abbr, StringComparison.OrdinalIgnoreCase));
        var wasSkipped = skippedEntry is not null;
        if (wasSkipped) skipped.Remove(skippedEntry!);

        var displayName = DisplayName(from);
        sightings.Add(new SightingRecord(abbr, from?.Id ?? 0, displayName));
        state.SeenStatesJson = _stateService.SerializeSightings(sightings);
        state.SkippedStatesJson = _stateService.SerializeSkippedStates(skipped);
        await _stateService.SaveAsync(state);

        var target = 51 - skipped.Count;
        var remaining = target - sightings.Count;
        var credit = displayName is { Length: > 0 } ? $" by {System.Net.WebUtility.HtmlEncode(displayName)}" : "";
        var unskipNote = wasSkipped ? " (removed from skip list)" : "";

        if (sightings.Count == target)
        {
            await SendAllStatesFoundCelebrationAsync(chatId, state, sightings, skipped);
            return $"✅ <b>{StateNames[abbr]}</b> ({abbr}) spotted{credit}!{unskipNote}\n🏁 <b>{target}/{target} — COMPLETE!</b> 🎆";
        }

        return $"✅ <b>{StateNames[abbr]}</b> ({abbr}) spotted{credit}!{unskipNote}\n{sightings.Count}/{target} plates found — {remaining} to go.";
    }

    private async Task<string> HandleSkip(long chatId, string[] args)
    {
        if (args.Length == 0)
            return "Usage: /skip CA — specify a state abbreviation or full name to skip.";

        var input = string.Join(" ", args);
        string abbr;
        if (AllStates.Contains(input))
            abbr = input.ToUpper();
        else if (StateNameToAbbr.TryGetValue(input, out var matched))
            abbr = matched;
        else
            return $"❓ <b>{System.Net.WebUtility.HtmlEncode(input)}</b> isn't a recognized US state. Use a 2-letter abbreviation (HI) or full name (Hawaii).";

        var state = await _stateService.GetOrCreateAsync(chatId);
        var sightings = _stateService.DeserializeSightings(state.SeenStatesJson);
        var skipped = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);

        if (sightings.Any(s => s.State.Equals(abbr, StringComparison.OrdinalIgnoreCase)))
            return $"❓ You've already spotted <b>{StateNames[abbr]}</b> ({abbr}) — can't skip a state you've already found.";

        if (skipped.Any(s => s.Equals(abbr, StringComparison.OrdinalIgnoreCase)))
            return $"ℹ️ <b>{StateNames[abbr]}</b> ({abbr}) is already on the skip list.";

        skipped.Add(abbr);
        state.SkippedStatesJson = _stateService.SerializeSkippedStates(skipped);
        await _stateService.SaveAsync(state);

        var target = 51 - skipped.Count;
        return $"⏭️ <b>{StateNames[abbr]}</b> ({abbr}) skipped. You now need {target} plates to complete the game.";
    }

    private async Task SendAllStatesFoundCelebrationAsync(long chatId, TripState state, List<SightingRecord> sightings, List<string> skippedStates)
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

        var target = 51 - skippedStates.Count;
        var foundStatesList = string.Join(", ", sightings.Select(s => s.State).OrderBy(s => s));
        var startedAt = state.StartedAt.ToString("MMM d, yyyy");
        var tripName = System.Net.WebUtility.HtmlEncode(state.TripName);

        var msg =
            "🎆🎇✨🎆🎇✨🎆🎇✨🎆🎇✨🎆🎇✨\n\n" +
            $"🏆 <b>ALL {target} PLATES COLLECTED!</b> 🏆\n\n" +
            "🌟🌟🌟 <b>LEGENDARY ACHIEVEMENT UNLOCKED!</b> 🌟🌟🌟\n\n" +
            $"You and your crew have spotted license plates from all {target} required states! " +
            "You've conquered every plate on your list! 🇺🇸\n\n" +
            "━━━━━━━━━━━━━━━━━━━━━━\n" +
            $"🗺️ <b>TRIP: {tripName}</b>\n" +
            $"📅 Started: {startedAt}\n" +
            $"⏱️ Duration: {durationText}\n" +
            $"🏁 Final score: <b>{target} / {target} plates</b>\n" +
            "━━━━━━━━━━━━━━━━━━━━━━";

        if (leaderboard.Count > 0)
        {
            msg += "\n\n🏆 <b>FINAL LEADERBOARD:</b>\n" + string.Join("\n", leaderboard);
        }

        msg +=
            $"\n\n🗺️ <b>ALL {target} PLATES SPOTTED:</b>\n{foundStatesList}\n\n" +
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
        var skipped = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);

        if (sightings.Count == 0 && skipped.Count == 0)
            return "No plates logged yet! Use /saw CA to log your first one.";

        var target = 51 - skipped.Count;
        var stateList = sightings.Count > 0
            ? string.Join(", ", sightings.Select(s => s.State).OrderBy(s => s))
            : "none yet";
        var bar = BuildProgressBar(sightings.Count, target);

        var startedAt = state.StartedAt.ToString("MMM d, yyyy 'at' h:mm tt UTC");
        var result = $"🗺 <b>{System.Net.WebUtility.HtmlEncode(state.TripName)}</b>\n" +
                     $"Started: {startedAt}\n" +
                     $"{bar} {sightings.Count}/{target}\n\n" +
                     $"<b>Found:</b> {stateList}";

        if (skipped.Count > 0)
            result += $"\n<b>Skipped:</b> {string.Join(", ", skipped.OrderBy(s => s))}";

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
        var skipped = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);
        var seenOrSkipped = sightings.Select(s => s.State)
            .Concat(skipped)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = AllStates
            .Where(s => !seenOrSkipped.Contains(s))
            .OrderBy(s => s)
            .ToList();

        if (missing.Count == 0)
            return "🎉 You've found all required plates! Nothing missing!";

        var list = string.Join(", ", missing);
        return $"🔍 <b>{missing.Count} states still needed:</b>\n{list}";
    }

    private async Task<string> HandleUndo(long chatId)
    {
        var state = await _stateService.GetOrCreateAsync(chatId);
        var sightings = _stateService.DeserializeSightings(state.SeenStatesJson);
        var skipped = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);

        if (sightings.Count == 0)
            return "Nothing to undo — no states logged yet.";

        var removed = sightings[^1];
        sightings.RemoveAt(sightings.Count - 1);
        state.SeenStatesJson = _stateService.SerializeSightings(sightings);
        await _stateService.SaveAsync(state);

        var target = 51 - skipped.Count;
        return $"↩️ Removed <b>{StateNames[removed.State]}</b> ({removed.State}). Back to {sightings.Count}/{target}.";
    }

    private async Task<string> HandleHistory(long chatId)
    {
        var history = await _stateService.GetHistoryAsync(chatId);
        if (history.Count == 0)
            return "No previous trips yet. Start a new trip with /newtrip!";

        var lines = history.Take(25).Select((t, i) =>
        {
            var sightings = _stateService.DeserializeSightings(t.SeenStatesJson);
            var skipped = _stateService.DeserializeSkippedStates(t.SkippedStatesJson);
            var tripTarget = 51 - skipped.Count;
            var date = t.StartedAt.ToString("MMM d, yyyy");
            var name = System.Net.WebUtility.HtmlEncode(t.TripName);
            var topSpotter = sightings
                .Where(s => s.UserId != 0)
                .GroupBy(s => s.UserId)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Select(s => s.UserName).FirstOrDefault(userName => !string.IsNullOrEmpty(userName)) ?? "Unknown")
                .FirstOrDefault();
            var mvp = topSpotter is not null ? $" 🏆 {System.Net.WebUtility.HtmlEncode(topSpotter)}" : "";
            var skippedNote = skipped.Count > 0 ? $" (skipped: {string.Join(", ", skipped.OrderBy(s => s))})" : "";
            return $"{i + 1}. <b>{name}</b> ({date}) — {sightings.Count}/{tripTarget} plates{mvp}{skippedNote}";
        });

        return "📋 <b>Trip History</b>\n\n" + string.Join("\n", lines);
    }

    private static string GetHelp() =>
        "<b>License Plate Game 🚗</b>\n\n" +
        "/saw CA — log a state you spotted (abbreviation or full name)\n" +
        "/skip HI — skip a state so it's not required to complete the game\n" +
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
