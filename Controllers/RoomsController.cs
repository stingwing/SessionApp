using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SessionApp.Hubs;
using SessionApp.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

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
            if (!ok) return NotFound(new { message = "Room not found or expired or user exists" });//change how errors work

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

        // New GET endpoint that starts the game for a room and notifies connected clients.
        // Note: this endpoint performs a state change (starts the game) and therefore returns 404 when it cannot start.
        [HttpGet("{code}/start")]
        public async Task<IActionResult> StartGame(string code)
        {
            var groups = _roomService.StartGame(code);
            if (groups is null)
            {
                return NotFound(new { message = "Room not found, expired, already started, or has no participants" });
            }

            var payload = new
            {
                RoomCode = code.ToUpperInvariant(),
                Groups = groups.Select(g => g.Select(p => new { p.Id, p.Name, p.JoinedAtUtc }).ToArray()).ToArray()
            };

            // Broadcast the GameStarted event to clients in the room group
            await _hubContext.Clients.Group(code.ToUpperInvariant()).SendAsync("GameStarted", payload);

            return Ok(payload);
        }

        // GET api/rooms/{code}/group/{participantId}
        // Returns which group the specified participant is in after the game has started.
        [HttpGet("{code}/group/{participantId}")]
        public IActionResult GetParticipantGroup(string code, string participantId )
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(participantId))
                return BadRequest(new { message = "code and participantId are required" });

            var session = _roomService.GetSession(code);
            if (session is null)
                return NotFound(new { message = "Room not found or expired" });

            if (!session.IsGameStarted || session.Groups is null)
                return NotFound(new { message = "Game has not been started for this room" });

            // Find the group that contains the participant
            var upperParticipantId = participantId;
            for (int i = 0; i < session.Groups.Count; i++)
            {
                var group = session.Groups[i];
                if (group.Any(p => string.Equals(p.Id, upperParticipantId, StringComparison.Ordinal)))
                {
                    var members = group.Select(p => new { p.Id, p.Name, p.JoinedAtUtc }).ToArray();
                    var result = new
                    {
                        RoomCode = session.Code,
                        ParticipantId = participantId,
                        GroupIndex = i, // zero-based
                        GroupNumber = i + 1, // one-based
                        Members = members
                    };
                    return Ok(result);
                }
            }

            return NotFound(new { message = "Participant not found in any group for this room" });
        }
    }

    public record CreateRoomRequest(string HostId, int CodeLength = 6, TimeSpan? Ttl = null);
    public record CreateRoomResponse(string Code, DateTime ExpiresAtUtc);
    public record JoinRoomRequest(string ParticipantId, string ParticipantName);

    // DTO used by GetRoomResponse to expose participant details
    public record RoomParticipant(string Id, string Name, DateTime JoinedAtUtc);

    // Updated GetRoomResponse now includes the participants array
    public record GetRoomResponse(string Code, string HostId, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, int ParticipantCount, RoomParticipant[] Participants);
}