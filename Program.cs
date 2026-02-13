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
builder.Services.AddHttpClient();
builder.Services.AddScoped<IRoomService, RoomService>();
builder.Services.AddScoped<IGoogleNestAuthService, GoogleNestAuthService>();
builder.Services.AddScoped<IGoogleNestDeviceService, GoogleNestDeviceService>();
builder.Services.AddSingleton<NestStreamReaderManager>();
builder.Services.AddSingleton<IAudioStreamingService, AudioStreamingService>();
builder.Services.AddHostedService(sp => (AudioStreamingService)sp.GetRequiredService<IAudioStreamingService>());
builder.Services.AddSingleton<IAudioWebRtcService, AudioWebRtcService>();
builder.Services.AddSingleton<IVideoStreamingService, VideoStreamingService>();
builder.Services.AddSingleton<IVideoWebRtcService, VideoWebRtcService>();
builder.Services.AddHostedService(sp => (VideoStreamingService)sp.GetRequiredService<IVideoStreamingService>());

// Add SignalR
builder.Services.AddSignalR();

var app = builder.Build();

// Initialize database and seed data
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
    db.Database.EnsureCreated();

    // Create GoogleNestSettings table if it doesn't exist (for existing databases)
    try
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS GoogleNestSettings (
                Id INTEGER NOT NULL PRIMARY KEY,
                ClientId TEXT,
                ClientSecret TEXT,
                ProjectId TEXT,
                AccessToken TEXT,
                RefreshToken TEXT,
                TokenExpiresAt TEXT,
                IsLinked INTEGER NOT NULL DEFAULT 0
            )");
        // Seed default row if table is empty
        db.Database.ExecuteSqlRaw(@"
            INSERT OR IGNORE INTO GoogleNestSettings (Id, IsLinked)
            VALUES (1, 0)");
    }
    catch (Microsoft.Data.Sqlite.SqliteException)
    {
        // Table already exists
    }

    // Drop obsolete UseCameraAudioStream column if it exists (renamed to EnableAudioStream)
    try
    {
        // Check if column exists first
        using var conn = db.Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(Rooms)";
        bool hasOldColumn = false;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetString(1) == "UseCameraAudioStream")
                {
                    hasOldColumn = true;
                    break;
                }
            }
        }

        if (hasOldColumn)
        {
            // Use table recreation pattern - preserves schema correctly
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE Rooms_new (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Icon TEXT NOT NULL DEFAULT 'baby',
                    MonitorType TEXT NOT NULL DEFAULT 'camera_audio',
                    EnableVideoStream INTEGER NOT NULL DEFAULT 0,
                    EnableAudioStream INTEGER NOT NULL DEFAULT 1,
                    CameraStreamUrl TEXT,
                    CameraUsername TEXT,
                    CameraPassword TEXT,
                    StreamSourceType TEXT NOT NULL DEFAULT 'rtsp',
                    NestDeviceId TEXT,
                    IsActive INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'
                )");
            db.Database.ExecuteSqlRaw(@"
                INSERT INTO Rooms_new (Id, Name, Icon, MonitorType, EnableVideoStream, EnableAudioStream,
                    CameraStreamUrl, CameraUsername, CameraPassword, StreamSourceType, NestDeviceId, IsActive, CreatedAt)
                SELECT Id, Name, Icon, MonitorType, EnableVideoStream,
                    COALESCE(EnableAudioStream, UseCameraAudioStream, 1),
                    CameraStreamUrl, CameraUsername, CameraPassword,
                    COALESCE(StreamSourceType, 'rtsp'), NestDeviceId, IsActive, CreatedAt
                FROM Rooms");
            db.Database.ExecuteSqlRaw("DROP TABLE Rooms");
            db.Database.ExecuteSqlRaw("ALTER TABLE Rooms_new RENAME TO Rooms");
            db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IX_Rooms_Name ON Rooms (Name)");
        }
    }
    catch (Microsoft.Data.Sqlite.SqliteException)
    {
        // Column doesn't exist or table doesn't have it - this is expected for new databases
    }

    // Add EnableAudioStream column if it doesn't exist (for existing databases)
    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Rooms ADD COLUMN EnableAudioStream INTEGER NOT NULL DEFAULT 1");
    }
    catch (Microsoft.Data.Sqlite.SqliteException)
    {
        // Column already exists - this is expected for new databases
    }

    // Add StreamSourceType column if it doesn't exist (for existing databases)
    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Rooms ADD COLUMN StreamSourceType TEXT NOT NULL DEFAULT 'rtsp'");
    }
    catch (Microsoft.Data.Sqlite.SqliteException)
    {
        // Column already exists
    }

    // Add NestDeviceId column if it doesn't exist (for existing databases)
    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Rooms ADD COLUMN NestDeviceId TEXT");
    }
    catch (Microsoft.Data.Sqlite.SqliteException)
    {
        // Column already exists
    }

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
