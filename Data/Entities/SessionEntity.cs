using System;
using System.Collections.Generic;

namespace SessionApp.Data.Entities
{
    public class SessionEntity
    {
        public string Code { get; set; } = null!;
        public string EventName { get; set; } = string.Empty;
        public string HostId { get; set; } = null!;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public bool IsGameStarted { get; set; }
        public bool IsGameEnded { get; set; }
        public bool Archived { get; set; } = false;
        public int CurrentRound { get; set; }
        public string? WinnerParticipantId { get; set; }
        // Settings stored as JSON
        public string SettingsJson { get; set; } = "{}";
        // Navigation properties
        public ICollection<ParticipantEntity> Participants { get; set; } = new List<ParticipantEntity>();
        public ICollection<GroupEntity> Groups { get; set; } = new List<GroupEntity>();
        public ICollection<ArchivedRoundEntity> ArchivedRounds { get; set; } = new List<ArchivedRoundEntity>();
    }

    public class ParticipantEntity
    {
        public Guid Id { get; set; }
        public string SessionCode { get; set; } = null!;
        public string ParticipantId { get; set; } = null!;
        public string Name { get; set; } = null!;
        public DateTime JoinedAtUtc { get; set; }
        public SessionEntity Session { get; set; } = null!;
        public string Commander { get; set; } = string.Empty;
        public int Points { get; set; } = 0;
        public bool Dropped { get; set; } = false;
        public Guid InCustomGroup { get; set; } = Guid.Empty;
    }

    public class GroupEntity
    {
        public Guid Id { get; set; }
        public string SessionCode { get; set; } = null!;
        public int GroupNumber { get; set; }
        public int RoundNumber { get; set; }
        public bool IsDraw { get; set; }
        public bool HasResult { get; set; }
        public string? WinnerParticipantId { get; set; }
        public bool IsArchived { get; set; }
        public Guid? ArchivedRoundId { get; set; }
        
        // Common statistics (fixed columns for queryability)
        public bool RoundStarted { get; set; } = false;
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        
        // Custom/flexible statistics (JSON for extensibility)
        public string StatisticsJson { get; set; } = "{}";
        
        public SessionEntity Session { get; set; } = null!;
        public ArchivedRoundEntity? ArchivedRound { get; set; }
        public ICollection<GroupParticipantEntity> GroupParticipants { get; set; } = new List<GroupParticipantEntity>();
    }

    public class GroupParticipantEntity
    {
        public Guid Id { get; set; }
        public Guid GroupId { get; set; }
        public string ParticipantId { get; set; } = null!;
        public string Name { get; set; } = null!;
        public DateTime JoinedAtUtc { get; set; }
        public int Order { get; set; } = 0;  
        public GroupEntity Group { get; set; } = null!;
        public bool AutoFill { get; set; } = false;
    }

    public class ArchivedRoundEntity
    {
        public Guid Id { get; set; }
        public string SessionCode { get; set; } = null!;
        public int RoundNumber { get; set; }       
        // Common statistics
        public DateTime? CompletedAtUtc { get; set; }
        public string Commander { get; set; } = string.Empty;
        public int TurnCount { get; set; } = -1;
        // Custom/flexible statistics
        public string StatisticsJson { get; set; } = "{}";
        public SessionEntity Session { get; set; } = null!;
        public ICollection<GroupEntity> Groups { get; set; } = new List<GroupEntity>();
    }
}