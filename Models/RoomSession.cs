using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SessionApp.Models
{
    public class RoomSession
    {
        public string Code { get; init; } = null!;
        public string HostId { get; init; } = null!;
        public DateTime CreatedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; set; }

        // Participants keyed by participant id
        public ConcurrentDictionary<string, Participant> Participants { get; } = new();

        // Tournament/game state
        // Once a game is started these will be populated and IsGameStarted will be true.
        public bool IsGameStarted { get; set; }

        // Groups are represented as an ordered, read-only list of read-only lists.
        // Each inner list is a group (up to 4 participants). The last group may contain fewer than 4 participants.
        public IReadOnlyList<IReadOnlyList<Participant>>? Groups { get; set; }

        public bool IsExpiredUtc() => DateTime.UtcNow >= ExpiresAtUtc;
    }

    public class Participant
    {
        public string Id { get; init; } = null!;
        public string Name { get; init; } = null!;
        public DateTime JoinedAtUtc { get; init; } = DateTime.UtcNow;
    }
}