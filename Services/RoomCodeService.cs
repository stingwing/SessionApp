using SessionApp.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

        // Events for real-time notifications (subscribers may forward to SignalR)
        public event Action<RoomSession>? SessionExpired;
        public event Action<RoomSession, Participant>? ParticipantJoined;

        // New event: invoked when a game is successfully started and groups are created
        public event Action<RoomSession, IReadOnlyList<IReadOnlyList<Participant>>>? GameStarted;

        // New event: invoked when a new round is started (re-grouping during an ongoing session)
        public event Action<RoomSession, IReadOnlyList<IReadOnlyList<Participant>>>? NewRoundStarted;

        // New event: invoked when a game/group ends (win/draw). groupIndex provided by controller/service consumer via GetSession.
        public event Action<RoomSession, ReportOutcomeType, string?>? GameEnded;

        // New event: invoked when a participant drops out
        public event Action<RoomSession, Participant>? ParticipantDropped;

        public RoomCodeService()
        {
            _cleanupTimer = new Timer(_ => CleanupExpiredSessions(), null, _cleanupInterval, _cleanupInterval);
        }

        public RoomSession CreateSession(string hostId, int codeLength = 6, TimeSpan? ttl = null)
        {
            if (string.IsNullOrWhiteSpace(hostId))
                throw new ArgumentException("hostId is required", nameof(hostId));

            ttl ??= TimeSpan.FromHours(2);

            const int maxAttempts = 1000;
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
                    return session;
            }

            throw new InvalidOperationException("Unable to generate a unique room code. Try increasing code length.");
        }

        public string TryJoin(string code, string participantId, string participantName = "")
        {
            if (string.IsNullOrWhiteSpace(code))
                return $"Code Doesn't Exist";

            if (string.IsNullOrWhiteSpace(participantId))
                return "Participant is null or Empty String";

            if (participantName == string.Empty)
                participantName = participantId;

            var key = code.ToUpperInvariant();
            if (!_sessions.TryGetValue(key, out var session) || session.IsExpiredUtc())
                return "Session Has Expired";

            // Do not allow joining after a game has started
            if (session.IsGameStarted) // maybe change this
                return "Game Has Started";

            var participant = new Participant
            {
                Id = participantId,
                Name = participantName,
                JoinedAtUtc = DateTime.UtcNow
            };

            if (session.Participants.ContainsKey(participantId))
                return $"A user with the id {participantId} is already in the game";

            session.Participants.AddOrUpdate(participantId, participant, (_, __) => participant);

            // Notify subscribers that a participant joined
            ParticipantJoined?.Invoke(session, participant);

            return "Success";
        }

        public RoomSession? GetSession(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            var key = code.ToUpperInvariant();
            if (!_sessions.TryGetValue(key, out var session) || session.IsExpiredUtc())
                return null;

            return session;
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

        /// <summary>
        /// Starts a game for the given room code. Participants are randomized and partitioned into groups of up to 4.
        /// The last group may contain fewer than 4 participants if the total is not divisible by 4.
        /// Returns the groups on success; returns null if session not found, expired, already started, or has no participants.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<Participant>>? StartGame(string code, ref string errorMessage)
        {

            if (string.IsNullOrWhiteSpace(code))
            {
                errorMessage = "Code is null or empty";
                return null;
            }

            var key = code.ToUpperInvariant();
            if (!_sessions.TryGetValue(key, out var session) || session.IsExpiredUtc())
            {
                errorMessage = "Code is Invalid or Expired";
                return null;
            }

            lock (session)
            {
                if (session.IsGameStarted)
                {
                    errorMessage = "Game has already started";
                    return null;
                }

                var participants = session.Participants.Values.ToList();
                if (!participants.Any())
                {
                    errorMessage = "Game has no players";
                    return null;
                }

                if (participants.Count % 3 != 0 && participants.Count % 4 != 0)
                {
                    errorMessage = $"The number of participants {participants.Count} does not divide into groups of 3 or 4";
                    return null;
                }

                // Shuffle using cryptographic RNG (Fisher-Yates)
                Shuffle(participants);

                var groups = new List<IReadOnlyList<Participant>>();
                for (int i = 0; i < participants.Count; i += 4)
                {
                    var group = participants.Skip(i).Take(4).ToArray();
                    groups.Add(Array.AsReadOnly(group));
                }

                session.Groups = Array.AsReadOnly(groups.ToArray());
                session.IsGameStarted = true;

                // initialize per-group states
                session.GroupStates = Array.AsReadOnly(groups.Select(_ => new GroupState()).ToArray());

                // Notify subscribers (SignalR hub can forward to clients)
                GameStarted?.Invoke(session, session.Groups);

                return session.Groups;
            }
        }

        /// <summary>
        /// Starts a new round for an ongoing session (re-shuffles remaining participants and re-partitions groups).
        /// Returns the new groups on success; returns null if session not found, expired, not started, or there are no participants.
        /// Dropped participants have already been removed from session.Participants and therefore will not be included.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<Participant>>? StartNewRound(string code, ref string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                errorMessage = "Code is null or empty"; 
                return null;
            }
           
            var key = code.ToUpperInvariant();
            if (!_sessions.TryGetValue(key, out var session) || session.IsExpiredUtc())
            {
                errorMessage = "Code is Invalid or Expired";
                return null;
            }

            lock (session)
            {
                // Only allow starting a new round for an already started session
                if (session.IsGameStarted)
                {
                    errorMessage = "Game has already started";
                    return null;
                }

                var participants = session.Participants.Values.ToList();
                if (!participants.Any())
                {
                    errorMessage = "Game has no players";
                    return null;
                }

                if (participants.Count % 3 == 0 || participants.Count % 4 == 0)
                {
                    errorMessage = $"The number of participants {participants.Count} does not divide into groups of 3 or 4";
                    return null;
                }

                // Shuffle and partition as in StartGame
                Shuffle(participants);

                var groups = new List<IReadOnlyList<Participant>>();
                for (int i = 0; i < participants.Count; i += 4)
                {
                    var group = participants.Skip(i).Take(4).ToArray();
                    groups.Add(Array.AsReadOnly(group));
                }

                session.Groups = Array.AsReadOnly(groups.ToArray());

                // initialize per-group states for the new round
                session.GroupStates = Array.AsReadOnly(groups.Select(_ => new GroupState()).ToArray());

                // Notify subscribers that a new round started
                NewRoundStarted?.Invoke(session, session.Groups);

                return session.Groups;
            }
        }

        private void Shuffle<T>(IList<T> list)
        {
            // Fisher-Yates using RandomNumberGenerator.GetInt32 (uniform)
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
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
            DropOut
        }

        public enum ReportOutcomeResult
        {
            Success,
            RoomNotFound,
            NotStarted,
            AlreadyEnded, // group already has a result
            ParticipantNotFound,
            Invalid
        }

        /// <summary>
        /// Report an outcome for a participant in a started game.
        /// - Win: marks that participant's group as having a winner (one winner max per group)
        /// - Draw: marks that participant's group as a draw (everyone in that group receives draw)
        /// - DropOut: removes participant from session.Participants (they're excluded from future rounds)
        /// Returns a ReportOutcomeResult and out parameters for additional details:
        /// - winnerParticipantId: set for Win
        /// - removedParticipant: set for DropOut
        /// - groupIndex: zero-based index of group affected (set for Win/Draw when participant belongs to a group)
        /// </summary>
        public ReportOutcomeResult ReportOutcome(string code, string participantId, ReportOutcomeType outcome, out string? winnerParticipantId, out Participant? removedParticipant, out int? groupIndex)
        {
            winnerParticipantId = null;
            removedParticipant = null;
            groupIndex = null;

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(participantId))
                return ReportOutcomeResult.Invalid;

            var key = code.ToUpperInvariant();
            if (!_sessions.TryGetValue(key, out var session) || session.IsExpiredUtc())
                return ReportOutcomeResult.RoomNotFound;

            lock (session)
            {
                if (!session.IsGameStarted || session.Groups is null || session.GroupStates is null)
                    return ReportOutcomeResult.NotStarted;

                // Find which group (if any) the participant belongs to in the current round
                var foundGroup = -1;
                for (var i = 0; i < session.Groups.Count; i++)
                {
                    var group = session.Groups[i];
                    if (group.Any(p => string.Equals(p.Id, participantId, StringComparison.Ordinal)))
                    {
                        foundGroup = i;
                        break;
                    }
                }

                if (outcome == ReportOutcomeType.DropOut)
                {
                    // Remove from active participants (so they won't be included in next round)
                    if (session.Participants.TryRemove(participantId, out var removed))
                    {
                        removedParticipant = removed;
                        ParticipantDropped?.Invoke(session, removed);
                        return ReportOutcomeResult.Success;
                    }

                    return ReportOutcomeResult.ParticipantNotFound;
                }

                // For Win/Draw we require the participant to be in a group for the current round
                if (foundGroup < 0)
                    return ReportOutcomeResult.ParticipantNotFound;

                groupIndex = foundGroup;
                var groupState = session.GroupStates[foundGroup];

                if (groupState.HasResult)
                    return ReportOutcomeResult.AlreadyEnded;

                if (outcome == ReportOutcomeType.Win)
                {
                    // verify participant still present
                    if (!session.Participants.ContainsKey(participantId))
                        return ReportOutcomeResult.ParticipantNotFound;

                    groupState.WinnerParticipantId = participantId;
                    groupState.IsDraw = false;
                    winnerParticipantId = participantId;

                    // Notify subscribers
                    GameEnded?.Invoke(session, outcome, participantId);
                    return ReportOutcomeResult.Success;
                }

                if (outcome == ReportOutcomeType.Draw)
                {
                    groupState.IsDraw = true;
                    groupState.WinnerParticipantId = null;

                    GameEnded?.Invoke(session, outcome, null);
                    return ReportOutcomeResult.Success;
                }

                return ReportOutcomeResult.Invalid;
            }
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