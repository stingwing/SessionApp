using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SessionApp.Hubs;
using SessionApp.Services;
using System;
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
            if (!ok) return NotFound(new { message = "Room not found or expired" });

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
            return Ok(new GetRoomResponse(session.Code, session.HostId, session.CreatedAtUtc, session.ExpiresAtUtc, session.Participants.Count));
        }
    }

    public record CreateRoomRequest(string HostId, int CodeLength = 6, TimeSpan? Ttl = null);
    public record CreateRoomResponse(string Code, DateTime ExpiresAtUtc);
    public record JoinRoomRequest(string ParticipantId, string ParticipantName);
    public record GetRoomResponse(string Code, string HostId, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, int ParticipantCount);
}