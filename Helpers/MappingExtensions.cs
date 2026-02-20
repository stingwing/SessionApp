using SessionApp.Controllers;
using SessionApp.Models;
using System.Collections.Generic;
using System.Linq;

namespace SessionApp.Helpers
{
    public static class MappingExtensions
    {
        public static MemberResponse ToMemberResponse(this Participant p)
        {
            return new MemberResponse(
                p.Id,
                p.Name,
                p.Commander,
                p.Points,
                p.JoinedAtUtc,
                p.Order,
                p.Dropped,
                p.AutoFill,
                p.InCustomGroup
            );
        }

        public static RoomSettingsResponse ToRoomSettingsResponse(this RoomSettings settings)
        {
            return new RoomSettingsResponse(
                settings.AllowJoinAfterStart,
                settings.PrioitizeWinners,
                settings.AllowGroupOfThree,
                settings.AllowGroupOfFive,
                settings.FurtherReduceOddsOfGroupOfThree,
                settings.RoundLength,
                settings.UsePoints,
                settings.PointsForWin,
                settings.PointsForDraw,
                settings.PointsForLoss,
                settings.PointsForABye,
                settings.AllowCustomGroups,
                settings.AllowPlayersToCreateCustomGroups,
                settings.TournamentMode,
                settings.MaxRounds,
                settings.MaxGroupSize
            );
        }

        public static GroupResponse ToGroupResponse(this Group group)
        {
            return new GroupResponse(
                group.GroupNumber,
                group.RoundNumber,
                group.RoundStarted,
                group.Participants.Values.Select(p => p.ToMemberResponse()).ToArray(),
                group.HasResult,
                group.WinnerParticipantId,
                group.IsDraw,
                group.IsCustom,
                group.StartedAtUtc,
                group.CompletedAtUtc,
                group.Statistics
            );
        }

        public static RoundResponse ToRoundResponse(this IEnumerable<Group> roundGroups, string roomCode)
        {
            var roundNumber = roundGroups.FirstOrDefault()?.RoundNumber ?? -1;
            return new RoundResponse(
                roomCode,
                roundNumber,
                roundGroups.Select(g => g.ToGroupResponse()).ToArray()
            );
        }

        public static SessionSummaryResponse ToSessionSummaryResponse(this RoomSession session)
        {
            return new SessionSummaryResponse(
                session.Code,
                session.EventName,
                session.HostId,
                session.CreatedAtUtc,
                session.ExpiresAtUtc,
                session.IsGameStarted,
                session.IsGameEnded,
                session.Archived,
                session.CurrentRound,
                session.WinnerParticipantId,
                session.Settings,
                session.Participants.Count,
                session.Participants.Values.Select(p => p.ToMemberResponse()).ToArray(),
                session.Groups?.Select(g => g.ToGroupResponse()).ToArray(),
                session.ArchivedRounds.Count,
                session.ArchivedRounds.Select(r => r.ToRoundResponse(session.Code)).ToArray()
            );
        }
    }
}