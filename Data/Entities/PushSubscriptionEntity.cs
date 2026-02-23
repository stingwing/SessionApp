using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessionApp.Data.Entities
{
    [Table("PushSubscriptions")]
    public class PushSubscriptionEntity
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string RoomCode { get; set; } = null!;

        [Required]
        [MaxLength(100)]
        public string ParticipantId { get; set; } = null!;

        [Required]
        [MaxLength(500)]
        public string Endpoint { get; set; } = null!;

        [Required]
        public string P256dh { get; set; } = null!;

        [Required]
        public string Auth { get; set; } = null!;

        public DateTime SubscribedAtUtc { get; set; }
    }
}