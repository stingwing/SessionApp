using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SessionApp.Data;
using SessionApp.Data.Entities;
using SessionApp.Models;
using SessionApp.Services;
using System.Net;
using System.Text.RegularExpressions;

namespace SessionApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FeedbackController : ControllerBase
    {
        private readonly FeedbackRepository _feedbackRepository;
        private readonly JwtTokenService _jwtTokenService;
        private readonly EmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<FeedbackController> _logger;

        public FeedbackController(
            FeedbackRepository feedbackRepository,
            JwtTokenService jwtTokenService,
            EmailService emailService,
            IConfiguration configuration,
            ILogger<FeedbackController> logger)
        {
            _feedbackRepository = feedbackRepository;
            _jwtTokenService = jwtTokenService;
            _emailService = emailService;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Submit feedback (authenticated or anonymous)
        /// </summary>
        [HttpPost]
        [EnableRateLimiting("auth")]
        [ProducesResponseType(typeof(FeedbackResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> SubmitFeedback([FromBody] SubmitFeedbackRequest request)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState
                    .Where(x => x.Value?.Errors.Count > 0)
                    .SelectMany(x => x.Value!.Errors.Select(e => e.ErrorMessage))
                    .ToList();

                return BadRequest(new { message = "Validation failed", errors = errors });
            }

            // Get user ID if authenticated
            Guid? userId = null;
            string? userEmail = null;

            if (User.Identity?.IsAuthenticated == true)
            {
                userId = _jwtTokenService.GetUserIdFromToken(User);
            }

            // Email is optional for all users (authenticated and anonymous)
            userEmail = request.Email;

            // SECURITY: Sanitize input to prevent malicious content
            var sanitizedSubject = SanitizeInput(request.Subject);
            var sanitizedMessage = SanitizeInput(request.Message);
            var sanitizedEmail = string.IsNullOrWhiteSpace(userEmail) ? null : userEmail.Trim();

            // SECURITY: Validate email format if provided
            if (!string.IsNullOrEmpty(sanitizedEmail) && !IsValidEmail(sanitizedEmail))
            {
                return BadRequest(new { message = "Invalid email format" });
            }

            // Create feedback entity with sanitized data
            var feedback = new FeedbackEntity
            {
                UserId = userId,
                Email = sanitizedEmail,
                Subject = sanitizedSubject,
                Message = sanitizedMessage,
                Category = request.Category,
                Status = FeedbackStatus.New
            };

            // Save to database
            await _feedbackRepository.CreateFeedbackAsync(feedback);

            _logger.LogInformation("Feedback submitted: {FeedbackId} by {UserId} ({Email})", 
                feedback.Id, userId?.ToString() ?? "anonymous", userEmail);

            // Send notification email to admin
            try
            {
                var adminEmail = _configuration["Email:AdminEmail"];
                if (!string.IsNullOrEmpty(adminEmail))
                {
                    var emailBody = GenerateFeedbackNotificationEmail(feedback);
                    await _emailService.SendEmailAsync(
                        adminEmail, 
                        $"[Feedback] {feedback.Category}: {feedback.Subject}", 
                        emailBody);

                    _logger.LogInformation("Feedback notification sent to admin for {FeedbackId}", feedback.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send feedback notification email for {FeedbackId}", feedback.Id);
                // Don't fail the request if email fails
            }

            // Return response
            var response = new FeedbackResponse
            {
                Id = feedback.Id,
                UserId = feedback.UserId,
                Email = feedback.Email,
                Subject = feedback.Subject,
                Message = feedback.Message,
                Category = feedback.Category,
                Status = feedback.Status,
                CreatedAtUtc = feedback.CreatedAtUtc
            };

            return CreatedAtAction(nameof(GetFeedback), new { id = feedback.Id }, response);
        }

        /// <summary>
        /// Get specific feedback by ID (owner or admin only)
        /// </summary>
        [HttpGet("{id}")]
        [Authorize]
        [ProducesResponseType(typeof(FeedbackResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetFeedback(Guid id)
        {
            var feedback = await _feedbackRepository.GetFeedbackByIdAsync(id);
            if (feedback == null)
            {
                return NotFound(new { message = "Feedback not found" });
            }

            // Check authorization - user can only see their own feedback
            // (unless they're an admin, which we'll implement later)
            var userId = _jwtTokenService.GetUserIdFromToken(User);
            if (feedback.UserId != userId)
            {
                return Forbid();
            }

            var response = new FeedbackResponse
            {
                Id = feedback.Id,
                UserId = feedback.UserId,
                Email = feedback.Email,
                Subject = feedback.Subject,
                Message = feedback.Message,
                Category = feedback.Category,
                Status = feedback.Status,
                CreatedAtUtc = feedback.CreatedAtUtc,
                RespondedAtUtc = feedback.RespondedAtUtc,
                Response = feedback.Response
            };

            return Ok(response);
        }

        /// <summary>
        /// Get all feedback submitted by the authenticated user
        /// </summary>
        [HttpGet("my-feedback")]
        [Authorize]
        [ProducesResponseType(typeof(List<FeedbackSummary>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> GetMyFeedback()
        {
            var userId = _jwtTokenService.GetUserIdFromToken(User);
            if (userId == null)
            {
                return Unauthorized(new { message = "Invalid token" });
            }

            var feedbackList = await _feedbackRepository.GetFeedbackByUserIdAsync(userId.Value);
            return Ok(feedbackList);
        }

        /// <summary>
        /// Generate HTML email for feedback notification
        /// SECURITY: HTML-encodes all user input to prevent XSS
        /// </summary>
        private string GenerateFeedbackNotificationEmail(FeedbackEntity feedback)
        {
            var userInfo = feedback.UserId.HasValue 
                ? $"User ID: {HtmlEncode(feedback.UserId.ToString())}" 
                : "Anonymous User";

            // SECURITY: HTML-encode all user-provided content
            var encodedCategory = HtmlEncode(feedback.Category);
            var encodedSubject = HtmlEncode(feedback.Subject);
            var encodedMessage = HtmlEncode(feedback.Message).Replace("\n", "<br>");
            var encodedEmail = HtmlEncode(feedback.Email ?? "Not provided");

            return $@"
<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background-color: #2196F3; color: white; padding: 20px; text-align: center; }}
        .content {{ padding: 20px; background-color: #f9f9f9; }}
        .field {{ margin: 15px 0; }}
        .label {{ font-weight: bold; color: #555; }}
        .value {{ margin-top: 5px; padding: 10px; background-color: white; border-left: 3px solid #2196F3; word-wrap: break-word; }}
        .category {{ display: inline-block; padding: 5px 10px; border-radius: 3px; font-size: 12px; font-weight: bold; }}
        .category-bug {{ background-color: #f44336; color: white; }}
        .category-feature {{ background-color: #4CAF50; color: white; }}
        .category-general {{ background-color: #2196F3; color: white; }}
        .category-other {{ background-color: #9E9E9E; color: white; }}
        .footer {{ text-align: center; padding: 20px; font-size: 12px; color: #666; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>New Feedback Received</h1>
        </div>
        <div class='content'>
            <div class='field'>
                <div class='label'>Category:</div>
                <div class='value'>
                    <span class='category category-{encodedCategory.ToLower()}'>{encodedCategory}</span>
                </div>
            </div>
            <div class='field'>
                <div class='label'>Subject:</div>
                <div class='value'>{encodedSubject}</div>
            </div>
            <div class='field'>
                <div class='label'>Message:</div>
                <div class='value'>{encodedMessage}</div>
            </div>
            <div class='field'>
                <div class='label'>From:</div>
                <div class='value'>
                    {userInfo}<br>
                    Email: {encodedEmail}
                </div>
            </div>
            <div class='field'>
                <div class='label'>Submitted:</div>
                <div class='value'>{feedback.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC</div>
            </div>
            <div class='field'>
                <div class='label'>Feedback ID:</div>
                <div class='value'>{feedback.Id}</div>
            </div>
        </div>
        <div class='footer'>
            <p>&copy; {DateTime.UtcNow.Year} Commander Pod Creator Admin</p>
        </div>
    </div>
</body>
</html>";
        }

        /// <summary>
        /// SECURITY: Sanitize user input to remove potentially malicious content
        /// </summary>
        private static string SanitizeInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            // Trim whitespace
            var sanitized = input.Trim();

            // Remove null bytes
            sanitized = sanitized.Replace("\0", string.Empty);

            // Remove control characters except newlines, carriage returns, and tabs
            sanitized = Regex.Replace(sanitized, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", string.Empty);

            // Limit consecutive whitespace
            sanitized = Regex.Replace(sanitized, @"\s{2,}", " ");

            return sanitized;
        }

        /// <summary>
        /// SECURITY: Validate email format using regex
        /// </summary>
        private static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            try
            {
                // Basic email validation regex (RFC 5322 simplified)
                var emailRegex = new Regex(
                    @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase);

                return emailRegex.IsMatch(email) && email.Length <= 256;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// SECURITY: HTML-encode string to prevent XSS attacks
        /// </summary>
        private static string HtmlEncode(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            return WebUtility.HtmlEncode(input);
        }
    }
}
