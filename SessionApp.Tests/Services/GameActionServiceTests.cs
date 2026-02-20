using Moq;
using SessionApp.Services;
using Xunit;
using FluentAssertions;
using SessionApp.Models;
using static SessionApp.Services.RoomCodeService;

namespace SessionApp.Tests.Services;

public class GameActionServiceTests
{
    private readonly Mock<RoomCodeService> _mockRoomService;
    private readonly GameActionService _service;

    public GameActionServiceTests()
    {
        _mockRoomService = new Mock<RoomCodeService>();
        _service = new GameActionService(_mockRoomService.Object);
    }

    [Fact]
    public void HandleStartOrResetRound_ShouldReturnError_WhenNoGroupsAvailable()
    {
        // Arrange
        var session = new RoomSession
        {
            Code = "TEST",
            Groups = null
        };

        // Act
        var result = _service.HandleStartOrResetRound(session, HandleRoundOptions.StartRound);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("No groups available to start");
    }

    [Fact]
    public void HandleStartOrResetRound_ShouldStartRound_WhenGroupsExist()
    {
        // Arrange
        var session = new RoomSession
        {
            Code = "TEST",
            Groups = new List<Group>
            {
                new Group { GroupNumber = 1, RoundNumber = 1 }
            }
        };

        // Act
        var result = _service.HandleStartOrResetRound(session, HandleRoundOptions.StartRound);

        // Assert
        result.IsSuccess.Should().BeTrue();
        session.Groups[0].RoundStarted.Should().BeTrue();
        session.Groups[0].StartedAtUtc.Should().NotBeNull();
    }
}