using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var token = context.Configuration["TelegramBotToken"]
            ?? throw new InvalidOperationException("TelegramBotToken is not configured.");

        services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(token));
        services.AddSingleton<TripStateService>();
        services.AddSingleton<BotCommandHandler>();
        services.AddHostedService<BotCommandRegistrar>();
    })
    .Build();

host.Run();

class BotCommandRegistrar(ITelegramBotClient bot, ILogger<BotCommandRegistrar> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await bot.SetMyCommands(BotCommandHandler.Commands, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Best-effort: a Telegram outage or rate-limit should not prevent the
            // function host from starting and serving webhook requests.
            logger.LogWarning(ex, "Failed to register BotFather command list on startup.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
