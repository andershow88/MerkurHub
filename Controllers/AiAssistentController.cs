using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkurHub.Controllers;

[Authorize]
[Route("[controller]/[action]")]
public class AiAssistentController : Controller
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<AiAssistentController> _logger;

    public AiAssistentController(IHttpClientFactory httpFactory, IConfiguration config, ILogger<AiAssistentController> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public record ChatNachrichtDto(string rolle, string text);
    public record ChatAnfrageDto(string frage, List<ChatNachrichtDto>? verlauf);

    [HttpPost]
    public async Task<IActionResult> Fragen([FromBody] ChatAnfrageDto anfrage)
    {
        if (string.IsNullOrWhiteSpace(anfrage?.frage))
            return Json(new { error = "Bitte eine Frage eingeben." });

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                     ?? _config["OpenAiApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return Json(new { error = "KI-Assistent ist nicht konfiguriert (OPENAI_API_KEY fehlt)." });

        try
        {
            var messages = new List<object>
            {
                new { role = "system", content = SystemPrompt }
            };

            if (anfrage.verlauf is { Count: > 0 })
                foreach (var n in anfrage.verlauf.TakeLast(10))
                    messages.Add(new { role = n.rolle == "assistant" ? "assistant" : "user", content = n.text });

            messages.Add(new { role = "user", content = anfrage.frage });

            var requestBody = new
            {
                model = "gpt-4o-mini",
                max_tokens = 1500,
                temperature = 0.4,
                messages
            };

            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
            req.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenAI {Status}: {Body}", resp.StatusCode, body);
                return Json(new { error = $"KI-Dienst antwortete mit {(int)resp.StatusCode}." });
            }

            using var doc = JsonDocument.Parse(body);
            var antwort = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            return Json(new { antwort });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KI-Anfrage fehlgeschlagen");
            return Json(new { error = "KI-Anfrage fehlgeschlagen: " + ex.Message });
        }
    }

    private const string SystemPrompt = """
        Du bist der KI-Assistent von MerkurHub, dem internen Portal der Merkur Privatbank KGaA.

        Du hilfst Mitarbeitern bei allgemeinen Fragen zu:
        - Bankfachlichen Themen (Kredit, Leasing, Bautraeger, Wertpapier, Compliance)
        - Kennzahlen und KPIs (Kernkapital, Grosskreditobergrenze, AuM, Obligo)
        - Internen Prozessen und Ablaeufen
        - IT und Digitalisierung
        - Allgemeinen Wissensfragen im Bankkontext

        Regeln:
        - Antworte auf Deutsch, praezise, freundlich und strukturiert.
        - Verwende Markdown (Listen, Fettdruck, Ueberschriften).
        - Wenn du etwas nicht weisst, sage es ehrlich.
        - Erfinde keine konkreten Zahlen oder Fakten.
        - Halte Antworten kompakt (max. 300 Woerter), es sei denn der Nutzer fragt ausdruecklich nach Details.
        """;
}
