using Microsoft.EntityFrameworkCore;
using BabyMonitarr.Backend.Data;
using BabyMonitarr.Backend.Models;
using BabyMonitarr.Backend.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Add CORS policy for SignalR clients
builder.Services.AddCors(options =>
{
    options.AddPolicy("SignalRPolicy", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });

    options.AddPolicy("SignalRWithCredentials", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Add SQLite via EF Core
builder.Services.AddDbContext<BabyMonitarrDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Register services
builder.Services.AddScoped<IRoomService, RoomService>();
builder.Services.AddSingleton<IAudioProcessingService, AudioProcessingService>();
builder.Services.AddSingleton<IWebRtcService, WebRtcService>();
builder.Services.AddHostedService<AudioStreamingBackgroundService>();

// Add SignalR
builder.Services.AddSignalR();

var app = builder.Build();

// Initialize database and seed data
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
    db.Database.EnsureCreated();

    // Seed from appsettings.json if DB has no rooms yet
    if (!db.Rooms.Any())
    {
        var legacySettings = builder.Configuration.GetSection("AudioSettings").Get<AudioSettings>();
        if (legacySettings != null && !string.IsNullOrEmpty(legacySettings.CameraStreamUrl))
        {
            db.Rooms.Add(new Room
            {
                Name = "Default Room",
                Icon = "baby",
                MonitorType = "camera_audio",
                CameraStreamUrl = legacySettings.CameraStreamUrl,
                CameraUsername = legacySettings.CameraUsername,
                CameraPassword = legacySettings.CameraPassword,
                UseCameraAudioStream = legacySettings.UseCameraAudioStream,
                IsActive = true
            });
        }

        // Seed global settings from legacy config
        var globalSettings = db.GlobalSettings.Find(1);
        if (globalSettings != null && legacySettings != null)
        {
            globalSettings.SoundThreshold = legacySettings.SoundThreshold;
            globalSettings.AverageSampleCount = legacySettings.AverageSampleCount;
            globalSettings.FilterEnabled = legacySettings.FilterEnabled;
            globalSettings.LowPassFrequency = legacySettings.LowPassFrequency;
            globalSettings.HighPassFrequency = legacySettings.HighPassFrequency;
            globalSettings.ThresholdPauseDuration = legacySettings.ThresholdPauseDuration;
            globalSettings.VolumeAdjustmentDb = legacySettings.VolumeAdjustmentDb;
        }

        db.SaveChanges();
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseCors("SignalRWithCredentials");

app.UseAuthorization();

// Map SignalR hub
app.MapHub<BabyMonitarr.Backend.Hubs.AudioStreamHub>("/audioHub")
   .RequireCors("SignalRWithCredentials");

app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Dashboard}/{id?}");

app.Run();
