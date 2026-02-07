using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
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

        public RoomsController(RoomCodeService roomService, IHubContext<RoomsHub> hubContext)
        {
            _roomService = roomService;
            _hubContext = hubContext;
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
                .Select(p => new RoomParticipant(p.Id, p.Name, p.Commander, p.Points, p.JoinedAtUtc))
                .ToArray();

            return Ok(new GetRoomResponse(
                session.Code,
                session.EventName,
                session.HostId,
                session.CreatedAtUtc,
                session.ExpiresAtUtc,
                session.Participants.Count,
                participants));
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

            var errorMessage = string.Empty;
            var sanitizedCode = InputSanitizer.SanitizeString(code).ToUpperInvariant();
            var sanitizedHostId = InputSanitizer.SanitizeString(request.HostId);

            var session = await _roomService.GetSessionAsync(sanitizedCode);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            // Only host may end the session
            if (!string.Equals(session.HostId, sanitizedHostId, StringComparison.Ordinal))
                return Forbid();

            // Sanitize player dictionary
            var sanitizedPlayers = InputSanitizer.SanitizeDictionary(request.Players);

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
                _ => HandleRoundOptions.Invalid,
            };

            if (task == HandleRoundOptions.Invalid)
            {
                return BadRequest(new { message = "Invalid game action" });
            }

            if (task == HandleRoundOptions.StartRound || task == HandleRoundOptions.ResetRound)
            {
                if (session.Groups is null || session.Groups.Count == 0)
                    return BadRequest(new { message = "No groups available to start" });

                lock (session)
                {
                    var currentTime = DateTime.UtcNow;
                    foreach (var group in session.Groups)
                    {
                        if (task == HandleRoundOptions.StartRound)
                            group.RoundStarted = true;
                        else if (task == HandleRoundOptions.ResetRound)
                            group.RoundStarted = false;

                        group.StartedAtUtc = currentTime;

                        foreach (var participant in group.Participants)
                        {
                            if(session.Participants.ContainsKey(participant.Key))
                                participant.Value.Commander = session.Participants[participant.Key].Commander;                             
                        }
                    }
                }

                // Save the updated session to the database
                var saved = await _roomService.SaveSessionToDatabaseAsync(session);
                if (!saved)
                {
                    return StatusCode(StatusCodes.Status500InternalServerError,
                        new { message = "Failed to save session to database" });
                }

                var payload = new
                {
                    RoomCode = sanitizedCode,
                    Round = session.CurrentRound,
                    StartedAtUtc = DateTime.UtcNow,
                    RoundLength = session.Settings.RoundLength,
                    Groups = session.Groups.Select(group => new
                    {
                        group.GroupNumber,
                        group.RoundNumber,
                        group.RoundStarted,
                        group.StartedAtUtc,
                        Members = group.ParticipantsOrdered.Select(p => new { p.Id, p.Name, p.Commander, p.Points, p.JoinedAtUtc }).ToArray()
                    }).ToArray()
                };

                await _hubContext.Clients.Group(sanitizedCode).SendAsync("RoundStarted", payload);
                return Ok(payload);
            }

            //Handle Round
            if (task == HandleRoundOptions.GenerateRound || task == HandleRoundOptions.GenerateFirstRound || task == HandleRoundOptions.RegenerateRound)
            {
                var groups = _roomService.HandleRound(sanitizedCode, task, sanitizedPlayers, ref errorMessage);
                if (groups is null)
                    return NotFound(new { message = $"Error: {errorMessage}" });

                var round = -1;
                var testGroup = groups.FirstOrDefault();
                if (groups.Any() && testGroup != null)
                    round = testGroup.RoundNumber;

                var payload = new
                {
                    RoomCode = sanitizedCode,
                    Round = round,
                    Groups = groups.Select(group => new
                    {
                        GroupNumber = group.GroupNumber,
                        RoundNumber = group.RoundNumber,
                        RoundStarted = group.RoundStarted,
                        Members = group.ParticipantsOrdered.Select(p => new { p.Id, p.Name, p.Commander, p.Points, p.JoinedAtUtc }).ToArray()
                    }).ToArray()
                };

                // Broadcast game update to clients in the room group
                await _hubContext.Clients.Group(sanitizedCode).SendAsync("RoundGenerated", payload);
                return Ok(payload);
            }

            return BadRequest(new { message = "Invalid game action" });
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

            var currentRound = session.Groups;

            var groups = currentRound.Select(group => new
            {
                GroupNumber = group.GroupNumber,
                Round = group.RoundNumber,
                RoundStarted = group.RoundStarted,
                Members = group.ParticipantsOrdered.Select(p => new { p.Id, p.Name, p.Commander, p.Points, p.JoinedAtUtc }).ToArray(),
                Result = group.HasResult,
                Winner = group.WinnerParticipantId,
                Draw = group.IsDraw,
                StartedAtUtc = group.StartedAtUtc,
                CompletedAtUtc = group.CompletedAtUtc,
                Statistics = group.Statistics,
                RoundLength = session.Settings.RoundLength,
            }).ToArray();

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
                    var members = group.ParticipantsOrdered.Select(participant =>
                        new { participant.Id, participant.Name, participant.Commander, participant.Points, participant.JoinedAtUtc }).ToArray();

                    var result = new
                    {
                        RoomCode = session.Code,
                        ParticipantId = sanitizedParticipantId,
                        GroupNumber = group.GroupNumber,
                        Members = members,
                        Round = group.RoundNumber,
                        RoundStarted = group.RoundStarted,
                        Result = group.HasResult,
                        Winner = group.WinnerParticipantId,
                        Draw = group.IsDraw,
                        Statistics = group.Statistics,
                        StartedAtUtc = group.StartedAtUtc,
                        CompletedAtUtc = group.CompletedAtUtc,
                        RoundLength = session.Settings.RoundLength,
                    };
                    return Ok(result);
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

            var archived = session.ArchivedRounds;
            var rounds = archived.Select(roundGroups =>
            {
                var roundNumber = roundGroups.FirstOrDefault()?.RoundNumber ?? -1;
                var groups = roundGroups.Select(group => new
                {
                    GroupNumber = group.GroupNumber,
                    Round = group.RoundNumber,
                    RoundStarted = group.RoundStarted,
                    Members = group.ParticipantsOrdered.Select(participant => new { participant.Id, participant.Name, participant.Commander, participant.Points, participant.JoinedAtUtc }).ToArray(),
                    Result = group.HasResult,
                    Winner = group.WinnerParticipantId,
                    Draw = group.IsDraw,
                    StartedAtUtc = group.StartedAtUtc,
                    CompletedAtUtc = group.CompletedAtUtc,
                    Statistics = group.Statistics,
                }).ToArray();

                return new
                {
                    RoomCode = session.Code,
                    RoundNumber = roundNumber,
                    Groups = groups
                };
            }).ToArray();

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
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.Win => await HandleGroupEndedBroadcast(sanitizedCode, "win", winnerId, groupIndex),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.Draw => await HandleGroupEndedBroadcast(sanitizedCode, "draw", null, groupIndex),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.DataOnly => await HandleGroupEndedBroadcast(sanitizedCode, "data", null, groupIndex),
                _ => StatusCode(500, new { message = "Unknown error reporting outcome" })
            };
        }

        private async Task<IActionResult> HandleGroupEndedBroadcast(string code, string result, string? winnerId, int? groupIndex)
        {
            var session = await _roomService.GetSessionAsync(code);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            if (!groupIndex.HasValue || session.Groups is null || groupIndex < 0 || groupIndex >= session.Groups.Count)
                return BadRequest(new { message = "Invalid group index" });

            var members = session.Groups[groupIndex.Value].ParticipantsOrdered
                .Select(p => new { p.Id, p.Name, p.Commander, p.Points, p.JoinedAtUtc })
                .ToArray();

            var payload = new
            {
                RoomCode = code,
                GroupNumber = groupIndex.Value + 1,
                Result = result,
                WinnerParticipantId = winnerId,
                Members = members,
                Statistics = session.Groups[groupIndex.Value].Statistics
            };

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

            var result = sessions.Select(session => new
            {
                Code = session.Code,
                EventName = session.EventName,
                HostId = session.HostId,
                CreatedAtUtc = session.CreatedAtUtc,
                ExpiresAtUtc = session.ExpiresAtUtc,
                IsGameStarted = session.IsGameStarted,
                IsGameEnded = session.IsGameEnded,
                Archived = session.Archived,
                CurrentRound = session.CurrentRound,
                WinnerParticipantId = session.WinnerParticipantId,
                Settings = session.Settings,
                ParticipantCount = session.Participants.Count,
                Participants = session.Participants.Values.Select(participant => new RoomParticipant(
                    participant.Id,
                    participant.Name,
                    participant.Commander,
                    participant.Points,
                    participant.JoinedAtUtc)).ToArray(),
                CurrentGroups = session.Groups?.Select(group => new
                {
                    GroupNumber = group.GroupNumber,
                    Round = group.RoundNumber,
                    RoundStarted = group.RoundStarted,
                    Members = group.ParticipantsOrdered.Select(p => new { p.Id, p.Name, p.Commander, p.Points, p.JoinedAtUtc }).ToArray(),
                    Result = group.HasResult,
                    Winner = group.WinnerParticipantId,
                    Draw = group.IsDraw,
                    StartedAtUtc = group.StartedAtUtc,
                    CompletedAtUtc = group.CompletedAtUtc,
                    Statistics = group.Statistics
                }).ToArray(),
                ArchivedRoundsCount = session.ArchivedRounds.Count,
                ArchivedRounds = session.ArchivedRounds.Select(roundGroups =>
                {
                    var roundNumber = roundGroups.FirstOrDefault()?.RoundNumber ?? -1;
                    var groups = roundGroups.Select(group => new
                    {
                        GroupNumber = group.GroupNumber,
                        Round = group.RoundNumber,
                        RoundStarted = group.RoundStarted,
                        Members = group.ParticipantsOrdered.Select(p => new { p.Id, p.Name, p.Commander, p.Points, p.JoinedAtUtc }).ToArray(),
                        Result = group.HasResult,
                        Winner = group.WinnerParticipantId,
                        Draw = group.IsDraw,
                        StartTime = group.StartedAtUtc,
                        EndTime = group.CompletedAtUtc,
                        Statistics = group.Statistics
                    }).ToArray();

                    return new
                    {
                        RoundNumber = roundNumber,
                        Groups = groups
                    };
                }).ToArray()
            }).ToArray();

            return Ok(result);
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

    public record RoomParticipant(
        string Id,
        string Name,
        string Commander,
        int Points,
        DateTime JoinedAtUtc
    );

    public record GetRoomResponse(
        string Code,
        string EventName,
        string HostId,
        DateTime CreatedAtUtc,
        DateTime ExpiresAtUtc,
        int ParticipantCount,
        RoomParticipant[] Participants
    );

    public record UpdateRoomSettingsRequest(
        [Required(ErrorMessage = "HostId is required")]
        [SafeString(MinLength = 3, MaxLength = 100)]
        string HostId,

        [SafeString(MinLength = 0, MaxLength = 200)]
        string? EventName = null,

        [Range(3, 6, ErrorMessage = "MaxGroupSize must be between 3 and 6")]
        int MaxGroupSize = 4,

        bool AllowJoinAfterStart = false,
        bool AllowSpectators = true
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
        [SafeString(MinLength = 3, MaxLength = 100)]
        string HostId,

        [DictionarySizeLimit(MaxKeys = 100, MaxValueLength = 1000)]
        Dictionary<string, object> Players
    );
}