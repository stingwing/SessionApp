using SessionApp.Data;
using SessionApp.Models;
using System.Net;
using System.Text.Json;
using WebPush;

namespace SessionApp.Services
{
    public class PushNotificationService
    {
        private readonly VapidDetails _vapidDetails;
        private readonly WebPushClient _webPushClient;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PushNotificationService> _logger;

        public PushNotificationService(
            IConfiguration configuration,
            IServiceProvider serviceProvider,
            ILogger<PushNotificationService> logger)
        {
            _vapidDetails = new VapidDetails(
                subject: configuration["Push:Subject"] ?? "mailto:your-email@example.com",
                publicKey: configuration["Push:VapidPublicKey"] ?? throw new ArgumentNullException("VapidPublicKey"),
                privateKey: configuration["Push:VapidPrivateKey"] ?? throw new ArgumentNullException("VapidPrivateKey")
            );
            _webPushClient = new WebPushClient();
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task SendNotificationAsync(
            PushSubscription subscription,
            string title,
            string body,
            string roomCode,
            string? participantId = null,
            Dictionary<string, string>? additionalData = null)
        {
            var payload = new
            {
                title,
                body,
                roomCode,
                participantId,
                url = participantId != null ? $"/room/{roomCode}/{participantId}" : $"/room/{roomCode}",
                timestamp = DateTime.UtcNow,
                data = additionalData
            };

            try
            {
                await _webPushClient.SendNotificationAsync(
                    subscription,
                    JsonSerializer.Serialize(payload),
                    _vapidDetails
                );
                _logger.LogInformation("Push notification sent to {RoomCode}", roomCode);
            }
            catch (WebPushException ex) when (ex.StatusCode == HttpStatusCode.Gone)
            {
                _logger.LogWarning("Subscription expired for room {RoomCode}, removing from database", roomCode);
                await RemoveSubscriptionAsync(subscription);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send push notification for room {RoomCode}", roomCode);
            }
        }

        public async Task NotifyRoomAsync(string roomCode, string title, string body, Dictionary<string, string>? additionalData = null)
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<SessionRepository>();
            
            var subscriptions = await repository.GetSubscriptionsByRoomCodeAsync(roomCode);
            
            var tasks = subscriptions.Select(sub =>
                SendNotificationAsync(sub.Subscription, title, body, roomCode, sub.ParticipantId, additionalData));
            
            await Task.WhenAll(tasks);
        }

        public async Task NotifyParticipantAsync(
            string roomCode,
            string participantId,
            string title,
            string body,
            Dictionary<string, string>? additionalData = null)
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<SessionRepository>();
            
            var subscription = await repository.GetSubscriptionAsync(roomCode, participantId);
            if (subscription != null)
            {
                await SendNotificationAsync(subscription, title, body, roomCode, participantId, additionalData);
            }
        }

        private async Task RemoveSubscriptionAsync(PushSubscription subscription)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<SessionRepository>();
                await repository.RemoveSubscriptionAsync(subscription);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove expired subscription");
            }
        }
    }
}