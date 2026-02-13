using Microsoft.EntityFrameworkCore;
using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Data;

public class BabyMonitarrDbContext : DbContext
{
    public BabyMonitarrDbContext(DbContextOptions<BabyMonitarrDbContext> options) : base(options) { }

    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<GlobalSettings> GlobalSettings => Set<GlobalSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Room>(entity =>
        {
            entity.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<GlobalSettings>().HasData(new GlobalSettings { Id = 1 });
    }
}
