using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace SessionApp.Hubs
{
    public class RoomsHub : Hub
    {
        // Clients call this to join a room group to receive updates for that room
        public Task JoinRoomGroup(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode))
                return Task.CompletedTask;

            return Groups.AddToGroupAsync(Context.ConnectionId, roomCode.ToUpperInvariant());
        }

        // Clients call this to leave room group
        public Task LeaveRoomGroup(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode))
                return Task.CompletedTask;

            return Groups.RemoveFromGroupAsync(Context.ConnectionId, roomCode.ToUpperInvariant());
        }
    }
}