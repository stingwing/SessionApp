using System;
using System.Collections.Concurrent;

namespace SessionApp.Models
{
    public class RoomSession
    {
        public string Code { get; init; } = null!;
        public string HostId { get; init; } = null!;
        public DateTime CreatedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; set; }

        public ConcurrentDictionary<string, Participant> Participants { get; } = new();

        public bool IsExpiredUtc() => DateTime.UtcNow >= ExpiresAtUtc;
    }

    public class Participant
    {
        public string Id { get; init; } = null!;
        public string Name { get; init; } = null!;
        public DateTime JoinedAtUtc { get; init; } = DateTime.UtcNow;
    }
}