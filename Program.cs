using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

class BotCommandRegistrar(ITelegramBotClient bot) : IHostedService
{
    public Task StartAsync(CancellationToken ct) =>
        bot.SetMyCommands(BotCommandHandler.Commands, cancellationToken: ct);

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
