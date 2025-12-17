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

// Configure Audio Settings from appsettings.json
builder.Services.Configure<BabyMonitarr.Backend.Models.AudioSettings>(
    builder.Configuration.GetSection("AudioSettings"));

// Register services
builder.Services.AddSingleton<BabyMonitarr.Backend.Services.IAudioProcessingService, BabyMonitarr.Backend.Services.AudioProcessingService>();
builder.Services.AddSingleton<BabyMonitarr.Backend.Services.IWebRtcService, BabyMonitarr.Backend.Services.WebRtcService>();
builder.Services.AddHostedService<BabyMonitarr.Backend.Services.AudioStreamingBackgroundService>();

// Add SignalR
builder.Services.AddSignalR();

var app = builder.Build();

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
        pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();