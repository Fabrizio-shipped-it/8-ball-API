using Bogus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PoolManager.DTOs;
using PoolManager.Models;
using PoolManager.Services;
using PoolManager.Tests.Helpers;

namespace PoolManager.Tests;

public class MatchServiceTests
{
    private readonly Faker _faker = new();

    private async Task<(MatchService service, int player1Id, int player2Id)> SetupWithPlayers()
    {
        var context = TestDbContext.Create();
        var playerService = new PlayerService(context, NullLoggerFactory.Instance.CreateLogger<PlayerService>());
        var matchService = new MatchService(context, NullLoggerFactory.Instance.CreateLogger<MatchService>());

        var p1 = await playerService.Create(new CreatePlayerDto
        {
            Name = _faker.Name.FullName(),
            ProfilePictureUrl = _faker.Internet.Url()
        }, Guid.NewGuid().ToString());

        var p2 = await playerService.Create(new CreatePlayerDto
        {
            Name = _faker.Name.FullName(),
            ProfilePictureUrl = _faker.Internet.Url()
        }, Guid.NewGuid().ToString());

        return (matchService, p1.Id, p2.Id);
    }

    [Fact]
    public async Task Create_MatchSuccessfully()
    {
        var (service, p1, p2) = await SetupWithPlayers();

        var (match, error) = await service.Create(new CreateMatchDto
        {
            Player1Id = p1,
            Player2Id = p2,
            StartTime = DateTime.UtcNow.AddHours(1)
        });

        Assert.Null(error);
        Assert.NotNull(match);
        Assert.Equal("upcoming", match.Status);
    }

    [Fact]
    public async Task Create_RejectsDoubleBooking()
    {
        var (service, p1, p2) = await SetupWithPlayers();
        var startTime = DateTime.UtcNow.AddHours(5);

        // Primer match: OK
        await service.Create(new CreateMatchDto
        {
            Player1Id = p1,
            Player2Id = p2,
            StartTime = startTime,
            EndTime = startTime.AddHours(1)
        });

        // Segundo match solapado: debe fallar
        var (match, error) = await service.Create(new CreateMatchDto
        {
            Player1Id = p1,
            Player2Id = p2,
            StartTime = startTime.AddMinutes(30),
            EndTime = startTime.AddHours(1).AddMinutes(30)
        });

        Assert.NotNull(error);
        Assert.Contains("horario", error);
        Assert.Null(match);
    }

    [Fact]
    public async Task Create_RejectsSamePlayer()
    {
        var (service, p1, _) = await SetupWithPlayers();

        var (match, error) = await service.Create(new CreateMatchDto
        {
            Player1Id = p1,
            Player2Id = p1,
            StartTime = DateTime.UtcNow.AddHours(1)
        });

        Assert.NotNull(error);
        Assert.Contains("contra sí mismo", error);
    }

    [Fact]
    public async Task Update_SetsWinnerAndUpdatesRanking()
    {
        var (service, p1, p2) = await SetupWithPlayers();

        var (match, _) = await service.Create(new CreateMatchDto
        {
            Player1Id = p1,
            Player2Id = p2,
            StartTime = DateTime.UtcNow.AddHours(1)
        });

        var (updated, error) = await service.Update(match!.Id, new UpdateMatchDto
        {
            WinnerId = p1
        });

        Assert.Null(error);
        Assert.Equal(p1, updated!.WinnerId);
    }

    [Fact]
    public async Task Update_RejectsInvalidWinner()
    {
        var (service, p1, p2) = await SetupWithPlayers();

        var (match, _) = await service.Create(new CreateMatchDto
        {
            Player1Id = p1,
            Player2Id = p2,
            StartTime = DateTime.UtcNow.AddHours(1)
        });

        var (updated, error) = await service.Update(match!.Id, new UpdateMatchDto
        {
            WinnerId = 9999 // No es ninguno de los dos jugadores
        });

        Assert.NotNull(error);
        Assert.Contains("jugadores del match", error);
    }

    [Fact]
    public async Task Create_AllowsNonOverlappingMatches()
    {
        var (service, p1, p2) = await SetupWithPlayers();

        // Match de 15:00 a 16:00
        await service.Create(new CreateMatchDto
        {
            Player1Id = p1,
            Player2Id = p2,
            StartTime = DateTime.UtcNow.AddHours(3),
            EndTime = DateTime.UtcNow.AddHours(4)
        });

        // Match de 16:00 a 17:00 — no se solapa
        var (match, error) = await service.Create(new CreateMatchDto
        {
            Player1Id = p1,
            Player2Id = p2,
            StartTime = DateTime.UtcNow.AddHours(4),
            EndTime = DateTime.UtcNow.AddHours(5)
        });

        Assert.Null(error);
        Assert.NotNull(match);
    }
}