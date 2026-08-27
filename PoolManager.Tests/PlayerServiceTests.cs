using Bogus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PoolManager.DTOs;
using PoolManager.Models;
using PoolManager.Services;
using PoolManager.Tests.Helpers;

namespace PoolManager.Tests;

public class PlayerServiceTests
{
    private readonly Faker _faker = new();

    private static PlayerService NewService(out PoolManager.Data.AppDbContext context)
    {
        context = TestDbContext.Create();
        return new PlayerService(context, NullLoggerFactory.Instance.CreateLogger<PlayerService>());
    }

    [Fact]
    public async Task GetOrCreateFromToken_CreatesPlayerOnFirstLogin()
    {
        var service = NewService(out _);
        var keycloakId = Guid.NewGuid().ToString();
        var name = _faker.Name.FullName();

        var result = await service.GetOrCreateFromToken(keycloakId, name);

        Assert.NotNull(result);
        Assert.Equal(name, result.Name);
        Assert.Equal(0, result.Wins);
        // Único jugador de la tabla: aunque no ganó nada, su posición es la 1.
        Assert.Equal(1, result.Ranking);
        Assert.Null(result.ProfilePictureKey);
    }

    [Fact]
    public async Task GetOrCreateFromToken_ReturnsSamePlayerOnSecondLogin()
    {
        var service = NewService(out _);
        var keycloakId = Guid.NewGuid().ToString();
        var name = _faker.Name.FullName();

        var first = await service.GetOrCreateFromToken(keycloakId, name);
        var second = await service.GetOrCreateFromToken(keycloakId, name);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Create_CreatesPlayerWithValidData()
    {
        var service = NewService(out _);
        var dto = new CreatePlayerDto
        {
            Name = _faker.Name.FullName(),
            PreferredCue = "Oak"
        };

        var result = await service.Create(dto, Guid.NewGuid().ToString());

        Assert.Equal("Oak", result.PreferredCue);
    }

    [Fact]
    public async Task GetAll_FiltersByName()
    {
        var service = NewService(out _);

        await service.Create(new CreatePlayerDto { Name = "Juan Perez" }, Guid.NewGuid().ToString());
        await service.Create(new CreatePlayerDto { Name = "Maria Garcia" }, Guid.NewGuid().ToString());
        await service.Create(new CreatePlayerDto { Name = "Juan Lopez" }, Guid.NewGuid().ToString());

        var results = await service.GetAll("juan");

        Assert.Equal(2, results.Count);
        Assert.All(results, p => Assert.Contains("Juan", p.Name));
    }

    [Fact]
    public async Task Update_OnlyChangesProvidedFields()
    {
        var service = NewService(out _);
        var keycloakId = Guid.NewGuid().ToString();

        await service.Create(new CreatePlayerDto
        {
            Name = "Original",
            PreferredCue = "Maple"
        }, keycloakId);

        var updated = await service.Update(keycloakId, new UpdatePlayerDto { Name = "Nuevo Nombre" });

        Assert.Equal("Nuevo Nombre", updated!.Name);
        Assert.Equal("Maple", updated.PreferredCue); // No cambió
    }

    [Fact]
    public async Task Delete_RemovesPlayer()
    {
        var service = NewService(out _);
        var keycloakId = Guid.NewGuid().ToString();

        var player = await service.Create(new CreatePlayerDto { Name = "A borrar" }, keycloakId);

        var deleted = await service.Delete(player.Id);
        var found = await service.GetByKeycloakId(keycloakId);

        Assert.True(deleted);
        Assert.Null(found);
    }

    [Fact]
    public async Task Ranking_IsDerivedFromWins()
    {
        var service = NewService(out var context);

        await service.Create(new CreatePlayerDto { Name = "Poco" }, Guid.NewGuid().ToString());
        await service.Create(new CreatePlayerDto { Name = "Mucho" }, Guid.NewGuid().ToString());

        var mucho = context.Players.First(p => p.Name == "Mucho");
        mucho.Wins = 5;
        await context.SaveChangesAsync();

        var all = await service.GetAll(null);

        Assert.Equal(1, all.First(p => p.Name == "Mucho").Ranking);
        Assert.Equal(2, all.First(p => p.Name == "Poco").Ranking);
    }

    [Fact]
    public async Task Ranking_TiedPlayersShareTheSamePosition()
    {
        var service = NewService(out var context);

        await service.Create(new CreatePlayerDto { Name = "A" }, Guid.NewGuid().ToString());
        await service.Create(new CreatePlayerDto { Name = "B" }, Guid.NewGuid().ToString());
        await service.Create(new CreatePlayerDto { Name = "C" }, Guid.NewGuid().ToString());

        // ToList() antes de mutar: modificar mientras se enumera la query rompe.
        foreach (var p in context.Players.Where(p => p.Name != "C").ToList())
            p.Wins = 3;
        await context.SaveChangesAsync();

        var all = await service.GetAll(null);

        Assert.Equal(1, all.First(p => p.Name == "A").Ranking);
        Assert.Equal(1, all.First(p => p.Name == "B").Ranking);
        // Dos empatados en el puesto 1, el siguiente arranca en el 3.
        Assert.Equal(3, all.First(p => p.Name == "C").Ranking);
    }

    [Fact]
    public async Task IsPublishedProfileKey_OnlyMatchesKeysInUse()
    {
        var service = NewService(out var context);
        var keycloakId = Guid.NewGuid().ToString();
        var player = await service.Create(new CreatePlayerDto { Name = "Con foto" }, keycloakId);

        var key = $"players/{player.Id}/abc-foto.jpg";
        context.Players.First(p => p.Id == player.Id).ProfilePictureKey = key;
        await context.SaveChangesAsync();

        Assert.True(await service.IsPublishedProfileKey(key));
        Assert.False(await service.IsPublishedProfileKey("players/999/otra.jpg"));
    }
}
