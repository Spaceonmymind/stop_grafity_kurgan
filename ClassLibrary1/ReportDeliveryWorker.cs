namespace StopGraffitiKurganBot;

public sealed class ReportDeliveryWorker : BackgroundService
{
    private readonly ReportOutbox _outbox;
    private readonly VikaReportClient _vika;
    private readonly ILogger<ReportDeliveryWorker> _logger;

    public ReportDeliveryWorker(
        ReportOutbox outbox,
        VikaReportClient vika,
        ILogger<ReportDeliveryWorker> logger)
    {
        _outbox = outbox;
        _vika = vika;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_vika.IsConfigured)
        {
            _logger.LogWarning("Vika report delivery is disabled because integration settings are missing.");
            return;
        }

        await _outbox.RecoverAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var path in _outbox.PendingFiles())
            {
                try
                {
                    var report = await _outbox.ReadAsync(path, stoppingToken);
                    if (report is null)
                    {
                        _logger.LogError("Invalid report outbox item {Path}", path);
                        continue;
                    }

                    await _vika.SendAsync(report, stoppingToken);
                    await _outbox.MarkDeliveredAsync(report, path, stoppingToken);
                    _logger.LogInformation("Delivered report {ReportId} to Vika", report.Id);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Failed to deliver report outbox item {Path}", path);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
