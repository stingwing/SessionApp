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

                var groups = RanzomizeRound(participants, session, Array.Empty<Group>(), task);

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

        private List<Group> RanzomizeRound(List<Participant> participants, RoomSession session, IReadOnlyList<Group> snapshotGroups, HandleRoundOptions task)
        {
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

            
            // Build pairing history from all archived rounds
            var pairingHistory = BuildPairingHistory(session.ArchivedRounds);

            // NEW: Build comprehensive group-of-3 history across ALL rounds
            var groupOf3History = BuildGroupOf3History(session.ArchivedRounds, session.Settings.FurtherReduceOddsOfGroupOfThree);

            // Collect all winners from last round
            var allWinners = new List<Participant>();

            if (task != HandleRoundOptions.GenerateFirstRound && lastRound != null)
            {
                // Track winners from last round only (for prioritization)
                foreach (var group in lastRound)
                {
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

            // Pass the comprehensive history instead of just last round
            var remainingWinners = CreateWinnersGroups(allWinners, groups, session, regularParticipants, pairingHistory, groupOf3History);

            // Combine priority, regular participants, and any leftover winners
            var allRemaining = new List<Participant>();
            allRemaining.AddRange(remainingWinners);
            allRemaining.AddRange(regularParticipants);
            ShuffleList(allRemaining);
            
            // Calculate how many more groups we need to create
            var remainingGroupsOf4 = groupsOf4 - groups.Count;
            var remainingGroupsOf3 = groupsOf3;

            // Create groups of 4 based on calculated groupsOf4
            for (int i = 0; i < remainingGroupsOf4 && allRemaining.Count >= 4; i++)
            {
                var groupMembers = new List<Participant>();

                // Select 4 participants to minimize pairing repetition
                // Pass the comprehensive history
                var selected = SelectParticipantsMinimizingPairings(
                    allRemaining, 
                    new List<Participant>(), // Start with empty group
                    4, // Select 4 participants
                    pairingHistory, 
                    groupOf3History);

                // Add selected participants and remove them from available pool
                foreach (var p in selected)
                {
                    groupMembers.Add(p);
                    allRemaining.Remove(p);
                }

                var grp = new Group
                {
                    RoundNumber = session.CurrentRound,
                    GroupNumber = groups.Count + 1,
                    StartedAtUtc = DateTime.UtcNow
                };

                // Use AddParticipant to preserve selection order
                foreach (var p in groupMembers)
                    grp.AddParticipant(p);

                groups.Add(grp);
            }

            // Create groups of 3 based on calculated groupsOf3 (only if AllowGroupOfThree is true)
            if (session.Settings.AllowGroupOfThree)
            {
                for (int i = 0; i < remainingGroupsOf3 && allRemaining.Count >= 3; i++)
                {
                    var groupMembers = new List<Participant>();

                    // Select 3 participants to minimize pairing repetition
                    // Prioritize those who have NEVER been in a group of 3
                    var selected = SelectParticipantsMinimizingPairings(
                        allRemaining, 
                        new List<Participant>(), // Start with empty group
                        3, // Select 3 participants
                        pairingHistory, 
                        groupOf3History);

                    // Add selected participants and remove them from available pool
                    foreach (var p in selected)
                    {
                        groupMembers.Add(p);
                        allRemaining.Remove(p);
                    }

                    var grp = new Group
                    {
                        RoundNumber = session.CurrentRound,
                        GroupNumber = groups.Count + 1,
                        StartedAtUtc = DateTime.UtcNow
                    };

                    // Use AddParticipant to preserve selection order
                    foreach (var p in groupMembers)
                        grp.AddParticipant(p);

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
                    groups[^1].AddParticipant(p);
                }
            }

            return groups;
        }

        private List<Participant> CreateWinnersGroups(List<Participant> allWinners, List<Group> groups, RoomSession session, List<Participant> regularParticipants, Dictionary<string, int> pairingHistory, Dictionary<string, int> groupOf3History)
        {
            // Calculate how many winner groups we can create (one group per 4 winners)
            var winnerGroupCount = allWinners.Count / 4;
            var remainingWinners = new List<Participant>();

            // Create winner groups (full groups of 4)
            for (var i = 0; i < winnerGroupCount; i++)
            {
                ShuffleList(allWinners);

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

                    // Use AddParticipant to preserve shuffled order
                    foreach (var p in winnersForGroup)
                        winnersGroup.AddParticipant(p);

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
                        // Pass comprehensive history
                        var selected = SelectParticipantsMinimizingPairings(regularParticipants, remainingWinners, needed, pairingHistory, groupOf3History);

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

                    // Use AddParticipant to preserve shuffled order
                    foreach (var p in remainingWinners)
                        winnersGroup.AddParticipant(p);

                    groups.Add(winnersGroup);
                    remainingWinners.Clear();
                }
            }

            return remainingWinners;
        }

        /// <summary>
        /// Selects participants to minimize repeat pairings using weighted random selection.
        /// Returns the specified count of participants, or fewer if not enough are available.
        /// Does NOT modify the availableParticipants list - caller must handle removal.
        /// </summary>
        private List<Participant> SelectParticipantsMinimizingPairings(List<Participant> availableParticipants, List<Participant> existingGroupMembers, int count, Dictionary<string, int> pairingHistory, Dictionary<string, int> groupOf3History)
        {
            var selected = new List<Participant>();

            // Create a working copy so we don't modify the original list
            var workingList = new List<Participant>(availableParticipants);

            // Select up to 'count' participants
            for (int i = 0; i < count && workingList.Count > 0; i++)
            {
                // Calculate pairing scores for each available participant
                var scores = new Dictionary<Participant, int>();

                foreach (var participant in workingList)
                {
                    int totalPairings = 0;

                    // Count pairings with existing group members
                    foreach (var groupMember in existingGroupMembers)
                    {
                        var pairKey = GetPairKey(participant.Id, groupMember.Id);
                        totalPairings += pairingHistory.GetValueOrDefault(pairKey);
                    }

                    // Count pairings with already-selected participants in this iteration
                    foreach (var selectedMember in selected)
                    {
                        var pairKey = GetPairKey(participant.Id, selectedMember.Id);
                        totalPairings += pairingHistory.GetValueOrDefault(pairKey);
                    }

                    // NEW: Count pairings with other candidates in the working list
                    // This ensures we minimize repeat pairings even on the first selection
                    foreach (var otherCandidate in workingList)
                    {
                        if (otherCandidate.Id == participant.Id)
                            continue; // Don't count pairing with self

                        var pairKey = GetPairKey(participant.Id, otherCandidate.Id);
                        totalPairings += pairingHistory.GetValueOrDefault(pairKey);
                    }

                    scores[participant] = totalPairings;
                }

                // Use weighted random selection favoring participants with fewer pairings
                // and fewer group-of-3 occurrences
                var selectedParticipant = WeightedRandomSelectionByPairingScore(workingList, scores, groupOf3History);

                selected.Add(selectedParticipant);

                // Remove from working list to prevent selecting the same participant twice
                workingList.Remove(selectedParticipant);
            }

            return selected;
        }

        /// <summary>
        /// Selects a participant using weighted random selection where lower pairing scores have higher probability.
        /// Participants with no previous pairings have the highest chance of selection.
        /// Participants who were in groups of 3 receive a bonus weight multiplier to help balance group sizes.
        /// </summary>
        private Participant WeightedRandomSelectionByPairingScore(List<Participant> participants, Dictionary<Participant, int> pairingScores, Dictionary<string, int> groupOf3History)
        {
            // Edge case: if only one participant, return immediately
            if (participants.Count == 1)
                return participants[0];

            var weights = new Dictionary<Participant, double>();

            // Find min and max scores for normalization
            int minScore = pairingScores.Values.Min();
            int maxScore = pairingScores.Values.Max();
            int scoreRange = maxScore - minScore;

            foreach (var participant in participants)
            {
                int score = pairingScores[participant];

                // Use exponential decay for weights to create stronger differentiation
                double baseWeight;
                if (scoreRange == 0)
                {
                    // All scores are the same, use equal weights
                    baseWeight = 1.0;
                }
                else
                {
                    // Exponential decay: weight = 2^(-score)
                    baseWeight = Math.Pow(2, -score);
                }

                // Apply PROGRESSIVE BONUS based on group-of-3 history
                // The more times they've been in a group of 3, the HIGHER their weight for being selected for groups of 4
                // This compensates for the disadvantage of being in smaller groups
                int groupOf3Count = groupOf3History.GetValueOrDefault(participant.Id);

                double groupOf3Multiplier = groupOf3Count switch
                {
                    0 => 1.0,     // Never in group of 3 - neutral
                    1 => 2.0,     // Been in 1 group of 3 - 2x bonus
                    2 => 4.0,     // Been in 2 groups of 3 - 4x bonus
                    3 => 8.0,     // Been in 3 groups of 3 - 8x bonus (exponential)
                    4 => 16.0,    // Been in 4 groups of 3 - 16x bonus
                    _ => 32.0     // Been in 5+ groups of 3 - 32x bonus (very strong)
                };

                weights[participant] = baseWeight * groupOf3Multiplier;
            }

            // Calculate total weight
            double totalWeight = weights.Values.Sum();

            // Generate random number between 0 and totalWeight
            double randomValue = RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue * totalWeight;

            // Select participant based on cumulative weight
            double cumulativeWeight = 0.0;
            foreach (var participant in participants)
            {
                cumulativeWeight += weights[participant];
                if (randomValue <= cumulativeWeight)
                {
                    return participant;
                }
            }

            // Fallback
            return participants[^1];
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
        /// Builds a dictionary tracking how many times each participant has been in a group of 3.
        /// Key: participantId
        /// Value: number of times they've been in a group of 3 across all archived rounds
        /// </summary>
        private Dictionary<string, int> BuildGroupOf3History(List<IReadOnlyList<Group>> archivedRounds, bool FurtherReduceOddsOfGroupOfThree)
        {
            var groupOf3Counts = new Dictionary<string, int>();

            foreach (var round in archivedRounds)
            {
                foreach (var group in round)
                {
                    // Only count groups with exactly 3 participants
                    if (group.Participants.Count == 3)
                    {
                        foreach (var participantId in group.Participants.Keys)
                        {
                            groupOf3Counts[participantId] = groupOf3Counts.GetValueOrDefault(participantId) + 1;
                            
                            if(FurtherReduceOddsOfGroupOfThree)
                                groupOf3Counts[participantId] = groupOf3Counts.GetValueOrDefault(participantId) + 3;
                        }
                    }
                }
            }

            return groupOf3Counts;
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
            Invalid
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

            lock (session)
            {
                //if (!session.IsGameStarted || session.Groups is null)
                //    return ReportOutcomeResult.NotStarted;

              //  if(session.IsGameEnded)
             //       return ReportOutcomeResult.AlreadyEnded;

                // Handle DropOut (remove from active participants)
                if (outcome == ReportOutcomeType.DropOut)
                {
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