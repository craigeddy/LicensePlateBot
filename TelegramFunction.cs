using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using Telegram.Bot.Types;

public class TelegramFunction
{
    private readonly BotCommandHandler _handler;
    private readonly ILogger<TelegramFunction> _logger;

    public TelegramFunction(BotCommandHandler handler, ILogger<TelegramFunction> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [Function("TelegramWebhook")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "telegram")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body))
                return req.CreateResponse(HttpStatusCode.BadRequest);

            var update = JsonSerializer.Deserialize<Update>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (update is not null)
                await _handler.HandleUpdateAsync(update);

            return req.CreateResponse(HttpStatusCode.OK);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Telegram update");
            // Always return 200 to Telegram so it doesn't retry
            return req.CreateResponse(HttpStatusCode.OK);
        }
    }
}
