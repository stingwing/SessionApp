using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using SessionApp.Data;
using SessionApp.Hubs;
using SessionApp.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// CORS: allow cross-origin requests with specific origins for SignalR
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:64496",  // Your React dev server
                "http://localhost:3000",   // Common React port
                "http://localhost:5173",   // Common Vite port
                "https://localhost:7086",   // Your own backend (if needed)
                "https://magicreactrandomizerapi.onrender.com:443"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();  // Required for SignalR
    });
});

// Add Database Context - PostgreSQL
builder.Services.AddDbContext<SessionDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
        ?? "Host=localhost;Database=sessionapp;Username=postgres;Password=12345";
    
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorCodesToAdd: null);
    });
});

// Register repository  
builder.Services.AddScoped<SessionRepository>();

// Register room/session service as singleton without repository dependency
// Repository will be resolved per-request via IServiceProvider
builder.Services.AddSingleton<RoomCodeService>();

// SignalR
builder.Services.AddSignalR();

// OpenAPI / Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SessionApp API",
        Version = "v1",
        Description = "API for creating, joining and querying rooms"
    });
});

var app = builder.Build();

// Create database and apply migrations
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SessionDbContext>();
    await db.Database.MigrateAsync();
}

// Wire RoomCodeService events to SignalR hub
var roomService = app.Services.GetRequiredService<RoomCodeService>();
var hubContext = app.Services.GetService<IHubContext<RoomsHub>>();
if (hubContext != null)
{
    roomService.SessionExpired += async session =>
    {
        await hubContext.Clients.Group(session.Code).SendAsync("RoomExpired", new { session.Code });
    };

    roomService.ParticipantJoined += async (session, participant) =>
    {
        await hubContext.Clients.Group(session.Code)
            .SendAsync("ParticipantJoined", new { participant.Id, participant.Name, RoomCode = session.Code });
    };
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "SessionApp API v1");
        options.RoutePrefix = "swagger";
    });
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();
app.MapHub<RoomsHub>("/hubs/rooms");

app.Run();