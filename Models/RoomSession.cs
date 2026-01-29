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

        // If a single winner is reported for the entire session previously — kept for compatibility but
        // per-group results are stored in GroupStates.
        public bool IsGameEnded { get; set; }

        // If a single winner is stored previously — kept for compatibility; per-group winners are in GroupStates.
        public string? WinnerParticipantId { get; set; }

        // Groups are represented as an ordered, read-only list of read-only lists.
        // Each inner list is a group (up to 4 participants). The last group may contain fewer than 4 participants.
        public IReadOnlyList<IReadOnlyList<Participant>>? Groups { get; set; }

        // Per-group state: stores whether a group has a win/draw and (if win) the winner participant id.
        // The collection length matches Groups (one state entry per group).
        public IReadOnlyList<GroupState>? GroupStates { get; set; }

        public bool IsExpiredUtc() => DateTime.UtcNow >= ExpiresAtUtc;
    }

    public class Participant
    {
        public string Id { get; init; } = null!;
        public string Name { get; init; } = null!;
        public DateTime JoinedAtUtc { get; init; } = DateTime.UtcNow;
    }

    // Represents the outcome state for a single group in a session/round.
    public class GroupState
    {
        // If true the group was declared a draw.
        public bool IsDraw { get; set; }

        // If non-null the participant id of the winner for this group.
        public string? WinnerParticipantId { get; set; }

        // Convenience
        public bool HasResult => IsDraw || !string.IsNullOrEmpty(WinnerParticipantId);
    }
}