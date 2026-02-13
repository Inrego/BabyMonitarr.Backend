using Microsoft.EntityFrameworkCore;
using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Data;

public class BabyMonitarrDbContext : DbContext
{
    public BabyMonitarrDbContext(DbContextOptions<BabyMonitarrDbContext> options) : base(options) { }

    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<GlobalSettings> GlobalSettings => Set<GlobalSettings>();
    public DbSet<GoogleNestSettings> GoogleNestSettings => Set<GoogleNestSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Room>(entity =>
        {
            entity.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<GlobalSettings>().HasData(new GlobalSettings { Id = 1 });
        modelBuilder.Entity<GoogleNestSettings>().HasData(new GoogleNestSettings { Id = 1 });
    }
}
