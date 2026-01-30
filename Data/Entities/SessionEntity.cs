using System;
using System.Collections.Generic;

namespace SessionApp.Data.Entities
{
    public class SessionEntity
    {
        public string Code { get; set; } = null!;
        public string HostId { get; set; } = null!;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public bool IsGameStarted { get; set; }
        public bool IsGameEnded { get; set; }
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

        public GroupEntity Group { get; set; } = null!;
    }

    public class ArchivedRoundEntity
    {
        public Guid Id { get; set; }
        public string SessionCode { get; set; } = null!;
        public int RoundNumber { get; set; }

        public SessionEntity Session { get; set; } = null!;
        public ICollection<GroupEntity> Groups { get; set; } = new List<GroupEntity>();
    }
}