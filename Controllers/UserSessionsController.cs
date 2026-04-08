//using Microsoft.AspNetCore.Mvc;
//using SessionApp.Data;
//using SessionApp.Models;
//using SessionApp.Services;
//using System;
//using System.Collections.Generic;
//using System.Threading.Tasks;

//namespace SessionApp.Controllers
//{
//    [ApiController]
//    [Route("api/user-sessions")]
//    public class UserSessionsController : ControllerBase
//    {
//        private readonly SessionRepository _repository;
//        private readonly UserService _userService;
//        private readonly ILogger<UserSessionsController> _logger;

//        public UserSessionsController(
//            SessionRepository repository,
//            UserService userService,
//            ILogger<UserSessionsController> logger)
//        {
//            _repository = repository;
//            _userService = userService;
//            _logger = logger;
//        }

//        /// <summary>
//        /// Link a session to a user account (when creating a session as a logged-in user)
//        /// </summary>
//        [HttpPost("{sessionCode}/link-host")]
//        [ProducesResponseType(StatusCodes.Status200OK)]
//        [ProducesResponseType(StatusCodes.Status400BadRequest)]
//        [ProducesResponseType(StatusCodes.Status404NotFound)]
//        public async Task<IActionResult> LinkSessionToUser(string sessionCode, [FromQuery] Guid userId)
//        {
//            // Verify user exists
//            if (!await _userService.IsValidUserAsync(userId))
//            {
//                return BadRequest(new { message = "Invalid user ID" });
//            }

//            var success = await _repository.LinkSessionToUserAsync(sessionCode, userId);

//            if (!success)
//            {
//                return NotFound(new { message = "Session not found" });
//            }

//            _logger.LogInformation("Session {SessionCode} linked to user {UserId}", sessionCode, userId);
//            return Ok(new { message = "Session linked to user successfully" });
//        }

//        /// <summary>
//        /// Link a participant to a user account (when joining a session as a logged-in user)
//        /// </summary>
//        [HttpPost("{sessionCode}/link-participant")]
//        [ProducesResponseType(StatusCodes.Status200OK)]
//        [ProducesResponseType(StatusCodes.Status400BadRequest)]
//        [ProducesResponseType(StatusCodes.Status404NotFound)]
//        public async Task<IActionResult> LinkParticipantToUser(
//            string sessionCode,
//            [FromQuery] string participantId,
//            [FromQuery] Guid userId)
//        {
//            // Verify user exists
//            if (!await _userService.IsValidUserAsync(userId))
//            {
//                return BadRequest(new { message = "Invalid user ID" });
//            }

//            var success = await _repository.LinkParticipantToUserAsync(sessionCode, participantId, userId);

//            if (!success)
//            {
//                return NotFound(new { message = "Session or participant not found" });
//            }

//            _logger.LogInformation(
//                "Participant {ParticipantId} in session {SessionCode} linked to user {UserId}",
//                participantId, sessionCode, userId);
            
//            return Ok(new { message = "Participant linked to user successfully" });
//        }

//        /// <summary>
//        /// Get all sessions created by a specific user (as host)
//        /// </summary>
//        [HttpGet("by-host/{userId}")]
//        [ProducesResponseType(typeof(List<SessionSummary>), StatusCodes.Status200OK)]
//        [ProducesResponseType(StatusCodes.Status400BadRequest)]
//        public async Task<IActionResult> GetSessionsByHost(Guid userId)
//        {
//            // Verify user exists
//            if (!await _userService.IsValidUserAsync(userId))
//            {
//                return BadRequest(new { message = "Invalid user ID" });
//            }

//            var sessions = await _repository.GetSessionsByUserAsync(userId);
//            return Ok(sessions);
//        }

//        /// <summary>
//        /// Get all sessions where a specific user is a participant (player)
//        /// </summary>
//        [HttpGet("by-participant/{userId}")]
//        [ProducesResponseType(typeof(List<SessionSummary>), StatusCodes.Status200OK)]
//        [ProducesResponseType(StatusCodes.Status400BadRequest)]
//        public async Task<IActionResult> GetSessionsByParticipant(Guid userId)
//        {
//            // Verify user exists
//            if (!await _userService.IsValidUserAsync(userId))
//            {
//                return BadRequest(new { message = "Invalid user ID" });
//            }

//            var sessions = await _repository.GetSessionsWhereUserIsParticipantAsync(userId);
//            return Ok(sessions);
//        }

//        /// <summary>
//        /// Get all sessions for a user (both as host and participant)
//        /// </summary>
//        [HttpGet("by-user/{userId}")]
//        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
//        [ProducesResponseType(StatusCodes.Status400BadRequest)]
//        public async Task<IActionResult> GetAllUserSessions(Guid userId)
//        {
//            // Verify user exists
//            if (!await _userService.IsValidUserAsync(userId))
//            {
//                return BadRequest(new { message = "Invalid user ID" });
//            }

//            var hostedSessions = await _repository.GetSessionsByUserAsync(userId);
//            var participatedSessions = await _repository.GetSessionsWhereUserIsParticipantAsync(userId);

//            return Ok(new
//            {
//                hostedSessions = hostedSessions,
//                participatedSessions = participatedSessions,
//                totalSessions = hostedSessions.Count + participatedSessions.Count
//            });
//        }
//    }
//}
