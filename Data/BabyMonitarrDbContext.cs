using Microsoft.EntityFrameworkCore;
using BabyMonitarr.Backend.Models;

namespace BabyMonitarr.Backend.Data;

public class BabyMonitarrDbContext : DbContext
{
    public BabyMonitarrDbContext(DbContextOptions<BabyMonitarrDbContext> options) : base(options) { }

    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<GlobalSettings> GlobalSettings => Set<GlobalSettings>();
    public DbSet<GoogleNestSettings> GoogleNestSettings => Set<GoogleNestSettings>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Room>(entity =>
        {
            entity.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Username).IsUnique();
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasIndex(k => k.KeyHash).IsUnique();
            entity.HasIndex(k => k.KeyPrefix);
            entity.HasOne(k => k.User)
                .WithMany()
                .HasForeignKey(k => k.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GlobalSettings>().HasData(new GlobalSettings { Id = 1 });
        modelBuilder.Entity<GoogleNestSettings>().HasData(new GoogleNestSettings { Id = 1 });
    }
}
