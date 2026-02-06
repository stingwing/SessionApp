using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SessionApp.Services
{
    public class CommanderSyncHostedService : IHostedService, IDisposable
    {
        private readonly ILogger<CommanderSyncHostedService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private Timer? _timer;

        public CommanderSyncHostedService(
            ILogger<CommanderSyncHostedService> logger,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Commander Sync Hosted Service is starting.");

            // Calculate time until next midnight UTC
            var now = DateTime.UtcNow;
            var nextMidnight = now.Date.AddDays(1);
            var timeUntilMidnight = nextMidnight - now;

            // Schedule the first sync at midnight, then repeat every 24 hours
            _timer = new Timer(
                DoWork,
                null,
                timeUntilMidnight,
                TimeSpan.FromHours(24));

            return Task.CompletedTask;
        }

        private async void DoWork(object? state)
        {
            _logger.LogInformation("Commander Sync: Starting daily sync at {time}", DateTime.UtcNow);

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var scryfallService = scope.ServiceProvider.GetRequiredService<ScryfallService>();

                var count = await scryfallService.FetchAndStoreCommandersAsync();
                _logger.LogInformation("Commander Sync: Successfully synced {count} commanders", count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Commander Sync: Error occurred while syncing commanders");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Commander Sync Hosted Service is stopping.");
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}