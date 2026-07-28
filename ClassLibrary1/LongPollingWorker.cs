using System.Text.Json;
using Microsoft.Extensions.Options;

namespace StopGraffitiKurganBot;

public sealed class LongPollingWorker : BackgroundService
{
    private readonly MaxApiClient _api;
    private readonly ConversationService _conversations;
    private readonly BotOptions _options;
    private readonly ILogger<LongPollingWorker> _logger;

    public LongPollingWorker(
        MaxApiClient api,
        ConversationService conversations,
        IOptions<BotOptions> options,
        ILogger<LongPollingWorker> logger)
    {
        _api = api;
        _conversations = conversations;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.UseLongPolling)
        {
            _logger.LogInformation("Long polling is disabled; waiting for webhook updates.");
            return;
        }

        if (!_api.IsConfigured)
        {
            _logger.LogWarning("Long polling is enabled, but Bot:Token is empty.");
            return;
        }

        long? marker = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var response = await _api.GetUpdatesAsync(marker, stoppingToken);
                var root = response.RootElement;
                if (root.TryGetProperty("updates", out var updates) &&
                    updates.ValueKind == JsonValueKind.Array)
                {
                    foreach (var update in updates.EnumerateArray())
                    {
                        await _conversations.ProcessAsync(update, stoppingToken);
                    }
                }

                if (root.TryGetProperty("marker", out var nextMarker) &&
                    nextMarker.TryGetInt64(out var markerValue))
                {
                    marker = markerValue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Long polling failed; retrying in 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
