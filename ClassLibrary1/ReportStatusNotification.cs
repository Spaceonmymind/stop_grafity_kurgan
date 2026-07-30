namespace StopGraffitiKurganBot;

public sealed record ReportStatusNotification(
    string ReportId,
    long UserId,
    long RecipientId,
    bool RecipientIsChat,
    string Status,
    string StatusLabel,
    string? Comment);
