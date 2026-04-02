using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SessionApp.Data;
using SessionApp.Models;
using SessionApp.Services;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;

namespace SessionApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserService _userService;
        private readonly SessionRepository _sessionRepository;
        private readonly JwtTokenService _jwtTokenService;
        private readonly ILogger<AuthController> _logger;
        private readonly EmailService _emailService;
        private readonly IConfiguration _configuration;

        public AuthController(
            UserService userService, 
            SessionRepository sessionRepository,
            JwtTokenService jwtTokenService,
            ILogger<AuthController> logger,
            EmailService emailService,
            IConfiguration configuration)
        {
            _userService = userService;
            _sessionRepository = sessionRepository;
            _jwtTokenService = jwtTokenService;
            _logger = logger;
            _emailService = emailService;
            _configuration = configuration;
        }

        /// <summary>
        /// Register a new user account and return JWT token
        /// </summary>
        [HttpPost("register")]
        [EnableRateLimiting("auth")]
        [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState
                    .Where(x => x.Value?.Errors.Count > 0)
                    .SelectMany(x => x.Value!.Errors.Select(e => e.ErrorMessage))
                    .ToList();

                return BadRequest(new { message = "Validation failed", errors = errors });
            }

            var (success, errorMessage, user) = await _userService.RegisterUserAsync(request);

            if (!success || user == null)
            {
                return BadRequest(new { message = errorMessage });
            }

            // Generate email verification token
            string? verificationToken = null;
            try
            {
                verificationToken = await _userService.GenerateEmailVerificationTokenAsync(user.Id);
                _logger.LogInformation("Email verification token generated and saved for user: {Username}", user.Username);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate/save email verification token for {Email}", user.Email);
                // Don't fail registration if token generation fails, but skip email
            }

            // Send verification email only if token was successfully generated
            if (verificationToken != null)
            {
                try
                {
                    // Build verification URL (frontend URL, not API)
                    var baseUrl = _configuration["App:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}";
                    var verificationUrl = $"{baseUrl}/verify-email?token={Uri.EscapeDataString(verificationToken)}";

                    // Send verification email
                    var emailBody = _emailService.GenerateEmailVerificationBody(user.Username, verificationUrl);
                    await _emailService.SendEmailAsync(user.Email, "Verify Your Email Address", emailBody);

                    _logger.LogInformation("Verification email sent to {Email}", user.Email);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send verification email to {Email}", user.Email);
                    // Don't fail registration if email fails
                }
            }

            // Generate JWT token for immediate login after registration
            var token = _jwtTokenService.GenerateToken(user);

            var response = new AuthResponse
            {
                UserId = user.Id,
                Username = user.Username,
                Email = user.Email,
                DisplayName = user.DisplayName,
                Token = token,
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            };

            return CreatedAtAction(nameof(GetProfile), new { userId = user.Id }, response);
        }

        /// <summary>
        /// Login with username/email and password, returns JWT token
        /// </summary>
        [HttpPost("login")]
        [EnableRateLimiting("auth")]
        [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var (success, errorMessage, user) = await _userService.LoginAsync(request);

            if (!success || user == null)
            {
                return Unauthorized(new { message = errorMessage });
            }

            // Generate JWT token
            var token = _jwtTokenService.GenerateToken(user);
            var expiryDays = _configuration.GetValue<int>("Jwt:ExpiryDays", 30);

            var response = new AuthResponse
            {
                UserId = user.Id,
                Username = user.Username,
                Email = user.Email,
                DisplayName = user.DisplayName,
                Token = token,
                ExpiresAt = DateTime.UtcNow.AddDays(expiryDays)
            };

            return Ok(response);
        }

        /// <summary>
        /// Change user password (requires authentication and current password)
        /// </summary>
        [HttpPost("change-password")]
        [Authorize] // SECURITY: Requires authentication
        [EnableRateLimiting("auth")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState
                    .Where(x => x.Value?.Errors.Count > 0)
                    .SelectMany(x => x.Value!.Errors.Select(e => e.ErrorMessage))
                    .ToList();

                return BadRequest(new { message = "Validation failed", errors = errors });
            }

            // Get authenticated user's ID from JWT token
            var userId = _jwtTokenService.GetUserIdFromToken(User);

            if (userId == null)
            {
                return Unauthorized(new { message = "Invalid token" });
            }

            var (success, errorMessage) = await _userService.ChangePasswordAsync(userId.Value, request);

            if (!success)
            {
                return BadRequest(new { message = errorMessage });
            }

            return Ok(new { message = "Password changed successfully" });
        }

        /// <summary>
        /// Get public user profile by ID (email hidden for privacy)
        /// </summary>
        [HttpGet("profile/{userId}")]
        [ProducesResponseType(typeof(PublicUserProfile), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetProfile(Guid userId)
        {
            var user = await _userService.GetUserByIdAsync(userId);

            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            var profile = new PublicUserProfile
            {
                UserId = user.Id,
                Username = user.Username,
                DisplayName = user.DisplayName,
                CreatedAtUtc = user.CreatedAtUtc,
                LastLoginUtc = user.LastLoginUtc
            };

            return Ok(profile);
        }

        /// <summary>
        /// Get public user profile by username (email hidden for privacy)
        /// </summary>
        [HttpGet("profile/username/{username}")]
        [ProducesResponseType(typeof(PublicUserProfile), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetProfileByUsername(string username)
        {
            var user = await _userService.GetUserByUsernameAsync(username);

            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            var profile = new PublicUserProfile
            {
                UserId = user.Id,
                Username = user.Username,
                DisplayName = user.DisplayName,
                CreatedAtUtc = user.CreatedAtUtc,
                LastLoginUtc = user.LastLoginUtc
            };

            return Ok(profile);
        }

        /// <summary>
        /// Get authenticated user's own profile (includes email and confirmation status)
        /// </summary>
        [HttpGet("profile/me")]
        [Authorize] // SECURITY: Requires authentication
        [ProducesResponseType(typeof(UserProfile), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetMyProfile()
        {
            // Get authenticated user's ID from JWT token
            var userId = _jwtTokenService.GetUserIdFromToken(User);

            if (userId == null)
            {
                return Unauthorized(new { message = "Invalid token" });
            }

            var user = await _userService.GetUserByIdAsync(userId.Value);

            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            var profile = new UserProfile
            {
                UserId = user.Id,
                Username = user.Username,
                Email = user.Email,
                DisplayName = user.DisplayName,
                CreatedAtUtc = user.CreatedAtUtc,
                LastLoginUtc = user.LastLoginUtc,
                EmailConfirmed = user.EmailConfirmed
            };

            return Ok(profile);
        }

        /// <summary>
        /// Get all games where the user is the host
        /// </summary>
        [HttpGet("{userId}/hosted-games")]
        [ProducesResponseType(typeof(List<SessionSummary>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetHostedGames(Guid userId)
        {
            var user = await _userService.GetUserByIdAsync(userId);
            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            var sessions = await _sessionRepository.GetSessionsByUserAsync(userId);
            return Ok(sessions);
        }

        /// <summary>
        /// Get all games where the user is a player (participant)
        /// </summary>
        [HttpGet("{userId}/played-games")]
        [ProducesResponseType(typeof(List<SessionSummary>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetPlayedGames(Guid userId)
        {
            var user = await _userService.GetUserByIdAsync(userId);
            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            var sessions = await _sessionRepository.GetSessionsWhereUserIsParticipantAsync(userId);
            return Ok(sessions);
        }

        /// <summary>
        /// Get all games for a user (both hosted and played)
        /// </summary>
        [HttpGet("{userId}/all-games")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetAllGames(Guid userId)
        {
            var user = await _userService.GetUserByIdAsync(userId);
            if (user == null)
            {
                return NotFound(new { message = "User not found" });
            }

            var hostedSessions = await _sessionRepository.GetSessionsByUserAsync(userId);
            var playedSessions = await _sessionRepository.GetSessionsWhereUserIsParticipantAsync(userId);

            return Ok(new
            {
                hostedGames = hostedSessions,
                playedGames = playedSessions,
                totalGames = hostedSessions.Count + playedSessions.Count
            });
        }

        /// <summary>
        /// Verify email address using token from email
        /// </summary>
        [HttpGet("verify-email")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> VerifyEmail([FromQuery] string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return BadRequest(new { message = "Token is required" });
            }

            var (success, errorMessage) = await _userService.VerifyEmailAsync(token);

            if (!success)
            {
                return BadRequest(new { message = errorMessage });
            }

            return Ok(new { message = "Email verified successfully" });
        }

        /// <summary>
        /// Resend email verification
        /// </summary>
        [HttpPost("resend-verification")]
        [EnableRateLimiting("auth")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> ResendVerification([FromBody] ForgotPasswordRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var (success, errorMessage, token, user) = await _userService.ResendEmailVerificationAsync(request.Email);

            if (!success || user == null || token == null)
            {
                return BadRequest(new { message = errorMessage });
            }

            try
            {
                var baseUrl = _configuration["App:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}";
                var verificationUrl = $"{baseUrl}/verify-email?token={Uri.EscapeDataString(token)}";

                var emailBody = _emailService.GenerateEmailVerificationBody(user.Username, verificationUrl);
                await _emailService.SendEmailAsync(user.Email, "Verify Your Email Address", emailBody);

                _logger.LogInformation("Verification email resent to {Email}", user.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resend verification email to {Email}", user.Email);
                return StatusCode(StatusCodes.Status500InternalServerError, 
                    new { message = "Failed to send verification email" });
            }

            return Ok(new { message = "Verification email sent successfully" });
        }

        /// <summary>
        /// Request password reset (sends email with reset link)
        /// </summary>
        [HttpPost("forgot-password")]
        [EnableRateLimiting("auth")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var (success, errorMessage, token, user) = await _userService.GeneratePasswordResetTokenAsync(request.Email);

            // Always return success to prevent email enumeration
            if (!success || user == null || token == null)
            {
                // Still return OK to prevent user enumeration
                return Ok(new { message = "If the email exists, a password reset link has been sent" });
            }

            try
            {
                var baseUrl = _configuration["App:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}";
                var resetUrl = $"{baseUrl}/reset-password?token={Uri.EscapeDataString(token)}";

                var emailBody = _emailService.GeneratePasswordResetBody(user.Username, resetUrl);
                await _emailService.SendEmailAsync(user.Email, "Password Reset Request", emailBody);

                _logger.LogInformation("Password reset email sent to {Email}", user.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send password reset email to {Email}", user.Email);
                // Still return success to prevent email enumeration
            }

            return Ok(new { message = "If the email exists, a password reset link has been sent" });
        }

        /// <summary>
        /// Reset password using token from email
        /// </summary>
        [HttpPost("reset-password")]
        [EnableRateLimiting("auth")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var (success, errorMessage) = await _userService.ResetPasswordAsync(request.Token, request.NewPassword);

            if (!success)
            {
                return BadRequest(new { message = errorMessage });
            }

            return Ok(new { message = "Password reset successfully" });
        }
    }
}
