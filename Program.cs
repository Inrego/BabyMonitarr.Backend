using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using BabyMonitarr.Backend.Auth;
using BabyMonitarr.Backend.Data;
using BabyMonitarr.Backend.Models;
using BabyMonitarr.Backend.Services;
using System.Data;

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

builder.Services.Configure<FfmpegDiagnosticsOptions>(
    builder.Configuration.GetSection("FFmpegDiagnostics"));
builder.Services.Configure<WebRtcOptions>(
    builder.Configuration.GetSection("WebRtc"));

// Register services
builder.Services.AddHttpClient();
builder.Services.AddScoped<IRoomService, RoomService>();
builder.Services.AddScoped<IGoogleNestAuthService, GoogleNestAuthService>();
builder.Services.AddScoped<IGoogleNestDeviceService, GoogleNestDeviceService>();
builder.Services.AddSingleton<IVideoCodecProbeService, VideoCodecProbeService>();
builder.Services.AddSingleton<FfprobeSnapshotService>();
builder.Services.AddSingleton<NestStreamReaderManager>();
builder.Services.AddSingleton<IWebRtcConfigService, WebRtcConfigService>();
builder.Services.AddSingleton<IAudioStreamingService, AudioStreamingService>();
builder.Services.AddHostedService(sp => (AudioStreamingService)sp.GetRequiredService<IAudioStreamingService>());
builder.Services.AddSingleton<IAudioWebRtcService, AudioWebRtcService>();
builder.Services.AddSingleton<IVideoStreamingService, VideoStreamingService>();
builder.Services.AddSingleton<IVideoWebRtcService, VideoWebRtcService>();
builder.Services.AddHostedService(sp => (VideoStreamingService)sp.GetRequiredService<IVideoStreamingService>());

// Add authentication
builder.Services.AddBabyMonitarrAuth(builder.Configuration);

// Add SignalR
builder.Services.AddSignalR();

var app = builder.Build();

// Ensure the database directory exists
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (connectionString != null)
{
    var match = System.Text.RegularExpressions.Regex.Match(connectionString, @"Data Source=(.+)");
    if (match.Success)
    {
        var dir = Path.GetDirectoryName(match.Groups[1].Value);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}

// Initialize database and seed data
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BabyMonitarrDbContext>();
    db.Database.EnsureCreated();
    EnsureRoomVideoCodecColumns(db);
    EnsureAuthTables(db);

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

// Support reverse proxies that terminate TLS (e.g., nginx)
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                       ForwardedHeaders.XForwardedProto |
                       ForwardedHeaders.XForwardedHost,
    ForwardLimit = null,
    RequireHeaderSymmetry = false
};
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

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

app.UseAuthentication();
app.UseAuthorization();

// Map SignalR hub
app.MapHub<BabyMonitarr.Backend.Hubs.AudioStreamHub>("/audioHub")
   .RequireCors("SignalRWithCredentials");

app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Dashboard}/{id?}");

app.Run();

static void EnsureRoomVideoCodecColumns(BabyMonitarrDbContext db)
{
    var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var connection = db.Database.GetDbConnection();
    bool closeAfter = connection.State != ConnectionState.Open;

    if (closeAfter)
    {
        connection.Open();
    }

    try
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('Rooms');";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string columnName = reader["name"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(columnName))
            {
                existingColumns.Add(columnName);
            }
        }
    }
    finally
    {
        if (closeAfter)
        {
            connection.Close();
        }
    }

    AddRoomColumnIfMissing("VideoSourceCodecName");
    AddRoomColumnIfMissing("VideoPassthroughCodec");
    AddRoomColumnIfMissing("VideoCodecFailureReason");
    AddRoomColumnIfMissing("VideoCodecCheckedAtUtc");

    void AddRoomColumnIfMissing(string columnName)
    {
        if (existingColumns.Contains(columnName))
        {
            return;
        }

        string alterSql = columnName switch
        {
            "VideoSourceCodecName" => "ALTER TABLE Rooms ADD COLUMN VideoSourceCodecName TEXT NULL;",
            "VideoPassthroughCodec" => "ALTER TABLE Rooms ADD COLUMN VideoPassthroughCodec TEXT NULL;",
            "VideoCodecFailureReason" => "ALTER TABLE Rooms ADD COLUMN VideoCodecFailureReason TEXT NULL;",
            "VideoCodecCheckedAtUtc" => "ALTER TABLE Rooms ADD COLUMN VideoCodecCheckedAtUtc TEXT NULL;",
            _ => throw new InvalidOperationException($"Unsupported Room column '{columnName}'.")
        };

        db.Database.ExecuteSqlRaw(alterSql);
        existingColumns.Add(columnName);
    }
}

static void EnsureAuthTables(BabyMonitarrDbContext db)
{
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS Users (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Username TEXT NOT NULL,
            PasswordHash TEXT,
            DisplayName TEXT,
            Email TEXT,
            IsAdmin INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT NOT NULL DEFAULT '0001-01-01T00:00:00'
        );
        """);
    db.Database.ExecuteSqlRaw(
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Username ON Users (Username);");

    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS ApiKeys (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            KeyHash TEXT NOT NULL,
            KeyPrefix TEXT NOT NULL,
            Name TEXT NOT NULL,
            CreatedAt TEXT NOT NULL DEFAULT '0001-01-01T00:00:00',
            LastUsedAt TEXT,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
        );
        """);
    db.Database.ExecuteSqlRaw(
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_ApiKeys_KeyHash ON ApiKeys (KeyHash);");
    db.Database.ExecuteSqlRaw(
        "CREATE INDEX IF NOT EXISTS IX_ApiKeys_KeyPrefix ON ApiKeys (KeyPrefix);");
}
