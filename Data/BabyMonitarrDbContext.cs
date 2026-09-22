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
    public DbSet<CastDevice> CastDevices => Set<CastDevice>();
    public DbSet<RoomCastTarget> RoomCastTargets => Set<RoomCastTarget>();
    public DbSet<HaMonitoredRoom> HaMonitoredRooms => Set<HaMonitoredRoom>();

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

        modelBuilder.Entity<CastDevice>(entity =>
        {
            entity.HasIndex(d => d.DeviceId).IsUnique();
        });

        modelBuilder.Entity<RoomCastTarget>(entity =>
        {
            entity.HasIndex(t => new { t.RoomId, t.DeviceId }).IsUnique();
        });

        modelBuilder.Entity<HaMonitoredRoom>(entity =>
        {
            entity.HasIndex(m => m.RoomId).IsUnique();
        });

        modelBuilder.Entity<GlobalSettings>().HasData(new GlobalSettings { Id = 1 });
        modelBuilder.Entity<GoogleNestSettings>().HasData(new GoogleNestSettings { Id = 1 });
    }
}
