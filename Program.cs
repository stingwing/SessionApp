using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using SessionApp.Data;
using SessionApp.Hubs;
using SessionApp.Services;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .Select(e => new
            {
                Field = e.Key,
                Errors = e.Value?.Errors.Select(x => x.ErrorMessage).ToArray()
            })
            .ToArray();

        var result = new
        {
            message = "Validation failed",
            errors = errors
        };

        return new BadRequestObjectResult(result);
    };
});

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
                "https://magicreactrandomizerapi.onrender.com",
                "https://commanderpodcreator.com"  // Production frontend
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();  // Required for SignalR
    });
});

// Add Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    // Default policy: Fixed window
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            }));

    // Strict policy for resource-intensive operations
    options.AddPolicy("strict", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 2
            }));

    // Sliding window for API endpoints (more granular control)
    options.AddPolicy("api", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 50,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,  // 10-second segments
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 5
            }));

    // Token bucket for burst traffic (searches, etc.)
    options.AddPolicy("search", httpContext =>
        RateLimitPartition.GetTokenBucketLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 20,
                ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                TokensPerPeriod = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 3
            }));

    // Concurrency limiter for sync operations (one at a time per IP)
    options.AddPolicy("sync", httpContext =>
        RateLimitPartition.GetConcurrencyLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new ConcurrencyLimiterOptions
            {
                PermitLimit = 1,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 1
            }));

    // Custom rejection response
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        
        double? retryAfterSeconds = null;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = retryAfter.TotalSeconds.ToString();
            retryAfterSeconds = retryAfter.TotalSeconds;
        }

        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "Too many requests",
            message = "Rate limit exceeded. Please try again later.",
            retryAfter = retryAfterSeconds
        }, cancellationToken: cancellationToken);
    };
});

// Add Database Context - PostgreSQL
builder.Services.AddDbContext<SessionDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    
    if (string.IsNullOrEmpty(connectionString))
    {
        throw new InvalidOperationException(
            "Database connection string 'DefaultConnection' not found. " +
            "Please configure it using User Secrets (development) or Environment Variables (production).");
    }
    
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorCodesToAdd: null);
        npgsqlOptions.CommandTimeout(30);
    });
    
    // Don't log sensitive data in production
    options.EnableSensitiveDataLogging(builder.Environment.IsDevelopment());
    options.EnableDetailedErrors(builder.Environment.IsDevelopment());
});

// Register repository  
builder.Services.AddScoped<SessionRepository>();

// Register room/session service as singleton without repository dependency
// Repository will be resolved per-request via IServiceProvider
builder.Services.AddSingleton<RoomCodeService>();

// Register HttpClient for ScryfallService
builder.Services.AddHttpClient<ScryfallService>();

// Register ScryfallService as scoped (since it depends on DbContext which is scoped)
builder.Services.AddScoped<ScryfallService>();

// Register the Commander Sync Hosted Service
builder.Services.AddHostedService<CommanderSyncHostedService>();

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

// Create database and apply migrations with better error handling
using (var scope = app.Services.CreateScope())
{
    try
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var db = scope.ServiceProvider.GetRequiredService<SessionDbContext>();
        
        logger.LogInformation("Attempting to connect to database and apply migrations...");
        await db.Database.MigrateAsync();
        logger.LogInformation("Database migrations applied successfully");
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while migrating the database. Connection string may be invalid.");
        
        // In production, you might want to throw to prevent the app from starting with a broken database
        if (!app.Environment.IsDevelopment())
        {
            throw;
        }
    }
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

// IMPORTANT: Rate limiting must come before authorization
app.UseRateLimiter();

app.UseAuthorization();
app.MapControllers();
app.MapHub<RoomsHub>("/hubs/rooms");

app.Run();