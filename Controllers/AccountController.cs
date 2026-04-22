using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MerkurHub.Models;

namespace MerkurHub.Controllers;

public class AccountController : Controller
{
    private readonly AppDbContext _db;

    public AccountController(AppDbContext db) => _db = db;

    [HttpGet]
    public IActionResult Login() => View();

    [HttpPost]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Benutzername) || string.IsNullOrWhiteSpace(request.Passwort))
            return Json(new { ok = false, error = "Benutzername und Passwort erforderlich." });

        var hash = Hash(request.Passwort);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Benutzername == request.Benutzername && u.PasswortHash == hash && u.IstAktiv);

        if (user is null)
            return Json(new { ok = false, error = "Benutzername oder Passwort ungueltig." });

        await SignInUser(user);
        return Json(new { ok = true });
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync("MerkurHub");
        return RedirectToAction(nameof(Login));
    }

    // Registrierung per Einladungslink
    [HttpGet]
    public async Task<IActionResult> Registrieren(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return RedirectToAction(nameof(Login));

        var user = await _db.Users.FirstOrDefaultAsync(u => u.EinladungsToken == token);
        if (user == null || user.EinladungGueltigBis < DateTime.UtcNow)
        {
            TempData["Fehler"] = "Einladungslink ungueltig oder abgelaufen.";
            return RedirectToAction(nameof(Login));
        }

        ViewBag.Token = token;
        ViewBag.Email = user.Email;
        ViewBag.Anzeigename = user.Anzeigename;
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Registrieren(string token, string benutzername, string passwort, string anzeigename)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(benutzername) || string.IsNullOrWhiteSpace(passwort))
        {
            ViewBag.Token = token; ViewBag.Fehler = "Alle Felder ausfuellen.";
            return View();
        }

        if (passwort.Length < 6)
        {
            ViewBag.Token = token; ViewBag.Fehler = "Passwort muss mindestens 6 Zeichen haben.";
            return View();
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.EinladungsToken == token);
        if (user == null || user.EinladungGueltigBis < DateTime.UtcNow)
        {
            TempData["Fehler"] = "Einladungslink ungueltig oder abgelaufen.";
            return RedirectToAction(nameof(Login));
        }

        if (await _db.Users.AnyAsync(u => u.Benutzername == benutzername && u.Id != user.Id))
        {
            ViewBag.Token = token; ViewBag.Fehler = "Benutzername bereits vergeben.";
            return View();
        }

        user.Benutzername = benutzername.Trim();
        user.PasswortHash = Hash(passwort);
        user.Anzeigename = string.IsNullOrWhiteSpace(anzeigename) ? benutzername : anzeigename.Trim();
        user.IstAktiv = true;
        user.EinladungsToken = null;
        user.EinladungGueltigBis = null;
        await _db.SaveChangesAsync();

        await SignInUser(user);
        return RedirectToAction("Index", "Hub");
    }

    private async Task SignInUser(AppUser user)
    {
        var claims = new List<Claim>
        {
            new("UserId", user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Benutzername),
            new(ClaimTypes.GivenName, user.Anzeigename),
            new(ClaimTypes.Role, user.Rolle)
        };
        var identity = new ClaimsIdentity(claims, "MerkurHub");
        await HttpContext.SignInAsync("MerkurHub", new ClaimsPrincipal(identity));
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public class LoginRequest
    {
        public string Benutzername { get; set; } = string.Empty;
        public string Passwort { get; set; } = string.Empty;
    }
}
