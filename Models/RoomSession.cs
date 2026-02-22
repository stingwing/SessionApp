using Microsoft.AspNetCore.Connections;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionApp.Models
{
    public enum GameState
    {
        Invalid,
        Open,
        GameCreated,
        GameStarted,
        RoundStarted,
        RoundEnded,
        GameEnded,
        Archived,
    }

    public class RoomSession
    {
        public string Code { get; init; } = null!;
        public string HostId { get; init; } = null!;
        public DateTime CreatedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime LastModifiedAtUtc { get; set; } // Add this property

        // New: session-level settings that can be changed at runtime.
        // These settings are intentionally mutable and simple; validation is performed by the controller.
        public RoomSettings Settings { get; set; } = new RoomSettings();

        // Participants keyed by participant id
        public ConcurrentDictionary<string, Participant> Participants { get; } = new();

        public GameState GameState { get; set; } = GameState.Invalid;
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
        public bool Archived { get; set; } = false;
        public string EventName { get; set; } = string.Empty;

        public bool IsExpiredUtc() => DateTime.UtcNow >= ExpiresAtUtc;

        /// <summary>
        /// Checks if any group in the current round has been started.
        /// </summary>
        /// <returns>True if at least one group has RoundStarted set to true; otherwise false.</returns>
        public bool HasAnyRoundStarted()
        {
            return Groups?.Any(g => g.RoundStarted) ?? false;
        }
    }

    /// <summary>
    /// Mutable per-room settings that clients/host can change.
    /// Keep small and explicit so changes are easy to reason about.
    /// </summary>
    public class RoomSettings
    {
        ///// </summary>
        public bool AllowJoinAfterStart { get; set; } = true;
        public bool PrioitizeWinners { get; set; } = true;
        public bool AllowGroupOfThree { get; set; } = true;
        public bool AllowGroupOfFive { get; set; } = false;
        public bool FurtherReduceOddsOfGroupOfThree { get; set; } = false;
        public int RoundLength { get; set; } = 90;
        public bool UsePoints { get; set; } = false;
        public int PointsForWin { get; set; } = 1;
        public int PointsForDraw { get; set; } = 0;
        public int PointsForLoss { get; set; } = 0;
        public int PointsForABye { get; set; } = 1;
        public bool AllowCustomGroups { get; set; } = true;
        public bool AllowPlayersToCreateCustomGroups { get; set; } = true;
        public bool TournamentMode { get; set; } = false;
        public int MaxRounds { get; set; } = 10000;
        public int MaxGroupSize { get; set; } = 4;
    }

    public class Participant
    {
        public string Id { get; init; } = null!;
        public string Name { get; init; } = null!;
        public DateTime JoinedAtUtc { get; init; } = DateTime.UtcNow;
        public string Commander { get; set; } = string.Empty;
        public int Points { get; set; } = 0;
        public bool Dropped { get; set; } = false;
        public int Order { get; set; } = 0;
        public Guid InCustomGroup { get; set; } = Guid.Empty;
        public bool AutoFill { get; set; } = false;
    }

    // Represents a single group (up to 4 participants) and its per-round state.
    public class Group
    {
        public int GroupNumber { get; set; }
        
        // Dictionary for fast lookup - order is maintained via Participant.Order property
        private ConcurrentDictionary<string, Participant> _participantsDict = new();
        
        public ConcurrentDictionary<string, Participant> Participants => _participantsDict;

        public void AddParticipant(Participant participant)
        {
            if (_participantsDict.TryAdd(participant.Id, participant))
            {
                // Set order based on current count
                participant.Order = _participantsDict.Count - 1;
            }
        }

        public bool RemoveParticipant(string participantId)
        {
            return _participantsDict.TryRemove(participantId, out _);
        }

        // Round number this group belongs to
        public int RoundNumber { get; set; }

        // If true the group was declared a draw.
        public bool IsDraw { get; set; }

        // If non-null the participant id of the winner for this group.
        public string? WinnerParticipantId { get; set; }

        // Indicates whether the round has been started
        public bool RoundStarted { get; set; } = false;
        
        // Indicates if this is a custom group created by the host
        public bool IsCustom { get; set; } = false;
        
        // If true and IsCustom is true, this group of 3 can be auto-filled to 4 during randomization
        public bool AutoFill { get; set; } = false;
        
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