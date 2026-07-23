using System.Collections.Concurrent;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

const string botTokenEnvironmentName = "TELEGRAM_BOT_TOKEN";
const string adminChatIdEnvironmentName = "TELEGRAM_ADMIN_CHAT_ID";

string botToken = GetRequiredEnvironmentVariable(botTokenEnvironmentName);
string adminChatIdText = GetRequiredEnvironmentVariable(adminChatIdEnvironmentName);

if (!long.TryParse(adminChatIdText, out long adminChatId))
{
    throw new InvalidOperationException(
        $"Переменная {adminChatIdEnvironmentName} должна содержать корректный Telegram ID."
    );
}

var botClient = new TelegramBotClient(botToken);

var userSessions = new ConcurrentDictionary<long, TourRequestSession>();

// Не даёт одновременно обрабатывать несколько сообщений одного пользователя.
var chatLocks = new ConcurrentDictionary<long, SemaphoreSlim>();

var shutdownCancellationTokenSource = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdownCancellationTokenSource.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    if (!shutdownCancellationTokenSource.IsCancellationRequested)
    {
        shutdownCancellationTokenSource.Cancel();
    }
};

botClient.OnMessage += HandleMessageAsync;

try
{
    var botInfo = await botClient.GetMe();

    Console.WriteLine($"Бот @{botInfo.Username} запущен.");
    Console.WriteLine("Бот работает в постоянном режиме.");

    await Task.Delay(
        Timeout.Infinite,
        shutdownCancellationTokenSource.Token
    );
}
catch (OperationCanceledException)
    when (shutdownCancellationTokenSource.IsCancellationRequested)
{
    Console.WriteLine("Получена команда на остановку бота.");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Критическая ошибка при запуске бота: {exception}");
    Environment.ExitCode = 1;
}
finally
{
    botClient.OnMessage -= HandleMessageAsync;
    shutdownCancellationTokenSource.Dispose();

    Console.WriteLine("Бот остановлен.");
}

async Task HandleMessageAsync(Message message, UpdateType updateType)
{
    if (message.Text is null)
        return;

    long chatId = message.Chat.Id;

    SemaphoreSlim chatLock = chatLocks.GetOrAdd(
        chatId,
        _ => new SemaphoreSlim(1, 1)
    );

    await chatLock.WaitAsync();

    try
    {
        string text = message.Text.Trim();

        if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
        {
            userSessions[chatId] = new TourRequestSession
            {
                Step = TourRequestStep.Destination
            };

            await botClient.SendMessage(
                chatId: chatId,
                text:
                    "Здравствуйте! 👋\n\n" +
                    "Я помогу оформить заявку на подбор тура.\n\n" +
                    "Куда вы хотите поехать?\n" +
                    "Можно указать одну или несколько стран."
            );

            return;
        }

        if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            userSessions.TryRemove(chatId, out _);

            await botClient.SendMessage(
                chatId: chatId,
                text:
                    "Заполнение заявки отменено.\n" +
                    "Для новой заявки отправьте команду /start."
            );

            return;
        }

        if (!userSessions.TryGetValue(
                chatId,
                out TourRequestSession? session))
        {
            await botClient.SendMessage(
                chatId: chatId,
                text:
                    "Чтобы начать заполнение заявки, " +
                    "отправьте команду /start."
            );

            return;
        }

        switch (session.Step)
        {
            case TourRequestStep.Destination:
                session.Destination = text;
                session.Step = TourRequestStep.Travelers;

                await botClient.SendMessage(
                    chatId: chatId,
                    text:
                        "Сколько человек планирует поехать?\n\n" +
                        "Укажите количество взрослых, детей и животных.\n" +
                        "Например: 2 взрослых, 1 ребёнок, без животных."
                );

                break;

            case TourRequestStep.Travelers:
                session.Travelers = text;
                session.Step = TourRequestStep.Dates;

                await botClient.SendMessage(
                    chatId: chatId,
                    text:
                        "На какие даты нужен тур?\n\n" +
                        "Можно указать конкретные даты или примерный период " +
                        "и продолжительность.\n" +
                        "Например: с 15 по 25 августа или " +
                        "в начале сентября на 10 дней."
                );

                break;

            case TourRequestStep.Dates:
                session.Dates = text;
                session.Step = TourRequestStep.Budget;

                await botClient.SendMessage(
                    chatId: chatId,
                    text:
                        "Какой у вас бюджет на поездку?\n\n" +
                        "Например: до 150 000 рублей."
                );

                break;

            case TourRequestStep.Budget:
                session.Budget = text;
                session.Step = TourRequestStep.Wishes;

                await botClient.SendMessage(
                    chatId: chatId,
                    text:
                        "Есть ли дополнительные пожелания по туру?\n\n" +
                        "Например: первая линия, собственный пляж, " +
                        "питание всё включено.\n\n" +
                        "Если пожеланий нет, напишите: Нет."
                );

                break;

            case TourRequestStep.Wishes:
                session.Wishes = text;

                string adminMessage = BuildAdminMessage(
                    message.From,
                    session
                );

                await botClient.SendMessage(
                    chatId: adminChatId,
                    text: adminMessage,
                    parseMode: ParseMode.Html
                );

                await botClient.SendMessage(
                    chatId: chatId,
                    text:
                        "Спасибо! ✅\n\n" +
                        "Ваша заявка передана специалисту.\n" +
                        "Скоро с вами свяжутся и предложат " +
                        "подходящие варианты тура.\n\n" +
                        "Для оформления новой заявки отправьте /start."
                );

                userSessions.TryRemove(chatId, out _);

                break;

            default:
                userSessions.TryRemove(chatId, out _);

                await botClient.SendMessage(
                    chatId: chatId,
                    text:
                        "Состояние заявки было сброшено.\n" +
                        "Отправьте /start, чтобы начать заново."
                );

                break;
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"Ошибка при обработке сообщения пользователя {chatId}: " +
            exception
        );

        try
        {
            await botClient.SendMessage(
                chatId: chatId,
                text:
                    "Произошла ошибка при обработке сообщения.\n" +
                    "Попробуйте отправить /start и заполнить заявку заново."
            );
        }
        catch (Exception sendException)
        {
            Console.Error.WriteLine(
                $"Не удалось отправить сообщение об ошибке " +
                $"пользователю {chatId}: {sendException}"
            );
        }
    }
    finally
    {
        chatLock.Release();
    }
}

static string BuildAdminMessage(
    Telegram.Bot.Types.User? user,
    TourRequestSession session)
{
    string username = string.IsNullOrWhiteSpace(user?.Username)
        ? "не указан"
        : $"@{EscapeHtml(user.Username)}";

    string fullName = string.Join(
        " ",
        new[] { user?.FirstName, user?.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
    );

    if (string.IsNullOrWhiteSpace(fullName))
        fullName = "не указано";

    string telegramId = user?.Id.ToString() ?? "не указан";

    var messageBuilder = new StringBuilder();

    messageBuilder.AppendLine("<b>✈️ Новая заявка на подбор тура</b>");
    messageBuilder.AppendLine();

    messageBuilder.AppendLine(
        $"<b>Клиент:</b> {EscapeHtml(fullName)}"
    );

    messageBuilder.AppendLine(
        $"<b>Username:</b> {username}"
    );

    messageBuilder.AppendLine(
        $"<b>Telegram ID:</b> <code>{telegramId}</code>"
    );

    messageBuilder.AppendLine();

    messageBuilder.AppendLine(
        $"<b>Направление:</b> {EscapeHtml(session.Destination)}"
    );

    messageBuilder.AppendLine(
        $"<b>Туристы:</b> {EscapeHtml(session.Travelers)}"
    );

    messageBuilder.AppendLine(
        $"<b>Даты:</b> {EscapeHtml(session.Dates)}"
    );

    messageBuilder.AppendLine(
        $"<b>Бюджет:</b> {EscapeHtml(session.Budget)}"
    );

    messageBuilder.AppendLine(
        $"<b>Пожелания:</b> {EscapeHtml(session.Wishes)}"
    );

    return messageBuilder.ToString();
}

static string EscapeHtml(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return "не указано";

    return value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}

static string GetRequiredEnvironmentVariable(string variableName)
{
    string? value = Environment.GetEnvironmentVariable(variableName);

    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException(
            $"Не задана переменная окружения {variableName}."
        );
    }

    return value.Trim();
}

enum TourRequestStep
{
    Destination,
    Travelers,
    Dates,
    Budget,
    Wishes
}

sealed class TourRequestSession
{
    public TourRequestStep Step { get; set; }

    public string Destination { get; set; } = string.Empty;

    public string Travelers { get; set; } = string.Empty;

    public string Dates { get; set; } = string.Empty;

    public string Budget { get; set; } = string.Empty;

    public string Wishes { get; set; } = string.Empty;
}