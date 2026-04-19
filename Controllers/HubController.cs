using MerkurHub.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace MerkurHub.Controllers;

[Authorize]
public class HubController : Controller
{
    private readonly AppDbContext _db;

    public HubController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        var uid = int.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : 0;

        var tiles = await _db.Tiles
            .Where(t => t.BenutzerId == uid)
            .OrderBy(t => t.Sortierung)
            .ToListAsync();

        return View(tiles);
    }

    public IActionResult AutoLogin(int tileId, string url, string? feldUser, string? feldPass)
    {
        ViewBag.TileId = tileId;
        ViewBag.Url = url;
        ViewBag.FeldUser = feldUser;
        ViewBag.FeldPass = feldPass;
        return View();
    }

    public IActionResult Error()
    {
        return View();
    }
}
