using SessionApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace SessionApp.Services
{
    /// <summary>
    /// Service responsible for generating and randomizing groups for game rounds.
    /// Handles participant distribution, pairing history tracking, and group balancing.
    /// </summary>
    /// 

    class CustomGroup
    {
        public Guid Id { get; set; } = Guid.Empty;
        public List<Participant> ParticipantList { get; set; } = new List<Participant>();
        public bool AutoFill { get; set; } = true;
    }

    class GroupsToFill
    {
        public bool CustomGroup { get; set; } = false;
        public bool AutoFill { get; set; }
        public bool WinnersGroup { get; set; } = false;
        public int GroupSize { get; set; }
        public Group FillGroup { get; set; } = new Group();
    }

    public class GroupGenerationService
    {
        int _GroupOf4 = 4;
        int _GroupOf3 = 3;

        /// <summary>
        /// Generates randomized groups for a round while minimizing repeat pairings
        /// and balancing group sizes based on session settings.
        /// </summary>
        public List<Group> RandomizeRound(List<Participant> participants, RoomSession session, IReadOnlyList<Group> snapshotGroups, RoomCodeService.HandleRoundOptions task)
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

            if (task != RoomCodeService.HandleRoundOptions.GenerateFirstRound && lastRound != null)
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

            return HandleGroupNumber(groups, session, lastRound);
        }

        /// <summary>
        /// Generates randomized groups for a round while minimizing repeat pairings
        /// and balancing group sizes based on session settings.
        /// Handles Custom Groups
        /// </summary>
        public List<Group> RandomizeRoundIncludeCustom(List<Participant> participants, RoomSession session, IReadOnlyList<Group> snapshotGroups, RoomCodeService.HandleRoundOptions task)
        {
            var completeCustomGroups = new List<Group>();
            var incompleteCustomGroups = new List<Group>();
            var participantsInCompleteCustomGroups = new HashSet<string>();
            var participantsInIncompleteCustomGroups = new HashSet<string>();

            // Process custom groups
            var customGroupList = new List<CustomGroup>();

            if (session.Settings.AllowCustomGroups)
            {
                foreach (var participant in participants.Where(x => x.InCustomGroup != Guid.Empty))
                {
                    if (!customGroupList.Any(g => g.Id == participant.InCustomGroup))
                    {
                        var newCustomGroup = new CustomGroup
                        {
                            Id = participant.InCustomGroup,
                            AutoFill = participant.AutoFill,
                            ParticipantList = new List<Participant> { participant }
                        };
                        customGroupList.Add(newCustomGroup);
                    }
                    else
                    {
                        customGroupList.First(g => g.Id == participant.InCustomGroup).ParticipantList.Add(participant);
                    }
                }
            }

            foreach (var customGroup in customGroupList)
            {
                // - Has 4+ participants
                // - Has AutoFill disabled
                if (customGroup.ParticipantList.Count >= _GroupOf4 || !customGroup.AutoFill)
                {
                    var completeGroup = new Group
                    {
                        RoundNumber = session.CurrentRound,
                        StartedAtUtc = DateTime.UtcNow,
                        IsCustom = true,
                        AutoFill = false
                    };

                    foreach (var p in customGroup.ParticipantList)
                    {
                        completeGroup.AddParticipant(p);
                        participantsInCompleteCustomGroups.Add(p.Id);
                    }

                    completeCustomGroups.Add(completeGroup);
                }
                else
                {
                    // Incomplete custom group with AutoFill enabled (will determine target size later)
                    var incompleteGroup = new Group
                    {
                        RoundNumber = session.CurrentRound,
                        StartedAtUtc = DateTime.UtcNow,
                        IsCustom = true,
                        AutoFill = true
                    };

                    foreach (var p in customGroup.ParticipantList)
                    {
                        incompleteGroup.AddParticipant(p);
                        participantsInIncompleteCustomGroups.Add(p.Id);
                    }

                    incompleteCustomGroups.Add(incompleteGroup);
                }
            }

            // Get available participants (excluding those in complete custom groups and incomplete custom groups)
            var availableParticipants = new List<Participant>();
            foreach (var participant in participants)
            {
                if (!participantsInCompleteCustomGroups.Contains(participant.Id) && !participantsInIncompleteCustomGroups.Contains(participant.Id))
                {
                    availableParticipants.Add(participant);
                }
            }

            // Calculate total participants for grouping
            var totalParticipantsForGrouping = availableParticipants.Count + participantsInIncompleteCustomGroups.Count;

            // Calculate how many groups we need total
            var (groupsOf4, groupsOf3) = CalculateNumberOfGroups(totalParticipantsForGrouping);

            // If AllowGroupOfThree is false, only create groups of 4
            if (!session.Settings.AllowGroupOfThree)
            {
                groupsOf4 = totalParticipantsForGrouping / 4;
                groupsOf3 = 0;
            }

            // Now create the groups to fill list with appropriate target sizes
            var groupsToFillList = new List<GroupsToFill>();

            // First, assign target sizes to incomplete custom groups
            foreach (var incompleteGroup in incompleteCustomGroups)
            {
                int targetSize;

                // Randomly choose between groups of 4 and 3 if both are available
                if (groupsOf4 > 0 && groupsOf3 > 0 && session.Settings.AllowGroupOfThree)
                {
                    // Random 50/50 choice between 4 and 3
                    targetSize = RandomNumberGenerator.GetInt32(2) == 0 ? _GroupOf4 : _GroupOf3;

                    if (targetSize == _GroupOf4)
                        groupsOf4--;
                    else
                        groupsOf3--;
                }
                else if (groupsOf4 > 0)
                {
                    targetSize = _GroupOf4;
                    groupsOf4--;
                }
                else if (groupsOf3 > 0 && session.Settings.AllowGroupOfThree)
                {
                    targetSize = _GroupOf3;
                    groupsOf3--;
                }
                else
                {
                    targetSize = _GroupOf4; // Fallback
                }

                groupsToFillList.Add(new GroupsToFill
                {
                    CustomGroup = true,
                    AutoFill = true,
                    WinnersGroup = false,
                    GroupSize = targetSize,
                    FillGroup = incompleteGroup
                });
            }

            // Create additional groups of 4
            for (int i = 0; i < groupsOf4; i++)
            {
                var grp = new Group
                {
                    RoundNumber = session.CurrentRound,
                    StartedAtUtc = DateTime.UtcNow,
                    IsCustom = false,
                    AutoFill = true
                };

                groupsToFillList.Add(new GroupsToFill
                {
                    CustomGroup = false,
                    AutoFill = true,
                    WinnersGroup = false,
                    GroupSize = _GroupOf4,
                    FillGroup = grp
                });
            }

            // Create groups of 3 if allowed
            if (session.Settings.AllowGroupOfThree)
            {
                for (int i = 0; i < groupsOf3; i++)
                {
                    var grp = new Group
                    {
                        RoundNumber = session.CurrentRound,
                        StartedAtUtc = DateTime.UtcNow,
                        IsCustom = false,
                        AutoFill = true
                    };

                    groupsToFillList.Add(new GroupsToFill
                    {
                        CustomGroup = false,
                        AutoFill = true,
                        WinnersGroup = false,
                        GroupSize = _GroupOf3,
                        FillGroup = grp
                    });
                }
            }

            // Get the last round's groups to determine winners
            var lastRound = session.ArchivedRounds.Count > 0
                ? session.ArchivedRounds[^1]
                : null;

            // Build pairing history from all archived rounds
            var pairingHistory = BuildPairingHistory(session.ArchivedRounds);

            // Build comprehensive group-of-3 history across ALL rounds
            var groupOf3History = BuildGroupOf3History(session.ArchivedRounds, session.Settings.FurtherReduceOddsOfGroupOfThree);

            // Collect all winners from last round
            var allWinners = new List<Participant>();

            if (task != RoomCodeService.HandleRoundOptions.GenerateFirstRound && lastRound != null)
            {
                foreach (var group in lastRound)
                {
                    if (!string.IsNullOrEmpty(group.WinnerParticipantId) && session.Settings.PrioitizeWinners)
                    {
                        if (availableParticipants.Any(p => p.Id == group.WinnerParticipantId))
                        {
                            var winner = availableParticipants.First(p => p.Id == group.WinnerParticipantId);
                            allWinners.Add(winner);
                        }
                    }
                }
            }

            // Separate regular participants
            var regularParticipants = new List<Participant>();
            foreach (var participant in availableParticipants)
            {
                if (!allWinners.Any(w => w.Id == participant.Id))
                {
                    regularParticipants.Add(participant);
                }
            }

            // Shuffle the groups to fill so custom groups don't get priority
            ShuffleList(groupsToFillList);

            // Fill winner groups first
            var remainingWinners = FillWinnerGroups(allWinners, groupsToFillList, regularParticipants, pairingHistory, groupOf3History);

            // Combine all remaining participants and shuffle
            var allRemaining = new List<Participant>();
            allRemaining.AddRange(remainingWinners);
            allRemaining.AddRange(regularParticipants);
            ShuffleList(allRemaining);

            // Fill all groups to their target size
            foreach (var groupToFill in groupsToFillList)
            {
                while (groupToFill.FillGroup.Participants.Values.Count < groupToFill.GroupSize && allRemaining.Count > 0)
                {
                    var existingMembers = groupToFill.FillGroup.Participants.Values.ToList();
                    var selected = SelectParticipantsMinimizingPairings(allRemaining, existingMembers, 1, pairingHistory, groupOf3History);

                    if (selected.Count > 0)
                    {
                        var participant = selected[0];
                        groupToFill.FillGroup.AddParticipant(participant);
                        allRemaining.Remove(participant);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            // Validate all groups have correct size
            foreach (var groupToFill in groupsToFillList)
            {
                if (groupToFill.FillGroup.Participants.Values.Count != groupToFill.GroupSize)
                {
                    throw new InvalidOperationException(
                        $"Group {groupToFill.FillGroup.GroupNumber} has {groupToFill.FillGroup.Participants.Values.Count} participants but expected {groupToFill.GroupSize}. " +
                        $"Total participants for grouping: {totalParticipantsForGrouping}, Remaining: {allRemaining.Count}");
                }
            }

            // Handle remaining participants - put in bye group
            if (allRemaining.Count > 0)
            {
                var byeGroup = new Group
                {
                    RoundNumber = session.CurrentRound,
                    StartedAtUtc = DateTime.UtcNow,
                    IsCustom = false,
                    AutoFill = true,
                    GroupNumber = 99
                };

                foreach (var p in allRemaining)
                    byeGroup.AddParticipant(p);

                completeCustomGroups.Add(byeGroup);
            }

            // Combine all groups
            var allGroups = new List<Group>();
            allGroups.AddRange(completeCustomGroups);
            allGroups.AddRange(groupsToFillList.Select(g => g.FillGroup));

            return HandleGroupNumber(allGroups, session, lastRound);
        }

        /// <summary>
        /// Fills groups with winners, prioritizing complete winner groups of 4.
        /// Returns any remaining winners that couldn't form complete groups.
        /// </summary>
        private List<Participant> FillWinnerGroups(List<Participant> allWinners, List<GroupsToFill> groupsToFillList, List<Participant> regularParticipants, Dictionary<string, int> pairingHistory, Dictionary<string, int> groupOf3History)
        {
            var remainingWinners = new List<Participant>();

            // Calculate how many winner groups we can create (one group per 4 winners)
            var winnerGroupCount = allWinners.Count / 4;

            // Shuffle winners once before distributing
            ShuffleList(allWinners);

            // Create winner groups (full groups of 4)
            for (var i = 0; i < winnerGroupCount; i++)
            {
                var winningGroupToFill = groupsToFillList.FirstOrDefault(x => x.FillGroup.Participants.Count == 0 && x.GroupSize == 4);

                if (winningGroupToFill != null)
                {
                    var winnersForGroup = allWinners.Skip(i * 4).Take(4).ToList();

                    if (winnersForGroup.Count == 4)
                    {
                        ShuffleList(winnersForGroup);
                        winningGroupToFill.WinnersGroup = true;

                        foreach (var p in winnersForGroup)
                            winningGroupToFill.FillGroup.AddParticipant(p);
                    }
                    else
                    {
                        remainingWinners.AddRange(winnersForGroup);
                    }
                }
            }

            // Handle remaining winners (fewer than 4)
            if (allWinners.Count % 4 != 0)
            {
                remainingWinners.AddRange(allWinners.Skip(winnerGroupCount * 4));
            }

            var groupToFill = groupsToFillList
                .Where(g => g.GroupSize == 4)
                .OrderBy(x => x.FillGroup.Participants.Count)
                .FirstOrDefault();

            if (groupToFill == null)
                return remainingWinners;

            // If we have remaining winners (1-3), try to fill them up to 4
            if (remainingWinners.Count > 0 && remainingWinners.Count < 4)
            {
                var needed = Math.Min(4 - remainingWinners.Count, regularParticipants.Count);
                if (needed > 0)
                {
                    var selected = SelectParticipantsMinimizingPairings(regularParticipants, remainingWinners, needed, pairingHistory, groupOf3History);

                    remainingWinners.AddRange(selected);
                    foreach (var p in selected)
                        regularParticipants.Remove(p);
                }

                // Create the partial winners group if we have 4 participants
                if (remainingWinners.Count == 4)
                {
                    ShuffleList(remainingWinners);
                    groupToFill.WinnersGroup = true;

                    foreach (var p in remainingWinners)
                        groupToFill.FillGroup.AddParticipant(p);

                    remainingWinners.Clear();
                }
            }

            return remainingWinners;
        }

        // look to remove this
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

        private List<Participant> CreateWinnersV2Groups(List<Participant> allWinners, List<Group> groupsToFill, RoomSession session, List<Participant> regularParticipants, Dictionary<string, int> pairingHistory, Dictionary<string, int> groupOf3History)
        {
            // Calculate how many winner groups we can create (one group per 4 winners)
            var winnerGroupCount = allWinners.Count / 4;
            var remainingWinners = new List<Participant>();

            // Create winner groups (full groups of 4)
            for (var i = 0; i < winnerGroupCount; i++)
            {
                var winningGroupToFill = groupsToFill.FirstOrDefault(x => x.Participants.Count() == 0);

                if (winningGroupToFill != null)
                {
                    ShuffleList(allWinners);

                    var winnersForGroup = allWinners.Skip(i * 4).Take(4).ToList();

                    // If we have exactly 4, create the group
                    if (winnersForGroup.Count == 4)
                    {
                        ShuffleList(winnersForGroup);

                        // Use AddParticipant to preserve shuffled order
                        foreach (var p in winnersForGroup)
                            winningGroupToFill.AddParticipant(p);
                    }
                    else
                    {
                        // These winners don't form a complete group, add them to remaining
                        remainingWinners.AddRange(winnersForGroup);
                    }
                }
            }

            // Handle remaining winners (fewer than 4)
            if (allWinners.Count % 4 != 0)
            {
                remainingWinners.AddRange(allWinners.Skip(winnerGroupCount * 4));
            }

            var groupToFill = groupsToFill.OrderBy(x => x.Participants.Count).FirstOrDefault();
            if (groupToFill == null)
                return remainingWinners;

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

                    // Use AddParticipant to preserve shuffled order
                    foreach (var p in remainingWinners)
                        groupToFill.AddParticipant(p);

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

                            if (FurtherReduceOddsOfGroupOfThree)
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

        public List<Group> HandleGroupNumber(List<Group> groups, RoomSession session, IReadOnlyList<Group> lastRound)
        {
            // Randomize group numbers
            var winnersGroups = new List<Group>();
            var customGroups = new List<Group>();
            var regularGroups = new List<Group>();

            foreach (var group in groups)
            {
                if (group.GroupNumber == 99)
                    continue;

                var hasWinner = lastRound?.Any(lr => !string.IsNullOrEmpty(lr.WinnerParticipantId) && group.Participants.Values.Any(p => p.Id == lr.WinnerParticipantId)) ?? false;

                if (hasWinner)
                {
                    winnersGroups.Add(group);
                }
                else if (group.IsCustom)
                {
                    customGroups.Add(group);
                }
                else
                {
                    regularGroups.Add(group);
                }
            }

            ShuffleList(winnersGroups);
            ShuffleList(regularGroups);
            ShuffleList(customGroups);

            var reorderedGroups = new List<Group>();
            reorderedGroups.AddRange(winnersGroups);
            reorderedGroups.AddRange(regularGroups);
            reorderedGroups.AddRange(customGroups);

            var groupnumber = 1;
            // Randomize participant order within each group // look into adding a feature to minimuze the number of times a player goes first or last
            foreach (var group in reorderedGroups)
            {
                group.GroupNumber = groupnumber++;
                var participantsList = group.Participants.Values.ToList();
                var participantCount = participantsList.Count;

                if (participantCount > 0)
                {
                    // Create a list of order numbers from 1 to participant count
                    var orderNumbers = Enumerable.Range(1, participantCount).ToList();

                    // Shuffle the order numbers
                    ShuffleList(orderNumbers);

                    // Assign shuffled order numbers to participants
                    for (int i = 0; i < participantsList.Count; i++)
                    {
                        participantsList[i].Order = orderNumbers[i];
                    }
                }
            }

            return reorderedGroups;
        }
    }
}