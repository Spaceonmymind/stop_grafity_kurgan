using System.Text.Json;
using Microsoft.Extensions.Options;

namespace StopGraffitiKurganBot;

public sealed record ViolationReport(
    string Id,
    DateTimeOffset CreatedAt,
    long UserId,
    long RecipientId,
    bool RecipientIsChat,
    string Category,
    string Address,
    string? Comment,
    IReadOnlyList<MediaReference> Media);

public sealed record MediaReference(string Type, string PayloadJson);

public sealed class ReportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ReportStore(IOptions<BotOptions> options, IHostEnvironment environment)
    {
        var configuredPath = options.Value.DataDirectory;
        var directory = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "reports.jsonl");
    }

    public async Task SaveAsync(ViolationReport report, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine;
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_filePath, line, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
