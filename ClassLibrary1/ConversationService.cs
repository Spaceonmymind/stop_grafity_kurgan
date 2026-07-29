using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace StopGraffitiKurganBot;

public sealed class ConversationService
{
    private readonly ConcurrentDictionary<long, Draft> _drafts = new();
    private readonly ConcurrentDictionary<long, long> _recentStarts = new();
    private readonly ConcurrentDictionary<string, long> _processedEvents = new();
    private readonly MaxApiClient _api;
    private readonly ReportStore _store;
    private readonly BotOptions _options;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        MaxApiClient api,
        ReportStore store,
        IOptions<BotOptions> options,
        ILogger<ConversationService> logger)
    {
        _api = api;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessAsync(JsonElement update, CancellationToken cancellationToken)
    {
        var parsed = IncomingUpdate.Parse(update);
        if (parsed is null)
        {
            _logger.LogWarning("Ignored an update without a supported recipient: {Update}", update);
            return;
        }

        _logger.LogInformation(
            "Received MAX update {UpdateType}; conversation={ConversationId}; user={UserId}; event={EventId}; attachments=[{AttachmentTypes}]",
            parsed.Type,
            parsed.RecipientId,
            parsed.UserId,
            parsed.EventId ?? "unknown",
            string.Join(",", parsed.AttachmentTypes));

        if (!TryReserveEvent(parsed.EventId))
        {
            _logger.LogInformation("Ignored duplicate MAX event {EventId}", parsed.EventId);
            return;
        }

        try
        {
            var isBotStarted = parsed.Type == "bot_started";
            var isStartCommand = IsCommand(parsed.Text, "/start");
            var isNewRequest = IsCommand(parsed.Text, "/new") || parsed.CallbackPayload == "new";

            if (isBotStarted)
            {
                // MAX can deliver bot_started together with /start and retry it later.
                // Never let that service event reset a conversation already in progress.
                if (_drafts.ContainsKey(parsed.RecipientId) || !TryReserveStart(parsed.RecipientId))
                {
                    return;
                }

                await StartAsync(parsed, cancellationToken);
                return;
            }

            if (isStartCommand)
            {
                if (!TryReserveStart(parsed.RecipientId))
                {
                    return;
                }

                await StartAsync(parsed, cancellationToken);
                return;
            }

            if (isNewRequest)
            {
                await StartAsync(parsed, cancellationToken);
                return;
            }

            if (IsCommand(parsed.Text, "/cancel") || parsed.CallbackPayload == "cancel")
            {
                _drafts.TryRemove(parsed.RecipientId, out _);
                await ReplyAsync(parsed, "Заявка отменена. Чтобы начать заново, отправьте /new.", null, cancellationToken);
                return;
            }

            if (!_drafts.TryGetValue(parsed.RecipientId, out var draft))
            {
                await StartAsync(parsed, cancellationToken);
                return;
            }

            switch (draft.Step)
            {
                case DraftStep.Intro:
                    await AcceptIntroAsync(parsed, draft, cancellationToken);
                    break;
                case DraftStep.Category:
                    await AcceptCategoryAsync(parsed, draft, cancellationToken);
                    break;
                case DraftStep.Address:
                    await AcceptAddressAsync(parsed, draft, cancellationToken);
                    break;
                case DraftStep.Media:
                    await AcceptMediaAsync(parsed, draft, cancellationToken);
                    break;
                case DraftStep.Comment:
                    await AcceptCommentAsync(parsed, draft, cancellationToken);
                    break;
                case DraftStep.Confirm:
                    await ConfirmAsync(parsed, draft, cancellationToken);
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to process update for user {UserId}", parsed.UserId);
            await ReplyAsync(
                parsed,
                "Не удалось обработать сообщение. Попробуйте еще раз или отправьте /new.",
                null,
                cancellationToken);
        }
    }

    private async Task StartAsync(IncomingUpdate update, CancellationToken cancellationToken)
    {
        _drafts[update.RecipientId] = new Draft
        {
            UserId = update.UserId,
            RecipientId = update.RecipientId,
            RecipientIsChat = update.RecipientIsChat,
            Step = DraftStep.Intro
        };

        await ReplyAsync(
            update,
            "**Здравствуйте! Это бот «Стоп граффити | Курганская область».**\n\n" +
            "Здесь можно сообщить о надписях с рекламой наркотиков, запрещенной символике и других незаконных граффити.\n\n" +
            "Для обращения понадобится указать вид нарушения, адрес и приложить фото или видео. Перед отправкой вы сможете проверить все данные.",
            Keyboard(("Сообщить о нарушении", "begin_report")),
            cancellationToken);
    }

    private async Task AcceptIntroAsync(IncomingUpdate update, Draft draft, CancellationToken cancellationToken)
    {
        if (update.CallbackPayload != "begin_report")
        {
            await ReplyAsync(
                update,
                "Чтобы оформить обращение, нажмите кнопку «Сообщить о нарушении».",
                Keyboard(("Сообщить о нарушении", "begin_report")),
                cancellationToken);
            return;
        }

        draft.Step = DraftStep.Category;
        await ReplyAsync(
            update,
            "**Шаг 1 из 4. Вид нарушения**\n\nЧто вы обнаружили?",
            CategoryKeyboard(),
            cancellationToken);
    }

    private async Task AcceptCategoryAsync(IncomingUpdate update, Draft draft, CancellationToken cancellationToken)
    {
        draft.Category = update.CallbackPayload switch
        {
            "category:drugs" => "Наркограффити",
            "category:symbols" => "Запрещенная символика",
            "category:other" => "Другая надпись",
            _ => null
        };

        if (draft.Category is null)
        {
            await ReplyAsync(update, "Выберите вид нарушения кнопкой ниже.", CategoryKeyboard(), cancellationToken);
            return;
        }

        draft.Step = DraftStep.Address;
        await ReplyAsync(
            update,
            "**Шаг 2 из 4. Адрес**\n\n" +
            "Где находится объект? Напишите адрес текстом, например: «г. Курган, ул. Ленина, 10», или поделитесь геопозицией.",
            LocationKeyboard(),
            cancellationToken);
    }

    private async Task AcceptAddressAsync(IncomingUpdate update, Draft draft, CancellationToken cancellationToken)
    {
        draft.Address = update.Location is not null
            ? $"{update.Location.Value.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
              $"{update.Location.Value.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : update.Text?.Trim();

        if (string.IsNullOrWhiteSpace(draft.Address))
        {
            await ReplyAsync(
                update,
                "Адрес не распознан. Напишите его текстом или отправьте геопозицию.",
                LocationKeyboard(),
                cancellationToken);
            return;
        }

        draft.Step = DraftStep.Media;
        await ReplyAsync(
            update,
            "**Шаг 3 из 4. Фото или видео**\n\n" +
            "Пришлите фотографию или видео, на котором надпись и окружающее место хорошо видны. Можно отправить несколько файлов одним сообщением.",
            CancelKeyboard(),
            cancellationToken);
    }

    private async Task AcceptMediaAsync(IncomingUpdate update, Draft draft, CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaReference> receivedAttachments = update.Attachments;
        if (receivedAttachments.Count == 0 && update.MessageId is not null)
        {
            using var fullMessage = await _api.GetMessageAsync(update.MessageId, cancellationToken);
            receivedAttachments = IncomingUpdate.ParseMessageMedia(fullMessage.RootElement);
            _logger.LogInformation(
                "Loaded {MediaCount} media attachments from full MAX message {MessageId}",
                receivedAttachments.Count,
                update.MessageId);
        }

        var media = receivedAttachments
            .Where(item => item.Type is "image" or "video" or "file")
            .ToArray();
        if (media.Length == 0)
        {
            await ReplyAsync(
                update,
                "Фото или видео не найдено. Прикрепите хотя бы один файл.",
                CancelKeyboard(),
                cancellationToken);
            return;
        }

        draft.Media.Clear();
        draft.Media.AddRange(media);
        draft.Step = DraftStep.Comment;
        await ReplyAsync(
            update,
            "**Шаг 4 из 4. Комментарий**\n\n" +
            $"Фото/видео получено: {media.Length}.\n" +
            "Добавьте ориентир или пояснение для исполнителя. Если уточнений нет, нажмите «Пропустить».",
            Keyboard(("Пропустить", "skip_comment"), ("Отменить", "cancel")),
            cancellationToken);
    }

    private async Task AcceptCommentAsync(IncomingUpdate update, Draft draft, CancellationToken cancellationToken)
    {
        if (update.CallbackPayload == "skip_comment")
        {
            draft.Comment = null;
        }
        else if (!string.IsNullOrWhiteSpace(update.Text))
        {
            draft.Comment = update.Text.Trim();
        }
        else
        {
            await ReplyAsync(update, "Напишите комментарий или нажмите «Пропустить».", null, cancellationToken);
            return;
        }

        draft.Step = DraftStep.Confirm;
        await ReplyAsync(
            update,
            Summary(draft),
            Keyboard(("Отправить", "submit"), ("Отменить", "cancel")),
            cancellationToken);
    }

    private async Task ConfirmAsync(IncomingUpdate update, Draft draft, CancellationToken cancellationToken)
    {
        if (update.CallbackPayload != "submit")
        {
            await ReplyAsync(update, "Подтвердите отправку кнопкой ниже.", Keyboard(("Отправить", "submit"), ("Отменить", "cancel")), cancellationToken);
            return;
        }

        var report = new ViolationReport(
            $"KGN-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..21].ToUpperInvariant(),
            DateTimeOffset.UtcNow,
            draft.UserId,
            draft.RecipientId,
            draft.RecipientIsChat,
            draft.Category!,
            draft.Address!,
            draft.Comment,
            draft.Media.ToArray());

        await _store.SaveAsync(report, cancellationToken);
        _drafts.TryRemove(update.RecipientId, out _);

        await ReplyAsync(
            update,
            $"**Спасибо за обращение!**\n\n" +
            $"Обращение **{report.Id}** зарегистрировано. Информация будет проверена и передана ответственным специалистам.\n\n" +
            "Сохраните номер обращения.",
            Keyboard(("Новое обращение", "new")),
            cancellationToken);

        if (_options.ReviewerChatId is not null)
        {
            var reviewerText =
                $"**Новое обращение {report.Id}**\n" +
                $"Категория: {report.Category}\n" +
                $"Адрес: {report.Address}\n" +
                $"Комментарий: {report.Comment ?? "нет"}\n" +
                $"Автор MAX ID: {report.UserId}\n" +
                $"Медиафайлов: {report.Media.Count}";
            await _api.SendAsync(_options.ReviewerChatId.Value, true, reviewerText, null, cancellationToken);
        }
    }

    private Task ReplyAsync(
        IncomingUpdate update,
        string text,
        object? attachments,
        CancellationToken cancellationToken) =>
        _api.SendAsync(update.RecipientId, update.RecipientIsChat, text, attachments, cancellationToken);

    private static bool IsCommand(string? text, string command) =>
        string.Equals(text?.Trim(), command, StringComparison.OrdinalIgnoreCase);

    private bool TryReserveStart(long conversationId)
    {
        var now = Environment.TickCount64;
        while (true)
        {
            if (!_recentStarts.TryGetValue(conversationId, out var previous))
            {
                if (_recentStarts.TryAdd(conversationId, now))
                {
                    return true;
                }

                continue;
            }

            if (now - previous < TimeSpan.FromSeconds(5).TotalMilliseconds)
            {
                return false;
            }

            if (_recentStarts.TryUpdate(conversationId, now, previous))
            {
                return true;
            }
        }
    }

    private bool TryReserveEvent(string? eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!_processedEvents.TryAdd(eventId, now))
        {
            return false;
        }

        if (_processedEvents.Count > 10_000)
        {
            var cutoff = now - (long)TimeSpan.FromDays(1).TotalMilliseconds;
            foreach (var item in _processedEvents.Where(item => item.Value < cutoff))
            {
                _processedEvents.TryRemove(item.Key, out _);
            }
        }

        return true;
    }

    private static string Summary(Draft draft) =>
        "**Проверьте обращение**\n\n" +
        $"Категория: {draft.Category}\n" +
        $"Адрес: {draft.Address}\n" +
        $"Фото/видео: {draft.Media.Count}\n" +
        $"Комментарий: {draft.Comment ?? "нет"}\n\n" +
        "Нажимая «Отправить», вы соглашаетесь на обработку переданных данных для рассмотрения обращения.";

    private static object[] CategoryKeyboard() =>
        Keyboard(
            ("Наркограффити", "category:drugs"),
            ("Запрещенная символика", "category:symbols"),
            ("Другая надпись", "category:other"));

    private static object[] CancelKeyboard() => Keyboard(("Отменить", "cancel"));

    private static object[] LocationKeyboard() =>
        new object[]
        {
            new
            {
                type = "inline_keyboard",
                payload = new
                {
                    buttons = new object[][]
                    {
                        new object[]
                        {
                            new { type = "request_geo_location", text = "Отправить геопозицию", quick = true }
                        },
                        new object[] { CallbackButton("Отменить", "cancel") }
                    }
                }
            }
        };

    private static object[] Keyboard(params (string Text, string Payload)[] buttons) =>
        new object[]
        {
            new
            {
                type = "inline_keyboard",
                payload = new
                {
                    buttons = buttons
                        .Select(button => new object[] { CallbackButton(button.Text, button.Payload) })
                        .ToArray()
                }
            }
        };

    private static object CallbackButton(string text, string payload) =>
        new { type = "callback", text, payload };

    private enum DraftStep
    {
        Intro,
        Category,
        Address,
        Media,
        Comment,
        Confirm
    }

    private sealed class Draft
    {
        public required long UserId { get; init; }
        public required long RecipientId { get; init; }
        public required bool RecipientIsChat { get; init; }
        public required DraftStep Step { get; set; }
        public string? Category { get; set; }
        public string? Address { get; set; }
        public string? Comment { get; set; }
        public List<MediaReference> Media { get; } = new();
    }

    private sealed record IncomingUpdate(
        string Type,
        long UserId,
        long RecipientId,
        bool RecipientIsChat,
        string? Text,
        string? CallbackPayload,
        string? EventId,
        string? MessageId,
        IReadOnlyList<MediaReference> Attachments,
        IReadOnlyList<string> AttachmentTypes,
        (double Latitude, double Longitude)? Location)
    {
        public static IncomingUpdate? Parse(JsonElement root)
        {
            var type = GetString(root, "update_type") ?? "";
            var message = TryGet(root, "message");
            var sender = message is not null ? TryGet(message.Value, "sender") : null;
            var recipient = message is not null ? TryGet(message.Value, "recipient") : null;
            var body = message is not null ? TryGet(message.Value, "body") : null;
            var callback = TryGet(root, "callback");
            var callbackUser = callback is not null ? TryGet(callback.Value, "user") : null;
            var rootUser = TryGet(root, "user");

            var userId =
                GetInt64(sender, "user_id") ??
                GetInt64(callbackUser, "user_id") ??
                GetInt64(rootUser, "user_id");
            if (userId is null)
            {
                return null;
            }

            var chatId = GetInt64(recipient, "chat_id") ?? GetInt64(root, "chat_id");
            var recipientId = chatId ?? userId.Value;
            var recipientIsChat = chatId is not null;
            var attachments = ParseAttachments(body);
            var messageId = GetString(body, "mid");
            var callbackId = GetString(callback, "callback_id");
            var timestamp = GetInt64(root, "timestamp");
            var eventId = type switch
            {
                "message_created" when messageId is not null => $"message:{messageId}",
                "message_callback" when callbackId is not null => $"callback:{callbackId}",
                "bot_started" when timestamp is not null => $"bot_started:{recipientId}:{timestamp}",
                _ => null
            };

            return new IncomingUpdate(
                type,
                userId.Value,
                recipientId,
                recipientIsChat,
                GetString(body, "text"),
                GetString(callback, "payload"),
                eventId,
                messageId,
                attachments.Media,
                attachments.Types,
                attachments.Location);
        }

        public static IReadOnlyList<MediaReference> ParseMessageMedia(JsonElement message) =>
            ParseAttachments(TryGet(message, "body")).Media;

        private static (
            IReadOnlyList<MediaReference> Media,
            IReadOnlyList<string> Types,
            (double, double)? Location) ParseAttachments(JsonElement? body)
        {
            var media = new List<MediaReference>();
            var types = new List<string>();
            (double, double)? location = null;
            if (body is null ||
                !body.Value.TryGetProperty("attachments", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return (media, types, location);
            }

            foreach (var item in items.EnumerateArray())
            {
                var attachmentType = GetString(item, "type")?.ToLowerInvariant();
                types.Add(attachmentType ?? "unknown");
                var payload = TryGet(item, "payload");
                if (attachmentType is "image" or "photo" or "video" or "file")
                {
                    var normalizedType = attachmentType == "photo" ? "image" : attachmentType;
                    media.Add(new MediaReference(
                        normalizedType,
                        (payload ?? item).GetRawText()));
                }
                else if (attachmentType == "location" && payload is not null)
                {
                    var latitude = GetDouble(payload, "latitude");
                    var longitude = GetDouble(payload, "longitude");
                    if (latitude is not null && longitude is not null)
                    {
                        location = (latitude.Value, longitude.Value);
                    }
                }
            }

            return (media, types, location);
        }

        private static JsonElement? TryGet(JsonElement? element, string property)
        {
            if (element is not null &&
                element.Value.ValueKind == JsonValueKind.Object &&
                element.Value.TryGetProperty(property, out var value))
            {
                return value;
            }

            return null;
        }

        private static string? GetString(JsonElement? element, string property)
        {
            var value = TryGet(element, property);
            return value is { ValueKind: JsonValueKind.String } ? value.Value.GetString() : null;
        }

        private static long? GetInt64(JsonElement? element, string property)
        {
            var value = TryGet(element, property);
            return value is not null && value.Value.TryGetInt64(out var result) ? result : null;
        }

        private static double? GetDouble(JsonElement? element, string property)
        {
            var value = TryGet(element, property);
            return value is not null && value.Value.TryGetDouble(out var result) ? result : null;
        }
    }
}
