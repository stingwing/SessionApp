using System;
using System.ComponentModel.DataAnnotations;

namespace SessionApp.Models
{
    /// <summary>
    /// Request model for submitting feedback
    /// </summary>
    public class SubmitFeedbackRequest
    {
        /// <summary>
        /// Email address (optional for all users)
        /// </summary>
        [MaxLength(256)]
        public string? Email { get; set; }

        /// <summary>
        /// Subject/title of the feedback
        /// </summary>
        [Required(ErrorMessage = "Subject is required")]
        [StringLength(200, MinimumLength = 0, ErrorMessage = "Subject must be between 0 and 200 characters")]
        public string Subject { get; set; } = null!;

        /// <summary>
        /// Detailed feedback message
        /// </summary>
        [Required(ErrorMessage = "Message is required")]
        [StringLength(5000, MinimumLength = 0, ErrorMessage = "Message must be between 0 and 5000 characters")]
        public string Message { get; set; } = null!;

        /// <summary>
        /// Category: Bug, Feature, General, Other
        /// </summary>
        [Required(ErrorMessage = "Category is required")]
        [RegularExpression("^(Bug|Feature|General|Other)$", ErrorMessage = "Invalid category")]
        public string Category { get; set; } = null!;
    }

    /// <summary>
    /// Response model for submitted feedback
    /// </summary>
    public class FeedbackResponse
    {
        public Guid Id { get; set; }
        public Guid? UserId { get; set; }
        public string? Email { get; set; }
        public string Subject { get; set; } = null!;
        public string Message { get; set; } = null!;
        public string Category { get; set; } = null!;
        public string Status { get; set; } = null!;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? RespondedAtUtc { get; set; }
        public string? Response { get; set; }
    }

    /// <summary>
    /// Summary model for feedback list
    /// </summary>
    public class FeedbackSummary
    {
        public Guid Id { get; set; }
        public string Subject { get; set; } = null!;
        public string Category { get; set; } = null!;
        public string Status { get; set; } = null!;
        public DateTime CreatedAtUtc { get; set; }
        public bool HasResponse { get; set; }
    }

    /// <summary>
    /// Admin request to update feedback status
    /// </summary>
    public class UpdateFeedbackStatusRequest
    {
        [Required(ErrorMessage = "Status is required")]
        [RegularExpression("^(New|InProgress|Resolved|Closed)$", ErrorMessage = "Invalid status")]
        public string Status { get; set; } = null!;

        [MaxLength(2000)]
        public string? AdminNotes { get; set; }

        [MaxLength(2000)]
        public string? Response { get; set; }
    }
}
