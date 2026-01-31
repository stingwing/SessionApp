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
                    HostId = session.HostId,
                    CreatedAtUtc = session.CreatedAtUtc,
                    ExpiresAtUtc = session.ExpiresAtUtc,
                    IsGameStarted = session.IsGameStarted,
                    IsGameEnded = session.IsGameEnded,
                    CurrentRound = session.CurrentRound,
                    WinnerParticipantId = session.WinnerParticipantId,
                    SettingsJson = JsonSerializer.Serialize(session.Settings)
                };
                _context.Sessions.Add(entity);
            }
            else
            {
                entity.ExpiresAtUtc = session.ExpiresAtUtc;
                entity.IsGameStarted = session.IsGameStarted;
                entity.IsGameEnded = session.IsGameEnded;
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
                        JoinedAtUtc = participant.JoinedAtUtc
                    });
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
            // Remove existing active groups if not archived
            if (!isArchived)
            {
                var existingGroups = await _context.Groups
                    .Where(g => g.SessionCode == sessionCode && !g.IsArchived)
                    .ToListAsync();
                _context.Groups.RemoveRange(existingGroups);
            }

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
                    ArchivedRoundId = archivedRoundId
                };

                _context.Groups.Add(groupEntity);
                await _context.SaveChangesAsync(); // Save to get groupEntity.Id

                foreach (var participant in group.Participants.Values)
                {
                    _context.GroupParticipants.Add(new GroupParticipantEntity
                    {
                        GroupId = groupEntity.Id,
                        ParticipantId = participant.Id,
                        Name = participant.Name,
                        JoinedAtUtc = participant.JoinedAtUtc
                    });
                }
            }
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

                var archivedEntity = new ArchivedRoundEntity
                {
                    SessionCode = session.Code,
                    RoundNumber = roundNumber
                };
                _context.ArchivedRounds.Add(archivedEntity);
                await _context.SaveChangesAsync(); // Get ID

                await SaveGroupsAsync(session.Code, archivedRound, isArchived: true, archivedRoundId: archivedEntity.Id);
            }
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
                .FirstOrDefaultAsync(s => s.Code == code.ToUpperInvariant());

            if (entity == null)
                return null;

            var session = new RoomSession
            {
                Code = entity.Code,
                HostId = entity.HostId,
                CreatedAtUtc = entity.CreatedAtUtc,
                ExpiresAtUtc = entity.ExpiresAtUtc,
                IsGameStarted = entity.IsGameStarted,
                IsGameEnded = entity.IsGameEnded,
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
                    JoinedAtUtc = p.JoinedAtUtc
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

                    groupsList.Add(MapGroup(group));
                }
                session.Groups = groupsList.AsReadOnly();
            }
        
            // Load archived rounds
            foreach (var archivedRound in entity.ArchivedRounds.OrderBy(a => a.RoundNumber))
            {
                var archived = archivedRound.Groups
                    .Where(g => g.IsArchived)
                    .OrderBy(g => g.GroupNumber)
                    .Select(g => MapGroup(g))
                    .ToList()
                    .AsReadOnly();

                session.ArchivedRounds.Add(archived);
            }

            return session;
        }

        private Group MapGroup(GroupEntity entity)
        {
            var group = new Group
            {
                GroupNumber = entity.GroupNumber,
                RoundNumber = entity.RoundNumber,
                IsDraw = entity.IsDraw,
                WinnerParticipantId = entity.WinnerParticipantId
            };

            foreach (var p in entity.GroupParticipants)
            {
                group.Participants[p.ParticipantId] = new Participant
                {
                    Id = p.ParticipantId,
                    Name = p.Name,
                    JoinedAtUtc = p.JoinedAtUtc
                };
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

        public async Task<List<RoomSession>> GetAllActiveSessionsAsync()
        {
            var entities = await _context.Sessions
             //   .Where(s => s.ExpiresAtUtc > DateTime.UtcNow)
                .Include(s => s.Participants)
                .Include(s => s.Groups.Where(g => !g.IsArchived))
                    .ThenInclude(g => g.GroupParticipants)
                .ToListAsync();

            var sessions = new List<RoomSession>();
            foreach (var entity in entities)
            {
                var session = await LoadSessionAsync(entity.Code);
                if (session != null)
                    sessions.Add(session);
            }

            return sessions;
        }
    }
}