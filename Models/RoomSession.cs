using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace SessionApp.Models
{
    public class RoomSession
    {
        public string Code { get; init; } = null!;
        public string HostId { get; init; } = null!;
        public DateTime CreatedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; set; }

        // New: session-level settings that can be changed at runtime.
        // These settings are intentionally mutable and simple; validation is performed by the controller.
        public RoomSettings Settings { get; set; } = new RoomSettings();

        // Participants keyed by participant id
        public ConcurrentDictionary<string, Participant> Participants { get; } = new();

        // Tournament/game state
        // Once a game is started these will be populated and IsGameStarted will be true.
        public bool IsGameStarted { get; set; }

        // If a single winner is reported for the entire session previously — kept for compatibility but
        // per-group results are stored in Groups.
        public bool IsGameEnded { get; set; }

        public int CurrentRound { get; set; } = 0;

        // If a single winner is stored previously — kept for compatibility; per-group winners are in Groups.
        public string? WinnerParticipantId { get; set; }

        // Groups are represented as an ordered, read-only list.
        // Each Group holds its participants (keyed by participant id) and per-group state such as round number and result.
        public IReadOnlyList<Group>? Groups { get; set; }

        // Archived groups for completed rounds. Each entry is a read-only list representing the groups of a past round.
        // The list is ordered by round (older rounds first).
        public List<IReadOnlyList<Group>> ArchivedRounds { get; } = new();

        public bool IsExpiredUtc() => DateTime.UtcNow >= ExpiresAtUtc;
    }

    /// <summary>
    /// Mutable per-room settings that clients/host can change.
    /// Keep small and explicit so changes are easy to reason about.
    /// </summary>
    public class RoomSettings
    {
        //To Do add settings
        //Allow users to join after game has started
        //
        ///// </summary>
        public bool AllowJoinAfterStart { get; set; } = true;
        public bool PrioitizeWinners { get; set; } = true;
        public bool AllowGroupOfThree { get; set; } = true;
        public bool FurtherReduceOddsOfGroupOfThree { get; set; } = false;
        public bool AllowGroupOfFive { get; set; } = false;
        public TimeSpan RoundLength { get; set; } = TimeSpan.FromMinutes(90);


        /// <summary>
        /// Maximum group size used when partitioning participants. Supported values: 3 or 4.
        /// Default is 4.
        /// </summary>
        //public int MaxGroupSize { get; set; } = 4;

        ///// <summary>
        ///// If true, spectators (non-playing observers) are permitted.
        ///// Semantic handling of spectators is left to client/server logic.
        ///// Default is true.
        ///// </summary>
        //public bool AllowSpectators { get; set; } = true;
    }

    public class Participant
    {
        public string Id { get; init; } = null!;
        public string Name { get; init; } = null!;
        public DateTime JoinedAtUtc { get; init; } = DateTime.UtcNow;
        public string Commander { get; set; } = string.Empty;
        public int Points { get; set; } = 0;
    }

    // Represents a single group (up to 4 participants) and its per-round state.
    public class Group
    {
        public int GroupNumber { get; set; }
        
        // Use a List to preserve order + dictionary for fast lookup
        private List<Participant> _participantsList = new();
        private ConcurrentDictionary<string, Participant> _participantsDict = new();
        
        public IReadOnlyList<Participant> ParticipantsOrdered => _participantsList.AsReadOnly();
        public ConcurrentDictionary<string, Participant> Participants => _participantsDict;

        public void AddParticipant(Participant participant)
        {
            if (_participantsDict.TryAdd(participant.Id, participant))
            {
                _participantsList.Add(participant);
            }
        }

        public bool RemoveParticipant(string participantId)
        {
            if (_participantsDict.TryRemove(participantId, out var participant))
            {
                _participantsList.Remove(participant);
                return true;
            }
            return false;
        }

        // Round number this group belongs to
        public int RoundNumber { get; set; }

        // If true the group was declared a draw.
        public bool IsDraw { get; set; }

        // If non-null the participant id of the winner for this group.
        public string? WinnerParticipantId { get; set; }

        // Indicates whether the round has been started
        public bool RoundStarted { get; set; }

        // Fixed statistics for queryability
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public int TurnCount { get; set; } = -1;
        // Custom/flexible statistics stored as dictionary (will be serialized to JSON)
        public Dictionary<string, object> Statistics { get; set; } = new Dictionary<string, object>();

        // Convenience
        public bool HasResult => IsDraw || !string.IsNullOrEmpty(WinnerParticipantId);

        /// <summary>
        /// Gets statistics as JSON string for database storage
        /// </summary>
        public string GetStatisticsJson()
        {
            return Statistics.Count > 0 
                ? JsonSerializer.Serialize(Statistics) 
                : "{}";
        }

        /// <summary>
        /// Sets statistics from JSON string (for loading from database)
        /// </summary>
        public void SetStatisticsFromJson(string json)
        {
            if (!string.IsNullOrWhiteSpace(json) && json != "{}")
            {
                try
                {
                    var deserialized = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    if (deserialized != null)
                    {
                        Statistics = deserialized;
                    }
                }
                catch
                {
                    Statistics = new Dictionary<string, object>();
                }
            }
        }
    }
}