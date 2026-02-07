using SessionApp.Data;
using SessionApp.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SessionApp.Services
{
    public class RoomCodeService : IDisposable
    {
        private const string AllowedChars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();
        private readonly ConcurrentDictionary<string, RoomSession> _sessions = new();
        private readonly Timer _cleanupTimer;
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);
        private bool _disposed;

        private readonly IServiceProvider? _serviceProvider;
        private readonly GroupGenerationService _groupGenerationService;

        // Events for real-time notifications (subscribers may forward to SignalR)
        public event Action<RoomSession>? SessionExpired;
        public event Action<RoomSession, Participant>? ParticipantJoined;

        // New event: invoked when a game is successfully started and groups are created
      //  public event Action<RoomSession, IReadOnlyList<Group>>? GameStarted;

        // New event: invoked when a new round is started (re-grouping during an ongoing session)
        public event Action<RoomSession, IReadOnlyList<Group>>? NewRoundStarted;

        // New event: invoked when a game/group ends (win/draw). groupIndex provided by controller/service consumer via GetSession.
        public event Action<RoomSession, ReportOutcomeType, string?>? GameEnded;

        // New event: invoked when a participant drops out
        public event Action<RoomSession, Participant>? ParticipantDropped;

        public RoomCodeService(IServiceProvider? serviceProvider = null)
        {
            _serviceProvider = serviceProvider;
            _groupGenerationService = new GroupGenerationService();
            _cleanupTimer = new Timer(_ => CleanupExpiredSessions(), null, _cleanupInterval, _cleanupInterval);

            // Load existing sessions from database on startup
            if (_serviceProvider != null)
            {
                _ = LoadSessionsFromDatabaseAsync();
            }
        }

        private async Task LoadSessionsFromDatabaseAsync()
        {
            try
            {
                using var scope = _serviceProvider!.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<SessionRepository>();
                var sessions = await repository.GetAllSessionsAsync();
                foreach (var session in sessions)
                {
                    _sessions[session.Code.ToUpperInvariant()] = session;

                    if (session.ExpiresAtUtc <= DateTime.UtcNow && !session.IsGameEnded)
                        CleanupExpiredSession(session.Code);
                }
            }
            catch (Exception)
            {
                // Log error - database might not be initialized yet
            }
        }

        /// <summary>
        /// Saves a session to the database asynchronously.
        /// Returns true if save was successful, false otherwise.
        /// </summary>
        public async Task<bool> SaveSessionToDatabaseAsync(RoomSession session)
        {
            if (_serviceProvider == null)
                return false;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<SessionRepository>();
                await repository.SaveSessionAsync(session);
                return true;
            }
            catch (Exception)
            {
                // Log error
                return false;
            }
        }

        /// <summary>
        /// Saves a session to the database using fire-and-forget pattern (for non-critical saves).
        /// Does not block the calling thread and does not return success/failure.
        /// </summary>
        private void SaveSessionToDatabaseFireAndForget(RoomSession session)
        {
            if (_serviceProvider == null)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var repository = scope.ServiceProvider.GetRequiredService<SessionRepository>();
                    await repository.SaveSessionAsync(session);
                }
                catch (Exception)
                {
                    // Log error - don't fail session operations if DB save fails
                }
            });
        }

        public RoomSession CreateSession(string hostId, int codeLength = 6, TimeSpan? ttl = null)
        {
            if (string.IsNullOrWhiteSpace(hostId))
                throw new ArgumentException("hostId is required", nameof(hostId));

            const int maxAttempts = 1000;

            if (ttl == null)
                ttl = TimeSpan.FromDays(7);

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                var code = GenerateCode(codeLength);
                var key = code.ToUpperInvariant();
                var now = DateTime.UtcNow;
                var session = new RoomSession
                {
                    Code = code,
                    HostId = hostId,
                    CreatedAtUtc = now,
                    ExpiresAtUtc = now.Add(ttl.Value)
                };

                if (_sessions.TryAdd(key, session))
                {
                    // Save to database asynchronously (fire and forget with proper error handling)
                    SaveSessionToDatabaseFireAndForget(session);
                    return session;
                }
            }

            throw new InvalidOperationException("Unable to generate a unique room code. Try increasing code length.");
        }

        public string TryJoin(string code, string participantId, string participantName = "", string commander = "")
        {
            if (string.IsNullOrWhiteSpace(code))
                return $"Code Doesn't Exist";

            if (string.IsNullOrWhiteSpace(participantId))
                return "Participant is null or Empty String";

            if (participantName == string.Empty)
                participantName = participantId;

            var key = code.ToUpperInvariant();
            if (!_sessions.TryGetValue(key, out var session))
                return "Session is invalid";

            if (session.IsExpiredUtc())
                return "Session has expired";

            if (session.IsGameEnded)
                return "Game has ended";

            // Do not allow joining after a game has started
            if (session.IsGameStarted && !session.Settings.AllowJoinAfterStart)
                return "Game Has Started";

            var participant = new Participant
            {
                Id = participantId,
                Name = participantName,
                JoinedAtUtc = DateTime.UtcNow,
                Commander = commander
            };

            if (session.Participants.ContainsKey(participantId))
                return $"A user with the id {participantId} is already in the game";

            session.Participants.AddOrUpdate(participantId, participant, (_, __) => participant);
            
            // Save to database (fire and forget)
            SaveSessionToDatabaseFireAndForget(session);

            // Notify subscribers that a participant joined
            ParticipantJoined?.Invoke(session, participant);

            return "Success";
        }

        public async Task<RoomSession?> GetSessionAsync(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            var key = code.ToUpperInvariant();

            if (_sessions.TryGetValue(key, out var session))
            {
                //if (session.IsExpiredUtc()) //im not sure about this one
                //    return null;
                return session;
            }

            // Try loading from database
            if (_serviceProvider is not null)
            {
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<SessionRepository>();
                var dbSession = await repository.LoadSessionAsync(key);
                if (dbSession != null )// && !dbSession.IsExpiredUtc())
                {
                    _sessions[key] = dbSession;
                    return dbSession;
                }
            }

            return null;
        }

        public bool InvalidateSession(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            var key = code.ToUpperInvariant();
            var removed = _sessions.TryRemove(key, out var session);
            if (removed && session != null)
            {
                SessionExpired?.Invoke(session);
            }

            return removed;
        }

        private void CleanupExpiredSessions()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _sessions.ToArray())
            {
                if (kv.Value.ExpiresAtUtc <= now)
                {
                    if (_sessions.TryRemove(kv.Key, out var removed))
                    {
                        SessionExpired?.Invoke(removed);
                    }
                }
            }
        }

        public IReadOnlyList<Group>? HandleRound(string code, HandleRoundOptions task, Dictionary<string, object> playerGroup, ref string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                errorMessage = "Code is null or empty";
                return null;
            }

            var key = code.ToUpperInvariant();
            if (!_sessions.TryGetValue(key, out var session))
            {
                errorMessage = "Code is Invalid or Expired";
                return null;
            }

            lock (session)
            {
                var participants = session.Participants.Values.ToList();
                if (!participants.Any())
                {
                    errorMessage = "Game has no players";
                    return null;
                }

                if (participants.Count < 6)
                {
                    errorMessage = $"At least 6 participants are required";
                    return null;
                }

                // FIXED: Archive current round BEFORE generating the next one
                // This ensures Round 1 is archived before Round 2 is created
                if (session.Groups != null && session.Groups.Count > 0)
                {
                    // Check if we're starting a new round (not just regenerating the current one)
                    if (task == HandleRoundOptions.GenerateRound || task == HandleRoundOptions.EndRound || task == HandleRoundOptions.EndGame)
                    {
                        foreach (var group in session.Groups)
                        {
                            // Check for missing commander keys in statistics for all participants in the group
                            EnsureCommanderStatistics(group);
                        }

                        var snapshotGroups = SnapshotGroups(session.Groups);
                        session.ArchivedRounds.Add(snapshotGroups);
                    }
                }

                // Increment round counter when generating a new round
                if (task == HandleRoundOptions.GenerateRound || task == HandleRoundOptions.GenerateFirstRound)
                {
                    if (session.CurrentRound == 0 || task == HandleRoundOptions.GenerateRound)
                        session.CurrentRound++;
                }

                var groups = _groupGenerationService.RanzomizeRound(participants, session, Array.Empty<Group>(), task);

                session.Groups = Array.AsReadOnly(groups.ToArray());
                session.IsGameStarted = true;

                // Save to database (fire and forget)
                SaveSessionToDatabaseFireAndForget(session);

                NewRoundStarted?.Invoke(session, session.Groups);
                return session.Groups;
            }
        }

        public IReadOnlyList<Group> SnapshotGroups(IReadOnlyList<Group> groups)
        {
            var snapshot = new List<Group>(groups.Count);
            foreach (var g in groups)
            {
                if(g.CompletedAtUtc == null)
                    g.CompletedAtUtc = DateTime.UtcNow;

                var copy = new Group
                {
                    GroupNumber = g.GroupNumber,
                    RoundNumber = g.RoundNumber,
                    IsDraw = g.IsDraw,
                    WinnerParticipantId = g.WinnerParticipantId,
                    StartedAtUtc = g.StartedAtUtc,
                    CompletedAtUtc = g.CompletedAtUtc,
                    Statistics = new Dictionary<string, object>(g.Statistics) // Deep copy
                };

                // Copy participants using AddParticipant to preserve order
                foreach (var p in g.ParticipantsOrdered)
                {
                    copy.AddParticipant(p);
                }

                snapshot.Add(copy);
            }

            return Array.AsReadOnly(snapshot.ToArray());
        }
    
        private string GenerateCode(int length)
        {
            var buffer = new byte[length];
            _rng.GetBytes(buffer);
            var sb = new StringBuilder(length);
            for (var i = 0; i < length; i++)
            {
                var idx = buffer[i] % AllowedChars.Length;
                sb.Append(AllowedChars[idx]);
            }

            return sb.ToString().ToUpperInvariant();
        }

        /// <summary>
        /// Types and results for reporting outcomes
        /// </summary>
        public enum ReportOutcomeType
        {
            Win,
            Draw,
            DropOut,
            DataOnly // Add this new type for statistics-only updates
        }

        public enum HandleRoundOptions
        {
            Invalid,
            GenerateRound,
            GenerateFirstRound,
            RegenerateRound,
            StartRound,
            ResetRound,
            CreateGroup,
            EndRound,
            EndGame,
        }

        public enum ReportOutcomeResult
        {
            Success,
            RoomNotFound,
            NotStarted,
            AlreadyEnded, // group already has a result
            ParticipantNotFound,
            Invalid,
        }

        /// <summary>
        /// Report an outcome for a participant in a started game.
        /// - Win: marks that participant's group as having a winner (one winner max per group)
        /// - Draw: marks that participant's group as a draw (everyone in that group receives draw)
        /// - DropOut: removes participant from session.Participants (they're excluded from future rounds)
        /// Statistics: optional dictionary of custom data to store with the group result
        /// Returns a ReportOutcomeResult and out parameters for additional details:
        /// - winnerParticipantId: set for Win
        /// - removedParticipant: set for DropOut
        /// - groupIndex: zero-based index of group affected (set for Win/Draw when participant belongs to a group)
        /// </summary>
        public ReportOutcomeResult ReportOutcome(string code, string participantId, ReportOutcomeType outcome, string commander, Dictionary<string, object> statistics, out string? winnerParticipantId, out Participant? removedParticipant, out int? groupIndex)
        {
            winnerParticipantId = null;
            removedParticipant = null;
            groupIndex = null;

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(participantId))
                return ReportOutcomeResult.Invalid;

            var key = code.ToUpperInvariant();
            if (!_sessions.TryGetValue(key, out var session) || session.IsExpiredUtc())
                return ReportOutcomeResult.RoomNotFound;

            if (session.Groups == null)
                return ReportOutcomeResult.Invalid;

            lock (session)
            {
                //if (!session.IsGameStarted || session.Groups is null)
                //    return ReportOutcomeResult.NotStarted;

              //  if(session.IsGameEnded)
             //       return ReportOutcomeResult.AlreadyEnded;

                // Handle DropOut (remove from active participants)
                if (outcome == ReportOutcomeType.DropOut)
                {
                    if (session.HasAnyRoundStarted())
                        return ReportOutcomeResult.Invalid;

                    if (session.Participants.TryRemove(participantId, out var removed))
                    {
                        removedParticipant = removed;
                        
                        // Save to database (fire and forget)
                        SaveSessionToDatabaseFireAndForget(session);

                        ParticipantDropped?.Invoke(session, removed);
                        return ReportOutcomeResult.Success;
                    }

                    return ReportOutcomeResult.ParticipantNotFound;
                }

                //Update commander going forward
                var participantExists = session.Participants.ContainsKey(participantId);
                if (participantExists && commander != string.Empty)
                {
                    session.Participants[participantId].Commander = commander;
                }

                // For Win/Draw/DataOnly we require the participant to be in a group for the current round.
                var currentGroup = session.Groups.FirstOrDefault(g => g.Participants.ContainsKey(participantId));
                if (currentGroup is null)
                    return ReportOutcomeResult.ParticipantNotFound;

                if(currentGroup.CompletedAtUtc == null)
                    currentGroup.CompletedAtUtc = DateTime.UtcNow;
                // Maintain existing external contract: groupIndex is zero-based index into session.Groups
                groupIndex = currentGroup.GroupNumber - 1;
                if (groupIndex < 0)
                    groupIndex = null;

                // For DataOnly, allow updates even if result exists
                if (outcome != ReportOutcomeType.DataOnly && currentGroup.HasResult)
                    return ReportOutcomeResult.AlreadyEnded;

                // Merge provided statistics with existing ones
                if (statistics != null && statistics.Count > 0)
                {
                    foreach (var stat in statistics)
                    {
                        currentGroup.Statistics[stat.Key] = stat.Value;
                    }
                }

                //Update Commander for this round.
                currentGroup.Participants[participantId].Commander = commander;
                
                // Check for missing commander keys in statistics for all participants in the group
                EnsureCommanderStatistics(currentGroup);

                // Handle DataOnly - just update statistics without changing result
                if (outcome == ReportOutcomeType.DataOnly)
                {
                    // Save to database (fire and forget)
                    SaveSessionToDatabaseFireAndForget(session);
                    return ReportOutcomeResult.Success;
                }

                // Store completion timestamp for Win/Draw
                currentGroup.CompletedAtUtc = DateTime.UtcNow;

                if (outcome == ReportOutcomeType.Win)
                {
                    // verify participant still present
                    if (!session.Participants.ContainsKey(participantId))
                        return ReportOutcomeResult.ParticipantNotFound;

                    currentGroup.WinnerParticipantId = participantId;
                    currentGroup.IsDraw = false;
                    winnerParticipantId = participantId;

                    // Save to database (fire and forget)
                    SaveSessionToDatabaseFireAndForget(session);

                    // Notify subscribers
                    GameEnded?.Invoke(session, outcome, participantId);
                    return ReportOutcomeResult.Success;
                }

                if (outcome == ReportOutcomeType.Draw)
                {
                    currentGroup.IsDraw = true;
                    currentGroup.WinnerParticipantId = null;

                    // Save to database (fire and forget)
                    SaveSessionToDatabaseFireAndForget(session);

                    GameEnded?.Invoke(session, outcome, null);
                    return ReportOutcomeResult.Success;
                }

                return ReportOutcomeResult.Invalid;
            }
        }

        /// <summary>
        /// Ensures all participants' commanders are stored in the group statistics with the key pattern {participantId}_Commander.
        /// Only adds missing keys where the participant has a non-empty commander.
        /// </summary>
        private void EnsureCommanderStatistics(Group group)
        {
            foreach (var participant in group.Participants.Values)
            {
                if (string.IsNullOrWhiteSpace(participant.Commander))
                    continue;

                var commanderKey = $"{participant.Id}_Commander";
                if (!group.Statistics.ContainsKey(commanderKey))
                {
                    group.Statistics[commanderKey] = participant.Commander;
                }
            }
        }

        /// <summary>
        /// Cleanup method that archives current groups and marks expired sessions as ended.
        /// Unlike EndSession in the controller, this doesn't require host authorization and is intended for expired sessions.
        /// Returns true if the session was found and cleaned up, false otherwise.
        /// </summary>
        public bool CleanupExpiredSession(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            var key = code.ToUpperInvariant();
            if (!_sessions.TryGetValue(key, out var session))
                return false;

            // Check if session has expired
            var isExpired = session.ExpiresAtUtc <= DateTime.UtcNow;

            lock (session)
            {
                // If game was started and has groups, archive them
                if (session.IsGameStarted && session.Groups is not null && session.Groups.Count > 0)
                {
                    var snapshot = SnapshotGroups(session.Groups);
                    session.ArchivedRounds.Add(snapshot);
                }

                session.Archived = true;
                session.IsGameEnded = true;  
            }

            // Save to database (fire and forget)
            SaveSessionToDatabaseFireAndForget(session);

            return true;
        }

        public async Task<List<RoomSession>> GetAllSessionsAsync()
        {
            // First get all sessions from memory
            var memorySessions = _sessions.Values.ToList();

            // If we have a service provider, also load from database and merge
            if (_serviceProvider != null)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var repository = scope.ServiceProvider.GetRequiredService<SessionRepository>();
                    var dbSessions = await repository.GetAllSessionsAsync();

                    // Merge database sessions into memory cache
                    foreach (var dbSession in dbSessions)
                    {
                        var key = dbSession.Code.ToUpperInvariant();
                        if (!_sessions.ContainsKey(key))
                        {
                            _sessions[key] = dbSession;
                            memorySessions.Add(dbSession);
                        }
                    }
                }
                catch (Exception)
                {
                    // Log error - fall back to memory-only sessions
                }
            }

            return memorySessions;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _rng.Dispose();
            _cleanupTimer.Dispose();
        }
    }
}