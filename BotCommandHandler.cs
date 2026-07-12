using LicensePlateBot.Models;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

public class BotCommandHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly TripStateService _stateService;
    private readonly long? _adminUserId;

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
        new() { Command = "newtrip",  Description = "Start a fresh collaborative trip" },
        new() { Command = "newrace",  Description = "Start a race — each player collects all 51 states independently" },
        new() { Command = "history",  Description = "View results from previous trips" },
        new() { Command = "help",     Description = "Show available commands" },
    ];

    public BotCommandHandler(ITelegramBotClient bot, TripStateService stateService, IConfiguration config)
    {
        _bot = bot;
        _stateService = stateService;
        _adminUserId = long.TryParse(config["AdminTelegramUserId"], out var id) ? id : null;
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
                "/newrace"             => await HandleNewRace(chatId, args),
                "/saw"                 => await HandleSaw(chatId, args, message.From),
                "/skip"                => await HandleSkip(chatId, args, message.From),
                "/status"              => await HandleStatus(chatId),
                "/missing"             => await HandleMissing(chatId, message.From),
                "/undo"                => await HandleUndo(chatId, message.From),
                "/history"             => await HandleHistory(chatId),
                "/help"                => await HandleHelp(chatId),
                "/broadcast"           => await HandleBroadcast(chatId, args, message.From),
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
            else if (state.PendingCommand == "skip")
            {
                state.PendingCommand = null;
                await _stateService.SaveAsync(state);
                reply = await HandleSkip(chatId, parts, message.From);
            }
            else if (state.PendingCommand is { } pc && pc.StartsWith("newtrip:", StringComparison.Ordinal))
            {
                var pendingDefault = pc["newtrip:".Length..];
                state.PendingCommand = null;
                await _stateService.SaveAsync(state);
                reply = await HandleNewTrip(chatId, parts, pendingDefault);
            }
            else if (state.PendingCommand is { } pc2 && pc2.StartsWith("newrace:", StringComparison.Ordinal))
            {
                var pendingDefault = pc2["newrace:".Length..];
                state.PendingCommand = null;
                await _stateService.SaveAsync(state);
                reply = await HandleNewRace(chatId, parts, pendingDefault);
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

    private async Task<string?> HandleNewRace(long chatId, string[] args, string? pendingDefault = null)
    {
        if (args.Length == 0)
        {
            var defaultName = $"Race {DateTime.UtcNow.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture)}";
            var pending = await _stateService.GetOrCreateAsync(chatId);
            pending.PendingCommand = $"newrace:{defaultName}";
            await _stateService.SaveAsync(pending);
            await _bot.SendMessage(chatId,
                $"What would you like to name this race? Reply with a name, or send <b>skip</b> to use \"{defaultName}\".",
                parseMode: ParseMode.Html,
                replyMarkup: new ForceReplyMarkup());
            return null;
        }

        var raceName = string.Join(" ", args).Equals("skip", StringComparison.OrdinalIgnoreCase)
            ? (pendingDefault ?? $"Race {DateTime.UtcNow:MM/dd/yyyy}")
            : string.Join(" ", args);
        await _stateService.ResetAsync(chatId, raceName, raceMode: true);
        return $"🏁 <b>Race started: {System.Net.WebUtility.HtmlEncode(raceName)}</b>\n\n" +
               "Each player collects all 51 plates independently — first to finish wins! 🏆\n\n" +
               "Use /saw CA to log a plate. You can /skip states before logging your first one.";
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
            abbr = input.ToUpperInvariant();
        else if (StateNameToAbbr.TryGetValue(input, out var matched))
            abbr = matched;
        else
            return $"❓ <b>{input}</b> isn't a recognized US state. Use a 2-letter abbreviation (CA) or full name (California).";

        var state = await _stateService.GetOrCreateAsync(chatId);
        state.PendingCommand = null;
        var sightings = _stateService.DeserializeSightings(state.SeenStatesJson);
        var displayName = DisplayName(from);
        var userId = from?.Id ?? 0;

        var skippedStates = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);
        if (skippedStates.Any(s => s.Equals(abbr, StringComparison.OrdinalIgnoreCase)))
            return $"⏭️ <b>{StateNames[abbr]}</b> ({abbr}) was skipped and doesn't count toward your total.";

        if (state.RaceMode)
        {
            if (from is null)
                return "⚠️ Can't log a plate in race mode — your Telegram identity couldn't be determined.";

            // Per-player duplicate check
            if (sightings.Any(s => s.State.Equals(abbr, StringComparison.OrdinalIgnoreCase) && s.UserId == userId))
            {
                var myCount = sightings.Count(s => s.UserId == userId && AllStates.Contains(s.State));
                var myTarget = AllStates.Count - skippedStates.Count;
                return $"👀 You already logged <b>{StateNames[abbr]}</b> ({abbr})! Your count: {myCount}/{myTarget}.";
            }

            sightings.Add(new SightingRecord(abbr, userId, displayName));
            state.SeenStatesJson = _stateService.SerializeSightings(sightings);
            await _stateService.SaveAsync(state);

            var myTarget2 = AllStates.Count - skippedStates.Count;
            var myStates = sightings
                .Where(s => s.UserId == userId)
                .Select(s => s.State)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var myCount2 = myStates.Count;
            var credit = displayName is { Length: > 0 } ? $" by {System.Net.WebUtility.HtmlEncode(displayName)}" : "";

            // Check if this player has finished
            var requiredStates = AllStates.Where(s => !skippedStates.Contains(s, StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (requiredStates.IsSubsetOf(myStates))
            {
                await HandleRaceFinisher(chatId, state, sightings, from, displayName);
                return $"✅ <b>{StateNames[abbr]}</b> ({abbr}) spotted{credit}!\n🏁 <b>{myCount2}/{myTarget2} — YOU FINISHED!</b> 🎆";
            }

            var remaining = myTarget2 - myCount2;
            return $"✅ <b>{StateNames[abbr]}</b> ({abbr}) spotted{credit}!\nYour count: {myCount2}/{myTarget2} — {remaining} to go.";
        }
        else
        {
            // Collaborative mode
            var existing = sightings.FirstOrDefault(s => s.State.Equals(abbr, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var spotter = existing.UserName is { Length: > 0 } name ? $" — spotted by {System.Net.WebUtility.HtmlEncode(name)}" : "";
                var currentTarget = 51 - skippedStates.Count;
                return $"👀 Already got <b>{StateNames[abbr]}</b> ({abbr}){spotter}! That's {sightings.Count}/{currentTarget}.";
            }

            sightings.Add(new SightingRecord(abbr, userId, displayName));
            state.SeenStatesJson = _stateService.SerializeSightings(sightings);
            await _stateService.SaveAsync(state);

            var target = 51 - skippedStates.Count;
            var remaining = target - sightings.Count;
            var credit = displayName is { Length: > 0 } ? $" by {System.Net.WebUtility.HtmlEncode(displayName)}" : "";

            if (sightings.Count == target)
            {
                await SendAllStatesFoundCelebrationAsync(chatId, state, sightings, skippedStates);
                return $"✅ <b>{StateNames[abbr]}</b> ({abbr}) spotted{credit}!\n🏁 <b>{target}/{target} — COMPLETE!</b> 🎆";
            }

            return $"✅ <b>{StateNames[abbr]}</b> ({abbr}) spotted{credit}!\n{sightings.Count}/{target} plates found — {remaining} to go.";
        }
    }

    private async Task<string?> HandleSkip(long chatId, string[] args, Telegram.Bot.Types.User? from)
    {
        if (args.Length == 0)
        {
            var pending = await _stateService.GetOrCreateAsync(chatId);
            pending.PendingCommand = "skip";
            await _stateService.SaveAsync(pending);
            await _bot.SendMessage(chatId,
                "Which state do you want to skip? Reply with the 2-letter abbreviation or full name (e.g. HI, Hawaii).",
                replyMarkup: new ForceReplyMarkup());
            return null;
        }

        var input = string.Join(" ", args);
        string abbr;
        if (AllStates.Contains(input))
            abbr = input.ToUpperInvariant();
        else if (StateNameToAbbr.TryGetValue(input, out var matched))
            abbr = matched;
        else
            return $"❓ <b>{System.Net.WebUtility.HtmlEncode(input)}</b> isn't a recognized US state. Use a 2-letter abbreviation (HI) or full name (Hawaii).";

        var state = await _stateService.GetOrCreateAsync(chatId);
        state.PendingCommand = null;
        var sightings = _stateService.DeserializeSightings(state.SeenStatesJson);

        if (state.RaceMode)
        {
            // Lock skips once any plate has been logged
            if (sightings.Count > 0)
                return "⚠️ Skips are locked once the race has started (first plate has been logged).";

            var sharedSkipped = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);

            if (sightings.Any(s => s.State.Equals(abbr, StringComparison.OrdinalIgnoreCase)))
                return $"❓ <b>{StateNames[abbr]}</b> ({abbr}) has already been spotted — can't skip a state that's been found.";

            if (sharedSkipped.Any(s => s.Equals(abbr, StringComparison.OrdinalIgnoreCase)))
                return $"ℹ️ <b>{StateNames[abbr]}</b> ({abbr}) is already on the skip list.";

            if (sharedSkipped.Count >= 50)
                return "⚠️ You must keep at least one state required to complete the race.";

            sharedSkipped.Add(abbr);
            state.SkippedStatesJson = _stateService.SerializeSkippedStates(sharedSkipped);
            await _stateService.SaveAsync(state);

            var target = AllStates.Count - sharedSkipped.Count;
            return $"⏭️ <b>{StateNames[abbr]}</b> ({abbr}) skipped for everyone. You now need {target} plates to win the race.";
        }
        else
        {
            var skipped = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);

            if (sightings.Any(s => s.State.Equals(abbr, StringComparison.OrdinalIgnoreCase)))
                return $"❓ You've already spotted <b>{StateNames[abbr]}</b> ({abbr}) — can't skip a state you've already found.";

            if (skipped.Any(s => s.Equals(abbr, StringComparison.OrdinalIgnoreCase)))
                return $"ℹ️ <b>{StateNames[abbr]}</b> ({abbr}) is already on the skip list.";

            if (skipped.Count >= 50)
                return "⚠️ You must keep at least one state required to complete the game.";

            skipped.Add(abbr);
            state.SkippedStatesJson = _stateService.SerializeSkippedStates(skipped);
            await _stateService.SaveAsync(state);

            var target = 51 - skipped.Count;
            return $"⏭️ <b>{StateNames[abbr]}</b> ({abbr}) skipped. You now need {target} plates to complete the game.";
        }
    }

    private async Task HandleRaceFinisher(long chatId, TripState state, List<SightingRecord> sightings, Telegram.Bot.Types.User? from, string displayName)
    {
        var userId = from?.Id ?? 0;
        var finishers = _stateService.DeserializeFinishers(state.FinishersJson);

        // Guard against double-finisher (e.g. two rapid /saw calls)
        if (finishers.Any(f => f.UserId == userId))
            return;

        finishers.Add(new RaceFinisher(userId, displayName, DateTimeOffset.UtcNow));
        state.FinishersJson = _stateService.SerializeFinishers(finishers);
        await _stateService.SaveAsync(state);

        var placement = finishers.Count;
        var placementText = Ordinal(placement);

        var duration = DateTimeOffset.UtcNow - state.StartedAt;
        var durationText = FormatDuration(duration);
        var tripName = System.Net.WebUtility.HtmlEncode(state.TripName);
        var safeDisplayName = System.Net.WebUtility.HtmlEncode(displayName is { Length: > 0 } ? displayName : "Someone");

        if (placement == 1)
        {
            // Load this player's states for the found list
            var playerSkips = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);
            var myStates = sightings
                .Where(s => s.UserId == userId)
                .Select(s => s.State)
                .OrderBy(s => s);
            var statesList = string.Join(", ", myStates);
            var myTarget = AllStates.Count - playerSkips.Count;

            var html =
                $"<h1>🏆 {safeDisplayName} WINS THE RACE! 🏆</h1>" +
                $"<h2>🥇 First to collect all {myTarget} plates!</h2>" +
                $"<h3>🗺️ {tripName}</h3>" +
                "<ul>" +
                $"<li>📅 Race started: {state.StartedAt:MMM d, yyyy}</li>" +
                $"<li>⏱️ Time to finish: {durationText}</li>" +
                $"<li>🏁 Final score: <b>{myTarget} / {myTarget} plates</b></li>" +
                "</ul>" +
                $"<h3>🗺️ All {myTarget} plates</h3>" +
                $"<p>{statesList}</p>" +
                "<p>🏁 Race continues — who will finish 2nd? Keep spotting!</p>";

            await SendRichHtmlAsync(chatId, html);
        }
        else
        {
            // Build finisher standings table
            var standingsRows = finishers.Select((f, i) =>
            {
                var medal = i switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => $"{i + 1}." };
                var finishDate = f.FinishedAt.ToString("MMM d");
                return $"<tr><td>{medal}</td><td>{System.Net.WebUtility.HtmlEncode(f.UserName is { Length: > 0 } n ? n : "Unknown")}</td><td>{finishDate}</td></tr>";
            });

            var html =
                $"<h2>🎉 {safeDisplayName} finishes in <b>{placementText} place</b>!</h2>" +
                $"<p>⏱️ Time to finish: {durationText}</p>" +
                "<h3>Race Standings</h3><table>" +
                "<tr><th>#</th><th>Player</th><th>Finished</th></tr>" +
                string.Join("", standingsRows) +
                "</table>";

            await SendRichHtmlAsync(chatId, html);
        }
    }

    private async Task SendAllStatesFoundCelebrationAsync(long chatId, TripState state, List<SightingRecord> sightings, List<string> skippedStates)
    {
        var duration = DateTimeOffset.UtcNow - state.StartedAt;
        var durationText = FormatDuration(duration);

        var leaderboard = sightings
            .Where(s => s.UserId != 0)
            .GroupBy(s => s.UserId)
            .OrderByDescending(g => g.Count())
            .Select((g, i) =>
            {
                var spotterName = g.Select(s => s.UserName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Unknown";
                var medal = i switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => $"{i + 1}." };
                return $"<tr><td>{medal}</td><td>{System.Net.WebUtility.HtmlEncode(spotterName)}</td><td>{g.Count()}</td></tr>";
            })
            .ToList();

        var target = 51 - skippedStates.Count;
        var foundStatesList = string.Join(", ", sightings.Select(s => s.State).OrderBy(s => s));
        var startedAt = state.StartedAt.ToString("MMM d, yyyy");
        var tripName = System.Net.WebUtility.HtmlEncode(state.TripName);

        var html =
            $"<h1>🏆 ALL {target} PLATES COLLECTED! 🏆</h1>" +
            "<h2>🌟 LEGENDARY ACHIEVEMENT UNLOCKED! 🌟</h2>" +
            $"<p>You and your crew have spotted license plates from all {target} required states! " +
            "You've conquered every plate on your list! 🇺🇸</p>" +
            $"<h3>🗺️ {tripName}</h3>" +
            "<ul>" +
            $"<li>📅 Started: {startedAt}</li>" +
            $"<li>⏱️ Duration: {durationText}</li>" +
            $"<li>🏁 Final score: <b>{target} / {target} plates</b></li>" +
            "</ul>";

        if (leaderboard.Count > 0)
        {
            html += "<h3>🏆 Final Leaderboard</h3><table>" +
                    "<tr><th>#</th><th>Player</th><th>States</th></tr>" +
                    string.Join("", leaderboard) +
                    "</table>";
        }

        html +=
            $"<h3>🗺️ All {target} plates spotted</h3>" +
            $"<p>{foundStatesList}</p>" +
            "<h2>🎉 CONGRATULATIONS, ROAD TRIP LEGENDS! 🎉</h2>";

        await SendRichHtmlAsync(chatId, html);
    }

    // Sends a Telegram rich message (tables, headings, lists) via the Bot API's
    // sendRichMessage method, introduced in Bot API 10.1.
    private async Task SendRichHtmlAsync(long chatId, string html)
    {
        await _bot.SendRichMessage(chatId, new InputRichMessage { Html = html });
    }

    private static string DisplayName(Telegram.Bot.Types.User? user)
    {
        if (user is null) return string.Empty;
        var name = $"{user.FirstName} {user.LastName}".Trim();
        return name.Length > 0 ? name : user.Username ?? string.Empty;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            var days = (int)duration.TotalDays;
            var hours = duration.Hours;
            return hours > 0 ? $"{days} day{(days == 1 ? "" : "s")} and {hours} hour{(hours == 1 ? "" : "s")}" : $"{days} day{(days == 1 ? "" : "s")}";
        }
        if (duration.TotalHours >= 1)
        {
            var hrs = (int)duration.TotalHours;
            return $"{hrs} hour{(hrs == 1 ? "" : "s")}";
        }
        var mins = Math.Max(1, (int)duration.TotalMinutes);
        return $"{mins} minute{(mins == 1 ? "" : "s")}";
    }

    private async Task<string?> HandleStatus(long chatId)
    {
        var state = await _stateService.GetOrCreateAsync(chatId);
        var sightings = _stateService.DeserializeSightings(state.SeenStatesJson);

        if (state.RaceMode)
        {
            var finishers = _stateService.DeserializeFinishers(state.FinishersJson);
            var sharedSkipped = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);
            var sharedTarget = AllStates.Count - sharedSkipped.Count;

            if (sightings.Count == 0 && finishers.Count == 0)
                return "No plates logged yet! Use /saw CA to log your first one. You can /skip states before the first plate is logged.";

            // Build per-player standings
            var playerGroups = sightings
                .Where(s => s.UserId != 0)
                .GroupBy(s => s.UserId)
                .Select(g =>
                {
                    var name = g.Select(s => s.UserName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Unknown";
                    var count = g.Count(s => AllStates.Contains(s.State));
                    var finisher = finishers.FirstOrDefault(f => f.UserId == g.Key);
                    return (UserId: g.Key, Name: name, Count: count, Target: sharedTarget, Finisher: finisher);
                })
                .OrderBy(p => p.Finisher is null ? 1 : 0)
                .ThenBy(p => p.Finisher?.FinishedAt)
                .ThenByDescending(p => p.Count)
                .ToList();

            var startedAt = state.StartedAt.ToString("MMM d, yyyy 'at' h:mm tt UTC");
            var html = $"<h2>🏁 {System.Net.WebUtility.HtmlEncode(state.TripName)} — Race Mode</h2>" +
                       $"<p>Started: {startedAt}</p>";

            if (playerGroups.Count > 0)
            {
                var leader = playerGroups.First();
                var leaderBar = BuildProgressBar(leader.Count, leader.Target);
                html += $"<p>Leader: {leaderBar} {leader.Count}/{leader.Target}</p>";

                var rows = playerGroups.Select((p, i) =>
                {
                    var medal = i switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => $"{i + 1}." };
                    var status = p.Finisher is not null
                        ? $"✅ Finished {p.Finisher.FinishedAt:MMM d}"
                        : $"{p.Count}/{p.Target}";
                    return $"<tr><td>{medal}</td><td>{System.Net.WebUtility.HtmlEncode(p.Name)}</td><td>{status}</td></tr>";
                });

                html += "<h3>Race Standings</h3><table>" +
                        "<tr><th>#</th><th>Player</th><th>Progress</th></tr>" +
                        string.Join("", rows) +
                        "</table>";
            }
            else
            {
                html += "<p>No plates logged yet — use /saw CA to start!</p>";
            }

            if (sharedSkipped.Count > 0)
                html += $"<p><b>Skipped by all:</b> {string.Join(", ", sharedSkipped.OrderBy(s => s))}</p>";

            await SendRichHtmlAsync(chatId, html);
            return null;
        }
        else
        {
            var skipped = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);

            if (sightings.Count == 0 && skipped.Count == 0)
                return "No plates logged yet! Use /saw CA to log your first one.";

            var target = 51 - skipped.Count;
            var stateList = sightings.Count > 0
                ? string.Join(", ", sightings.Select(s => s.State).OrderBy(s => s))
                : "none yet";
            var bar = BuildProgressBar(sightings.Count, target);

            var startedAt = state.StartedAt.ToString("MMM d, yyyy 'at' h:mm tt UTC");
            var html = $"<h2>🗺 {System.Net.WebUtility.HtmlEncode(state.TripName)}</h2>" +
                       $"<p>Started: {startedAt}</p>" +
                       $"<p>{bar} {sightings.Count}/{target}</p>" +
                       $"<p><b>Found:</b> {stateList}</p>";

            if (skipped.Count > 0)
                html += $"<p><b>Skipped:</b> {string.Join(", ", skipped.OrderBy(s => s))}</p>";

            var leaderboard = sightings
                .Where(s => s.UserId != 0)
                .GroupBy(s => s.UserId)
                .OrderByDescending(g => g.Count())
                .Select(g =>
                {
                    var spotterName = g.Select(s => s.UserName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Unknown";
                    return (Name: System.Net.WebUtility.HtmlEncode(spotterName), Count: g.Count());
                })
                .ToList();

            if (leaderboard.Count > 0)
            {
                html += "<h3>Leaderboard</h3><table><tr><th>#</th><th>Player</th><th>States</th></tr>" +
                        string.Join("", leaderboard.Select((entry, i) => $"<tr><td>{i + 1}</td><td>{entry.Name}</td><td>{entry.Count}</td></tr>")) +
                        "</table>";
            }

            await SendRichHtmlAsync(chatId, html);
            return null;
        }
    }

    private async Task<string> HandleMissing(long chatId, Telegram.Bot.Types.User? from)
    {
        var state = await _stateService.GetOrCreateAsync(chatId);
        var sightings = _stateService.DeserializeSightings(state.SeenStatesJson);

        if (state.RaceMode)
        {
            if (from is null)
                return "⚠️ Can't check missing states in race mode — your Telegram identity couldn't be determined.";

            var userId = from.Id;
            var sharedSkipped = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);
            var mySeenStates = sightings
                .Where(s => s.UserId == userId)
                .Select(s => s.State)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = AllStates
                .Where(s => !mySeenStates.Contains(s) && !sharedSkipped.Contains(s, StringComparer.OrdinalIgnoreCase))
                .OrderBy(s => s)
                .ToList();

            if (missing.Count == 0)
                return "🎉 You've found all your required plates! Nothing missing!";

            var myTarget = AllStates.Count - sharedSkipped.Count;
            var list = string.Join(", ", missing);
            return $"🔍 <b>Your {missing.Count} missing states ({mySeenStates.Count}/{myTarget}):</b>\n{list}";
        }
        else
        {
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
    }

    private async Task<string> HandleUndo(long chatId, Telegram.Bot.Types.User? from)
    {
        var state = await _stateService.GetOrCreateAsync(chatId);
        var sightings = _stateService.DeserializeSightings(state.SeenStatesJson);
        var skipped = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);

        if (state.RaceMode)
        {
            if (from is null)
                return "⚠️ Can't undo in race mode — your Telegram identity couldn't be determined.";

            var userId = from.Id;
            var myEntries = sightings
                .Select((s, i) => (s, i))
                .Where(x => x.s.UserId == userId)
                .ToList();

            if (myEntries.Count == 0)
                return "Nothing to undo — you haven't logged any states yet.";

            var (lastEntry, lastIndex) = myEntries[^1];
            sightings.RemoveAt(lastIndex);
            state.SeenStatesJson = _stateService.SerializeSightings(sightings);

            // If they were a finisher, remove them from the finisher list
            var finishers = _stateService.DeserializeFinishers(state.FinishersJson);
            if (finishers.RemoveAll(f => f.UserId == userId) > 0)
                state.FinishersJson = _stateService.SerializeFinishers(finishers);

            await _stateService.SaveAsync(state);

            var sharedSkipped = _stateService.DeserializeSkippedStates(state.SkippedStatesJson);
            var myTarget = AllStates.Count - sharedSkipped.Count;
            var myCount = sightings.Count(s => s.UserId == userId);
            return $"↩️ Removed <b>{StateNames[lastEntry.State]}</b> ({lastEntry.State}). Your count: {myCount}/{myTarget}.";
        }
        else
        {
            if (sightings.Count == 0)
                return "Nothing to undo — no states logged yet.";

            var removed = sightings[^1];
            sightings.RemoveAt(sightings.Count - 1);
            state.SeenStatesJson = _stateService.SerializeSightings(sightings);
            await _stateService.SaveAsync(state);

            var target = 51 - skipped.Count;
            return $"↩️ Removed <b>{StateNames[removed.State]}</b> ({removed.State}). Back to {sightings.Count}/{target}.";
        }
    }

    private async Task<string?> HandleHistory(long chatId)
    {
        var history = await _stateService.GetHistoryAsync(chatId);
        if (history.Count == 0)
            return "No previous trips yet. Start a new trip with /newtrip!";

        var rows = history.Take(25).Select((t, i) =>
        {
            var sightings = _stateService.DeserializeSightings(t.SeenStatesJson);
            var skipped = _stateService.DeserializeSkippedStates(t.SkippedStatesJson);
            var tripTarget = 51 - skipped.Count;
            var date = t.StartedAt.ToString("MMM d, yyyy");
            var name = System.Net.WebUtility.HtmlEncode(t.TripName);

            string scoreCell, mvpCell, skippedCell;

            if (t.RaceMode)
            {
                var finishers = _stateService.DeserializeFinishers(t.FinishersJson);
                scoreCell = finishers.Count == 1 ? "1 finisher" : $"{finishers.Count} finishers";
                var winner = finishers.FirstOrDefault();
                mvpCell = winner is not null ? $"🏆 {System.Net.WebUtility.HtmlEncode(winner.UserName is { Length: > 0 } n ? n : "Unknown")}" : "—";
                skippedCell = "Race Mode";
            }
            else
            {
                scoreCell = $"{sightings.Count}/{tripTarget}";
                var topSpotter = sightings
                    .Where(s => s.UserId != 0)
                    .GroupBy(s => s.UserId)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Select(s => s.UserName).FirstOrDefault(userName => !string.IsNullOrEmpty(userName)) ?? "Unknown")
                    .FirstOrDefault();
                mvpCell = topSpotter is not null ? $"🏆 {System.Net.WebUtility.HtmlEncode(topSpotter)}" : "—";
                skippedCell = skipped.Count > 0 ? string.Join(", ", skipped.OrderBy(s => s)) : "—";
            }

            return $"<tr><td>{i + 1}</td><td>{name}</td><td>{date}</td><td>{scoreCell}</td><td>{mvpCell}</td><td>{skippedCell}</td></tr>";
        });

        var html = "<h2>📋 Trip History</h2><table>" +
                   "<tr><th>#</th><th>Trip</th><th>Date</th><th>Score</th><th>MVP/Winner</th><th>Skipped</th></tr>" +
                   string.Join("", rows) +
                   "</table>";

        await SendRichHtmlAsync(chatId, html);
        return null;
    }

    private async Task<string?> HandleHelp(long chatId)
    {
        var html = "<h2>License Plate Game 🚗</h2><ul>" +
                   "<li><code>/saw CA</code> — log a state you spotted (abbreviation or full name)</li>" +
                   "<li><code>/skip HI</code> — skip a state so it's not required to complete the game</li>" +
                   "<li><code>/status</code> — see your progress</li>" +
                   "<li><code>/missing</code> — see what's left</li>" +
                   "<li><code>/undo</code> — remove the last logged state</li>" +
                   "<li><code>/newtrip [name]</code> — start a fresh collaborative trip</li>" +
                   "<li><code>/newrace [name]</code> — start a race where each player collects all 51 states independently; first to finish wins!</li>" +
                   "<li><code>/history</code> — view results from previous trips</li>" +
                   "<li><code>/help</code> — show this message</li>" +
                   "</ul>";

        await SendRichHtmlAsync(chatId, html);
        return null;
    }

    private static string Ordinal(int n)
    {
        var lastTwo = n % 100;
        if (lastTwo >= 11 && lastTwo <= 13) return $"{n}th";
        return (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" };
    }

    private static string BuildProgressBar(int current, int total)
    {
        var filled = (int)Math.Round((double)current / total * 10);
        return "[" + new string('█', filled) + new string('░', 10 - filled) + "]";
    }

    private async Task<string?> HandleBroadcast(long chatId, string[] args, Telegram.Bot.Types.User? from)
    {
        // Silently ignore non-admins
        if (_adminUserId is null || from?.Id != _adminUserId)
            return null;

        if (args.Length == 0)
            return "Usage: /broadcast &lt;message&gt;\n       /broadcast &lt;chatId&gt; &lt;message&gt;";

        // If the first arg looks like a chat ID, target that chat only
        long? targetChatId = null;
        string[] messageArgs = args;
        if (args.Length > 1 && long.TryParse(args[0], out var parsedId))
        {
            targetChatId = parsedId;
            messageArgs = args[1..];
        }

        var text = string.Join(" ", messageArgs);
        if (string.IsNullOrWhiteSpace(text))
            return "Usage: /broadcast &lt;message&gt;\n       /broadcast &lt;chatId&gt; &lt;message&gt;";

        var chatIds = targetChatId.HasValue
            ? new List<long> { targetChatId.Value }
            : await _stateService.GetAllChatIdsAsync();

        int sent = 0, failed = 0;
        foreach (var id in chatIds)
        {
            try
            {
                await _bot.SendMessage(id, text);
                sent++;
            }
            catch
            {
                failed++;
            }
        }

        return $"📣 Broadcast sent: {sent} delivered, {failed} failed ({chatIds.Count} total).";
    }
}
