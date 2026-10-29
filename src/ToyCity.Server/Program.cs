using Microsoft.Extensions.FileProviders;
using ToyCity.Server;
using ToyCity.Sim;

// ToyCity server: runs one city and serves the web client.
//   dotnet run --project src/ToyCity.Server [-- --urls http://0.0.0.0:5180]
//   dotnet run --project src/ToyCity.Server -- --dedicated    players sign in; host.json beside the saves sets who may join
// Settings (appsettings or environment): ToyCity:ContentDir, ToyCity:SaveDir, ToyCity:WebDir, ToyCity:Dedicated,
// ToyCity:HostConfig (default: host.json in the save folder).
bool dedicated = args.Contains("--dedicated");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--dedicated").ToArray());
dedicated |= builder.Configuration.GetValue<bool>("ToyCity:Dedicated");
// Solo play listens only on this machine; a dedicated server listens on every network it's on.
if (builder.Configuration["urls"] == null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") == null)
    builder.WebHost.UseUrls(dedicated ? "http://0.0.0.0:5180" : "http://localhost:5180");

string contentDir = builder.Configuration["ToyCity:ContentDir"] ?? ContentDb.FindContentDir(Directory.GetCurrentDirectory());
string repoRoot = Path.GetDirectoryName(contentDir)!;
string saveDir = builder.Configuration["ToyCity:SaveDir"] ?? Path.Combine(repoRoot, "saves");
string webDir = builder.Configuration["ToyCity:WebDir"] ?? Path.Combine(repoRoot, "clients", "web", "dist");

var host = dedicated ? HostConfig.LoadOrCreate(builder.Configuration["ToyCity:HostConfig"] ?? Path.Combine(saveDir, "host.json")) : HostConfig.Solo();
builder.Services.AddSingleton(sp => new SimHost(ContentDb.Load(contentDir), saveDir, sp.GetRequiredService<ILogger<SimHost>>(), host));

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.Map("/ws", async (HttpContext ctx, SimHost host) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await host.RunClientAsync(socket, ctx.RequestAborted);
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

if (Directory.Exists(webDir))
{
    var files = new PhysicalFileProvider(Path.GetFullPath(webDir));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}
else
{
    app.MapGet("/", () => Results.Text($"ToyCity server is running. Build the web client (clients/web: npm install && npm run build) to play here, or run its dev server.\nLooked for: {webDir}"));
}

// Start the sim as soon as the server is up, not on the first connection.
app.Services.GetRequiredService<SimHost>();
app.Logger.LogInformation("ToyCity content: {Content}, saves: {Saves}", contentDir, saveDir);
if (dedicated) app.Logger.LogInformation("Dedicated server \"{Name}\": joining is {Join}; settings in {Path}", host.Name, host.Join, host.Path);
app.Run();
