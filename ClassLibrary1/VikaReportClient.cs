using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace StopGraffitiKurganBot;

public sealed class VikaReportClient
{
    private readonly HttpClient _http;
    private readonly BotOptions _options;

    public VikaReportClient(HttpClient http, IOptions<BotOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public bool IsConfigured =>
        Uri.TryCreate(_options.VikaApiBaseUrl, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(_options.VikaApiToken);

    public async Task SendAsync(ViolationReport report, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Vika integration is not configured. Set Bot__VikaApiBaseUrl and Bot__VikaApiToken.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(_options.VikaApiBaseUrl.TrimEnd('/') + "/"), "api/integrations/stop-graffiti/reports"))
        {
            Content = JsonContent.Create(report)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.VikaApiToken);

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Vika API returned {(int)response.StatusCode}: {responseBody}",
                null,
                response.StatusCode);
        }
    }
}
