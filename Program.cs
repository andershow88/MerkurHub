using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using MerkurHub.Models;

var builder = WebApplication.CreateBuilder(args);

var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrWhiteSpace(dbUrl))
{
    var cs = ParsePostgresUrl(dbUrl);
    builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs));
}
else
{
    var path = Path.Combine(builder.Environment.ContentRootPath, "merkurhub.db");
    builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={path}"));
}

builder.Services.AddAuthentication("MerkurHub")
    .AddCookie("MerkurHub", opt =>
    {
        opt.LoginPath = "/Account/Login";
        opt.LogoutPath = "/Account/Logout";
        opt.ExpireTimeSpan = TimeSpan.FromHours(10);
        opt.SlidingExpiration = true;
        opt.Cookie.Name = "MerkurHub.Auth";
        opt.Cookie.HttpOnly = true;
        opt.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();

var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Hub/Error");

app.UseStaticFiles();
Directory.CreateDirectory(Path.Combine(app.Environment.WebRootPath, "uploads", "documents"));
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute("default", "{controller=Hub}/{action=Index}/{id?}");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await db.Database.EnsureCreatedAsync();

        // Fehlende Tabellen nachträglich anlegen (EnsureCreated überspringt existierende DBs)
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        var isPostgres = db.Database.ProviderName?.Contains("Npgsql") == true;

        if (isPostgres)
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS "PdfDokumente" (
                    "Id" SERIAL PRIMARY KEY,
                    "Titel" TEXT NOT NULL DEFAULT '',
                    "Dateiname" TEXT NOT NULL DEFAULT '',
                    "Dateipfad" TEXT NOT NULL DEFAULT '',
                    "Dateigroesse" BIGINT NOT NULL DEFAULT 0,
                    "Seitenanzahl" INT NOT NULL DEFAULT 0,
                    "HochgeladenVonId" INT NOT NULL DEFAULT 0,
                    "HochgeladenVonName" TEXT NOT NULL DEFAULT '',
                    "HochgeladenAm" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
                    "IstVerarbeitet" BOOLEAN NOT NULL DEFAULT FALSE
                );
                CREATE TABLE IF NOT EXISTS "DokumentSeiten" (
                    "Id" SERIAL PRIMARY KEY,
                    "PdfDokumentId" INT NOT NULL DEFAULT 0,
                    "Seitennummer" INT NOT NULL DEFAULT 0,
                    "Text" TEXT NOT NULL DEFAULT '',
                    "EmbeddingJson" TEXT NULL
                );
                """;
        }
        else
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS "PdfDokumente" (
                    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "Titel" TEXT NOT NULL DEFAULT '',
                    "Dateiname" TEXT NOT NULL DEFAULT '',
                    "Dateipfad" TEXT NOT NULL DEFAULT '',
                    "Dateigroesse" INTEGER NOT NULL DEFAULT 0,
                    "Seitenanzahl" INTEGER NOT NULL DEFAULT 0,
                    "HochgeladenVonId" INTEGER NOT NULL DEFAULT 0,
                    "HochgeladenVonName" TEXT NOT NULL DEFAULT '',
                    "HochgeladenAm" TEXT NOT NULL DEFAULT '',
                    "IstVerarbeitet" INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS "DokumentSeiten" (
                    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "PdfDokumentId" INTEGER NOT NULL DEFAULT 0,
                    "Seitennummer" INTEGER NOT NULL DEFAULT 0,
                    "Text" TEXT NOT NULL DEFAULT '',
                    "EmbeddingJson" TEXT NULL
                );
                """;
        }
        await cmd.ExecuteNonQueryAsync();

        // Neue Spalten + Tabelle nachrüsten
        using var cmdTiles = conn.CreateCommand();
        if (isPostgres)
            cmdTiles.CommandText = """
                ALTER TABLE "Tiles" ADD COLUMN IF NOT EXISTS "AutoLogin" BOOLEAN NOT NULL DEFAULT FALSE;
                ALTER TABLE "Tiles" ADD COLUMN IF NOT EXISTS "FeldUsername" TEXT NULL;
                ALTER TABLE "Tiles" ADD COLUMN IF NOT EXISTS "FeldPasswort" TEXT NULL;
                CREATE TABLE IF NOT EXISTS "UserCredentials" (
                    "Id" SERIAL PRIMARY KEY,
                    "TileId" INT NOT NULL DEFAULT 0,
                    "BenutzerId" INT NOT NULL DEFAULT 0,
                    "Username" TEXT NOT NULL DEFAULT '',
                    "Passwort" TEXT NOT NULL DEFAULT ''
                );
                """;
        else
            cmdTiles.CommandText = """
                CREATE TABLE IF NOT EXISTS "UserCredentials" (
                    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "TileId" INTEGER NOT NULL DEFAULT 0,
                    "BenutzerId" INTEGER NOT NULL DEFAULT 0,
                    "Username" TEXT NOT NULL DEFAULT '',
                    "Passwort" TEXT NOT NULL DEFAULT ''
                );
                """;
        try { await cmdTiles.ExecuteNonQueryAsync(); } catch { }

        // SQLite: Spalten einzeln nachrüsten (kein IF NOT EXISTS)
        if (!isPostgres)
        {
            foreach (var col in new[] { "ALTER TABLE \"Tiles\" ADD COLUMN \"AutoLogin\" INTEGER NOT NULL DEFAULT 0",
                                         "ALTER TABLE \"Tiles\" ADD COLUMN \"FeldUsername\" TEXT NULL",
                                         "ALTER TABLE \"Tiles\" ADD COLUMN \"FeldPasswort\" TEXT NULL" })
            {
                using var c3 = conn.CreateCommand(); c3.CommandText = col;
                try { await c3.ExecuteNonQueryAsync(); } catch { }
            }
        }

        // Tiles.Global Spalte nachrüsten
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = isPostgres
            ? "ALTER TABLE \"Tiles\" ADD COLUMN IF NOT EXISTS \"Global\" BOOLEAN NOT NULL DEFAULT FALSE;"
            : "ALTER TABLE \"Tiles\" ADD COLUMN \"Global\" INTEGER NOT NULL DEFAULT 0;";
        try { await cmd2.ExecuteNonQueryAsync(); } catch { /* Spalte existiert bereits */ }

        log.LogInformation("Tabellen-Check abgeschlossen");

        if (!await db.Users.AnyAsync())
        {
            db.Users.AddRange(
                new AppUser { Benutzername = "admin", PasswortHash = Hash("Admin1234!"), Anzeigename = "Administrator", Rolle = "Admin" },
                new AppUser { Benutzername = "user", PasswortHash = Hash("Demo1234!"), Anzeigename = "Max Mustermann", Rolle = "User" }
            );
            await db.SaveChangesAsync();
        }
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Seed fehlgeschlagen, DB wird neu erstellt");
        try
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.Users.AddRange(
                new AppUser { Benutzername = "admin", PasswortHash = Hash("Admin1234!"), Anzeigename = "Administrator", Rolle = "Admin" },
                new AppUser { Benutzername = "user", PasswortHash = Hash("Demo1234!"), Anzeigename = "Max Mustermann", Rolle = "User" }
            );
            await db.SaveChangesAsync();
        }
        catch { }
    }
}

app.Run();

static string Hash(string s) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

static string ParsePostgresUrl(string url)
{
    var uri = new Uri(url);
    var ui = uri.UserInfo.Split(':', 2);
    var db = uri.AbsolutePath.TrimStart('/');
    return $"Host={uri.Host};Port={(uri.Port > 0 ? uri.Port : 5432)};Username={ui[0]};Password={ui.ElementAtOrDefault(1)};Database={db};SSL Mode=Require;Trust Server Certificate=true;";
}
