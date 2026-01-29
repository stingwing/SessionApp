using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SessionApp.Hubs;
using SessionApp.Models;
using SessionApp.Services;
using System;
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
            var session = _roomService.CreateSession(request.HostId, request.CodeLength, request.Ttl ?? TimeSpan.FromHours(2));

            // Notify connected clients (optional: clients may join groups by room code)
            await _hubContext.Clients.Group(session.Code).SendAsync("RoomCreated", new { session.Code, session.HostId, session.ExpiresAtUtc });

            return CreatedAtAction(nameof(Get), new { code = session.Code }, new CreateRoomResponse(session.Code, session.ExpiresAtUtc));
        }

        [HttpPost("{code}/join")]
        public async Task<IActionResult> Join(string code, [FromBody] JoinRoomRequest request)
        {
            var ok = _roomService.TryJoin(code, request.ParticipantId, request.ParticipantName);
            if (!ok.Contains("Success")) 
                return NotFound(new { message = ok });

            // Broadcast participant joined to clients in the room group
            await _hubContext.Clients.Group(code.ToUpperInvariant())
                .SendAsync("ParticipantJoined", new { ParticipantId = request.ParticipantId, ParticipantName = request.ParticipantName, RoomCode = code.ToUpperInvariant() });

            return Ok();
        }

        [HttpGet("{code}")]
        public IActionResult Get(string code)
        {
            var session = _roomService.GetSession(code);
            if (session is null) return NotFound();

            var participants = session.Participants.Values
                .Select(p => new RoomParticipant(p.Id, p.Name, p.JoinedAtUtc))
                .ToArray();

            return Ok(new GetRoomResponse(session.Code, session.HostId, session.CreatedAtUtc, session.ExpiresAtUtc, session.Participants.Count, participants));
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

            var session = _roomService.GetSession(code);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            // Simple authorization: only the host may change settings
            if (!string.Equals(session.HostId, request.HostId, StringComparison.Ordinal))
                return Forbid();

            lock (session)
            {
                // Add settings here
            }

            var payload = new
            {
                RoomCode = session.Code,
                Settings = session.Settings
            };

            // Notify connected clients in the room that settings changed
            await _hubContext.Clients.Group(code.ToUpperInvariant()).SendAsync("SettingsChanged", payload);

            return Ok(session.Settings);
        }

        // New GET endpoint that starts the game for a room and notifies connected clients.
        // Note: this endpoint performs a state change (starts the game) and therefore returns 404 when it cannot start.
        [HttpGet("{code}/start")]
        public async Task<IActionResult> StartGame(string code)
        {
            var errorMessage = string.Empty;
            var groups = _roomService.StartGame(code, ref errorMessage);

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
                Groups = groups.Select(g => g.Participants.Values.Select(p => new { p.Id, p.Name, p.JoinedAtUtc }).ToArray()).ToArray(),
            };

            // Broadcast the GameStarted event to clients in the room group
            await _hubContext.Clients.Group(code.ToUpperInvariant()).SendAsync("GameStarted", payload);

            return Ok(payload);
        }

        // POST api/rooms/{code}/round/start
        // Start a new round for an already-started session (re-shuffles remaining participants and re-partitions groups).
        [HttpPost("{code}/round/start")]
        public async Task<IActionResult> StartNewRound(string code)
        {   
            var errorMessage = string.Empty;

            var groups = _roomService.StartNewRound(code, ref errorMessage);
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
                Groups = groups.Select(g => g.Participants.Values.Select(p => new { p.Id, p.Name, p.JoinedAtUtc }).ToArray()).ToArray()
            };

            // Broadcast the RoundStarted event to clients in the room group
            await _hubContext.Clients.Group(code.ToUpperInvariant()).SendAsync("RoundStarted", payload);

            return Ok(payload);
        }

        // GET api/rooms/{code}/group/{participantId}
        // Returns which group the specified participant is in after the game has started.
        [HttpGet("{code}/group/{participantId}")]
        public IActionResult GetParticipantGroup(string code, string participantId )
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(participantId))
                return BadRequest(new { message = "code and participantId are required" });

            var session = _room_service_snapshot(code);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            if (!session.IsGameStarted || session.Groups is null)
                return NotFound(new { message = "Game has not been started for this room" });

            // Find the group that contains the participant
            foreach (var group in session.Groups)
            {
                if (group.Participants.ContainsKey(participantId))
                {
                    var members = group.Participants.Values.Select(p => new { p.Id, p.Name, p.JoinedAtUtc }).ToArray();
                    var result = new
                    {
                        RoomCode = session.Code,
                        ParticipantId = participantId,
                        GroupNumber = group.GroupNumber,
                        Members = members,
                        Round = group.RoundNumber,
                        Result = group.HasResult,
                        Winner = group.WinnerParticipantId,
                        Draw = group.IsDraw,
                    };
                    return Ok(result);
                }
            }

            return NotFound(new { message = "Participant not found in any group for this room" });
        }

        // GET api/rooms/{code}/archived
        // Returns all archived rounds (older rounds first) with their groups and member details.
        [HttpGet("{code}/archived")]
        public IActionResult GetArchivedRounds(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return BadRequest(new { message = "code is required" });

            var session = _room_service_snapshot(code);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            var archived = session.ArchivedRounds;
            var rounds = archived.Select(roundGroups =>
            {
                var roundNumber = roundGroups.FirstOrDefault()?.RoundNumber ?? -1;
                var groups = roundGroups.Select(g => new
                {
                    GroupNumber = g.GroupNumber,
                    Round = g.RoundNumber,
                    Members = g.Participants.Values.Select(p => new { p.Id, p.Name, p.JoinedAtUtc }).ToArray(),
                    Result = g.HasResult,
                    Winner = g.WinnerParticipantId,
                    Draw = g.IsDraw
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
        [HttpPost("{code}/report")]
        public async Task<IActionResult> ReportOutcome(string code, [FromBody] ReportOutcomeRequest request)
        {
            if (string.IsNullOrWhiteSpace(code) || request is null || string.IsNullOrWhiteSpace(request.ParticipantId) || string.IsNullOrWhiteSpace(request.Result))
                return BadRequest(new { message = "code, participantId and result are required" });

            // parse result
            var normalized = request.Result.Trim().ToLowerInvariant();
            if (normalized != "win" && normalized != "draw" && normalized != "drop")
                return BadRequest(new { message = "result must be one of: win, draw, drop" });

            var outcome = normalized == "win"
                ? ReportOutcomeType.Win
                : normalized == "draw"
                    ? ReportOutcomeType.Draw
                    : ReportOutcomeType.DropOut;
                
            var serviceResult = _roomService.ReportOutcome(code, request.ParticipantId, outcome, out var winnerId, out var removedParticipant, out var groupIndex);

            return serviceResult switch
            {
                ReportOutcomeResult.RoomNotFound => NotFound(new { message = "Room not found or expired" }),
                ReportOutcomeResult.NotStarted => BadRequest(new { message = "Game has not been started for this room" }),
                ReportOutcomeResult.ParticipantNotFound => NotFound(new { message = "Participant not found in room or not in current round" }),
                ReportOutcomeResult.AlreadyEnded => BadRequest(new { message = "This group already has a result" }),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.DropOut => await HandleDropoutBroadcast(code, removedParticipant),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.Win => await HandleGroupEndedBroadcast(code, "win", winnerId, groupIndex),
                ReportOutcomeResult.Success when outcome == ReportOutcomeType.Draw => await HandleGroupEndedBroadcast(code, "draw", null, groupIndex),
                _ => StatusCode(500, new { message = "Unknown error reporting outcome" })
            };
        }

        // POST api/rooms/{code}/end
        // Host calls this to end the current session: archives the current groups (current round),
        // marks the session ended and broadcasts a SessionEnded event with per-group results.
        [HttpPost("{code}/end")]
        public async Task<IActionResult> EndSession(string code, [FromBody] EndSessionRequest request)
        {
            if (string.IsNullOrWhiteSpace(code) || request is null)
                return BadRequest(new { message = "code and request body are required" });

            if (string.IsNullOrWhiteSpace(request.HostId))
                return BadRequest(new { message = "HostId is required to end session" });

            var session = _roomService.GetSession(code);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            // Only host may end the session
            if (!string.Equals(session.HostId, request.HostId, StringComparison.Ordinal))
                return Forbid();

            lock (session)
            {
                if (!session.IsGameStarted || session.Groups is null)
                    return BadRequest(new { message = "Game has not been started for this room" });

                var snapshot = _roomService.SnapshotGroups(session.Groups);
                session.ArchivedRounds.Add(System.Array.AsReadOnly(snapshot.ToArray()));

                // Mark session ended
                session.IsGameEnded = true;
            }

            // Prepare result payload to broadcast/return
            var resultGroups = session.Groups.Select(g => new
            {
                GroupNumber = g.GroupNumber,
                Round = g.RoundNumber,
                Members = g.Participants.Values.Select(p => new { p.Id, p.Name, p.JoinedAtUtc }).ToArray(),
                Result = g.HasResult,
                Winner = g.WinnerParticipantId,
                Draw = g.IsDraw
            }).ToArray();

            var payload = new
            {
                RoomCode = session.Code,
                Round = session.CurrentRound,
                Groups = resultGroups,
                ArchivedRoundsCount = session.ArchivedRounds.Count
            };

            // Broadcast final session results
            await _hubContext.Clients.Group(code.ToUpperInvariant()).SendAsync("SessionEnded", payload);

            // Optionally remove session from memory if requested
            if (request.Invalidate)
            {
                _roomService.InvalidateSession(code);
                // also broadcast that room was removed
                await _hubContext.Clients.Group(code.ToUpperInvariant()).SendAsync("RoomInvalidated", new { RoomCode = code.ToUpperInvariant() });
            }

            return Ok(payload);
        }

        private RoomSession? _room_service_snapshot(string code) => _roomService.GetSession(code);

        private async Task<IActionResult> HandleGroupEndedBroadcast(string code, string result, string? winnerId, int? groupIndex)
        {
            var session = _roomService.GetSession(code);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            if (!groupIndex.HasValue || session.Groups is null || groupIndex < 0 || groupIndex >= session.Groups.Count)
                return BadRequest(new { message = "Invalid group index" });

            var members = session.Groups[groupIndex.Value].Participants.Values
                .Select(p => new { p.Id, p.Name, p.JoinedAtUtc })
                .ToArray();

            var payload = new
            {
                RoomCode = code.ToUpperInvariant(),
                GroupNumber = groupIndex.Value + 1,
                Result = result,
                WinnerParticipantId = winnerId,
                Members = members
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
    public record JoinRoomRequest(string ParticipantId, string ParticipantName);
    public record ReportOutcomeRequest(string ParticipantId, string Result);

    // DTO used by GetRoomResponse to expose participant details
    public record RoomParticipant(string Id, string Name, DateTime JoinedAtUtc);

    // Updated GetRoomResponse now includes the participants array
    public record GetRoomResponse(string Code, string HostId, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, int ParticipantCount, RoomParticipant[] Participants);

    // Request DTO for updating room settings.
    public record UpdateRoomSettingsRequest(string HostId, int MaxGroupSize = 4, bool AllowJoinAfterStart = false, bool AllowSpectators = true);

    // Request DTO to end a session. HostId required for authorization.
    public record EndSessionRequest(string HostId, bool Invalidate = false);
}