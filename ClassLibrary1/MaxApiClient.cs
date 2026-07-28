using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace StopGraffitiKurganBot;

public sealed class MaxApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly BotOptions _options;
    private readonly ILogger<MaxApiClient> _logger;

    public MaxApiClient(HttpClient http, IOptions<BotOptions> options, ILogger<MaxApiClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.BaseAddress = new Uri(_options.ApiBaseUrl.TrimEnd('/') + "/");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Token);

    public async Task SendAsync(
        long recipientId,
        bool recipientIsChat,
        string text,
        object? attachments,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var parameter = recipientIsChat ? "chat_id" : "user_id";
        var body = new { text, attachments, format = "markdown" };
        using var request = CreateRequest(
            HttpMethod.Post,
            $"messages?{parameter}={recipientId}",
            body);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> GetUpdatesAsync(long? marker, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var query = "updates?timeout=30&limit=100&types=message_created,message_callback,bot_started";
        if (marker is not null)
        {
            query += $"&marker={marker.Value}";
        }

        using var request = CreateRequest(HttpMethod.Get, query);
        var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        finally
        {
            response.Dispose();
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string uri, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(_options.Token);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        return request;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("MAX API returned {StatusCode}: {Response}", response.StatusCode, body);
        throw new HttpRequestException(
            $"MAX API returned {(int)response.StatusCode} {response.ReasonPhrase}.",
            null,
            response.StatusCode);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "MAX bot token is not configured. Set Bot__Token.");
        }
    }
}
