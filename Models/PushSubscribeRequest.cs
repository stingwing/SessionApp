using System.ComponentModel.DataAnnotations;

namespace SessionApp.Models
{
    public class PushSubscribeRequest
    {
        [Required]
        public string ParticipantId { get; set; } = null!;

        [Required]
        public string Endpoint { get; set; } = null!;

        [Required]
        public PushSubscriptionKeys Keys { get; set; } = null!;
    }

    public class PushSubscriptionKeys
    {
        [Required]
        public string P256dh { get; set; } = null!;

        [Required]
        public string Auth { get; set; } = null!;
    }
}