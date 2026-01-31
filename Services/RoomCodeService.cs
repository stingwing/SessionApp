using SessionApp.Data;
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

        private readonly IServiceProvider? _serviceProvider;

        // Events for real-time notifications (subscribers may forward to SignalR)
        public event Action<RoomSession>? SessionExpired;
        public event Action<RoomSession, Participant>? ParticipantJoined;

        // New event: invoked when a game is successfully started and groups are created
        public event Action<RoomSession, IReadOnlyList<Group>>? GameStarted;

        // New event: invoked when a new round is started (re-grouping during an ongoing session)
        public event Action<RoomSession, IReadOnlyList<Group>>? NewRoundStarted;

        // New event: invoked when a game/group ends (win/draw). groupIndex provided by controller/service consumer via GetSession.
        public event Action<RoomSession, ReportOutcomeType, string?>? GameEnded;

        // New event: invoked when a participant drops out
        public event Action<RoomSession, Participant>? ParticipantDropped;

        public RoomCodeService(IServiceProvider? serviceProvider = null)
        {
            _serviceProvider = serviceProvider;
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
                var sessions = await repository.GetAllActiveSessionsAsync();
                foreach (var session in sessions)
                {
                    _sessions[session.Code.ToUpperInvariant()] = session;
                }
            }
            catch (Exception)
            {
                // Log error - database might not be initialized yet
            }
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
                {
                    // Save to database asynchronously (fire and forget with proper error handling)
                    if (_serviceProvider != null)
                    {
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
                                // Log error - don't fail session creation if DB save fails
                            }
                        });
                    }
                    return session;
                }
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
            if (session.IsGameStarted && !session.Settings.AllowJoinAfterStart)
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
            // Save to database (fire and forget)
            if (_serviceProvider != null)
            {
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
                        // Log error
                    }
                });
            }

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
                if (session.IsExpiredUtc())
                    return null;
                return session;
            }

            // Try loading from database
            if (_serviceProvider is not null)
            {
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<SessionRepository>();
                var dbSession = await repository.LoadSessionAsync(key);
                if (dbSession != null && !dbSession.IsExpiredUtc())
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

        /// <summary>
        /// Starts a game for the given room code. Participants are randomized and partitioned into groups of up to 4.
        /// The last group may contain fewer than 4 participants if the total is not divisible by 4.
        /// Returns the groups on success; returns null if session not found, expired, already started, or has no participants.
        /// </summary>
        public IReadOnlyList<Group>? StartGame(string code, ref string errorMessage)
        {
            var session = RoundHandler(code, false, ref errorMessage);
            if (session == null || session.Groups == null || errorMessage != string.Empty)
                return null;

            // Notify subscribers (SignalR hub can forward to clients)
            GameStarted?.Invoke(session, session.Groups);
            return session.Groups;
        }

        /// <summary>
        /// Starts a new round for an ongoing session (re-shuffles remaining participants and re-partitions groups).
        /// Returns the new groups on success; returns null if session not found, expired, not started, or there are no participants.
        /// Dropped participants have already been removed from session.Participants and therefore will not be included.
        /// </summary>
        public IReadOnlyList<Group>? StartNewRound(string code, ref string errorMessage)
        {
            var session = RoundHandler(code, true, ref errorMessage);

            if (session == null || session.Groups == null || errorMessage != string.Empty)
                return null;

            // Notify subscribers that a new round started
            NewRoundStarted?.Invoke(session, session.Groups);
            return session.Groups;
        }

        private RoomSession? RoundHandler(string code, bool newRound,ref string errorMessage)
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
                //// Only allow starting a new round for an already started session
                //if (session.IsGameStarted && !newRound)
                //{
                //    errorMessage = "Game Has Already Started";
                //    return null;
                //}

                var participants = session.Participants.Values.ToList();
                if (!participants.Any())
                {
                    errorMessage = "Game has no players";
                    return null;
                }

                if (participants.Count < 6) // add this back in later
                {
                    errorMessage = $"At least 6 participants are required";
                    return null;
                }

                if (session.Groups != null && newRound)
                {
                    var snapshot = SnapshotGroups(session.Groups);
                    session.ArchivedRounds.Add(snapshot);
                   
                }

                if(session.CurrentRound == 0 || newRound)
                    session.CurrentRound++;

                var groups = RanzomizeRound(participants, session);

                session.Groups = Array.AsReadOnly(groups.ToArray());
                session.IsGameStarted = true;

                // Save to database (fire and forget)
                if (_serviceProvider != null)
                {
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
                            // Log error
                        }
                    });
                }
                return session;
            }
        }

        public IReadOnlyList<Group> SnapshotGroups(IReadOnlyList<Group> groups)
        {
            var snapshot = new List<Group>(groups.Count);
            foreach (var g in groups)
            {
                var copy = new Group
                {
                    GroupNumber = g.GroupNumber,
                    RoundNumber = g.RoundNumber,
                    IsDraw = g.IsDraw,
                    WinnerParticipantId = g.WinnerParticipantId
                };

                // Copy participants (Participant is immutable-like — we can reuse the instances)
                foreach (var p in g.Participants.Values)
                {
                    copy.Participants[p.Id] = p;
                }

                snapshot.Add(copy);
            }

            return Array.AsReadOnly(snapshot.ToArray());
        }

        private List<Group> RanzomizeRound(List<Participant> participants, RoomSession session)
        {
            var firstRound = false;
            var groups = new List<Group>();
            var (groupsOf4, groupsOf3) = CalculateNumberOfGroups(participants.Count);

            // If AllowGroupOfThree is false, only create groups of 4
            if (!session.Settings.AllowGroupOfThree)
            {
                groupsOf4 = participants.Count / 4;
                groupsOf3 = 0;
            }

            var totalGroups = groupsOf4 + groupsOf3;

            // Get the last round's groups to determine winners and group sizes
            var lastRound = session.ArchivedRounds.Count > 0
                ? session.ArchivedRounds[^1]
                : null;

            //Handle the first round
            if (lastRound == null)
            {
                firstRound = true;
                lastRound = session.Groups;
            }

            if (lastRound == null)
                return groups;

            // Build pairing history from all archived rounds
            var pairingHistory = BuildPairingHistory(session.ArchivedRounds);

            // Collect all winners from last round
            var allWinners = new List<Participant>();
            var participantsInGroupOf3 = new HashSet<string>();

            if (!firstRound)
            {
                // Build a set of all participant IDs from last round for quick lookup
                foreach (var group in lastRound)
                {
                    // Track participants who were in groups of 3
                    if (group.Participants.Count == 3)
                    {
                        foreach (var p in group.Participants.Keys)
                            participantsInGroupOf3.Add(p);
                    }

                    // Track winners
                    if (!string.IsNullOrEmpty(group.WinnerParticipantId) && session.Settings.PrioitizeWinners)
                    {
                        if (participants.Any(p => p.Id == group.WinnerParticipantId))
                        {
                            var winner = participants.First(p => p.Id == group.WinnerParticipantId);
                            allWinners.Add(winner);
                        }
                    }
                }
            }

            // Separate remaining participants into those who were in groups of 3 and others
            var regularParticipants = new List<Participant>();

            foreach (var participant in participants)
            {
                if (allWinners.Any(w => w.Id == participant.Id))
                    continue;

                regularParticipants.Add(participant);
            }

            // Calculate how many winner groups we can create (one group per 4 winners)
            var winnerGroupCount = allWinners.Count / 4;
            var remainingWinners = new List<Participant>();

            // Create winner groups (full groups of 4)
            for (var i = 0; i < winnerGroupCount; i++)
            {
                var winnersForGroup = allWinners.Skip(i * 4).Take(4).ToList();

                // If we have exactly 4, create the group
                if (winnersForGroup.Count == 4)
                {
                    ShuffleList(winnersForGroup);

                    var winnersGroup = new Group
                    {
                        RoundNumber = session.CurrentRound,
                        GroupNumber = groups.Count + 1
                    };

                    foreach (var p in winnersForGroup)
                        winnersGroup.Participants[p.Id] = p;

                    groups.Add(winnersGroup);
                }
                else
                {
                    // These winners don't form a complete group, add them to remaining
                    remainingWinners.AddRange(winnersForGroup);
                }
            }

            // Handle remaining winners (fewer than 4)
            if (allWinners.Count % 4 != 0)
            {
                remainingWinners.AddRange(allWinners.Skip(winnerGroupCount * 4));
            }

            // If we have remaining winners (1-3), try to fill them up to 4
            if (remainingWinners.Count > 0 && remainingWinners.Count < 4)
            {
                // If still not enough, fill with regular participants
                if (remainingWinners.Count < 4)
                {
                    var needed = Math.Min(4 - remainingWinners.Count, regularParticipants.Count);
                    if (needed > 0)
                    {
                        var selected = SelectParticipantsMinimizingPairings(regularParticipants, remainingWinners, needed, pairingHistory, participantsInGroupOf3);
                        remainingWinners.AddRange(selected);
                        foreach (var p in selected)
                            regularParticipants.Remove(p);
                    }
                }

                // Create the partial winners group if we have 4 participants
                if (remainingWinners.Count == 4)
                {
                    ShuffleList(remainingWinners);

                    var winnersGroup = new Group
                    {
                        RoundNumber = session.CurrentRound,
                        GroupNumber = groups.Count + 1
                    };

                    foreach (var p in remainingWinners)
                        winnersGroup.Participants[p.Id] = p;

                    groups.Add(winnersGroup);
                    remainingWinners.Clear();
                }
            }

            // Combine priority, regular participants, and any leftover winners
            var allRemaining = new List<Participant>();
            allRemaining.AddRange(remainingWinners);
            //   allRemaining.AddRange(priorityNonWinners);
            allRemaining.AddRange(regularParticipants);
            ShuffleList(allRemaining);
            // Calculate how many more groups we need to create
            var remainingGroupsOf4 = groupsOf4 - groups.Count;
            var remainingGroupsOf3 = groupsOf3;

            // Create groups of 4 based on calculated groupsOf4
            for (int i = 0; i < remainingGroupsOf4 && allRemaining.Count >= 4; i++)
            {
                var groupMembers = new List<Participant>();

                // Select first participant randomly
                var firstIndex = RandomNumberGenerator.GetInt32(allRemaining.Count);
                groupMembers.Add(allRemaining[firstIndex]);
                allRemaining.RemoveAt(firstIndex);

                // Select remaining 3 participants to minimize pairing repetition
                for (int j = 1; j < 4 && allRemaining.Count > 0; j++)
                {
                    var selected = SelectParticipantsMinimizingPairings(allRemaining, groupMembers, 1, pairingHistory, participantsInGroupOf3);
                    if (selected.Count > 0)
                    {
                        groupMembers.Add(selected[0]);
                        allRemaining.Remove(selected[0]);
                    }
                }

                var grp = new Group
                {
                    RoundNumber = session.CurrentRound,
                    GroupNumber = groups.Count + 1
                };

                foreach (var p in groupMembers)
                    grp.Participants[p.Id] = p;

                groups.Add(grp);
            }

            // Create groups of 3 based on calculated groupsOf3 (only if AllowGroupOfThree is true)
            if (session.Settings.AllowGroupOfThree)
            {
                for (int i = 0; i < remainingGroupsOf3 && allRemaining.Count >= 3; i++)
                {
                    var groupMembers = new List<Participant>();

                    // Select first participant randomly
                    var firstIndex = RandomNumberGenerator.GetInt32(allRemaining.Count);
                    groupMembers.Add(allRemaining[firstIndex]);
                    allRemaining.RemoveAt(firstIndex);

                    // Select remaining 2 participants to minimize pairing repetition
                    for (int j = 1; j < 3 && allRemaining.Count > 0; j++)
                    {
                        var selected = SelectParticipantsMinimizingPairings(allRemaining, groupMembers, 1, pairingHistory, participantsInGroupOf3);
                        if (selected.Count > 0)
                        {
                            groupMembers.Add(selected[0]);
                            allRemaining.Remove(selected[0]);
                        }
                    }

                    var grp = new Group
                    {
                        RoundNumber = session.CurrentRound,
                        GroupNumber = groups.Count + 1
                    };

                    foreach (var p in groupMembers)
                        grp.Participants[p.Id] = p;

                    groups.Add(grp);
                }
            }

            // If AllowGroupOfThree is false, remaining participants are skipped
            // If AllowGroupOfThree is true, distribute any remaining participants to existing groups
            if (session.Settings.AllowGroupOfThree && allRemaining.Count > 0 && groups.Count > 0)
            {
                ShuffleList(allRemaining);
                foreach (var p in allRemaining)
                {
                    groups[^1].Participants[p.Id] = p;
                }
            }

            return groups;
        }

        private List<Participant> SelectParticipantsMinimizingPairings(List<Participant> availableParticipants, List<Participant> existingGroupMembers, int count, Dictionary<string, int> pairingHistory, HashSet<string> participantsInGroupOf3)
        {
            var selected = new List<Participant>();

            for (int i = 0; i < count && availableParticipants.Count > 0; i++)
            {
                // Calculate pairing scores for each available participant
                var scores = new Dictionary<Participant, int>();

                foreach (var participant in availableParticipants)
                {
                    int totalPairings = 0;

                    // Count pairings with existing group members
                    foreach (var groupMember in existingGroupMembers)
                    {
                        var pairKey = GetPairKey(participant.Id, groupMember.Id);
                        totalPairings += pairingHistory.GetValueOrDefault(pairKey);
                    }

                    // Also count pairings with already-selected participants in this iteration
                    foreach (var selectedMember in selected)
                    {
                        var pairKey = GetPairKey(participant.Id, selectedMember.Id);
                        totalPairings += pairingHistory.GetValueOrDefault(pairKey);
                    }

                    scores[participant] = totalPairings;
                }

                // Use weighted random selection favoring participants with fewer pairings
                var selectedParticipant = WeightedRandomSelectionByPairingScore(availableParticipants, scores, participantsInGroupOf3);
                selected.Add(selectedParticipant);

                // Don't remove from availableParticipants here - let caller handle it
                // This allows the method to work correctly when called multiple times
            }

            return selected;
        }

        /// <summary>
        /// Builds a dictionary tracking how many times each pair of participants have played together.
        /// Key format: "participantId1|participantId2" (lexicographically sorted to ensure consistency)
        /// Value: number of times they've been in the same group
        /// </summary>
        private Dictionary<string, int> BuildPairingHistory(List<IReadOnlyList<Group>> archivedRounds)
        {
            var pairingCounts = new Dictionary<string, int>();

            foreach (var round in archivedRounds)
            {
                foreach (var group in round)
                {
                    var participantIds = group.Participants.Keys.OrderBy(id => id).ToArray();

                    // Record all pairs within this group
                    for (int i = 0; i < participantIds.Length; i++)
                    {
                        for (int j = i + 1; j < participantIds.Length; j++)
                        {
                            var pairKey = $"{participantIds[i]}|{participantIds[j]}";
                            pairingCounts[pairKey] = pairingCounts.GetValueOrDefault(pairKey) + 1;
                        }
                    }
                }
            }

            return pairingCounts;
        }

        /// <summary>
        /// Creates a consistent pair key for two participant IDs (lexicographically sorted)
        /// </summary>
        private string GetPairKey(string participantId1, string participantId2)
        {
            return string.CompareOrdinal(participantId1, participantId2) < 0
                ? $"{participantId1}|{participantId2}"
                : $"{participantId2}|{participantId1}";
        }

        /// <summary>
        /// Fisher-Yates shuffle using cryptographic random number generator for uniform distribution
        /// </summary>
        private void ShuffleList<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        public static (int g4, int g3) CalculateNumberOfGroups(int n)
        {
            int g4 = n / 4, g3 = 0;

            switch (n % 4)
            {
                case 1:
                    g4 -= 2;
                    g3 = 3;
                    break;
                case 2:
                    g4 -= 1;
                    g3 = 2;
                    break;
                case 3:
                    g3 = 1;
                    break;
            }

            return (g4, g3);
        }

        /// <summary>
        /// Selects a participant using weighted random selection where lower pairing scores have higher probability.
        /// Participants with no previous pairings have the highest chance of selection.
        /// Participants who were in groups of 3 receive a 2x weight multiplier.
        /// </summary>
        private Participant WeightedRandomSelectionByPairingScore(
            List<Participant> participants,
            Dictionary<Participant, int> pairingScores,
            HashSet<string> participantsInGroupOf3)
        {
            // Convert pairing scores to weights (inverse relationship)
            // Score of 0 = weight of 10, score of 1 = weight of 5, score of 2+ = weight of 1
            var weights = new Dictionary<Participant, int>();

            foreach (var participant in participants)
            {
                int score = pairingScores[participant];
                int baseWeight = score switch
                {
                    0 => 4,  // Never played together - highest priority
                    1 => 3,   // Played once - medium priority
                    2 => 2,   // Played twice - low priority
                    _ => 1    // Played 3+ times - lowest priority
                };

                // Apply 2x multiplier for participants who were in groups of 3
                if (participantsInGroupOf3.Contains(participant.Id))
                {
                    baseWeight *= 3;
                }

                weights[participant] = baseWeight;
            }

            // Calculate total weight
            int totalWeight = weights.Values.Sum();

            // Generate random number between 0 and totalWeight
            int randomValue = RandomNumberGenerator.GetInt32(totalWeight);

            // Select participant based on cumulative weight
            int cumulativeWeight = 0;
            foreach (var participant in participants)
            {
                cumulativeWeight += weights[participant];
                if (randomValue < cumulativeWeight)
                {
                    return participant;
                }
            }

            // Fallback (should never reach here)
            return participants[^1];
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
                if (!session.IsGameStarted || session.Groups is null)
                    return ReportOutcomeResult.NotStarted;

                // Handle DropOut (remove from active participants)
                if (outcome == ReportOutcomeType.DropOut)
                {
                    if (session.Participants.TryRemove(participantId, out var removed))
                    {
                        removedParticipant = removed;
                        ParticipantDropped?.Invoke(session, removed);
                        return ReportOutcomeResult.Success;
                    }

                    return ReportOutcomeResult.ParticipantNotFound;
                }

                // For Win/Draw we require the participant to be in a group for the current round.
                // Use GroupNumber from the Group instance rather than deriving the index manually.
                var currentGroup = session.Groups.FirstOrDefault(g => g.Participants.ContainsKey(participantId));
                if (currentGroup is null)
                    return ReportOutcomeResult.ParticipantNotFound;

                // Maintain existing external contract: groupIndex is zero-based index into session.Groups
                groupIndex = currentGroup.GroupNumber - 1;
                if (groupIndex < 0)
                    groupIndex = null;

                if (currentGroup.HasResult)
                    return ReportOutcomeResult.AlreadyEnded;

                if (outcome == ReportOutcomeType.Win)
                {
                    // verify participant still present
                    if (!session.Participants.ContainsKey(participantId))
                        return ReportOutcomeResult.ParticipantNotFound;

                    currentGroup.WinnerParticipantId = participantId;
                    currentGroup.IsDraw = false;
                    winnerParticipantId = participantId;

                    // Notify subscribers
                    GameEnded?.Invoke(session, outcome, participantId);
                    return ReportOutcomeResult.Success;
                }

                if (outcome == ReportOutcomeType.Draw)
                {
                    currentGroup.IsDraw = true;
                    currentGroup.WinnerParticipantId = null;

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