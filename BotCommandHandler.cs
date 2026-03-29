using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

public class BotCommandHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly TripStateService _stateService;

    // All 50 US states
    private static readonly HashSet<string> AllStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA",
        "HI","ID","IL","IN","IA","KS","KY","LA","ME","MD",
        "MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ",
        "NM","NY","NC","ND","OH","OK","OR","PA","RI","SC",
        "SD","TN","TX","UT","VT","VA","WA","WV","WI","WY"
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
        {"VA","Virginia"},{"WA","Washington"},{"WV","West Virginia"},{"WI","Wisconsin"},{"WY","Wyoming"}
    };

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
        var isCommand = parts[0].StartsWith('/');
        var command = parts[0].ToLower().TrimEnd('@'); // handle /cmd@botname format
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
                "/saw"                 => await HandleSaw(chatId, args),
                "/status"              => await HandleStatus(chatId),
                "/missing"             => await HandleMissing(chatId),
                "/undo"                => await HandleUndo(chatId),
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
                reply = await HandleSaw(chatId, parts);
            }
            else
            {
                reply = null;
            }
        }

        if (reply is not null)
            await _bot.SendMessage(chatId, reply, parseMode: ParseMode.Html);
    }

    private async Task<string> HandleNewTrip(long chatId, string[] args)
    {
        var tripName = args.Length > 0 ? string.Join(" ", args) : "Road Trip";
        await _stateService.ResetAsync(chatId, tripName);
        return $"🚗 <b>New trip started: {tripName}</b>\n\nReady to collect all 50 states! Use /saw CA to log a plate.";
    }

    private async Task<string?> HandleSaw(long chatId, string[] args)
    {
        if (args.Length == 0)
        {
            var pending = await _stateService.GetOrCreateAsync(chatId);
            pending.PendingCommand = "saw";
            await _stateService.SaveAsync(pending);
            await _bot.SendMessage(chatId,
                "Which state did you spot? Reply with the 2-letter abbreviation (e.g. CA, TX, NY).",
                replyMarkup: new ForceReplyMarkup());
            return null;
        }

        var abbr = args[0].ToUpper();
        if (!AllStates.Contains(abbr))
            return $"❓ <b>{abbr}</b> isn't a recognized US state. Use a 2-letter abbreviation like CA, TX, NY.";

        var state = await _stateService.GetOrCreateAsync(chatId);
        state.PendingCommand = null;
        var seen = _stateService.DeserializeStates(state.SeenStatesJson);

        if (seen.Contains(abbr, StringComparer.OrdinalIgnoreCase))
            return $"👀 Already got <b>{StateNames[abbr]}</b> ({abbr})! That's {seen.Count}/50.";

        seen.Add(abbr);
        state.SeenStatesJson = _stateService.SerializeStates(seen);
        await _stateService.SaveAsync(state);

        var remaining = 50 - seen.Count;
        var congrats = seen.Count == 50 ? "\n\n🎉 <b>YOU GOT ALL 50!</b> 🎉" : "";
        return $"✅ <b>{StateNames[abbr]}</b> ({abbr}) spotted!\n{seen.Count}/50 states found — {remaining} to go.{congrats}";
    }

    private async Task<string> HandleStatus(long chatId)
    {
        var state = await _stateService.GetOrCreateAsync(chatId);
        var seen = _stateService.DeserializeStates(state.SeenStatesJson);

        if (seen.Count == 0)
            return "No plates logged yet! Use /saw CA to log your first one.";

        var sorted = seen.OrderBy(s => s).ToList();
        var stateList = string.Join(", ", sorted.Select(s => $"{s}"));
        var bar = BuildProgressBar(seen.Count, 50);

        return $"🗺 <b>{state.TripName}</b>\n" +
               $"{bar} {seen.Count}/50\n\n" +
               $"<b>Found:</b> {stateList}";
    }

    private async Task<string> HandleMissing(long chatId)
    {
        var state = await _stateService.GetOrCreateAsync(chatId);
        var seen = _stateService.DeserializeStates(state.SeenStatesJson);

        var missing = AllStates
            .Where(s => !seen.Contains(s, StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => s)
            .ToList();

        if (missing.Count == 0)
            return "🎉 You've found all 50 states! Nothing missing!";

        var list = string.Join(", ", missing.Select(s => $"{s}"));
        return $"🔍 <b>{missing.Count} states still needed:</b>\n{list}";
    }

    private async Task<string> HandleUndo(long chatId)
    {
        var state = await _stateService.GetOrCreateAsync(chatId);
        var seen = _stateService.DeserializeStates(state.SeenStatesJson);

        if (seen.Count == 0)
            return "Nothing to undo — no states logged yet.";

        var removed = seen[^1];
        seen.RemoveAt(seen.Count - 1);
        state.SeenStatesJson = _stateService.SerializeStates(seen);
        await _stateService.SaveAsync(state);

        return $"↩️ Removed <b>{StateNames[removed]}</b> ({removed}). Back to {seen.Count}/50.";
    }

    private static string GetHelp() =>
        "<b>License Plate Game 🚗</b>\n\n" +
        "/newtrip [name] — start a fresh trip\n" +
        "/saw CA — log a state you spotted\n" +
        "/status — see your progress\n" +
        "/missing — see what's left\n" +
        "/undo — remove the last logged state\n" +
        "/help — show this message";

    private static string BuildProgressBar(int current, int total)
    {
        var filled = (int)Math.Round((double)current / total * 10);
        return "[" + new string('█', filled) + new string('░', 10 - filled) + "]";
    }
}
