using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using SessionApp.Helpers;
using SessionApp.Hubs;
using SessionApp.Models;
using SessionApp.Services;
using SessionApp.Validation;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using static SessionApp.Services.RoomCodeService;

namespace SessionApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [EnableRateLimiting("api")]
    public class RoomsController : ControllerBase
    {
        private readonly RoomCodeService _roomService;
        private readonly IHubContext<RoomsHub> _hubContext;
        private readonly GameActionService _gameActionService;

        public RoomsController(RoomCodeService roomService, IHubContext<RoomsHub> hubContext, GameActionService gameActionService)
        {
            _roomService = roomService;
            _hubContext = hubContext;
            _gameActionService = gameActionService;
        }

        // Simple test endpoint to verify the service is running
        [HttpGet("test")]
        [DisableRateLimiting] // No rate limiting for test endpoint
        public IActionResult Test()
        {
            return Ok(new { message = "Service is working", utcNow = DateTime.UtcNow });
        }

        // Raw /api/Rooms { "hostId": "host1" }
        [HttpPost]
        [EnableRateLimiting("strict")] // Strict rate limiting for room creation
        public async Task<IActionResult> Create([FromBody] CreateRoomRequest request)
        {
            // Manual validation for better error messages
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    message = "Validation failed",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            // Sanitize input
            var sanitizedHostId = InputSanitizer.SanitizeString(request.HostId);

            if (string.IsNullOrWhiteSpace(sanitizedHostId))
            {
                return BadRequest(new { message = "HostId cannot be empty after sanitization" });
            }

            var session = _roomService.CreateSession(
                sanitizedHostId,
                request.CodeLength,
                request.Ttl ?? TimeSpan.FromDays(7));

            // Notify connected clients (optional: clients may join groups by room code)
            await _hubContext.Clients.Group(session.Code).SendAsync("RoomCreated",
                new { session.Code, session.HostId, session.ExpiresAtUtc });

            return CreatedAtAction(nameof(Get), new { code = session.Code },
                new CreateRoomResponse(session.Code, session.ExpiresAtUtc));
        }

        [HttpPost("{code}/join")]
        // Uses default "api" rate limiting
        public async Task<IActionResult> Join(
            [FromRoute][Required][RoomCode] string code,
            [FromBody] JoinRoomRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    message = "Validation failed",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            // Sanitize inputs
            var sanitizedCode = InputSanitizer.SanitizeString(code).ToUpperInvariant();
            var sanitizedParticipantId = InputSanitizer.SanitizeString(request.ParticipantId);
            var sanitizedParticipantName = InputSanitizer.SanitizeString(request.ParticipantName);
            var sanitizedCommander = InputSanitizer.SanitizeString(request.Commander);

            if (string.IsNullOrWhiteSpace(sanitizedParticipantId))
            {
                return BadRequest(new { message = "ParticipantId cannot be empty" });
            }

            var ok = _roomService.TryJoin(sanitizedCode, sanitizedParticipantId, sanitizedParticipantName, sanitizedCommander);

            if (!ok.Contains("Success"))
                return NotFound(new { message = ok });

            // Broadcast participant joined to clients in the room group
            await _hubContext.Clients.Group(sanitizedCode)
                .SendAsync("ParticipantJoined", new
                {
                    ParticipantId = sanitizedParticipantId,
                    ParticipantName = sanitizedParticipantName,
                    RoomCode = sanitizedCode
                });

            return Ok();
        }

        // GET api/rooms/{code}/
        [HttpGet("{code}")]
        // Uses default "api" rate limiting
        public async Task<IActionResult> Get([FromRoute][Required][RoomCode] string code)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    message = "Invalid room code format",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            var sanitizedCode = InputSanitizer.SanitizeString(code).ToUpperInvariant();
            var session = await _roomService.GetSessionAsync(sanitizedCode);

            if (session is null)
                return NotFound(new { message = "Room not found" });

            var participants = session.Participants.Values
                .Select(p => p.ToMemberResponse())
                .ToArray();

            return Ok(new GetRoomResponse(
                session.Code,
                session.EventName,
                session.HostId,
                session.CreatedAtUtc,
                session.ExpiresAtUtc,
                session.Participants.Count,
                participants,
                session.Settings.ToRoomSettingsResponse()
            ));
        }

        // New endpoint: update room settings
        // POST api/rooms/{code}/settings
        [HttpPost("{code}/settings")]
        [EnableRateLimiting("strict")] // Strict rate limiting for settings changes
        public async Task<IActionResult> UpdateSettings(
            [FromRoute][Required][RoomCode] string code,
            [FromBody] UpdateRoomSettingsRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    message = "Validation failed",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            var sanitizedCode = InputSanitizer.SanitizeString(code).ToUpperInvariant();
            var sanitizedHostId = InputSanitizer.SanitizeString(request.HostId);

            var session = await _roomService.GetSessionAsync(sanitizedCode);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            // Simple authorization: only the host may change settings
            if (!string.Equals(session.HostId, sanitizedHostId, StringComparison.Ordinal))
                return Forbid();

            if (session.IsGameEnded || session.IsExpiredUtc())
                return NotFound(new { message = "Game has ended" });

            if (session.IsGameStarted)
                return NotFound(new { message = "You can't change settings after starting the game" });

            lock (session)
            {
                // Update event name if provided
                if (!string.IsNullOrWhiteSpace(request.EventName))
                {
                    var sanitizedEventName = InputSanitizer.SanitizeString(request.EventName);
                    session.EventName = InputSanitizer.Truncate(sanitizedEventName, 200);
                }
                session.Settings.AllowJoinAfterStart = request.AllowJoinAfterStart;
                session.Settings.PrioitizeWinners = request.PrioitizeWinners;
                session.Settings.AllowGroupOfThree = request.AllowGroupOfThree;
                session.Settings.AllowGroupOfFive = request.AllowGroupOfFive;
                session.Settings.FurtherReduceOddsOfGroupOfThree = request.FurtherReduceOddsOfGroupOfThree;
                session.Settings.RoundLength = request.RoundLength;
                session.Settings.UsePoints = request.UsePoints;
                session.Settings.PointsForWin = request.PointsForWin;
                session.Settings.PointsForDraw = request.PointsForDraw;
                session.Settings.PointsForLoss = request.PointsForLoss;
                session.Settings.PointsForABye = request.PointsForABye;
                session.Settings.AllowCustomGroups = request.AllowCustomGroups;
                session.Settings.AllowPlayersToCreateCustomGroups = request.AllowPlayersToCreateCustomGroups;
                session.Settings.TournamentMode = request.TournamentMode;
                session.Settings.MaxRounds = request.MaxRounds;
                session.Settings.MaxGroupSize = request.MaxGroupSize;
            }

            // Save settings to database
            var saved = await _roomService.SaveSessionToDatabaseAsync(session);
            if (!saved)
            {
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { message = "Failed to save settings to database" });
            }

            var payload = new
            {
                RoomCode = session.Code,
                EventName = session.EventName,
                Settings = session.Settings
            };

            // Notify connected clients in the room that settings changed
            await _hubContext.Clients.Group(sanitizedCode).SendAsync("SettingsChanged", payload);

            return Ok(new { session.EventName, session.Settings });
        }

        // POST api/rooms/{code}/handlegame
        // Handle game-specific actions with player data
        [HttpPost("{code}/handlegame")]
        [EnableRateLimiting("strict")] // Strict rate limiting for game operations
        public async Task<IActionResult> HandleGame([FromRoute][Required][RoomCode] string code, [FromBody] HandleGameRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    message = "Validation failed",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            var sanitizedCode = InputSanitizer.SanitizeString(code).ToUpperInvariant();
            var sanitizedHostId = InputSanitizer.SanitizeString(request.HostId);

            var session = await _roomService.GetSessionAsync(sanitizedCode);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });



            var normalized = InputSanitizer.SanitizeString(request.Result).ToLowerInvariant();
            var task = normalized switch
            {
                "generate" => HandleRoundOptions.GenerateRound,
                "generatefirst" => HandleRoundOptions.GenerateFirstRound,
                "regenerate" => HandleRoundOptions.RegenerateRound,
                "start" => HandleRoundOptions.StartRound,
                "group" => HandleRoundOptions.CreateGroup,
                "endround" => HandleRoundOptions.EndRound,
                "endgame" => HandleRoundOptions.EndGame,
                "resetround" => HandleRoundOptions.ResetRound,
                "setwinner" => HandleRoundOptions.SetWinner,
                "setdraw" => HandleRoundOptions.SetDraw,
                "setnoresult" => HandleRoundOptions.SetNoResult,
                "movegroup" => HandleRoundOptions.MoveGroup,
                "createcustom" => HandleRoundOptions.CreateCustomGroup,
                "deletecustom" => HandleRoundOptions.DeleteCustomGroup,
                "createcustomplayer" => HandleRoundOptions.CreateCustomGroupPlayer,
                "deletecustomplayer" => HandleRoundOptions.DeleteCustomGroupPlayer,
                _ => HandleRoundOptions.Invalid,
            };

            if (task == HandleRoundOptions.Invalid)
            {
                return BadRequest(new { message = "Invalid game action" });
            }

            var playerControls = false;
            if (task == HandleRoundOptions.CreateCustomGroupPlayer || task == HandleRoundOptions.DeleteCustomGroupPlayer)
                playerControls = true;

            // Only host may perform game actions
            if (!string.Equals(session.HostId, sanitizedHostId, StringComparison.Ordinal) && !playerControls)
                return Forbid();

            // Handle different action types
            GameActionResult result;

            if (task == HandleRoundOptions.CreateCustomGroup || task == HandleRoundOptions.CreateCustomGroupPlayer)
            {
                if (request.ParticipantIds == null || request.ParticipantIds.Count == 0)
                    return BadRequest(new { message = "ParticipantIds are required for creating custom groups" });

                if(task == HandleRoundOptions.CreateCustomGroupPlayer && !session.Settings.AllowPlayersToCreateCustomGroups)
                    return BadRequest(new { message = "Players Cannot Create Custom Groups" });

                if(!session.Settings.AllowCustomGroups)
                    return BadRequest(new { message = "Custom Groups have been disabled" });

                var sanitizedIds = request.ParticipantIds
                    .Select(id => InputSanitizer.SanitizeString(id))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList();

                result = _gameActionService.HandleCreateCustomGroup(session, sanitizedIds, request.AutoFill);
                if (!result.IsSuccess)
                    return BadRequest(new { message = result.ErrorMessage });

                await _roomService.SaveSessionToDatabaseAsync(session);
                return await BroadcastHandleGame(sanitizedCode, session, "CustomGroupCreated");
            }

            if (task == HandleRoundOptions.DeleteCustomGroup || task == HandleRoundOptions.DeleteCustomGroupPlayer)
            {
                if (task == HandleRoundOptions.DeleteCustomGroupPlayer && !session.Settings.AllowPlayersToCreateCustomGroups)
                    return BadRequest(new { message = "Players Cannot Delete Custom Groups" });

                result = _gameActionService.HandleDeleteCustomGroup(session, request.CustomGroupId);
                if (!result.IsSuccess)
                    return BadRequest(new { message = result.ErrorMessage });

                await _roomService.SaveSessionToDatabaseAsync(session);
                return await BroadcastHandleGame(sanitizedCode, session, "CustomGroupDeleted");
            }

            if (task == HandleRoundOptions.StartRound || task == HandleRoundOptions.ResetRound)
            {
                result = _gameActionService.HandleStartOrResetRound(session, task);
                if (!result.IsSuccess)
                    return BadRequest(new { message = result.ErrorMessage });

                await _roomService.SaveSessionToDatabaseAsync(session);
                return await BroadcastHandleGame(sanitizedCode, session, "RoundStarted");
            }

            if (task == HandleRoundOptions.SetNoResult || task == HandleRoundOptions.SetWinner || task == HandleRoundOptions.SetDraw)
            {
                var participantId = InputSanitizer.SanitizeString(request.ParticipantId);
                result = _gameActionService.HandleSetResult(session, request.GroupNumber, request.RoundNumber, task, participantId);

                if (!result.IsSuccess)
                    return BadRequest(new { message = result.ErrorMessage });

                await _roomService.SaveSessionToDatabaseAsync(session);
                return await BroadcastHandleGame(sanitizedCode, session, "ResultsUpdated");
            }

            if (task == HandleRoundOptions.MoveGroup)
            {
                var participantId = InputSanitizer.SanitizeString(request.ParticipantId);
                result = _gameActionService.HandleMoveParticipant(session, request.GroupNumber, request.MoveGroup, request.RoundNumber, participantId);

                if (!result.IsSuccess)
                    return BadRequest(new { message = result.ErrorMessage });

                await _roomService.SaveSessionToDatabaseAsync(session);
                return await BroadcastHandleGame(sanitizedCode, session, "ParticipantMoved");
            }

            if (task == HandleRoundOptions.GenerateRound || task == HandleRoundOptions.GenerateFirstRound || task == HandleRoundOptions.RegenerateRound)
            {
                result = _gameActionService.HandleGenerateRound(sanitizedCode, task);

                if (!result.IsSuccess)
                    return NotFound(new { message = result.ErrorMessage });

                return await BroadcastHandleGame(sanitizedCode, session, "RoundGenerated");
            }

            if (task == HandleRoundOptions.EndGame)
            {
                var end = _gameActionService.EndRound(session);
       
                if (!end)
                    return NotFound(new { message = "Failed To End" });

                await _roomService.SaveSessionToDatabaseAsync(session);
                return await BroadcastHandleGame(sanitizedCode, session, "GameEnded");
            }

            return BadRequest(new { message = "Invalid game action" });
        }

        private async Task<IActionResult> BroadcastHandleGame(string code, RoomSession session, string method)
        {
            var participants = session.Participants.Values
                .Select(p => p.ToMemberResponse())
                .ToArray();

            var payload = new GameStateResponse(
                code,
                session.CurrentRound,
                DateTime.UtcNow,
                session.Settings.RoundLength,
                session.Groups?.Select(g => g.ToGroupResponse()).ToArray() ?? Array.Empty<GroupResponse>()
            );

            // Create extended payload with participants
            var extendedPayload = new
            {
                payload.RoomCode,
                payload.Round,
                payload.StartedAtUtc,
                payload.RoundLength,
                payload.Groups,
                Participants = participants
            };

            await _hubContext.Clients.Group(code).SendAsync(method, extendedPayload);
            return Ok(extendedPayload);
        }

        // GET api/rooms/{code}/current
        // Returns which group the specified participant is in after the game has started.
        [HttpGet("{code}/current")]
        // Uses default "api" rate limiting
        public async Task<IActionResult> GetCurrentRound([FromRoute][Required][RoomCode] string code)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    message = "Invalid room code",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            var sanitizedCode = InputSanitizer.SanitizeString(code).ToUpperInvariant();
            var session = await _roomService.GetSessionAsync(sanitizedCode);

            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            if (!session.IsGameStarted || session.Groups is null)
                return NotFound(new { message = "Game has not been started for this room" });

            var groups = session.Groups.Select(g => g.ToGroupResponse()).ToArray();
            return Ok(groups);
        }

        // GET api/rooms/{code}/group/{participantId}
        // Returns which group the specified participant is in after the game has started.
        [HttpGet("{code}/group/{participantId}")]
        // Uses default "api" rate limiting
        public async Task<IActionResult> GetParticipantGroup(
            [FromRoute][Required][RoomCode] string code,
            [FromRoute][Required][SafeString(MinLength = 1, MaxLength = 100)] string participantId)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    message = "Validation failed",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            var sanitizedCode = InputSanitizer.SanitizeString(code).ToUpperInvariant();
            var sanitizedParticipantId = InputSanitizer.SanitizeString(participantId);

            var session = await _roomService.GetSessionAsync(sanitizedCode);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            if (!session.IsGameStarted || session.Groups is null)
                return NoContent();

            // Find the group that contains the participant
            foreach (var group in session.Groups)
            {
                if (group.Participants.ContainsKey(sanitizedParticipantId))
                {
                    var response = new ParticipantGroupResponse(
                        session.Code,
                        sanitizedParticipantId,
                        group.GroupNumber,
                        group.Participants.Values.Select(p => p.ToMemberResponse()).ToArray(),
                        group.RoundNumber,
                        group.RoundStarted,
                        group.HasResult,
                        group.WinnerParticipantId,
                        group.IsDraw,
                        group.StartedAtUtc,
                        group.CompletedAtUtc,
                        session.Settings.RoundLength,
                        group.Statistics,
                        session.Settings.ToRoomSettingsResponse()
                    );
                    return Ok(response);
                }
            }

            return NotFound(new { message = "Participant not found in any group for this room" });
        }

        // GET api/rooms/{code}/archived
        // Returns all archived rounds (older rounds first) with their groups and member details.
        [HttpGet("{code}/archived")]
        // Uses default "api" rate limiting
        public async Task<IActionResult> GetArchivedRounds([FromRoute][Required][RoomCode] string code)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    message = "Invalid room code",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            var sanitizedCode = InputSanitizer.SanitizeString(code).ToUpperInvariant();
            var session = await _roomService.GetSessionAsync(sanitizedCode);

            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            var rounds = session.ArchivedRounds
                .Select(roundGroups => roundGroups.ToRoundResponse(session.Code))
                .ToArray();

            return Ok(rounds);
        }

        // POST api/rooms/{code}/report
        // Participants call this to report an outcome when a game is started:
        // - Result = "win" => participant reports they won (per-group max 1 winner)
        // - Result = "draw" => participant reports a draw for their group (everyone in that group gets draw)
        // - Result = "dropout" => participant reports they dropped out (removed from future rounds)
        // - Statistics = optional dictionary of custom statistics (e.g., { "turnCount": 12, "commander": "Atraxa" })
        [HttpPost("{code}/report")]
        // Uses default "api" rate limiting
        public async Task<IActionResult> ReportOutcome([FromRoute][Required][RoomCode] string code, [FromBody] ReportOutcomeRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    message = "Validation failed",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            var sanitizedCode = InputSanitizer.SanitizeString(code).ToUpperInvariant();
            var sanitizedParticipantId = InputSanitizer.SanitizeString(request.ParticipantId);
            var sanitizedCommander = InputSanitizer.SanitizeString(request.Commander);
            var sanitizedStatistics = InputSanitizer.SanitizeDictionary(request.Statistics);

            var normalized = InputSanitizer.SanitizeString(request.Result).ToLowerInvariant();
            if (normalized != "win" && normalized != "draw" && normalized != "drop" && normalized != "data")
                return BadRequest(new { message = "result must be one of: win, draw, drop, data" });

            var outcome = normalized switch
            {
                "win" => ReportOutcomeType.Win,
                "draw" => ReportOutcomeType.Draw,
                "drop" => ReportOutcomeType.DropOut,
                "data" => ReportOutcomeType.DataOnly,
                _ => ReportOutcomeType.DataOnly
            };

            var serviceResult = _roomService.ReportOutcome(sanitizedCode, sanitizedParticipantId, outcome, sanitizedCommander, sanitizedStatistics, out var winnerId, out var removedParticipant, out var groupIndex);

            return serviceResult switch
            {
                ReportOutcomeResult.RoomNotFound => NotFound(new { message = "Room not found or expired" }),
                ReportOutcomeResult.NotStarted => BadRequest(new { message = "Game has not been started for this room" }),
                ReportOutcomeResult.ParticipantNotFound => NotFound(new { message = "Participant not found in room or not in current round" }),
                ReportOutcomeResult.AlreadyEnded => BadRequest(new { message = "This group already has a result" }),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.DropOut => await HandleDropoutBroadcast(sanitizedCode, removedParticipant),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.Win => await HandleGroupEndedBroadcast(sanitizedCode, "win", winnerId, groupIndex, sanitizedParticipantId),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.Draw => await HandleGroupEndedBroadcast(sanitizedCode, "draw", null, groupIndex, sanitizedParticipantId),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.DataOnly => await HandleGroupEndedBroadcast(sanitizedCode, "data", null, groupIndex, sanitizedParticipantId),
                ReportOutcomeResult.RoundStarted => Ok(new { message = "The Round has already Started" }),
                _ => StatusCode(500, new { message = "Unknown error reporting outcome" })
            };
        }

        private async Task<IActionResult> HandleGroupEndedBroadcast(string code, string result, string? winnerId, int? groupIndex, string participantId)
        {
            var session = await _roomService.GetSessionAsync(code);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            if (!groupIndex.HasValue || session.Groups is null)
                return BadRequest(new { message = "Invalid group index" });

            var currentGroup = session.Groups.First(x => x.GroupNumber == groupIndex);

            if(currentGroup is null )
                return BadRequest(new { message = "Invalid group" });

            var members = currentGroup.Participants.Values
                .Select(p => p.ToMemberResponse())
                .ToArray();

            var payload = new ParticipantGroupResponse(
                code,
                participantId,
                groupIndex.Value,
                members,
                currentGroup.RoundNumber,
                currentGroup.RoundStarted,
                currentGroup.HasResult,
                currentGroup.WinnerParticipantId,
                currentGroup.IsDraw,
                currentGroup.StartedAtUtc,
                currentGroup.CompletedAtUtc,
                session.Settings.RoundLength,
                currentGroup.Statistics,
                session.Settings.ToRoomSettingsResponse()
            );

            await _hubContext.Clients.Group(code).SendAsync("GroupEnded", payload);
            return Ok(payload);
        }

        private async Task<IActionResult> HandleDropoutBroadcast(string code, Models.Participant? participant)
        {
            if (participant is null)
                return Ok(new { message = "Participant removed" });

            var payload = new
            {
                RoomCode = code,
                ParticipantId = participant.Id,
                ParticipantName = participant.Name
            };

            await _hubContext.Clients.Group(code).SendAsync("ParticipantDroppedOut", payload);
            return Ok(payload);
        }

        // GET api/rooms/all
        // Returns all sessions with their full data including participants, groups, and archived rounds.
        [HttpGet("/all")]
        [EnableRateLimiting("strict")] // VERY strict - this is a dangerous endpoint
        public async Task<IActionResult> GetAllSessions()
        {
            // TODO: This endpoint should require admin authentication
            // For now, just add warning in response
            var sessions = await _roomService.GetAllSessionsAsync();

            if (sessions == null)
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to retrieve sessions" });

            var result = sessions
                .Select(session => session.ToSessionSummaryResponse())
                .ToArray();

            return Ok(result);
        }

        // GET api/rooms/{code}/summary
        // Returns detailed session summary including archived rounds and full game state
        [HttpGet("{code}/summary")]
        // Uses default "api" rate limiting
        public async Task<IActionResult> GetSessionSummary([FromRoute][Required][RoomCode] string code)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    message = "Invalid room code format",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            var sanitizedCode = InputSanitizer.SanitizeString(code).ToUpperInvariant();
            var session = await _roomService.GetSessionAsync(sanitizedCode);

            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            var summary = session.ToSessionSummaryResponse();
            return Ok(summary);
        }
    }

    // ===== DTOs with Validation =====

    public record CreateRoomRequest(
        [Required(ErrorMessage = "HostId is required")]
        [SafeString(MinLength = 3, MaxLength = 100)]
        string HostId,

        [Range(4, 10, ErrorMessage = "CodeLength must be between 4 and 10")]
        int CodeLength = 6,

        [TimeSpanRange(MinHours = 1, MaxHours = 720)] // 1 hour to 30 days
        TimeSpan? Ttl = null
    );

    public record CreateRoomResponse(
        string Code,
        DateTime ExpiresAtUtc
    );

    public record JoinRoomRequest(
        [Required(ErrorMessage = "ParticipantId is required")]
        [SafeString(MinLength = 1, MaxLength = 100)]
        string ParticipantId,

        [SafeString(MinLength = 0, MaxLength = 200)]
        string ParticipantName,

        [SafeString(MinLength = 0, MaxLength = 200)]
        string Commander
    );

    public record ReportOutcomeRequest(
        [Required(ErrorMessage = "ParticipantId is required")]
        [SafeString(MinLength = 1, MaxLength = 100)]
        string ParticipantId,

        [Required(ErrorMessage = "Result is required")]
        [RegularExpression("^(win|draw|drop|data)$", ErrorMessage = "Result must be one of: win, draw, drop, data")]
        string Result,

        [SafeString(MinLength = 0, MaxLength = 200)]
        string Commander,

        [DictionarySizeLimit(MaxKeys = 20, MaxValueLength = 500)]
        Dictionary<string, object>? Statistics = null
    );

    public record RoomSettingsResponse(
        bool AllowJoinAfterStart,
        bool PrioitizeWinners,
        bool AllowGroupOfThree,
        bool AllowGroupOfFive,
        bool FurtherReduceOddsOfGroupOfThree,
        int RoundLength,
        bool UsePoints,
        int PointsForWin,
        int PointsForDraw,
        int PointsForLoss,
        int PointsForABye,
        bool AllowCustomGroups,
        bool AllowPlayersToCreateCustomGroups,
        bool TournamentMode,
        int MaxRounds,
        int MaxGroupSize);

    public record MemberResponse(
        string Id,
        string Name,
        string Commander,
        int Points,
        DateTime JoinedAtUtc,
        int Order,
        bool Dropped,
        bool AutoFill,
        Guid InCustomGroup
    );

    public record GetRoomResponse(
        string Code,
        string EventName,
        string HostId,
        DateTime CreatedAtUtc,
        DateTime ExpiresAtUtc,
        int ParticipantCount,
        MemberResponse[] Participants,
        RoomSettingsResponse Settings
    );

    public record UpdateRoomSettingsRequest(
        [Required(ErrorMessage = "HostId is required")]
        [SafeString(MinLength = 3, MaxLength = 100)]
        string HostId,

        [SafeString(MinLength = 0, MaxLength = 200)]
        string? EventName = null,

        [Range(2, 10, ErrorMessage = "MaxGroupSize must be between 2 and 10")]
        int MaxGroupSize = 4,
        bool AllowJoinAfterStart = false,
        bool PrioitizeWinners = true,
        bool AllowGroupOfThree = true,
        bool AllowGroupOfFive = true,
        bool FurtherReduceOddsOfGroupOfThree = true,
        int RoundLength = 90,
        bool UsePoints = false,
        int PointsForWin = 1,
        int PointsForDraw = 0,
        int PointsForLoss = 0,
        int PointsForABye = 1,
        bool AllowCustomGroups = false,
        bool AllowPlayersToCreateCustomGroups = false,
        bool TournamentMode = false,
        int MaxRounds = 10000
    );

    public record EndSessionRequest(
        [Required(ErrorMessage = "HostId is required")]
        [SafeString(MinLength = 3, MaxLength = 100)]
        string HostId
    );

    public record HandleGameRequest(
        [Required(ErrorMessage = "Result is required")]
        [SafeString(MinLength = 3, MaxLength = 50)]
        string Result,

        [Required(ErrorMessage = "HostId is required")]
        [SafeString(MinLength = 1, MaxLength = 100)]
        string HostId,

        [SafeString(MinLength = 1, MaxLength = 100)]
        string ParticipantId,

        int GroupNumber,
        int RoundNumber,
        int MoveGroup,
        // New fields for custom groups
        List<string>? ParticipantIds = null,
        bool AutoFill = true,
        Guid CustomGroupId = default
    );

    public record GroupResponse(
        int GroupNumber,
        int Round,
        bool RoundStarted,
        MemberResponse[] Members,
        bool Result,
        string? Winner,
        bool Draw,
        bool IsCustom,
        DateTime? StartedAtUtc,
        DateTime? CompletedAtUtc,
        Dictionary<string, object>? Statistics
    );

    public record RoundResponse(
        string RoomCode,
        int RoundNumber,
        GroupResponse[] Groups
    );

    public record GameStateResponse(
        string RoomCode,
        int? Round,
        DateTime StartedAtUtc,
        int RoundLength,
        GroupResponse[] Groups
    );

    public record ParticipantGroupResponse(
        string RoomCode,
        string ParticipantId,
        int GroupNumber,
        MemberResponse[] Members,
        int Round,
        bool RoundStarted,
        bool Result,
        string? Winner,
        bool Draw,
        DateTime? StartedAtUtc,
        DateTime? CompletedAtUtc,
        int RoundLength,
        Dictionary<string, object>? Statistics,
        RoomSettingsResponse Settings
    );

    public record SessionSummaryResponse(
        string Code,
        string EventName,
        string HostId,
        DateTime CreatedAtUtc,
        DateTime ExpiresAtUtc,
        bool IsGameStarted,
        bool IsGameEnded,
        bool Archived,
        int? CurrentRound,
        string? WinnerParticipantId,
        RoomSettings Settings,
        int ParticipantCount,
        MemberResponse[] Participants,
        GroupResponse[]? CurrentGroups,
        int ArchivedRoundsCount,
        RoundResponse[] ArchivedRounds
    );
}