using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MerkurHub.Models;

namespace MerkurHub.Controllers;

[Authorize]
[Route("[controller]/[action]")]
public class DokumentSucheApiController : Controller
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<DokumentSucheApiController> _logger;

    public DokumentSucheApiController(AppDbContext db, IHttpClientFactory httpFactory, IConfiguration config, ILogger<DokumentSucheApiController> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public record SucheAnfrageDto(string frage, List<ChatMsg>? verlauf);
    public record ChatMsg(string rolle, string text);

    [HttpPost]
    public async Task<IActionResult> Fragen([FromBody] SucheAnfrageDto anfrage)
    {
        if (string.IsNullOrWhiteSpace(anfrage?.frage))
            return Json(new { error = "Bitte eine Frage eingeben." });

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? _config["OpenAiApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return Json(new { error = "KI nicht konfiguriert (OPENAI_API_KEY fehlt)." });

        try
        {
            var queryEmbedding = await GeneriereEmbedding(apiKey, anfrage.frage);

            var alleSeiten = await _db.DokumentSeiten
                .Where(s => s.EmbeddingJson != null)
                .Select(s => new { s.Id, s.PdfDokumentId, s.Seitennummer, s.Text, s.EmbeddingJson })
                .ToListAsync();

            var dokTitel = await _db.PdfDokumente
                .ToDictionaryAsync(d => d.Id, d => d.Titel);

            var ranked = alleSeiten
                .Select(s =>
                {
                    var emb = JsonSerializer.Deserialize<float[]>(s.EmbeddingJson!);
                    var sim = emb != null ? CosineSimilarity(queryEmbedding, emb) : 0f;
                    return new { s.PdfDokumentId, s.Seitennummer, s.Text, Similarity = sim };
                })
                .OrderByDescending(x => x.Similarity)
                .Take(6)
                .ToList();

            if (!ranked.Any())
                return Json(new { antwort = "Es wurden noch keine Dokumente hochgeladen oder verarbeitet.", quellen = Array.Empty<object>() });

            var kontextSb = new StringBuilder();
            var quellen = new List<object>();
            for (int i = 0; i < ranked.Count; i++)
            {
                var r = ranked[i];
                var title = dokTitel.GetValueOrDefault(r.PdfDokumentId, "Unbekannt");
                kontextSb.AppendLine($"[{i + 1}] Dokument: \"{title}\", Seite {r.Seitennummer} (Relevanz: {r.Similarity:P0})");
                kontextSb.AppendLine(r.Text.Length > 2000 ? r.Text[..2000] + "..." : r.Text);
                kontextSb.AppendLine("---");
                quellen.Add(new { dokument = title, seite = r.Seitennummer, dokumentId = r.PdfDokumentId, relevanz = Math.Round(r.Similarity * 100) });
            }

            var systemPrompt = $"""
                Du bist der Dokumenten-Assistent von MerkurHub (Merkur Privatbank KGaA).
                Du beantwortest Fragen ausschliesslich anhand der bereitgestellten Dokumenten-Auszuege.

                Regeln:
                - Antworte auf Deutsch, praezise und strukturiert (Markdown erlaubt).
                - Zitiere immer die Quelle im Format [Dokumentname, Seite X].
                - Wenn die Auszuege die Frage nicht beantworten, sage das ehrlich.
                - Erfinde keine Informationen.

                Bereitgestellte Dokumenten-Auszuege:
                {kontextSb}
                """;

            var messages = new List<object> { new { role = "system", content = systemPrompt } };
            if (anfrage.verlauf is { Count: > 0 })
                foreach (var m in anfrage.verlauf.TakeLast(6))
                    messages.Add(new { role = m.rolle == "assistant" ? "assistant" : "user", content = m.text });
            messages.Add(new { role = "user", content = anfrage.frage });

            var requestBody = new { model = "gpt-4.1-2025-04-14", max_tokens = 2000, temperature = 0.2, messages };

            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(90);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
            req.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenAI {Status}: {Body}", resp.StatusCode, body);
                return Json(new { error = $"KI-Dienst: {(int)resp.StatusCode}" });
            }

            using var doc = JsonDocument.Parse(body);
            var antwort = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

            return Json(new { antwort, quellen });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dokumentensuche fehlgeschlagen");
            return Json(new { error = "Suche fehlgeschlagen: " + ex.Message });
        }
    }

    private async Task<float[]> GeneriereEmbedding(string apiKey, string text)
    {
        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var body = JsonSerializer.Serialize(new { model = "text-embedding-3-small", input = text });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
        req.Headers.Add("Authorization", $"Bearer {apiKey}");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var result = new float[arr.GetArrayLength()];
        for (int i = 0; i < result.Length; i++) result[i] = arr[i].GetSingle();
        return result;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        float dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom == 0 ? 0 : dot / denom;
    }
}
