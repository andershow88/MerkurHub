using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MerkurHub.Models;
using UglyToad.PdfPig;

namespace MerkurHub.Controllers;

[Authorize]
public class DokumenteController : Controller
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<DokumenteController> _logger;

    public DokumenteController(AppDbContext db, IWebHostEnvironment env, IHttpClientFactory httpFactory, IConfiguration config, ILogger<DokumenteController> logger)
    {
        _db = db;
        _env = env;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    private int Uid => int.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : 0;

    public async Task<IActionResult> Index()
    {
        var docs = await _db.PdfDokumente.OrderByDescending(d => d.HochgeladenAm).ToListAsync();
        return View(docs);
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Hochladen(IFormFile datei, string? titel)
    {
        if (datei == null || datei.Length == 0)
            return Json(new { error = "Keine Datei ausgewählt." });
        if (!datei.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Json(new { error = "Nur PDF-Dateien erlaubt." });
        if (datei.Length > 100 * 1024 * 1024)
            return Json(new { error = "Maximale Dateigröße: 100 MB." });

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? _config["OpenAiApiKey"];

        var guid = Guid.NewGuid().ToString("N");
        var fileName = $"{guid}.pdf";
        var relPath = $"uploads/documents/{fileName}";
        var fullPath = Path.Combine(_env.WebRootPath, "uploads", "documents", fileName);

        await using (var fs = new FileStream(fullPath, FileMode.Create))
            await datei.CopyToAsync(fs);

        var userName = User.FindFirst(ClaimTypes.GivenName)?.Value ?? User.Identity?.Name ?? "Unbekannt";

        var dok = new PdfDokument
        {
            Titel = string.IsNullOrWhiteSpace(titel) ? Path.GetFileNameWithoutExtension(datei.FileName) : titel.Trim(),
            Dateiname = datei.FileName,
            Dateipfad = relPath,
            Dateigroesse = datei.Length,
            HochgeladenVonId = Uid,
            HochgeladenVonName = userName,
            HochgeladenAm = DateTime.UtcNow
        };

        try
        {
            using var pdfDoc = PdfDocument.Open(fullPath);
            dok.Seitenanzahl = pdfDoc.NumberOfPages;
            _db.PdfDokumente.Add(dok);
            await _db.SaveChangesAsync();

            for (int i = 1; i <= pdfDoc.NumberOfPages; i++)
            {
                var page = pdfDoc.GetPage(i);
                var text = string.Join(" ", page.GetWords().Select(w => w.Text));
                if (string.IsNullOrWhiteSpace(text)) text = $"(Seite {i}: kein Text extrahierbar)";

                var seite = new DokumentSeite
                {
                    PdfDokumentId = dok.Id,
                    Seitennummer = i,
                    Text = text.Length > 8000 ? text[..8000] : text
                };

                if (!string.IsNullOrWhiteSpace(apiKey) && text.Length > 10)
                {
                    try
                    {
                        var embedding = await GeneriereEmbedding(apiKey, seite.Text);
                        seite.EmbeddingJson = JsonSerializer.Serialize(embedding);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Embedding für Seite {Nr} fehlgeschlagen", i);
                    }
                }

                _db.DokumentSeiten.Add(seite);
            }

            dok.IstVerarbeitet = true;
            await _db.SaveChangesAsync();

            return Json(new { ok = true, id = dok.Id, seiten = dok.Seitenanzahl });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PDF-Verarbeitung fehlgeschlagen");
            return Json(new { error = "PDF-Verarbeitung fehlgeschlagen: " + ex.Message });
        }
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Loeschen(int id)
    {
        var dok = await _db.PdfDokumente.FindAsync(id);
        if (dok == null) return NotFound();

        var fullPath = Path.Combine(_env.WebRootPath, dok.Dateipfad);
        if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);

        var seiten = await _db.DokumentSeiten.Where(s => s.PdfDokumentId == id).ToListAsync();
        _db.DokumentSeiten.RemoveRange(seiten);
        _db.PdfDokumente.Remove(dok);
        await _db.SaveChangesAsync();

        return Json(new { ok = true });
    }

    public async Task<IActionResult> Herunterladen(int id)
    {
        var dok = await _db.PdfDokumente.FindAsync(id);
        if (dok == null) return NotFound();

        var fullPath = Path.Combine(_env.WebRootPath, dok.Dateipfad);
        if (!System.IO.File.Exists(fullPath)) return NotFound();

        return PhysicalFile(fullPath, "application/pdf", dok.Dateiname);
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
        var json = await resp.Content.ReadAsStringAsync();
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var result = new float[arr.GetArrayLength()];
        for (int i = 0; i < result.Length; i++)
            result[i] = arr[i].GetSingle();
        return result;
    }
}
