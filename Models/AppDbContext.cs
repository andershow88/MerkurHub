using Microsoft.EntityFrameworkCore;

namespace MerkurHub.Models;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Tile> Tiles => Set<Tile>();
    public DbSet<AppUser> Users => Set<AppUser>();
}

public class Tile
{
    public int Id { get; set; }
    public string Titel { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Farbe { get; set; }
    public int Sortierung { get; set; }
    public int BenutzerId { get; set; }
    public DateTime ErstelltAm { get; set; } = DateTime.UtcNow;
}

public class AppUser
{
    public int Id { get; set; }
    public string Benutzername { get; set; } = string.Empty;
    public string PasswortHash { get; set; } = string.Empty;
    public string Anzeigename { get; set; } = string.Empty;
    public string Rolle { get; set; } = "User";
    public bool IstAktiv { get; set; } = true;
}
