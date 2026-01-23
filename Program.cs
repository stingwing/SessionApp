using Microsoft.AspNetCore.SignalR;
using Microsoft.OpenApi;
using SessionApp.Hubs;
using SessionApp.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
// register room/session service (in-memory singleton)
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

// Wire RoomCodeService events to SignalR hub to notify clients on server-side expiration events
var roomService = app.Services.GetRequiredService<RoomCodeService>();
var hubContext = app.Services.GetService<Microsoft.AspNetCore.SignalR.IHubContext<RoomsHub>>();
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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "SessionApp API v1");
        options.RoutePrefix = "swagger"; // reachable at /swagger
    });
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

// Map the SignalR hub
app.MapHub<RoomsHub>("/hubs/rooms");

app.Run();