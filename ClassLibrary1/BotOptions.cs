namespace StopGraffitiKurganBot;

public sealed class BotOptions
{
    public const string SectionName = "Bot";

    public string Token { get; init; } = "";
    public string ApiBaseUrl { get; init; } = "https://platform-api2.max.ru";
    public bool UseLongPolling { get; init; }
    public long? ReviewerChatId { get; init; }
    public string WebhookSecret { get; init; } = "";
    public string DataDirectory { get; init; } = "data";
}
