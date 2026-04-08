using Microsoft.EntityFrameworkCore;
using SessionApp.Data.Entities;
using SessionApp.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace SessionApp.Data
{
    public class SessionRepository
    {
        private readonly SessionDbContext _context;

        public SessionRepository(SessionDbContext context)
        {
            _context = context;
        }

        public async Task<bool> SaveSessionAsync(RoomSession session)
        {
            var entity = await _context.Sessions.FindAsync(session.Code);

            if (entity == null)
            {
                entity = new SessionEntity
                {
                    Code = session.Code,
                    EventName = session.EventName,
                    HostId = session.HostId,
                    HostUserId = session.HostUserId,  // Save the optional host UserId
                    CreatedAtUtc = session.CreatedAtUtc,
                    ExpiresAtUtc = session.ExpiresAtUtc,
                    IsGameStarted = session.IsGameStarted,
                    IsGameEnded = session.IsGameEnded,
                    Archived = session.Archived,
                    CurrentRound = session.CurrentRound,
                    WinnerParticipantId = session.WinnerParticipantId,
                    SettingsJson = JsonSerializer.Serialize(session.Settings)
                };
                _context.Sessions.Add(entity);
            }
            else
            {
                entity.EventName = session.EventName;
                entity.HostUserId = session.HostUserId;  // Update the optional host UserId
                entity.ExpiresAtUtc = session.ExpiresAtUtc;
                entity.IsGameStarted = session.IsGameStarted;
                entity.IsGameEnded = session.IsGameEnded;
                entity.Archived = session.Archived;
                entity.CurrentRound = session.CurrentRound;
                entity.WinnerParticipantId = session.WinnerParticipantId;
                entity.SettingsJson = JsonSerializer.Serialize(session.Settings);
            }

            // Update participants
            var existingParticipants = await _context.Participants
                .Where(p => p.SessionCode == session.Code)
                .ToListAsync();

            var participantIds = session.Participants.Keys.ToHashSet();
            
            // Remove participants that are no longer in the session
            var toRemove = existingParticipants.Where(p => !participantIds.Contains(p.ParticipantId)).ToList();
            _context.Participants.RemoveRange(toRemove);

            // Add or update participants
            foreach (var participant in session.Participants.Values)
            {
                var existing = existingParticipants.FirstOrDefault(p => p.ParticipantId == participant.Id);
                if (existing == null)
                {
                    _context.Participants.Add(new ParticipantEntity
                    {
                        SessionCode = session.Code,
                        ParticipantId = participant.Id,
                        Name = participant.Name,
                        Commander = participant.Commander,
                        Points = participant.Points,
                        JoinedAtUtc = participant.JoinedAtUtc,
                        Dropped = participant.Dropped,
                        InCustomGroup = participant.InCustomGroup,
                        UserId = participant.UserId  // Save the optional participant UserId
                    });
                }
                else
                {
                    // Update existing participant fields
                    existing.Name = participant.Name;
                    existing.Commander = participant.Commander;
                    existing.Points = participant.Points;
                    existing.Dropped = participant.Dropped;
                    existing.InCustomGroup = participant.InCustomGroup;
                    existing.UserId = participant.UserId;  // Update the optional participant UserId
                }
            }

            // Update groups if game started
            if (session.Groups != null)
            {
                await SaveGroupsAsync(session.Code, session.Groups, isArchived: false);
            }

            // Update archived rounds
            await SaveArchivedRoundsAsync(session);

            await _context.SaveChangesAsync();
            return true;
        }

        private async Task SaveGroupsAsync(string sessionCode, IReadOnlyList<Group> groups, bool isArchived, Guid? archivedRoundId = null)
        {
            if (!isArchived)
            {
                var existingGroups = await _context.Groups
                    .Where(g => g.SessionCode == sessionCode && !g.IsArchived)
                    .Include(g => g.GroupParticipants)
                    .ToListAsync();

                foreach (var existingGroup in existingGroups)
                {
                    _context.GroupParticipants.RemoveRange(existingGroup.GroupParticipants);
                }

                _context.Groups.RemoveRange(existingGroups);
            }

            // Add all groups first
            var groupEntities = new List<GroupEntity>();
            foreach (var group in groups)
            {
                var groupEntity = new GroupEntity
                {
                    SessionCode = sessionCode,
                    GroupNumber = group.GroupNumber,
                    RoundNumber = group.RoundNumber,
                    IsDraw = group.IsDraw,
                    HasResult = group.HasResult,
                    WinnerParticipantId = group.WinnerParticipantId,
                    IsArchived = isArchived,
                    ArchivedRoundId = archivedRoundId,
                    StartedAtUtc = group.StartedAtUtc,
                    CompletedAtUtc = group.CompletedAtUtc,
                    StatisticsJson = group.GetStatisticsJson(),
                    RoundStarted = group.RoundStarted
                };

                _context.Groups.Add(groupEntity);
                groupEntities.Add(groupEntity);
            }

            // Save once to get IDs for all groups
            await _context.SaveChangesAsync();

            // Now add all group participants
            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                var groupEntity = groupEntities[i];

                // Order by Participant.Order property
                foreach (var participant in group.Participants.Values.OrderBy(p => p.Order))
                {
                    _context.GroupParticipants.Add(new GroupParticipantEntity
                    {
                        GroupId = groupEntity.Id,
                        ParticipantId = participant.Id,
                        Name = participant.Name,
                        JoinedAtUtc = participant.JoinedAtUtc,
                        Order = participant.Order,
                        AutoFill = participant.AutoFill,
                        Commander = participant.Commander
                    });
                }
            }

            // Final save for all participants
            await _context.SaveChangesAsync();
        }

        private async Task SaveArchivedRoundsAsync(RoomSession session)
        {
            var existingArchived = await _context.ArchivedRounds
                .Where(a => a.SessionCode == session.Code)
                .ToListAsync();

            var existingRoundNumbers = existingArchived.Select(a => a.RoundNumber).ToHashSet();

            foreach (var archivedRound in session.ArchivedRounds)
            {
                var roundNumber = archivedRound.FirstOrDefault()?.RoundNumber ?? -1;
                if (roundNumber == -1 || existingRoundNumbers.Contains(roundNumber))
                    continue;

                // Calculate round completion time (use the latest group completion time)
                var completedAtUtc = archivedRound
                    .Where(g => g.CompletedAtUtc.HasValue)
                    .Max(g => g.CompletedAtUtc);

                // Extract statistics from groups (e.g., commander, turn count)
                var roundStatistics = new Dictionary<string, object>();
                var commanders = new List<string>();
                var turnCounts = new List<int>();

                foreach (var group in archivedRound)
                {
                    if (group.Statistics.TryGetValue("commander", out var commander))
                        commanders.Add(commander?.ToString() ?? "");
                    
                    if (group.Statistics.TryGetValue("turnCount", out var turnCount))
                    {
                        if (turnCount is int tc)
                            turnCounts.Add(tc);
                    }
                }

                // Store aggregated statistics
                var mostCommonCommander = commanders.GroupBy(c => c)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()?.Key ?? "";

                var averageTurnCount = turnCounts.Any() ? (int)turnCounts.Average() : -1;

                roundStatistics["commanders"] = commanders;
                roundStatistics["averageTurnCount"] = averageTurnCount;

                var archivedEntity = new ArchivedRoundEntity
                {
                    SessionCode = session.Code,
                    RoundNumber = roundNumber,
                    CompletedAtUtc = completedAtUtc,
                    Commander = mostCommonCommander,
                    TurnCount = averageTurnCount,
                    StatisticsJson = JsonSerializer.Serialize(roundStatistics)
                };
                _context.ArchivedRounds.Add(archivedEntity);
                await _context.SaveChangesAsync(); // Get ID

                await SaveGroupsAsync(session.Code, archivedRound, isArchived: true, archivedRoundId: archivedEntity.Id);
            }
        }

        public async Task<List<RoomSession>> GetAllActiveSessionsAsync()
        {
            var entities = await _context.Sessions
                .Where(s => s.Archived == false)
                .Include(s => s.Participants)
                .Include(s => s.Groups.Where(g => !g.IsArchived))
                    .ThenInclude(g => g.GroupParticipants)
                .Include(s => s.ArchivedRounds)
                    .ThenInclude(a => a.Groups)
                        .ThenInclude(g => g.GroupParticipants)
                .AsSplitQuery()
                .ToListAsync();

            var sessions = new List<RoomSession>();
            foreach (var entity in entities)
            {
                // Map directly from already-loaded entity instead of calling LoadSessionAsync
                var session = MapEntityToSession(entity);
                if (session != null)
                    sessions.Add(session);
            }

            return sessions;
        }

        public async Task<List<RoomSession>> GetAllSessionsAsync()
        {
            var entities = await _context.Sessions
                .Include(s => s.Participants)
                .Include(s => s.Groups.Where(g => !g.IsArchived))
                    .ThenInclude(g => g.GroupParticipants)
                .Include(s => s.ArchivedRounds)
                    .ThenInclude(a => a.Groups)
                        .ThenInclude(g => g.GroupParticipants)
                .AsSplitQuery()
                .ToListAsync();

            var sessions = new List<RoomSession>();
            foreach (var entity in entities)
            {
                // Map directly from already-loaded entity instead of calling LoadSessionAsync
                var session = MapEntityToSession(entity);
                if (session != null)
                    sessions.Add(session);
            }

            return sessions;
        }

        public async Task<RoomSession?> LoadSessionAsync(string code)
        {
            var entity = await _context.Sessions
                .Include(s => s.Participants)
                .Include(s => s.Groups.Where(g => !g.IsArchived))
                    .ThenInclude(g => g.GroupParticipants)
                .Include(s => s.ArchivedRounds)
                    .ThenInclude(a => a.Groups)
                        .ThenInclude(g => g.GroupParticipants)
                .AsSplitQuery()
                .FirstOrDefaultAsync(s => s.Code == code.ToUpperInvariant());

            if (entity == null)
                return null;

            return MapEntityToSession(entity);
        }

        private RoomSession MapEntityToSession(SessionEntity entity)
        {
            var session = new RoomSession
            {
                Code = entity.Code,
                EventName = entity.EventName,
                HostId = entity.HostId,
                HostUserId = entity.HostUserId,  // Load the optional host UserId
                CreatedAtUtc = entity.CreatedAtUtc,
                ExpiresAtUtc = entity.ExpiresAtUtc,
                IsGameStarted = entity.IsGameStarted,
                IsGameEnded = entity.IsGameEnded,
                Archived = entity.Archived,
                CurrentRound = entity.CurrentRound,
                WinnerParticipantId = entity.WinnerParticipantId,
                Settings = JsonSerializer.Deserialize<RoomSettings>(entity.SettingsJson) ?? new RoomSettings()
            };

            // Load participants
            foreach (var p in entity.Participants)
            {
                session.Participants[p.ParticipantId] = new Participant
                {
                    Id = p.ParticipantId,
                    Name = p.Name,
                    Commander = p.Commander,
                    Points = p.Points,
                    JoinedAtUtc = p.JoinedAtUtc,
                    Dropped = p.Dropped,
                    InCustomGroup = p.InCustomGroup,
                    UserId = p.UserId
                };
            }

            // Load current groups
            if (entity.Groups.Any())
            {
                var groupsList = new List<Group>();
                foreach (var group in entity.Groups)
                {
                    if (group.IsArchived)
                        continue;

                    groupsList.Add(MapGroup(group, session, false));
                }
                session.Groups = groupsList.AsReadOnly();
            }

            // Load archived rounds
            foreach (var archivedRound in entity.ArchivedRounds.OrderBy(a => a.RoundNumber))
            {
                var archived = archivedRound.Groups
                    .Where(g => g.IsArchived)
                    .OrderBy(g => g.GroupNumber)
                    .Select(g => MapGroup(g, session, true))
                    .ToList()
                    .AsReadOnly();

                session.ArchivedRounds.Add(archived);
            }

            return session;
        }

        private Group MapGroup(GroupEntity entity, RoomSession session, bool archive)
        {
            var group = new Group
            {
                GroupNumber = entity.GroupNumber,
                RoundNumber = entity.RoundNumber,
                IsDraw = entity.IsDraw,
                WinnerParticipantId = entity.WinnerParticipantId,
                StartedAtUtc = entity.StartedAtUtc,
                CompletedAtUtc = entity.CompletedAtUtc,
                RoundStarted = entity.RoundStarted
            };

            // Load statistics from JSON
            group.SetStatisticsFromJson(entity.StatisticsJson);

            // Load participants ordered by Order property
            foreach (var p in entity.GroupParticipants.OrderBy(gp => gp.Order))
            {
                session.Participants.TryGetValue(p.ParticipantId, out var sessionParticipant);

                // Backward compatibility: Fall back to Participant.Commander if GroupParticipant.Commander is empty
                // This handles existing data before the Commander field was moved to GroupParticipants
                var commander = !string.IsNullOrEmpty(p.Commander) 
                    ? p.Commander 
                    : (sessionParticipant?.Commander ?? string.Empty);

                var participant = new Participant
                {
                    Id = p.ParticipantId,
                    Name = p.Name,
                    JoinedAtUtc = p.JoinedAtUtc,
                    Order = p.Order,   
                    AutoFill = p.AutoFill,
                    Commander = commander
                };

                if (sessionParticipant != null) 
                {
                    if (!archive)
                    {
                        sessionParticipant.Order = participant.Order;
                        sessionParticipant.Commander = commander;
                    }
                    sessionParticipant.AutoFill = participant.AutoFill;
                    participant.InCustomGroup = sessionParticipant.InCustomGroup;
                    participant.UserId = sessionParticipant.UserId;
                }

                group.AddParticipant(participant);         
            }

            return group;
        }

        public async Task<bool> DeleteSessionAsync(string code)
        {
            var entity = await _context.Sessions.FindAsync(code.ToUpperInvariant());
            if (entity == null)
                return false;

            _context.Sessions.Remove(entity);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<List<string>> GetExpiredSessionCodesAsync()
        {
            return await _context.Sessions
                .Where(s => s.ExpiresAtUtc <= DateTime.UtcNow)
                .Select(s => s.Code)
                .ToListAsync();
        }

        public async Task<List<SessionSummary>> GetAllSessionSummariesAsync()
        {
            var entities = await _context.Sessions
                .Select(s => new SessionSummary
                {
                    Code = s.Code,
                    EventName = s.EventName,
                    CreatedAtUtc = s.CreatedAtUtc,
                    ExpiresAtUtc = s.ExpiresAtUtc,
                    IsGameStarted = s.IsGameStarted,
                    IsGameEnded = s.IsGameEnded,
                    Archived = s.Archived,
                    ParticipantCount = s.Participants.Count
                })
                .ToListAsync();

            return entities;
        }

        /// <summary>
        /// Links a session to a user account (when creating a session with a logged-in user)
        /// </summary>
        public async Task<bool> LinkSessionToUserAsync(string sessionCode, Guid userId)
        {
            var entity = await _context.Sessions.FindAsync(sessionCode.ToUpperInvariant());
            if (entity == null)
                return false;

            entity.LinkHostUser(userId);
            await _context.SaveChangesAsync();
            return true;
        }

        /// <summary>
        /// Links a participant to a user account (when joining with a logged-in user)
        /// </summary>
        public async Task<bool> LinkParticipantToUserAsync(string sessionCode, string participantId, Guid userId)
        {
            var entity = await _context.Participants
                .FirstOrDefaultAsync(p => p.SessionCode == sessionCode.ToUpperInvariant() 
                    && p.ParticipantId == participantId);

            if (entity == null)
                return false;

            entity.LinkParticipantUser(userId);
            await _context.SaveChangesAsync();
            return true;
        }

        /// <summary>
        /// Gets all sessions created by a specific user
        /// </summary>
        public async Task<List<SessionSummary>> GetSessionsByUserAsync(Guid userId)
        {
            var entities = await _context.Sessions
                .Where(s => s.HostUserId == userId)
                .Select(s => new SessionSummary
                {
                    Code = s.Code,
                    EventName = s.EventName,
                    CreatedAtUtc = s.CreatedAtUtc,
                    ExpiresAtUtc = s.ExpiresAtUtc,
                    IsGameStarted = s.IsGameStarted,
                    IsGameEnded = s.IsGameEnded,
                    Archived = s.Archived,
                    ParticipantCount = s.Participants.Count
                })
                .OrderByDescending(s => s.CreatedAtUtc)
                .ToListAsync();

            return entities;
        }

        /// <summary>
        /// Gets all sessions where a specific user is a participant
        /// </summary>
        public async Task<List<SessionSummary>> GetSessionsWhereUserIsParticipantAsync(Guid userId)
        {
            var sessionCodes = await _context.Participants
                .Where(p => p.UserId == userId)
                .Select(p => p.SessionCode)
                .Distinct()
                .ToListAsync();

            var entities = await _context.Sessions
                .Where(s => sessionCodes.Contains(s.Code))
                .Select(s => new SessionSummary
                {
                    Code = s.Code,
                    EventName = s.EventName,
                    CreatedAtUtc = s.CreatedAtUtc,
                    ExpiresAtUtc = s.ExpiresAtUtc,
                    IsGameStarted = s.IsGameStarted,
                    IsGameEnded = s.IsGameEnded,
                    Archived = s.Archived,
                    ParticipantCount = s.Participants.Count
                })
                .OrderByDescending(s => s.CreatedAtUtc)
                .ToListAsync();

            return entities;
        }
    }
}