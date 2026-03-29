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
    })
    .Build();

host.Run();
