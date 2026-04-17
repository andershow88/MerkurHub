using MerkurHub.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MerkurHub.Controllers;

[Authorize]
[Route("api/[action]")]
public class ApiController : Controller
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;

    public ApiController(AppDbContext db, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
    }

    private int CurrentUserId =>
        int.TryParse(User.FindFirst("UserId")?.Value, out var uid) ? uid : 0;

    // ── Tiles CRUD ──────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Tiles()
    {
        var tiles = await _db.Tiles
            .Where(t => t.BenutzerId == CurrentUserId)
            .OrderBy(t => t.Sortierung)
            .ToListAsync();

        return Ok(tiles);
    }

    [HttpPost]
    public async Task<IActionResult> TilesCreate([FromBody] TileCreateDto dto)
    {
        var uid = CurrentUserId;

        var maxSort = await _db.Tiles
            .Where(t => t.BenutzerId == uid)
            .MaxAsync(t => (int?)t.Sortierung) ?? 0;

        var tile = new Tile
        {
            Titel = dto.Titel,
            Url = dto.Url,
            Icon = dto.Icon,
            Farbe = dto.Farbe,
            Sortierung = maxSort + 1,
            BenutzerId = uid,
            ErstelltAm = DateTime.UtcNow
        };

        _db.Tiles.Add(tile);
        await _db.SaveChangesAsync();

        return Ok(tile);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> TilesUpdate(int id, [FromBody] TileCreateDto dto)
    {
        var tile = await _db.Tiles.FindAsync(id);

        if (tile == null || tile.BenutzerId != CurrentUserId)
            return NotFound();

        tile.Titel = dto.Titel;
        tile.Url = dto.Url;
        tile.Icon = dto.Icon;
        tile.Farbe = dto.Farbe;

        await _db.SaveChangesAsync();

        return Ok(tile);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> TilesDelete(int id)
    {
        var tile = await _db.Tiles.FindAsync(id);

        if (tile == null || tile.BenutzerId != CurrentUserId)
            return NotFound();

        _db.Tiles.Remove(tile);
        await _db.SaveChangesAsync();

        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> TilesReorder([FromBody] List<TileReorderDto> items)
    {
        var uid = CurrentUserId;

        var tileIds = items.Select(i => i.Id).ToList();
        var tiles = await _db.Tiles
            .Where(t => t.BenutzerId == uid && tileIds.Contains(t.Id))
            .ToListAsync();

        foreach (var tile in tiles)
        {
            var match = items.FirstOrDefault(i => i.Id == tile.Id);
            if (match != null)
                tile.Sortierung = match.Sortierung;
        }

        await _db.SaveChangesAsync();

        return Ok();
    }

    // ── Weather Proxy ───────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Weather([FromQuery] double lat, [FromQuery] double lon)
    {
        var client = _httpClientFactory.CreateClient();

        var url = $"https://api.open-meteo.com/v1/forecast"
            + $"?latitude={lat}&longitude={lon}"
            + $"&current=temperature_2m,weather_code"
            + $"&daily=weather_code,temperature_2m_max,temperature_2m_min"
            + $"&timezone=Europe/Berlin&forecast_days=5";

        var response = await client.GetAsync(url);
        var json = await response.Content.ReadAsStringAsync();

        return Content(json, "application/json");
    }

    // ── Deutsche Bahn (Mock) ────────────────────────────────────────────

    [HttpGet]
    public IActionResult BahnSearch([FromQuery] string from, [FromQuery] string to, [FromQuery] string date)
    {
        var connections = new[]
        {
            new
            {
                abfahrt = "06:02",
                ankunft = "10:07",
                dauer = "4h 05min",
                umstiege = 0,
                preis = "59,90 \u20ac",
                zuege = "ICE 1001"
            },
            new
            {
                abfahrt = "07:55",
                ankunft = "12:30",
                dauer = "4h 35min",
                umstiege = 1,
                preis = "39,90 \u20ac",
                zuege = "ICE 785 / ICE 502"
            },
            new
            {
                abfahrt = "09:30",
                ankunft = "13:28",
                dauer = "3h 58min",
                umstiege = 0,
                preis = "79,90 \u20ac",
                zuege = "ICE 1005"
            },
            new
            {
                abfahrt = "12:02",
                ankunft = "16:45",
                dauer = "4h 43min",
                umstiege = 1,
                preis = "44,90 \u20ac",
                zuege = "IC 2292 / ICE 1590"
            },
            new
            {
                abfahrt = "15:30",
                ankunft = "19:28",
                dauer = "3h 58min",
                umstiege = 0,
                preis = "69,90 \u20ac",
                zuege = "ICE 1009"
            }
        };

        return Ok(new { von = from, nach = to, datum = date, verbindungen = connections });
    }

    // ── MVG (Mock) ──────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult MvgSearch([FromQuery] string from, [FromQuery] string to)
    {
        var connections = new[]
        {
            new { abfahrt = "08:03", linie = "U3", richtung = "F\u00fcrstenried West", dauer = "12 min", typ = "U-Bahn" },
            new { abfahrt = "08:07", linie = "S1", richtung = "Flughafen M\u00fcnchen", dauer = "22 min", typ = "S-Bahn" },
            new { abfahrt = "08:11", linie = "Bus 54", richtung = "Lorettoplatz", dauer = "18 min", typ = "Bus" },
            new { abfahrt = "08:15", linie = "Tram 19", richtung = "St.-Veit-Stra\u00dfe", dauer = "15 min", typ = "Tram" },
            new { abfahrt = "08:22", linie = "U6", richtung = "Klinikum Gro\u00dfhadern", dauer = "9 min", typ = "U-Bahn" }
        };

        return Ok(new { von = from, nach = to, verbindungen = connections });
    }

    // ── Traffic (Mock) ──────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Traffic([FromQuery] string from, [FromQuery] string to)
    {
        var result = new
        {
            strecke = $"{from} \u2192 {to}",
            status = "stockend",
            dauer = "47 min (normal: 32 min)",
            staus = new[]
            {
                new { ort = "A9 H\u00f6he Garching", laenge = "3 km", verzoegerung = "+8 min" },
                new { ort = "Mittlerer Ring / Schenkendorfstra\u00dfe", laenge = "1,2 km", verzoegerung = "+5 min" }
            },
            baustellen = new[]
            {
                new { ort = "Donnersberger Br\u00fccke", beschreibung = "Fahrbahnverengung auf 1 Spur", bis = "30.06.2026" }
            },
            hinweise = new[]
            {
                "Alternativroute \u00fcber A99 spart ca. 6 Minuten",
                "Berufsverkehr bis voraussichtlich 09:30 Uhr"
            }
        };

        return Ok(result);
    }

    // ── DTOs ────────────────────────────────────────────────────────────

    public class TileCreateDto
    {
        public string Titel { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? Icon { get; set; }
        public string? Farbe { get; set; }
    }

    public class TileReorderDto
    {
        public int Id { get; set; }
        public int Sortierung { get; set; }
    }
}
