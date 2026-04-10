using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessionApp.Data.Entities
{
    /// <summary>
    /// Represents user feedback submitted through the application
    /// </summary>
    public class FeedbackEntity
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// User ID if feedback is from authenticated user (null if anonymous)
        /// </summary>
        public Guid? UserId { get; set; }

        /// <summary>
        /// Email address for contact (required for anonymous users)
        /// </summary>
        [MaxLength(256)]
        public string? Email { get; set; }

        /// <summary>
        /// Subject/title of the feedback
        /// </summary>
        [Required]
        [MaxLength(200)]
        public string Subject { get; set; } = null!;

        /// <summary>
        /// Detailed feedback message
        /// </summary>
        [Required]
        [MaxLength(5000)]
        public string Message { get; set; } = null!;

        /// <summary>
        /// Category of feedback
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string Category { get; set; } = null!;

        /// <summary>
        /// Status of the feedback
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string Status { get; set; } = FeedbackStatus.New;

        /// <summary>
        /// When the feedback was submitted
        /// </summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When feedback was last updated (status change, etc.)
        /// </summary>
        public DateTime? UpdatedAtUtc { get; set; }

        /// <summary>
        /// When admin responded to feedback
        /// </summary>
        public DateTime? RespondedAtUtc { get; set; }

        /// <summary>
        /// Admin notes (internal use)
        /// </summary>
        [MaxLength(2000)]
        public string? AdminNotes { get; set; }

        /// <summary>
        /// Response sent to user
        /// </summary>
        [MaxLength(2000)]
        public string? Response { get; set; }

        /// <summary>
        /// Navigation property to user (if authenticated)
        /// </summary>
        [ForeignKey(nameof(UserId))]
        public UserEntity? User { get; set; }
    }

    /// <summary>
    /// Feedback status constants
    /// </summary>
    public static class FeedbackStatus
    {
        public const string New = "New";
        public const string InProgress = "InProgress";
        public const string Resolved = "Resolved";
        public const string Closed = "Closed";
    }

    /// <summary>
    /// Feedback category constants
    /// </summary>
    public static class FeedbackCategory
    {
        public const string Bug = "Bug";
        public const string Feature = "Feature";
        public const string General = "General";
        public const string Other = "Other";
    }
}
