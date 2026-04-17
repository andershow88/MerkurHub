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
    try
    {
        await db.Database.EnsureCreatedAsync();
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
        var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
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
