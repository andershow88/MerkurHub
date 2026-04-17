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

    // GET /Account/Login
    [HttpGet]
    public IActionResult Login() => View();

    // POST /Account/Login  (AJAX, JSON body)
    [HttpPost]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Benutzername)
            || string.IsNullOrWhiteSpace(request.Passwort))
        {
            return Json(new { ok = false, error = "Benutzername und Passwort erforderlich." });
        }

        var hash = Hash(request.Passwort);

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Benutzername == request.Benutzername
                                   && u.PasswortHash == hash
                                   && u.IstAktiv);

        if (user is null)
            return Json(new { ok = false, error = "Benutzername oder Passwort ungueltig." });

        var claims = new List<Claim>
        {
            new("UserId",                  user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name,           user.Benutzername),
            new(ClaimTypes.GivenName,      user.Anzeigename),
            new(ClaimTypes.Role,           user.Rolle)
        };

        var identity  = new ClaimsIdentity(claims, "MerkurHub");
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync("MerkurHub", principal);

        return Json(new { ok = true });
    }

    // POST /Account/Logout
    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync("MerkurHub");
        return RedirectToAction(nameof(Login));
    }

    // ---- helpers ----

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public class LoginRequest
    {
        public string Benutzername { get; set; } = string.Empty;
        public string Passwort { get; set; } = string.Empty;
    }
}
