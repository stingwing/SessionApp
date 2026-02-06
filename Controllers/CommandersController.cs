using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SessionApp.Data;
using SessionApp.Services;
using System.Text.Json;
using System.Threading.Tasks;

namespace SessionApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [EnableRateLimiting("api")] // Apply default API rate limiting to all endpoints
    public class CommandersController : ControllerBase
    {
        private readonly ScryfallService _scryfallService;
        private readonly SessionDbContext _dbContext;

        public CommandersController(ScryfallService scryfallService, SessionDbContext dbContext)
        {
            _scryfallService = scryfallService;
            _dbContext = dbContext;
        }

        // GET api/commanders/search?query=atraxa&format=commander
        [HttpGet("search")]
        [EnableRateLimiting("search")] // Override with search-specific rate limiting
        public async Task<IActionResult> SearchCommanders([FromQuery] string? query, [FromQuery] string? format, [FromQuery] int limit = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
                return BadRequest(new { message = "Query parameter is required" });

            if (limit < 1 || limit > 100)
                return BadRequest(new { message = "Limit must be between 1 and 100" });

            var normalizedQuery = EscapeLikePattern(query.Trim().ToLowerInvariant());

            var commanders = await _dbContext.Commanders
                .Where(c => EF.Functions.ILike(c.Name, $"%{normalizedQuery}%"))
                .OrderBy(c => c.Name)
                .Take(limit)
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.ScryfallUri,
                    Legalities = c.LegalitiesJson
                })
                .ToListAsync();

            // Parse and filter legalities in memory for accurate format checking
            var results = commanders
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.ScryfallUri,
                    Legalities = JsonSerializer.Deserialize<Dictionary<string, string>>(c.Legalities)
                })
                .Where(c => string.IsNullOrWhiteSpace(format) || 
                           (c.Legalities != null && 
                            c.Legalities.TryGetValue(format.Trim().ToLowerInvariant(), out var legality) && 
                            legality == "legal"))
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.ScryfallUri
                })
                .ToList();

            return Ok(new
            {
                query = query,
                format = format,
                count = results.Count,
                results = results
            });
        }

        private static string EscapeLikePattern(string input)
        {
            return input
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
        }

        // POST api/commanders/sync
        [HttpPost("sync")]
        [EnableRateLimiting("commanderSync")]
        public async Task<IActionResult> SyncCommanders()
        {
            var count = await _scryfallService.FetchAndStoreCommandersAsync();
            return Ok(new { message = "Commanders synced successfully", count });
        }
    }
}