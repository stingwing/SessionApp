using SessionApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using static SessionApp.Services.RoomCodeService;

namespace SessionApp.Services
{
    public class GameActionService
    {
        private readonly RoomCodeService _roomService;

        public GameActionService(RoomCodeService roomService)
        {
            _roomService = roomService;
        }

        public GameActionResult HandleStartOrResetRound(RoomSession session, HandleRoundOptions task)
        {
            if (session.Groups is null || session.Groups.Count == 0)
                return GameActionResult.Error("No groups available to start");

            lock (session)
            {
                var currentTime = DateTime.UtcNow;
                foreach (var group in session.Groups)
                {
                    if (task == HandleRoundOptions.StartRound)
                        group.RoundStarted = true;
                    else if (task == HandleRoundOptions.ResetRound)
                        group.RoundStarted = false;

                    group.StartedAtUtc = currentTime;

                    foreach (var participant in group.Participants)
                    {
                        if (session.Participants.ContainsKey(participant.Key))
                            participant.Value.Commander = session.Participants[participant.Key].Commander;
                    }
                }
            }

            return GameActionResult.Success(session.Groups);
        }

        public GameActionResult HandleSetResult(RoomSession session, int groupNumber, int roundNumber, HandleRoundOptions task, string participantId)
        {
            if (session.Groups is null || session.Groups.Count == 0)
                return GameActionResult.Error("No groups available");

            Group? updateGroup = null;

            lock (session)
            {
                // Try to find group in current round
                if (session.CurrentRound == roundNumber)
                {
                    updateGroup = session.Groups.FirstOrDefault(g => g.GroupNumber == groupNumber);
                }
                else
                {
                    // Search archived rounds
                    updateGroup = FindGroupInArchivedRounds(session, groupNumber, roundNumber);
                }

                if (updateGroup is null)
                    return GameActionResult.Error("Group not found");

                ApplyResultToGroup(updateGroup, task, participantId);
            }

            return GameActionResult.Success(session.Groups);
        }

        public GameActionResult HandleMoveParticipant(RoomSession session, int sourceGroupNumber, int targetGroupNumber, int roundNumber, string participantId)
        {
            if (session.Groups is null || session.Groups.Count == 0)
                return GameActionResult.Error("No groups available");

            Group? sourceGroup = null;
            Group? targetGroup = null;

            lock (session)
            {
                // Try to find groups in current round
                if (session.CurrentRound == roundNumber)
                {
                    sourceGroup = session.Groups.FirstOrDefault(g => g.GroupNumber == sourceGroupNumber);
                    targetGroup = session.Groups.FirstOrDefault(g => g.GroupNumber == targetGroupNumber);
                }
                else
                {
                    // Search archived rounds
                    (sourceGroup, targetGroup) = FindSourceAndTargetGroupsInArchivedRounds(session, sourceGroupNumber, targetGroupNumber, roundNumber);
                }

                if (sourceGroup is null)
                    return GameActionResult.Error("Source group not found");

                if (targetGroup is null)
                    return GameActionResult.Error("Target group not found");

                if (!sourceGroup.Participants.ContainsKey(participantId))
                    return GameActionResult.Error("Participant not found in source group");

                // Move participant
                var participant = sourceGroup.Participants[participantId];
                sourceGroup.RemoveParticipant(participantId);
                targetGroup.AddParticipant(participant);
            }

            return GameActionResult.SuccessWithMove(session.Groups, participantId, sourceGroupNumber, targetGroupNumber);
        }

        public GameActionResult HandleGenerateRound(string code, HandleRoundOptions task)
        {
            var errorMessage = string.Empty;
            var groups = _roomService.HandleRound(code, task, ref errorMessage);
            
            if (groups is null)
                return GameActionResult.Error($"Error: {errorMessage}");

            var round = groups.FirstOrDefault()?.RoundNumber ?? -1;
            return GameActionResult.SuccessWithRound(groups, round);
        }

        public GameActionResult HandleCreateCustomGroup(RoomSession session, List<string> participantIds, bool autoFill)
        {
            if (!session.Settings.AllowCustomGroups)
                return GameActionResult.Error("Custom groups are not allowed for this session");

            if (participantIds == null || !participantIds.Any())
                return GameActionResult.Error("Custom groups have at least one Player");

            if (session.HasAnyRoundStarted())
                return GameActionResult.Error("Cannot create custom groups after the round has started");

            var existingCustomGroupGUID = new List<Guid>();

            lock (session)
            {
                // Verify all participants exist in the session
                var participants = new List<Participant>();
                foreach (var id in participantIds)
                {
                    if (!session.Participants.ContainsKey(id))
                        return GameActionResult.Error($"Participant {id} not found in session");

                    participants.Add(session.Participants[id]);
                }

                // Create new custom group with a unique Guid
                var customGroupGuid = Guid.NewGuid();
                foreach (var participant in participants)
                {
                    // Set the InCustomGroup field on the participant in the session
                    if (session.Participants.TryGetValue(participant.Id, out var sessionParticipant))
                    {
                        sessionParticipant.InCustomGroup = customGroupGuid;
                        sessionParticipant.AutoFill = autoFill;
                    }
                }

                // Identify custom groups that now have only 1 participant
                var singleParticipantGroupIds = session.Participants.Values
                    .Where(p => p.InCustomGroup != Guid.Empty && p.InCustomGroup != customGroupGuid)
                    .GroupBy(p => p.InCustomGroup)
                    .Where(g => g.Count() == 1)
                    .Select(g => g.Key)
                    .ToList();

                // Delete single-participant custom groups
                foreach (var groupId in singleParticipantGroupIds)
                {
                    HandleDeleteCustomGroup(session, groupId);
                }
            }

            return GameActionResult.Success(session.Groups);
        }

        public GameActionResult HandleDeleteCustomGroup(RoomSession session, Guid inCustomGroupId)
        {
            //if (session.Groups is null || session.Groups.Count == 0)
            //    return GameActionResult.Error("No groups available");

            //if (!session.Settings.AllowCustomGroups)
            //    return GameActionResult.Error("Custom groups are not allowed for this session");

            if (session.HasAnyRoundStarted())
                return GameActionResult.Error("Cannot delete custom groups after the round has started");

            lock (session)
            {
                foreach (var participant in session.Participants.Values.Where(p => p.InCustomGroup == inCustomGroupId))
                {
                    participant.AutoFill = false; // Set AutoFill flag for participant
                    participant.InCustomGroup = Guid.Empty; // Clear InCustomGroup flag for participant before checking existing groups

                    if (session.Groups == null)
                        continue;

                    foreach (var existingGroup in session.Groups.ToList())
                    {
                        if (existingGroup.Participants.ContainsKey(participant.Id))
                        {
                            participant.InCustomGroup = Guid.Empty; // Clear InCustomGroup flag for participant
                            participant.AutoFill = false; // Clear AutoFill flag for participant
                        }
                    }
                }
            }

            return GameActionResult.Success(session.Groups);
        }

        public bool EndRound(RoomSession session)
        {
            RoomCodeService.CleanupExpiredSession(session);
            return true;
        }

        private Group? FindGroupInArchivedRounds(RoomSession session, int groupNumber, int roundNumber)
        {
            foreach (var archivedRound in session.ArchivedRounds)
            {
                var group = archivedRound.FirstOrDefault(g => g.GroupNumber == groupNumber && g.RoundNumber == roundNumber);
                if (group != null)
                    return group;
            }
            return null;
        }

        private (Group? sourceGroup, Group? targetGroup) FindSourceAndTargetGroupsInArchivedRounds(RoomSession session, int sourceGroupNumber, int targetGroupNumber, int roundNumber)
        {
            Group? sourceGroup = null;
            Group? targetGroup = null;

            foreach (var archivedRound in session.ArchivedRounds)
            {
                if (sourceGroup == null)
                    sourceGroup = archivedRound.FirstOrDefault(g => g.GroupNumber == sourceGroupNumber && g.RoundNumber == roundNumber);

                if (targetGroup == null)
                    targetGroup = archivedRound.FirstOrDefault(g => g.GroupNumber == targetGroupNumber && g.RoundNumber == roundNumber);

                if (sourceGroup != null && targetGroup != null)
                    break;
            }

            return (sourceGroup, targetGroup);
        }

        private void ApplyResultToGroup(Group group, HandleRoundOptions task, string participantId)
        {
            if (task == HandleRoundOptions.SetWinner && !string.IsNullOrEmpty(participantId))
            {
                group.WinnerParticipantId = participantId;
                group.IsDraw = false;
            }
            else if (task == HandleRoundOptions.SetDraw)
            {
                group.WinnerParticipantId = null;
                group.IsDraw = true;
            }
            else if (task == HandleRoundOptions.SetNoResult)
            {
                group.IsDraw = false;
                group.WinnerParticipantId = null;
            }
        }
    }

    public class GameActionResult
    {
        public bool IsSuccess { get; set; }
        public string? ErrorMessage { get; set; }
        public IReadOnlyList<Group>? Groups { get; set; }
        public int? RoundNumber { get; set; }
        public string? ParticipantId { get; set; }
        public int? SourceGroup { get; set; }
        public int? TargetGroup { get; set; }

        public static GameActionResult Success(IReadOnlyList<Group>? groups)
        {
            return new GameActionResult { IsSuccess = true, Groups = groups };
        }

        public static GameActionResult SuccessWithRound(IReadOnlyList<Group> groups, int roundNumber)
        {
            return new GameActionResult { IsSuccess = true, Groups = groups, RoundNumber = roundNumber };
        }

        public static GameActionResult SuccessWithMove(IReadOnlyList<Group> groups, string participantId, int sourceGroup, int targetGroup)
        {
            return new GameActionResult 
            { 
                IsSuccess = true, 
                Groups = groups, 
                ParticipantId = participantId, 
                SourceGroup = sourceGroup, 
                TargetGroup = targetGroup 
            };
        }

        public static GameActionResult Error(string message)
        {
            return new GameActionResult { IsSuccess = false, ErrorMessage = message };
        }
    }
}