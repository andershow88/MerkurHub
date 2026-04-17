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
            var messages = new List<object> { new { role = "system", content = SystemPrompt } };

            if (anfrage.verlauf is { Count: > 0 })
                foreach (var n in anfrage.verlauf.TakeLast(10))
                    messages.Add(new { role = n.rolle == "assistant" ? "assistant" : "user", content = n.text });

            messages.Add(new { role = "user", content = anfrage.frage });

            var requestBody = new
            {
                model = "gpt-4o-mini",
                max_tokens = 1500,
                temperature = 0.3,
                messages,
                tools = ToolDefinitionen(),
                tool_choice = "auto"
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
            var choice = doc.RootElement.GetProperty("choices")[0];
            var finishReason = choice.GetProperty("finish_reason").GetString();
            var message = choice.GetProperty("message");

            // Tool-Call erkannt -> Frontend soll Suche ausloesen
            if (finishReason == "tool_calls" && message.TryGetProperty("tool_calls", out var toolCalls))
            {
                var tc = toolCalls[0];
                var funcName = tc.GetProperty("function").GetProperty("name").GetString()!;
                var funcArgs = tc.GetProperty("function").GetProperty("arguments").GetString()!;
                var args = JsonDocument.Parse(funcArgs).RootElement;

                return Json(new
                {
                    toolCall = new
                    {
                        funktion = funcName,
                        parameter = args
                    }
                });
            }

            var antwort = message.GetProperty("content").GetString() ?? string.Empty;
            return Json(new { antwort });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KI-Anfrage fehlgeschlagen");
            return Json(new { error = "KI-Anfrage fehlgeschlagen: " + ex.Message });
        }
    }

    private static object[] ToolDefinitionen() => new object[]
    {
        new
        {
            type = "function",
            function = new
            {
                name = "bahn_suchen",
                description = "Sucht Zugverbindungen der Deutschen Bahn zwischen zwei Orten. Nutze dies wenn der Benutzer nach Zuegen, Bahnverbindungen, ICE, IC oder Zugfahrten fragt.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["von"] = new { type = "string", description = "Abfahrtsbahnhof oder Stadt (z.B. 'Muenchen Hbf')" },
                        ["nach"] = new { type = "string", description = "Zielbahnhof oder Stadt (z.B. 'Berlin Hbf')" },
                        ["datum"] = new { type = "string", description = "Reisedatum im Format YYYY-MM-DD (optional, Standard: heute)" },
                        ["uhrzeit"] = new { type = "string", description = "Gewuenschte Abfahrtszeit im Format HH:MM (optional, Standard: jetzt)" },
                        ["erste_klasse"] = new { type = "boolean", description = "true fuer 1. Klasse, false fuer 2. Klasse (Standard: false)" }
                    },
                    required = new[] { "von", "nach" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "mvg_suchen",
                description = "Sucht OEPNV-Verbindungen in Muenchen (U-Bahn, S-Bahn, Tram, Bus). Nutze dies wenn der Benutzer nach MVG, Muenchner Nahverkehr, U-Bahn, S-Bahn, Tram oder Bus fragt.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["von"] = new { type = "string", description = "Abfahrtshaltestelle (z.B. 'Marienplatz')" },
                        ["nach"] = new { type = "string", description = "Zielhaltestelle (z.B. 'Muenchen Ost')" },
                        ["uhrzeit"] = new { type = "string", description = "Gewuenschte Abfahrtszeit HH:MM (optional)" }
                    },
                    required = new[] { "von", "nach" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "verkehr_pruefen",
                description = "Prueft die aktuelle Verkehrslage/Stau auf einer Strecke (Autobahn). Nutze dies wenn der Benutzer nach Stau, Verkehr, Baustellen oder Autobahn fragt.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["von"] = new { type = "string", description = "Startort (z.B. 'Muenchen')" },
                        ["nach"] = new { type = "string", description = "Zielort (z.B. 'Stuttgart')" }
                    },
                    required = new[] { "von", "nach" }
                }
            }
        }
    };

    private const string SystemPrompt = """
        Du bist der KI-Assistent von MerkurHub, dem internen Portal der Merkur Privatbank KGaA.

        Du hilfst Mitarbeitern bei:
        - Zugverbindungen suchen (Deutsche Bahn) -> nutze die Funktion bahn_suchen
        - OEPNV-Verbindungen in Muenchen (MVG) -> nutze die Funktion mvg_suchen
        - Verkehrslage/Stau pruefen -> nutze die Funktion verkehr_pruefen
        - Bankfachlichen Themen (Kredit, Leasing, Bautraeger, Wertpapier, Compliance)
        - Kennzahlen und KPIs
        - Allgemeinen Wissensfragen im Bankkontext

        Regeln:
        - Antworte auf Deutsch.
        - Wenn der Benutzer nach Zuegen, Bahn, Fahrplan, Verbindungen fragt -> rufe bahn_suchen auf.
        - Wenn der Benutzer nach U-Bahn, S-Bahn, MVG, Tram, Bus in Muenchen fragt -> rufe mvg_suchen auf.
        - Wenn der Benutzer nach Stau, Verkehr, Autobahn, Baustellen fragt -> rufe verkehr_pruefen auf.
        - Fuer Datums-/Zeitangaben: interpretiere relative Ausdruecke (morgen, uebermorgen, heute Abend, um 14 Uhr).
        - Heute ist der aktuelle Tag. Verwende ISO-Format fuer Datum (YYYY-MM-DD) und 24h-Format fuer Uhrzeit (HH:MM).
        - Halte textuelle Antworten kompakt.
        """;
}
