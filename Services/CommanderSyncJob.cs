using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NCronJob;
using SessionApp.Data;
using System.Threading.Tasks;

namespace SessionApp.Services
{   
    public class CommanderSyncJob : IJob
    {
        private readonly ILogger<CommanderSyncJob> _logger;
        private readonly ScryfallService _scryfallService;
        private readonly SessionDbContext _dbContext;

        public CommanderSyncJob(
            ILogger<CommanderSyncJob> logger,
            ScryfallService scryfallService,
            SessionDbContext dbContext)
        {
            _logger = logger;
            _scryfallService = scryfallService;
            _dbContext = dbContext;
        }

        public async Task RunAsync(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Commander Sync Job: Starting at {time}", DateTime.UtcNow);

            try
            {
                // Check the last update time from the Commanders table
                var lastUpdated = await _dbContext.Commanders
                    .MaxAsync(c => (DateTime?)c.LastUpdatedUtc, cancellationToken);

                if (lastUpdated.HasValue)
                {
                    var timeSinceLastUpdate = DateTime.UtcNow - lastUpdated.Value;
                    
                    if (timeSinceLastUpdate.TotalDays < 1)
                    {
                        _logger.LogInformation(
                            "Commander Sync Job: Skipping sync. Last update was {hours:F1} hours ago (less than 24 hours)",
                            timeSinceLastUpdate.TotalHours);
                        return;
                    }

                    _logger.LogInformation(
                        "Commander Sync Job: Proceeding with sync. Last update was {days:F1} days ago",
                        timeSinceLastUpdate.TotalDays);
                }
                else
                {
                    _logger.LogInformation("Commander Sync Job: No previous sync found. Proceeding with initial sync.");
                }

                var count = await _scryfallService.FetchAndStoreCommandersAsync();
                _logger.LogInformation("Commander Sync Job: Successfully synced {count} commanders", count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Commander Sync Job: Error occurred while syncing commanders");
            }
        }
    }
}