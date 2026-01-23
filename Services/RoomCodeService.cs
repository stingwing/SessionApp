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

        public bool TryJoin(string code, string participantId, string participantName)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(participantId))
                return false;

            var key = code.ToUpperInvariant();
            if (!_sessions.TryGetValue(key, out var session) || session.IsExpiredUtc())
                return false;

            // Do not allow joining after a game has started
            if (session.IsGameStarted)
                return false;

            var participant = new Participant
            {
                Id = participantId,
                Name = participantName,
                JoinedAtUtc = DateTime.UtcNow
            };

            session.Participants.AddOrUpdate(participantId, participant, (_, __) => participant);

            // Notify subscribers that a participant joined
            ParticipantJoined?.Invoke(session, participant);

            return true;
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
        public IReadOnlyList<IReadOnlyList<Participant>>? StartGame(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            var key = code.ToUpperInvariant();
            if (!_sessions.TryGetValue(key, out var session) || session.IsExpiredUtc())
                return null;

            lock (session)
            {
                if (session.IsGameStarted)
                    return null;

                var participants = session.Participants.Values.ToList();
                if (participants.Count == 0)
                    return null;

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

                // Notify subscribers (SignalR hub can forward to clients)
                GameStarted?.Invoke(session, session.Groups);

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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _rng.Dispose();
            _cleanupTimer.Dispose();
        }
    }
}