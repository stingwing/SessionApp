using FluentAssertions;
using SessionApp.Helpers;
using SessionApp.Models;
using Xunit;

namespace SessionApp.Tests.Helpers;

public class MappingExtensionsTests
{
    [Fact]
    public void ToMemberResponse_ShouldMapAllProperties()
    {
        // Arrange
        var participant = new Participant
        {
            Id = "player1",
            Name = "John Doe",
            Commander = "Atraxa",
            Points = 10,
            JoinedAtUtc = new DateTime(2026, 2, 19, 10, 0, 0, DateTimeKind.Utc),
            Order = 1,
            Dropped = false,
            AutoFill = true,
            InCustomGroup = Guid.NewGuid()
        };

        // Act
        var result = participant.ToMemberResponse();

        // Assert
        result.Id.Should().Be(participant.Id);
        result.Name.Should().Be(participant.Name);
        result.Commander.Should().Be(participant.Commander);
        result.Points.Should().Be(participant.Points);
        result.JoinedAtUtc.Should().Be(participant.JoinedAtUtc);
        result.Order.Should().Be(participant.Order);
        result.Dropped.Should().Be(participant.Dropped);
        result.AutoFill.Should().Be(participant.AutoFill);
        result.InCustomGroup.Should().Be(participant.InCustomGroup);
    }

    [Fact]
    public void ToGroupResponse_ShouldMapGroupWithParticipants()
    {
        // Arrange
        var group = new Group
        {
            GroupNumber = 1,
            RoundNumber = 2,
            RoundStarted = true,
            WinnerParticipantId = "player1",
            IsDraw = false,
            IsCustom = false,
            StartedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = DateTime.UtcNow.AddMinutes(30)
        };

        var participant = new Participant
        {
            Id = "player1",
            Name = "John Doe",
            Commander = "Atraxa",
            Points = 10,
            JoinedAtUtc = DateTime.UtcNow,
            Order = 0
        };
        
        group.AddParticipant(participant);

        // Act
        var result = group.ToGroupResponse();

        // Assert
        result.GroupNumber.Should().Be(group.GroupNumber);
        result.Round.Should().Be(group.RoundNumber);
        result.RoundStarted.Should().BeTrue();
        result.Members.Should().HaveCount(1);
        result.Members[0].Id.Should().Be(participant.Id);
        result.Result.Should().BeTrue();
        result.Winner.Should().Be("player1");
        result.Draw.Should().BeFalse();
    }

    [Fact]
    public void ToRoundResponse_ShouldMapMultipleGroups()
    {
        // Arrange
        var roomCode = "ABC123";
        var groups = new List<Group>
        {
            new Group { GroupNumber = 1, RoundNumber = 1 },
            new Group { GroupNumber = 2, RoundNumber = 1 }
        };

        // Act
        var result = groups.ToRoundResponse(roomCode);

        // Assert
        result.RoomCode.Should().Be(roomCode);
        result.RoundNumber.Should().Be(1);
        result.Groups.Should().HaveCount(2);
    }

    [Fact]
    public void ToSessionSummaryResponse_ShouldMapCompleteSession()
    {
        // Arrange
        var session = new RoomSession
        {
            Code = "TEST123",
            EventName = "Test Event",
            HostId = "host1",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(24),
            IsGameStarted = true,
            IsGameEnded = false,
            CurrentRound = 1
        };

        var participant = new Participant
        {
            Id = "player1",
            Name = "Test Player",
            Commander = "Test Commander"
        };

        session.Participants.AddOrUpdate(participant.Id, participant, (_, __) => participant);

        // Act
        var result = session.ToSessionSummaryResponse();

        // Assert
        result.Code.Should().Be(session.Code);
        result.EventName.Should().Be(session.EventName);
        result.HostId.Should().Be(session.HostId);
        result.IsGameStarted.Should().BeTrue();
        result.ParticipantCount.Should().Be(1);
        result.Participants.Should().HaveCount(1);
    }
}