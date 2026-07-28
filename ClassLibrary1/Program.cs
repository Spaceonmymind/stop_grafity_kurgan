using StopGraffitiKurganBot;

if (args.Contains("--healthcheck", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        using var healthClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        using var response = await healthClient.GetAsync("http://127.0.0.1:8080/health");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        Environment.ExitCode = 1;
    }

    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<BotOptions>()
    .Bind(builder.Configuration.GetSection(BotOptions.SectionName))
    .Validate(options => Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out _),
        "Bot:ApiBaseUrl must be an absolute URL.")
    .ValidateOnStart();

builder.Services.AddHttpClient<MaxApiClient>();
builder.Services.AddSingleton<ReportStore>();
builder.Services.AddSingleton<ConversationService>();
builder.Services.AddHostedService<LongPollingWorker>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "stop_graffiti_kurgan_bot",
    status = "ok"
}));

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/webhook", async (
    HttpRequest request,
    ConversationService conversations,
    Microsoft.Extensions.Options.IOptions<BotOptions> options,
    CancellationToken cancellationToken) =>
{
    var expectedSecret = options.Value.WebhookSecret;
    if (!string.IsNullOrWhiteSpace(expectedSecret))
    {
        var actualSecret = request.Headers["X-Max-Bot-Api-Secret"].ToString();
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(actualSecret),
                System.Text.Encoding.UTF8.GetBytes(expectedSecret)))
        {
            return Results.Unauthorized();
        }
    }

    using var update = await System.Text.Json.JsonDocument.ParseAsync(
        request.Body,
        cancellationToken: cancellationToken);
    await conversations.ProcessAsync(update.RootElement, cancellationToken);
    return Results.Ok();
});

app.Run();

public partial class Program;
