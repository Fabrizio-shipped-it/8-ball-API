using Bogus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PoolManager.Data;
using PoolManager.DTOs;
using PoolManager.Models;
using PoolManager.Services;
using PoolManager.Tests.Helpers;

namespace PoolManager.Tests;

public class MatchServiceTests
{
    private readonly Faker _faker = new();

    private sealed record Setup(MatchService Service, AppDbContext Context, int P1, int P2, int Outsider);

    private async Task<Setup> SetupWithPlayers()
    {
        var context = TestDbContext.Create();
        var playerService = new PlayerService(context, NullLoggerFactory.Instance.CreateLogger<PlayerService>());
        var matchService = new MatchService(context, NullLoggerFactory.Instance.CreateLogger<MatchService>());

        var p1 = await playerService.Create(new CreatePlayerDto { Name = _faker.Name.FullName() }, Guid.NewGuid().ToString());
        var p2 = await playerService.Create(new CreatePlayerDto { Name = _faker.Name.FullName() }, Guid.NewGuid().ToString());
        var outsider = await playerService.Create(new CreatePlayerDto { Name = "Ajeno" }, Guid.NewGuid().ToString());

        return new Setup(matchService, context, p1.Id, p2.Id, outsider.Id);
    }

    [Fact]
    public async Task Create_MatchSuccessfully()
    {
        var s = await SetupWithPlayers();

        var (match, error, kind) = await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = DateTime.UtcNow.AddHours(1)
        }, s.P1, isAdmin: false);

        Assert.Null(error);
        Assert.Equal(MatchError.None, kind);
        Assert.NotNull(match);
        Assert.Equal("upcoming", match.Status);
    }

    [Fact]
    public async Task Create_RejectsDoubleBooking()
    {
        var s = await SetupWithPlayers();
        var startTime = DateTime.UtcNow.AddHours(5);

        await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = startTime,
            EndTime = startTime.AddHours(1)
        }, s.P1, isAdmin: false);

        var (match, error, kind) = await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = startTime.AddMinutes(30),
            EndTime = startTime.AddHours(1).AddMinutes(30)
        }, s.P1, isAdmin: false);

        Assert.Equal(MatchError.Conflict, kind);
        Assert.Contains("horario", error);
        Assert.Null(match);
    }

    [Fact]
    public async Task Create_RejectsSamePlayer()
    {
        var s = await SetupWithPlayers();

        var (_, error, kind) = await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P1,
            StartTime = DateTime.UtcNow.AddHours(1)
        }, s.P1, isAdmin: false);

        Assert.Equal(MatchError.Validation, kind);
        Assert.Contains("contra sí mismo", error);
    }

    [Fact]
    public async Task Create_RejectsMatchBetweenOtherPlayers()
    {
        var s = await SetupWithPlayers();

        // El "ajeno" intenta agendar una partida entre P1 y P2.
        var (match, _, kind) = await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = DateTime.UtcNow.AddHours(1)
        }, s.Outsider, isAdmin: false);

        Assert.Equal(MatchError.Forbidden, kind);
        Assert.Null(match);
    }

    [Fact]
    public async Task Create_AdminCanScheduleForOthers()
    {
        var s = await SetupWithPlayers();

        var (match, _, kind) = await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = DateTime.UtcNow.AddHours(1)
        }, s.Outsider, isAdmin: true);

        Assert.Equal(MatchError.None, kind);
        Assert.NotNull(match);
    }

    [Fact]
    public async Task Update_SetsWinnerAndCountsTheWin()
    {
        var s = await SetupWithPlayers();

        var (match, _, _) = await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = DateTime.UtcNow.AddHours(1)
        }, s.P1, isAdmin: false);

        var (updated, error, kind) = await s.Service.Update(
            match!.Id, new UpdateMatchDto { WinnerId = s.P1 }, s.P1, isAdmin: false);

        Assert.Equal(MatchError.None, kind);
        Assert.Null(error);
        Assert.Equal(s.P1, updated!.WinnerId);
        Assert.Equal(1, (await s.Context.Players.FindAsync(s.P1))!.Wins);
        Assert.Equal(0, (await s.Context.Players.FindAsync(s.P2))!.Wins);
    }

    [Fact]
    public async Task Update_ReassigningWinnerTransfersTheWin()
    {
        // Este es el bug reportado: al corregir el ganador, antes se sumaba una
        // victoria al nuevo sin restársela al anterior, y los dos terminaban con 1.
        var s = await SetupWithPlayers();

        var (match, _, _) = await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = DateTime.UtcNow.AddHours(1)
        }, s.P1, isAdmin: false);

        await s.Service.Update(match!.Id, new UpdateMatchDto { WinnerId = s.P1 }, s.P1, isAdmin: false);
        await s.Service.Update(match.Id, new UpdateMatchDto { WinnerId = s.P2 }, s.P1, isAdmin: false);

        Assert.Equal(0, (await s.Context.Players.FindAsync(s.P1))!.Wins);
        Assert.Equal(1, (await s.Context.Players.FindAsync(s.P2))!.Wins);
    }

    [Fact]
    public async Task Update_SameWinnerTwiceDoesNotDoubleCount()
    {
        var s = await SetupWithPlayers();

        var (match, _, _) = await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = DateTime.UtcNow.AddHours(1)
        }, s.P1, isAdmin: false);

        await s.Service.Update(match!.Id, new UpdateMatchDto { WinnerId = s.P1 }, s.P1, isAdmin: false);
        await s.Service.Update(match.Id, new UpdateMatchDto { WinnerId = s.P1 }, s.P1, isAdmin: false);

        Assert.Equal(1, (await s.Context.Players.FindAsync(s.P1))!.Wins);
    }

    [Fact]
    public async Task Update_RejectsInvalidWinner()
    {
        var s = await SetupWithPlayers();

        var (match, _, _) = await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = DateTime.UtcNow.AddHours(1)
        }, s.P1, isAdmin: false);

        var (_, error, kind) = await s.Service.Update(
            match!.Id, new UpdateMatchDto { WinnerId = s.Outsider }, s.P1, isAdmin: false);

        Assert.Equal(MatchError.Validation, kind);
        Assert.Contains("jugadores del match", error);
    }

    [Fact]
    public async Task Update_OutsiderCannotDeclareWinner()
    {
        var s = await SetupWithPlayers();

        var (match, _, _) = await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = DateTime.UtcNow.AddHours(1)
        }, s.P1, isAdmin: false);

        var (updated, _, kind) = await s.Service.Update(
            match!.Id, new UpdateMatchDto { WinnerId = s.P1 }, s.Outsider, isAdmin: false);

        // 404 y no 403: un 403 confirmaría que el match existe.
        Assert.Equal(MatchError.NotFound, kind);
        Assert.Null(updated);
        Assert.Equal(0, (await s.Context.Players.FindAsync(s.P1))!.Wins);
    }

    [Fact]
    public async Task GetById_HidesMatchesOfOtherPlayers()
    {
        var s = await SetupWithPlayers();

        var (match, _, _) = await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = DateTime.UtcNow.AddHours(1)
        }, s.P1, isAdmin: false);

        var (asOutsider, kind) = await s.Service.GetById(match!.Id, s.Outsider, isAdmin: false);
        var (asAdmin, adminKind) = await s.Service.GetById(match.Id, s.Outsider, isAdmin: true);

        Assert.Equal(MatchError.NotFound, kind);
        Assert.Null(asOutsider);
        Assert.Equal(MatchError.None, adminKind);
        Assert.NotNull(asAdmin);
    }

    [Fact]
    public async Task GetAll_OnlyReturnsOwnMatches()
    {
        var s = await SetupWithPlayers();

        await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = DateTime.UtcNow.AddHours(1)
        }, s.P1, isAdmin: false);

        var mine = await s.Service.GetAll(null, null, s.P1, isAdmin: false, includeAll: false);
        var outsiders = await s.Service.GetAll(null, null, s.Outsider, isAdmin: false, includeAll: false);
        var everything = await s.Service.GetAll(null, null, s.Outsider, isAdmin: true, includeAll: true);

        Assert.Single(mine);
        Assert.Empty(outsiders);
        Assert.Single(everything);
    }

    [Fact]
    public async Task Create_AllowsNonOverlappingMatches()
    {
        var s = await SetupWithPlayers();

        await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = DateTime.UtcNow.AddHours(3),
            EndTime = DateTime.UtcNow.AddHours(4)
        }, s.P1, isAdmin: false);

        var (match, error, _) = await s.Service.Create(new CreateMatchDto
        {
            Player1Id = s.P1,
            Player2Id = s.P2,
            StartTime = DateTime.UtcNow.AddHours(4),
            EndTime = DateTime.UtcNow.AddHours(5)
        }, s.P1, isAdmin: false);

        Assert.Null(error);
        Assert.NotNull(match);
    }
}
