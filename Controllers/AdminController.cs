using System.Security.Cryptography;
using System.Text;
using MerkurHub.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MerkurHub.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{
    private readonly AppDbContext _db;

    public AdminController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Benutzer()
    {
        var users = await _db.Users.OrderBy(u => u.Benutzername).ToListAsync();
        return View(users);
    }

    [HttpPost]
    public async Task<IActionResult> BenutzerEinladen(string email, string anzeigename, string rolle)
    {
        if (string.IsNullOrWhiteSpace(email))
        { TempData["Fehler"] = "E-Mail ist erforderlich."; return RedirectToAction(nameof(Benutzer)); }

        var token = Guid.NewGuid().ToString("N");
        var user = new AppUser
        {
            Benutzername = "",
            PasswortHash = "",
            Anzeigename = anzeigename ?? email,
            Email = email.Trim(),
            Rolle = rolle ?? "User",
            IstAktiv = false,
            EinladungsToken = token,
            EinladungGueltigBis = DateTime.UtcNow.AddDays(7)
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        TempData["Erfolg"] = $"Einladungslink erstellt (7 Tage gültig):";
        TempData["EinladungsLink"] = $"{baseUrl}/Account/Registrieren?token={token}";

        return RedirectToAction(nameof(Benutzer));
    }

    [HttpPost]
    public async Task<IActionResult> BenutzerAktivieren(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user != null) { user.IstAktiv = !user.IstAktiv; await _db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Benutzer));
    }

    [HttpPost]
    public async Task<IActionResult> BenutzerLoeschen(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user != null && user.Benutzername != "yh04sc9")
        {
            _db.Users.Remove(user);
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Benutzer));
    }

    [HttpPost]
    public async Task<IActionResult> RolleAendern(int id, string rolle)
    {
        var user = await _db.Users.FindAsync(id);
        if (user != null) { user.Rolle = rolle; await _db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Benutzer));
    }
}
