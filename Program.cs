using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using SessionApp.Data;
using SessionApp.Hubs;
using SessionApp.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// CORS: allow cross-origin requests (adjust origins as needed)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// Add Database Context
builder.Services.AddDbContext<SessionDbContext>(options =>
{
    // Option 1: SQLite (recommended for easy deployment)
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") 
        ?? "Data Source=sessions.db");
    
    // Option 2: SQL Server (uncomment and comment out SQLite above)
    // options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
});

// Register repository
builder.Services.AddScoped<SessionRepository>();

// Register room/session service with database support
builder.Services.AddSingleton<RoomCodeService>(sp =>
{
    var scope = sp.CreateScope();
    var repository = scope.ServiceProvider.GetRequiredService<SessionRepository>();
    return new RoomCodeService(repository);
});

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
    db.Database.EnsureCreated();
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