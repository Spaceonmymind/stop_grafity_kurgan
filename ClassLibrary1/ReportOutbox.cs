using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace StopGraffitiKurganBot;

public sealed class ReportOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _reportsPath;
    private readonly string _pendingDirectory;
    private readonly string _deliveredPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _delivered = new();

    public ReportOutbox(IOptions<BotOptions> options, IHostEnvironment environment)
    {
        var configuredPath = options.Value.DataDirectory;
        var dataDirectory = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

        Directory.CreateDirectory(dataDirectory);
        _reportsPath = Path.Combine(dataDirectory, "reports.jsonl");
        _pendingDirectory = Path.Combine(dataDirectory, "outbox");
        _deliveredPath = Path.Combine(dataDirectory, "delivered-report-ids.jsonl");
        Directory.CreateDirectory(_pendingDirectory);

        if (File.Exists(_deliveredPath))
        {
            foreach (var id in File.ReadLines(_deliveredPath).Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                _delivered.TryAdd(id, 0);
            }
        }
    }

    public async Task EnqueueAsync(ViolationReport report, CancellationToken cancellationToken)
    {
        if (_delivered.ContainsKey(report.Id))
        {
            return;
        }

        var targetPath = PendingPath(report.Id);
        var temporaryPath = targetPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(report, JsonOptions),
            cancellationToken);
        File.Move(temporaryPath, targetPath, true);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_reportsPath))
        {
            return;
        }

        foreach (var line in File.ReadLines(_reportsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = JsonSerializer.Deserialize<ViolationReport>(line, JsonOptions);
            if (report is not null && !_delivered.ContainsKey(report.Id) && !File.Exists(PendingPath(report.Id)))
            {
                await EnqueueAsync(report, cancellationToken);
            }
        }
    }

    public IEnumerable<string> PendingFiles() =>
        Directory.EnumerateFiles(_pendingDirectory, "*.json").OrderBy(path => path);

    public async Task<ViolationReport?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ViolationReport>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    public async Task MarkDeliveredAsync(ViolationReport report, string path, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_delivered.TryAdd(report.Id, 0))
            {
                await File.AppendAllTextAsync(
                    _deliveredPath,
                    report.Id + Environment.NewLine,
                    cancellationToken);
            }

            File.Delete(path);
        }
        finally
        {
            _lock.Release();
        }
    }

    private string PendingPath(string reportId) =>
        Path.Combine(_pendingDirectory, reportId + ".json");
}
