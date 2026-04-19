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
    private readonly ILogger<ApiController> _logger;

    public ApiController(AppDbContext db, IHttpClientFactory httpClientFactory, ILogger<ApiController> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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

    // ── Deutsche Bahn (Real API via v6.db.transport.rest) ───────────────

    [HttpGet]
    public async Task<IActionResult> BahnSearch([FromQuery] string from, [FromQuery] string to, [FromQuery] string? date, [FromQuery] string? time, [FromQuery] bool ersteKlasse = false, [FromQuery] string? fromId = null, [FromQuery] string? toId = null)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(25);

            // Station-IDs: direkt nutzen wenn vorhanden, sonst parallel suchen
            if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId))
            {
                var fromTask = client.GetStringAsync(
                    $"https://v6.db.transport.rest/locations?query={Uri.EscapeDataString(from)}&results=1");
                var toTask = client.GetStringAsync(
                    $"https://v6.db.transport.rest/locations?query={Uri.EscapeDataString(to)}&results=1");

                await Task.WhenAll(fromTask, toTask);

                using var fromDoc = JsonDocument.Parse(fromTask.Result);
                using var toDoc = JsonDocument.Parse(toTask.Result);

                if (fromDoc.RootElement.GetArrayLength() == 0 || toDoc.RootElement.GetArrayLength() == 0)
                    return Json(new { error = $"Station nicht gefunden: {(fromDoc.RootElement.GetArrayLength() == 0 ? from : to)}" });

                fromId = fromDoc.RootElement[0].GetProperty("id").GetString();
                toId = toDoc.RootElement[0].GetProperty("id").GetString();
            }

            var depParam = "";
            {
                var berlinTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
                var berlinNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, berlinTz);
                var d = string.IsNullOrEmpty(date) ? berlinNow.Date : DateTime.Parse(date);
                var t = string.IsNullOrEmpty(time) ? berlinNow.TimeOfDay : TimeSpan.Parse(time);
                var local = d + t;
                var dto = new DateTimeOffset(local, berlinTz.GetUtcOffset(local));
                depParam = $"&departure={Uri.EscapeDataString(dto.ToString("O"))}";
            }

            var klasse = ersteKlasse ? "&firstClass=true" : "";
            var journeyUrl = $"https://v6.db.transport.rest/journeys?from={fromId}&to={toId}{depParam}&results=6{klasse}";
            var journeyResp = await client.GetStringAsync(journeyUrl);

            using var jDoc = JsonDocument.Parse(journeyResp);
            var results = new List<object>();

            foreach (var journey in jDoc.RootElement.GetProperty("journeys").EnumerateArray().Take(6))
            {
                var legs = journey.GetProperty("legs");
                var firstLeg = legs[0];
                var lastLeg = legs[legs.GetArrayLength() - 1];

                // Zeiten direkt aus ISO-String extrahieren (sind bereits in Lokalzeit)
                var depStr = firstLeg.GetProperty("departure").GetString() ?? "";
                var arrStr = lastLeg.GetProperty("arrival").GetString() ?? "";
                var abfahrt = depStr.Length >= 16 ? depStr.Substring(11, 5) : "??:??";
                var ankunft = arrStr.Length >= 16 ? arrStr.Substring(11, 5) : "??:??";

                // Dauer aus DateTimeOffset (korrekt über Zeitzonen)
                var depDto = DateTimeOffset.Parse(depStr);
                var arrDto = DateTimeOffset.Parse(arrStr);
                var dauer = arrDto - depDto;
                var dauerStr = $"{(int)dauer.TotalHours}:{dauer.Minutes:D2} h";

                var umstiege = legs.GetArrayLength() - 1;

                // Verspaetung — Zeiten sicher extrahieren
                var depDelay = firstLeg.TryGetProperty("departureDelay", out var dd) && dd.ValueKind == JsonValueKind.Number ? dd.GetInt32() : (int?)null;
                var arrDelay = lastLeg.TryGetProperty("arrivalDelay", out var ad) && ad.ValueKind == JsonValueKind.Number ? ad.GetInt32() : (int?)null;
                var depDelayMin = depDelay.HasValue ? depDelay.Value / 60 : (int?)null;
                var arrDelayMin = arrDelay.HasValue ? arrDelay.Value / 60 : (int?)null;

                var abfahrtReal = abfahrt;
                var ankunftReal = ankunft;

                if (depDelay.HasValue && depDelay.Value > 0)
                {
                    try
                    {
                        var planned = DateTimeOffset.Parse(depStr);
                        var real = planned.AddSeconds(depDelay.Value);
                        abfahrt = planned.ToString("HH:mm");
                        abfahrtReal = real.ToString("HH:mm");
                    }
                    catch { }
                }
                if (arrDelay.HasValue && arrDelay.Value > 0)
                {
                    try
                    {
                        var planned = DateTimeOffset.Parse(arrStr);
                        var real = planned.AddSeconds(arrDelay.Value);
                        ankunft = planned.ToString("HH:mm");
                        ankunftReal = real.ToString("HH:mm");
                    }
                    catch { }
                }

                var status = "unbekannt";
                if (depDelayMin.HasValue)
                    status = depDelayMin.Value <= 0 ? "puenktlich" : $"+{depDelayMin.Value} min";

                var zuege = new List<string>();
                foreach (var leg in legs.EnumerateArray())
                {
                    if (leg.TryGetProperty("line", out var line))
                    {
                        var name = line.TryGetProperty("name", out var n) ? n.GetString() : "";
                        if (!string.IsNullOrEmpty(name)) zuege.Add(name);
                    }
                    else if (leg.TryGetProperty("walking", out var w) && w.GetBoolean())
                    {
                        umstiege = Math.Max(0, umstiege - 1);
                    }
                }

                var preis = "Preis auf bahn.de";
                if (journey.TryGetProperty("price", out var price) && price.ValueKind != JsonValueKind.Null)
                {
                    if (price.TryGetProperty("amount", out var amount))
                        preis = amount.GetDecimal().ToString("0.00") + " \u20ac";
                }

                results.Add(new { abfahrt, abfahrtReal, ankunft, ankunftReal, dauer = dauerStr, umstiege, zuege = string.Join(" \u2192 ", zuege), preis, status, depDelayMin, arrDelayMin });
            }

            return Json(results);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DB-Suche fehlgeschlagen: {From} -> {To}", from, to);
            return Json(new { error = "Keine Verbindungen gefunden. Bitte pr\u00fcfen Sie die Eingabe oder versuchen Sie es sp\u00e4ter erneut." });
        }
    }

    // ── MVG M\u00fcnchen (Real API via v6.db.transport.rest) ────────────────

    [HttpGet]
    public async Task<IActionResult> MvgSearch([FromQuery] string from, [FromQuery] string to, [FromQuery] string? time)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            var fromResp = await client.GetStringAsync(
                $"https://v6.db.transport.rest/locations?query={Uri.EscapeDataString(from)}&results=1");
            var toResp = await client.GetStringAsync(
                $"https://v6.db.transport.rest/locations?query={Uri.EscapeDataString(to)}&results=1");

            using var fromDoc = JsonDocument.Parse(fromResp);
            using var toDoc = JsonDocument.Parse(toResp);

            var fromId = fromDoc.RootElement[0].GetProperty("id").GetString();
            var toId = toDoc.RootElement[0].GetProperty("id").GetString();

            var depParam = "";
            {
                var berlinTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
                var berlinNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, berlinTz);
                var d = berlinNow.Date;
                var t = string.IsNullOrEmpty(time) ? berlinNow.TimeOfDay : TimeSpan.Parse(time);
                var local = d + t;
                // Wenn gewählte Zeit vor aktueller Zeit liegt → nächster Tag
                if (local < berlinNow.AddMinutes(-5))
                    local = local.AddDays(1);
                var dto = new DateTimeOffset(local, berlinTz.GetUtcOffset(local));
                depParam = $"&departure={Uri.EscapeDataString(dto.ToString("O"))}";
            }

            var url = $"https://v6.db.transport.rest/journeys?from={fromId}&to={toId}{depParam}&results=6&suburban=true&subway=true&tram=true&bus=true&regional=true&nationalExpress=false&national=false";
            var journeyResp = await client.GetStringAsync(url);
            using var jDoc = JsonDocument.Parse(journeyResp);

            var results = new List<object>();
            foreach (var journey in jDoc.RootElement.GetProperty("journeys").EnumerateArray().Take(6))
            {
                var legs = journey.GetProperty("legs");
                var firstLeg = legs[0];
                var lastLeg = legs[legs.GetArrayLength() - 1];

                var depStr = firstLeg.GetProperty("departure").GetString() ?? "";
                var arrStr = lastLeg.GetProperty("arrival").GetString() ?? "";
                var abfahrt = depStr.Length >= 16 ? depStr.Substring(11, 5) : "??:??";
                var ankunft = arrStr.Length >= 16 ? arrStr.Substring(11, 5) : "??:??";

                var depDto = DateTimeOffset.Parse(depStr);
                var arrDto = DateTimeOffset.Parse(arrStr);
                var dauer = arrDto - depDto;
                var dauerStr = dauer.TotalHours >= 1 ? $"{(int)dauer.TotalHours}:{dauer.Minutes:D2} h" : $"{(int)dauer.TotalMinutes} min";

                var linien = new List<string>();
                var typ = "OEPNV";
                foreach (var leg in legs.EnumerateArray())
                {
                    if (leg.TryGetProperty("line", out var line))
                    {
                        var name = line.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var product = line.TryGetProperty("product", out var p) ? p.GetString() : null;
                        if (!string.IsNullOrEmpty(name)) linien.Add(name);
                        if (product != null) typ = product switch
                        {
                            "suburban" or "suburbanExpress" => "S-Bahn",
                            "subway" => "U-Bahn",
                            "tram" => "Tram",
                            "bus" => "Bus",
                            _ => typ
                        };
                    }
                }

                var richtung = lastLeg.TryGetProperty("destination", out var dest) && dest.TryGetProperty("name", out var dn) ? dn.GetString() : to;

                results.Add(new { abfahrt, linie = string.Join(" + ", linien), richtung, dauer = dauerStr, typ, ankunft });
            }
            return Json(results);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MVG-Suche fehlgeschlagen: {From} -> {To}", from, to);
            return Json(new { error = "Keine Verbindungen gefunden. Bitte pr\u00fcfen Sie die Haltestellen oder versuchen Sie es sp\u00e4ter." });
        }
    }

    // ── Autobahn Traffic (Real API via verkehr.autobahn.de) ─────────────

    [HttpGet]
    public async Task<IActionResult> Traffic([FromQuery] string from, [FromQuery] string to)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            var strecke = $"{from} \u2192 {to}";
            var autobahnen = DetectAutobahnen(from, to);

            var marker = new List<object>();

            foreach (var ab in autobahnen.Take(3))
            {
                try
                {
                    var wTask = client.GetStringAsync($"https://verkehr.autobahn.de/o/autobahn/{ab}/services/warning");
                    var rTask = client.GetStringAsync($"https://verkehr.autobahn.de/o/autobahn/{ab}/services/roadworks");
                    var cTask = client.GetStringAsync($"https://verkehr.autobahn.de/o/autobahn/{ab}/services/closure");
                    await Task.WhenAll(wTask, rTask, cTask);

                    void ParseItems(string json, string propName, string typ)
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (!doc.RootElement.TryGetProperty(propName, out var items)) return;
                        foreach (var item in items.EnumerateArray().Take(8))
                        {
                            var title = item.TryGetProperty("title", out var t) ? t.GetString() : "";
                            var desc = item.TryGetProperty("subtitle", out var s) ? s.GetString() : "";
                            var desc2 = item.TryGetProperty("description", out var d2) ? d2.GetString() : "";
                            double? lat = null, lng = null;
                            if (item.TryGetProperty("coordinate", out var coord))
                            {
                                lat = coord.TryGetProperty("lat", out var la) ? la.GetString() is { } ls ? double.TryParse(ls, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lv) ? lv : null : null : null;
                                lng = coord.TryGetProperty("long", out var lo) ? lo.GetString() is { } los ? double.TryParse(los, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lov) ? lov : null : null : null;
                            }
                            if (!string.IsNullOrEmpty(title))
                                marker.Add(new { typ, autobahn = ab, title, desc, desc2, lat, lng });
                        }
                    }

                    ParseItems(wTask.Result, "warning", "stau");
                    ParseItems(rTask.Result, "roadworks", "baustelle");
                    ParseItems(cTask.Result, "closure", "sperrung");
                }
                catch { }
            }

            var stauCount = marker.Count(m => ((dynamic)m).typ == "stau");
            var status = stauCount > 2 ? "stau" : stauCount > 0 ? "stockend" : "frei";
            var dauer = status == "stau" ? "Deutlich erh\u00f6ht" : status == "stockend" ? "Leicht erh\u00f6ht" : "Normal";

            return Json(new { strecke, status, dauer, marker, autobahnen });
        }
        catch
        {
            // Fallback
            return Json(new
            {
                strecke = $"{from} \u2192 {to}",
                status = "frei",
                dauer = "Normal",
                staus = Array.Empty<string>(),
                baustellen = Array.Empty<string>(),
                hinweise = new[] { "Verkehrsdaten konnten nicht geladen werden." }
            });
        }
    }

    // ── Aktien-Kurs (Yahoo Finance Proxy) ──────────────────────────────

    [HttpGet]
    public async Task<IActionResult> AktienKurs([FromQuery] string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return Json(new { error = "Symbol fehlt" });
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            // Chart-Daten fuer Sparkline (5min Intervall, 1 Tag fuer Intraday)
            var chartUrl = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?interval=5m&range=1d&includePrePost=false";
            var chartResp = await client.GetStringAsync(chartUrl);
            using var chartDoc = JsonDocument.Parse(chartResp);

            var result = chartDoc.RootElement.GetProperty("chart").GetProperty("result")[0];
            var meta = result.GetProperty("meta");

            var preis = meta.GetProperty("regularMarketPrice").GetDouble();
            var vorher = meta.GetProperty("chartPreviousClose").GetDouble();
            var waehrung = meta.TryGetProperty("currency", out var cur) ? cur.GetString() : "";
            var veraenderung = preis - vorher;
            var veraenderungProzent = vorher != 0 ? (veraenderung / vorher) * 100 : 0;

            // Intraday-Verlauf fuer Sparkline
            var verlauf = new List<double>();
            if (result.TryGetProperty("indicators", out var indicators) &&
                indicators.TryGetProperty("quote", out var quote) &&
                quote[0].TryGetProperty("close", out var closes))
            {
                foreach (var c in closes.EnumerateArray())
                {
                    if (c.ValueKind == JsonValueKind.Number)
                        verlauf.Add(c.GetDouble());
                    else if (verlauf.Count > 0)
                        verlauf.Add(verlauf.Last());
                }
            }

            // Wenn Intraday leer (Boerse geschlossen), 1-Monat-Daten holen
            if (verlauf.Count < 5)
            {
                verlauf.Clear();
                var monatUrl = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?interval=1d&range=1mo&includePrePost=false";
                var monatResp = await client.GetStringAsync(monatUrl);
                using var monatDoc = JsonDocument.Parse(monatResp);
                var monatResult = monatDoc.RootElement.GetProperty("chart").GetProperty("result")[0];
                if (monatResult.TryGetProperty("indicators", out var mi) &&
                    mi.TryGetProperty("quote", out var mq) &&
                    mq[0].TryGetProperty("close", out var mc))
                {
                    foreach (var c in mc.EnumerateArray())
                        if (c.ValueKind == JsonValueKind.Number) verlauf.Add(c.GetDouble());
                }
            }

            var zeitpunkt = meta.TryGetProperty("regularMarketTime", out var rmt)
                ? DateTimeOffset.FromUnixTimeSeconds(rmt.GetInt64()).ToOffset(TimeSpan.FromHours(2)).ToString("dd.MM.yyyy HH:mm")
                : "";

            return Json(new { preis, vorher, veraenderung, veraenderungProzent, waehrung, verlauf, zeitpunkt });
        }
        catch (Exception ex)
        {
            return Json(new { error = ex.Message });
        }
    }

    // ── Aktien-Suche (Yahoo Finance Autocomplete) ───────────────────────

    [HttpGet]
    public async Task<IActionResult> AktienSuche([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 1)
            return Json(Array.Empty<object>());
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var url = $"https://query1.finance.yahoo.com/v1/finance/search?q={Uri.EscapeDataString(q)}&quotesCount=15&newsCount=0&listsCount=0&enableFuzzyQuery=false";
            var resp = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(resp);

            var results = new List<object>();
            if (doc.RootElement.TryGetProperty("quotes", out var quotes))
            {
                foreach (var item in quotes.EnumerateArray().Take(15))
                {
                    var symbol = item.TryGetProperty("symbol", out var s) ? s.GetString() : null;
                    var name = item.TryGetProperty("shortname", out var n) ? n.GetString() :
                               item.TryGetProperty("longname", out var ln) ? ln.GetString() : null;
                    var typ = item.TryGetProperty("quoteType", out var t) ? t.GetString() : "";
                    var exchange = item.TryGetProperty("exchange", out var ex) ? ex.GetString() : "";
                    if (symbol != null)
                        results.Add(new { symbol, name = name ?? symbol, typ, exchange });
                }
            }
            return Json(results);
        }
        catch { return Json(Array.Empty<object>()); }
    }

    // ── Autocomplete: DB Stationen ──────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> BahnStationen(string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 1)
            return Json(Array.Empty<object>());
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var resp = await client.GetStringAsync(
                $"https://v6.db.transport.rest/locations?query={Uri.EscapeDataString(q)}&results=15&stops=true&addresses=false&poi=false");
            using var doc = JsonDocument.Parse(resp);
            var results = new List<object>();
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var name = s.TryGetProperty("name", out var n) ? n.GetString() : null;
                var id = s.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (name != null && id != null)
                    results.Add(new { id, name });
            }
            return Json(results);
        }
        catch { return Json(Array.Empty<object>()); }
    }

    // ── Autocomplete: MVG Stationen ─────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> MvgStationen(string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 1)
            return Json(Array.Empty<object>());
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var resp = await client.GetStringAsync(
                $"https://www.mvg.de/api/bgw-pt/v3/locations?query={Uri.EscapeDataString(q)}&limit=15");
            using var doc = JsonDocument.Parse(resp);
            var results = new List<object>();
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var name = s.TryGetProperty("name", out var n) ? n.GetString() : null;
                var place = s.TryGetProperty("place", out var p) ? p.GetString() : null;
                var gid = s.TryGetProperty("globalId", out var g) ? g.GetString() : null;
                var types = new List<string>();
                if (s.TryGetProperty("transportTypes", out var tt) && tt.ValueKind == JsonValueKind.Array)
                    foreach (var t in tt.EnumerateArray())
                        if (t.GetString() is { } ts) types.Add(ts);
                if (name != null)
                    results.Add(new { id = gid ?? name, name, place, types = string.Join(",", types) });
            }
            return Json(results);
        }
        catch { return Json(Array.Empty<object>()); }
    }

    // ── Helper: detect relevant Autobahn(s) from city names ─────────────

    private static string[] DetectAutobahnen(string from, string to)
    {
        var combined = (from + " " + to).ToLowerInvariant();
        var result = new List<string>();

        // Munich routes
        if (combined.Contains("m\u00fcnchen") || combined.Contains("munich"))
        {
            if (combined.Contains("berlin")) { result.Add("A9"); result.Add("A10"); }
            else if (combined.Contains("stuttgart")) { result.Add("A8"); }
            else if (combined.Contains("frankfurt")) { result.Add("A9"); result.Add("A3"); }
            else if (combined.Contains("n\u00fcrnberg") || combined.Contains("nuernberg")) { result.Add("A9"); }
            else if (combined.Contains("salzburg") || combined.Contains("innsbruck")) { result.Add("A8"); }
            else if (combined.Contains("augsburg")) { result.Add("A8"); }
            else if (combined.Contains("regensburg")) { result.Add("A93"); }
            else { result.Add("A9"); result.Add("A8"); } // default Munich
        }
        else if (combined.Contains("berlin"))
        {
            if (combined.Contains("hamburg")) { result.Add("A24"); }
            else if (combined.Contains("frankfurt")) { result.Add("A2"); result.Add("A5"); }
            else { result.Add("A2"); result.Add("A10"); }
        }
        else if (combined.Contains("frankfurt"))
        {
            result.Add("A5"); result.Add("A3");
        }
        else if (combined.Contains("hamburg"))
        {
            result.Add("A7"); result.Add("A1");
        }

        if (result.Count == 0) { result.Add("A9"); } // general fallback

        return result.ToArray();
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
