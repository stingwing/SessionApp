using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SessionApp.Data;
using SessionApp.Models;
using SessionApp.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SessionApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserService _userService;
        private readonly SessionRepository _sessionRepository;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            UserService userService, 
            SessionRepository sessionRepository,
            ILogger<AuthController> logger)
        {
            _userService = userService;
            _sessionRepository = sessionRepository;
            _logger = logger;
        }

        /// <summary>
        /// Register a new user account
        /// </summary>
        [HttpPost("register")]
        [EnableRateLimiting("auth")]
        [ProducesResponseType(typeof(UserProfile), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var (success, errorMessage, user) = await _userService.RegisterUserAsync(request);

            if (!success || user == null)
            {
                return BadRequest(new { message = errorMessage });
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

            return CreatedAtAction(nameof(GetProfile), new { userId = user.Id }, profile);
        }

        /// <summary>
        /// Login with username/email and password
        /// </summary>
        [HttpPost("login")]
        [EnableRateLimiting("auth")]
        [ProducesResponseType(typeof(UserProfile), StatusCodes.Status200OK)]
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
        /// Change user password (requires current password)
        /// </summary>
        [HttpPost("change-password")]
        [EnableRateLimiting("auth")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> ChangePassword([FromQuery] Guid userId, [FromBody] ChangePasswordRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var (success, errorMessage) = await _userService.ChangePasswordAsync(userId, request);

            if (!success)
            {
                return BadRequest(new { message = errorMessage });
            }

            return Ok(new { message = "Password changed successfully" });
        }

        /// <summary>
        /// Get user profile by ID
        /// </summary>
        [HttpGet("profile/{userId}")]
        [ProducesResponseType(typeof(UserProfile), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetProfile(Guid userId)
        {
            var user = await _userService.GetUserByIdAsync(userId);

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
        /// Get user profile by username
        /// </summary>
        [HttpGet("profile/username/{username}")]
        [ProducesResponseType(typeof(UserProfile), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetProfileByUsername(string username)
        {
            var user = await _userService.GetUserByUsernameAsync(username);

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
    }
}
