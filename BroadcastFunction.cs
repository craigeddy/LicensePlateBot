using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using Telegram.Bot;

public class BroadcastFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ITelegramBotClient _bot;
    private readonly TripStateService _stateService;
    private readonly ILogger<BroadcastFunction> _logger;

    public BroadcastFunction(ITelegramBotClient bot, TripStateService stateService, ILogger<BroadcastFunction> logger)
    {
        _bot = bot;
        _stateService = stateService;
        _logger = logger;
    }

    [Function("Broadcast")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "broadcast")] HttpRequestData req)
    {
        var body = await req.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        BroadcastRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BroadcastRequest>(body, JsonOptions);
        }
        catch
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Message))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("'message' is required.");
            return bad;
        }

        List<long> chatIds = request.ChatId.HasValue
            ? [request.ChatId.Value]
            : await _stateService.GetAllChatIdsAsync();

        int sent = 0, failed = 0;
        foreach (var chatId in chatIds)
        {
            try
            {
                await _bot.SendMessage(chatId, request.Message);
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Broadcast failed for chat {ChatId}", chatId);
                failed++;
            }
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { sent, failed, total = chatIds.Count });
        return response;
    }
}

public record BroadcastRequest(string Message, long? ChatId);
