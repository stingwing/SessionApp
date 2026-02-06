using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SessionApp.Hubs;
using SessionApp.Models;
using SessionApp.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static SessionApp.Services.RoomCodeService;

namespace SessionApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
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
        public IActionResult Test()
        {
            return Ok(new { message = "Service is working", utcNow = DateTime.UtcNow });
        }

        // Raw /api/Rooms { "hostId": "host1" }
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateRoomRequest request)
        {
            var session = _roomService.CreateSession(request.HostId, request.CodeLength, request.Ttl ?? TimeSpan.FromDays(7));

            // Notify connected clients (optional: clients may join groups by room code)
            await _hubContext.Clients.Group(session.Code).SendAsync("RoomCreated", new { session.Code, session.HostId, session.ExpiresAtUtc });

            return CreatedAtAction(nameof(Get), new { code = session.Code }, new CreateRoomResponse(session.Code, session.ExpiresAtUtc));
        }

        [HttpPost("{code}/join")]
        public async Task<IActionResult> Join(string code, [FromBody] JoinRoomRequest request)
        {
            var ok = _roomService.TryJoin(code, request.ParticipantId, request.ParticipantName, request.Commander);
            if (!ok.Contains("Success")) 
                return NotFound(new { message = ok });

            // Broadcast participant joined to clients in the room group
            await _hubContext.Clients.Group(code.ToUpperInvariant())
                .SendAsync("ParticipantJoined", new { ParticipantId = request.ParticipantId, ParticipantName = request.ParticipantName, RoomCode = code.ToUpperInvariant() });

            return Ok();
        }

        [HttpGet("{code}")]
        public async Task<IActionResult> Get(string code)
        {
            var session = await _roomService.GetSessionAsync(code);
            if (session is null) return NotFound();

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

        // GET api/rooms/all
        // Returns all sessions with their full data including participants, groups, and archived rounds.
        [HttpGet("/all")]
        public async Task<IActionResult> GetAllSessions()
        {
            var sessions = await _roomService.GetAllSessionsAsync();

            if(sessions == null)
                return StatusCode(StatusCodes.Status500InternalServerError);

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

        // New endpoint: update room settings
        // POST api/rooms/{code}/settings
        [HttpPost("{code}/settings")]
        public async Task<IActionResult> UpdateSettings(string code, [FromBody] UpdateRoomSettingsRequest request)
        {
            if (string.IsNullOrWhiteSpace(code) || request is null)
                return BadRequest(new { message = "code and request body are required" });

            if (string.IsNullOrWhiteSpace(request.HostId))
                return BadRequest(new { message = "HostId is required to update settings" });

            var session = await _roomService.GetSessionAsync(code);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            // Simple authorization: only the host may change settings
            if (!string.Equals(session.HostId, request.HostId, StringComparison.Ordinal))
                return Forbid();

            if(session.IsGameEnded || session.IsExpiredUtc())
                return NotFound(new { message = "Game has ended" });

            if(session.IsGameStarted)
                return NotFound(new { message = "You can't change settings after starting the game" });

            lock (session)
            {
                // Update event name if provided
                if (!string.IsNullOrWhiteSpace(request.EventName))
                    session.EventName = request.EventName;
                
                // Add other settings here as needed
            }

            // Save settings to database
            var saved = await _roomService.SaveSessionToDatabaseAsync(session);
            if (!saved)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to save settings to database" });
            }

            var payload = new
            {
                RoomCode = session.Code,
                EventName = session.EventName,
                Settings = session.Settings
            };

            // Notify connected clients in the room that settings changed
            await _hubContext.Clients.Group(code.ToUpperInvariant()).SendAsync("SettingsChanged", payload);

            return Ok(new { session.EventName, session.Settings });
        }

        // POST api/rooms/{code}/handlegame
        // Handle game-specific actions with player data
        [HttpPost("{code}/handlegame")]
        public async Task<IActionResult> HandleGame(string code, [FromBody] HandleGameRequest request)
        {
            var errorMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(code) || request is null)
                return BadRequest(new { message = "code and request body are required" });

            if (string.IsNullOrWhiteSpace(request.HostId))
                return BadRequest(new { message = "HostId is handle the round" });

            var session = await _roomService.GetSessionAsync(code);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            // Only host may end the session
            if (!string.Equals(session.HostId, request.HostId, StringComparison.Ordinal))
                return Forbid();

            var playerGroup = request.Players;

            var normalized = request.Result.Trim().ToLowerInvariant();
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

            if (task == HandleRoundOptions.StartRound || task == HandleRoundOptions.ResetRound)
            {
                if (session.Groups is null || session.Groups.Count == 0)
                    return BadRequest(new { message = "No groups available to start" });

                lock (session)
                {
                    var currentTime = DateTime.UtcNow;
                    foreach (var group in session.Groups)
                    {   
                        if(task == HandleRoundOptions.StartRound)
                            group.RoundStarted = true;
                        else if (task == HandleRoundOptions.ResetRound)
                            group.RoundStarted = false;

                        group.StartedAtUtc = currentTime;
                    }
                }

                // Save the updated session to the database
                var saved = await _roomService.SaveSessionToDatabaseAsync(session);
                if (!saved)
                {
                    return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to save session to database" });
                }

                var payload = new
                {
                    RoomCode = code.ToUpperInvariant(),
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

                await _hubContext.Clients.Group(code.ToUpperInvariant()).SendAsync("RoundStarted", payload);
                return Ok(payload);
            }

            //Handle Round
            if (task == HandleRoundOptions.GenerateRound || task == HandleRoundOptions.GenerateFirstRound || task == HandleRoundOptions.RegenerateRound)
            {
                var groups = _roomService.HandleRound(code, task, playerGroup, ref errorMessage);
                if (groups is null)
                    return NotFound(new { message = $"Error: {errorMessage}" });

                var round = -1;
                var testGroup = groups.FirstOrDefault();
                if (groups.Any() && testGroup != null)
                    round = testGroup.RoundNumber;

                var payload = new
                {
                    RoomCode = code.ToUpperInvariant(),
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
                await _hubContext.Clients.Group(code.ToUpperInvariant()).SendAsync("RoundGenerated", payload);
                return Ok(payload);
            }

            return BadRequest(new { message = "Invalid" });
        }

        // GET api/rooms/{code}/current
        // Returns which group the specified participant is in after the game has started.
        [HttpGet("{code}/current")]
        public async Task<IActionResult> GetCurrentRound(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return BadRequest(new { message = "code and participantId are required" });

            var session = await _roomService.GetSessionAsync(code);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            if (!session.IsGameStarted || session.Groups is null)
                return NotFound(new { message = "Game has not been started for this room" });

            var currentRound = session.Groups;
            var roundNumber = session.CurrentRound;

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
        public async Task<IActionResult> GetParticipantGroup(string code, string participantId)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(participantId))
                return BadRequest(new { message = "code and participantId are required" });

            var session = await _roomService.GetSessionAsync(code);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            if (!session.IsGameStarted || session.Groups is null)
                return NoContent();

            // Find the group that contains the participant
            foreach (var group in session.Groups)
            {
                if (group.Participants.ContainsKey(participantId))
                {
                    var members = group.ParticipantsOrdered.Select(participant => new { participant.Id, participant.Name, participant.Commander, participant.Points, participant.JoinedAtUtc }).ToArray();
                    var result = new
                    {
                        RoomCode = session.Code,
                        ParticipantId = participantId,
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
        public async Task<IActionResult> GetArchivedRounds(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return BadRequest(new { message = "code is required" });

            var session = await _roomService.GetSessionAsync(code);
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
        public async Task<IActionResult> ReportOutcome(string code, [FromBody] ReportOutcomeRequest request)
        {
            if (string.IsNullOrWhiteSpace(code) || request is null || string.IsNullOrWhiteSpace(request.ParticipantId) || string.IsNullOrWhiteSpace(request.Result))
                return BadRequest(new { message = "code, participantId and result are required" });

            var commander = request.Commander;
            // parse result
            var normalized = request.Result.Trim().ToLowerInvariant();
            if (normalized != "win" && normalized != "draw" && normalized != "drop" && normalized != "data")
                return BadRequest(new { message = "result must be one of: win, draw, drop, data" });

            var outcome = normalized == "win" ? ReportOutcomeType.Win : normalized == "draw" ? ReportOutcomeType.Draw : normalized == "drop" ? ReportOutcomeType.DropOut : ReportOutcomeType.DataOnly;

            // Pass statistics to the service
            var serviceResult = _roomService.ReportOutcome(code, request.ParticipantId, outcome, commander, request.Statistics ?? new Dictionary<string, object>(), out var winnerId, out var removedParticipant, out var groupIndex);

            return serviceResult switch
            {
                ReportOutcomeResult.RoomNotFound => NotFound(new { message = "Room not found or expired" }),
                ReportOutcomeResult.NotStarted => BadRequest(new { message = "Game has not been started for this room" }),
                ReportOutcomeResult.ParticipantNotFound => NotFound(new { message = "Participant not found in room or not in current round" }),
                ReportOutcomeResult.AlreadyEnded => BadRequest(new { message = "This group already has a result" }),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.DropOut => await HandleDropoutBroadcast(code, removedParticipant),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.Win => await HandleGroupEndedBroadcast(code, "win", winnerId, groupIndex),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.Draw => await HandleGroupEndedBroadcast(code, "draw", null, groupIndex),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.DataOnly => await HandleGroupEndedBroadcast(code, "data", null, groupIndex),
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
                RoomCode = code.ToUpperInvariant(),
                GroupNumber = groupIndex.Value + 1,
                Result = result,
                WinnerParticipantId = winnerId,
                Members = members,
                Statistics = session.Groups[groupIndex.Value].Statistics
            };

            await _hubContext.Clients.Group(code.ToUpperInvariant()).SendAsync("GroupEnded", payload);
            return Ok(payload);
        }

        private async Task<IActionResult> HandleDropoutBroadcast(string code, Models.Participant? participant)
        {
            if (participant is null)
                return Ok(new { message = "Participant removed" });

            var payload = new
            {
                RoomCode = code.ToUpperInvariant(),
                ParticipantId = participant.Id,
                ParticipantName = participant.Name
            };

            await _hubContext.Clients.Group(code.ToUpperInvariant()).SendAsync("ParticipantDroppedOut", payload);
            return Ok(payload);
        }
    }

    public record CreateRoomRequest(string HostId, int CodeLength = 6, TimeSpan? Ttl = null);
    public record CreateRoomResponse(string Code, DateTime ExpiresAtUtc);
    public record JoinRoomRequest(string ParticipantId, string ParticipantName, string Commander);
    
    // Updated to include optional statistics dictionary
    public record ReportOutcomeRequest(
        string ParticipantId, 
        string Result,
        string Commander,
        Dictionary<string, object>? Statistics = null);

    // DTO used by GetRoomResponse to expose participant details
    public record RoomParticipant(string Id, string Name, string Commander, int Points, DateTime JoinedAtUtc);

    // Updated GetRoomResponse now includes the participants array and EventName
    public record GetRoomResponse(string Code, string EventName, string HostId, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, int ParticipantCount, RoomParticipant[] Participants);

    // Request DTO for updating room settings.
    public record UpdateRoomSettingsRequest(string HostId, string? EventName = null, int MaxGroupSize = 4, bool AllowJoinAfterStart = false, bool AllowSpectators = true);

    // Request DTO to end a session. HostId required for authorization.
    public record EndSessionRequest(string HostId);

    // Request DTO for handling game with player list
    public record HandleGameRequest(
        string Result, 
        string HostId, 
        Dictionary<string, object> Players);
}