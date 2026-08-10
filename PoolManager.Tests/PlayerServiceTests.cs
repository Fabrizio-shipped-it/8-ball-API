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

    [Fact]
    public async Task GetOrCreateFromToken_CreatesPlayerOnFirstLogin()
    {
        // Arrange
        var context = TestDbContext.Create();
        var service = new PlayerService(context, NullLoggerFactory.Instance.CreateLogger<PlayerService>());
        var keycloakId = Guid.NewGuid().ToString();
        var name = _faker.Name.FullName();

        // Act
        var result = await service.GetOrCreateFromToken(keycloakId, name);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(name, result.Name);
        Assert.Equal(0, result.Ranking);
    }

    [Fact]
    public async Task GetOrCreateFromToken_ReturnsSamePlayerOnSecondLogin()
    {
        var context = TestDbContext.Create();
        var service = new PlayerService(context, NullLoggerFactory.Instance.CreateLogger<PlayerService>());
        var keycloakId = Guid.NewGuid().ToString();
        var name = _faker.Name.FullName();

        var first = await service.GetOrCreateFromToken(keycloakId, name);
        var second = await service.GetOrCreateFromToken(keycloakId, name);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Create_CreatesPlayerWithValidData()
    {
        var context = TestDbContext.Create();
        var service = new PlayerService(context, NullLoggerFactory.Instance.CreateLogger<PlayerService>());
        var dto = new CreatePlayerDto
        {
            Name = _faker.Name.FullName(),
            PreferredCue = "Oak",
            ProfilePictureUrl = _faker.Internet.Url()
        };

        var result = await service.Create(dto, Guid.NewGuid().ToString());

        Assert.Equal(dto.Name, result.Name);
        Assert.Equal("Oak", result.PreferredCue);
    }

    [Fact]
    public async Task GetAll_FiltersByName()
    {
        var context = TestDbContext.Create();
        var service = new PlayerService(context, NullLoggerFactory.Instance.CreateLogger<PlayerService>());

        // Crear 3 jugadores
        await service.Create(new CreatePlayerDto { Name = "Juan Perez", ProfilePictureUrl = "url1" }, Guid.NewGuid().ToString());
        await service.Create(new CreatePlayerDto { Name = "Maria Garcia", ProfilePictureUrl = "url2" }, Guid.NewGuid().ToString());
        await service.Create(new CreatePlayerDto { Name = "Juan Lopez", ProfilePictureUrl = "url3" }, Guid.NewGuid().ToString());

        var results = await service.GetAll("juan");

        Assert.Equal(2, results.Count);
        Assert.All(results, p => Assert.Contains("Juan", p.Name));
    }

    [Fact]
    public async Task Update_OnlyChangesProvidedFields()
    {
        var context = TestDbContext.Create();
        var service = new PlayerService(context, NullLoggerFactory.Instance.CreateLogger<PlayerService>());
        var keycloakId = Guid.NewGuid().ToString();

        await service.Create(new CreatePlayerDto
        {
            Name = "Original",
            PreferredCue = "Maple",
            ProfilePictureUrl = "url"
        }, keycloakId);

        var updated = await service.Update(keycloakId, new UpdatePlayerDto { Name = "Nuevo Nombre" });

        Assert.Equal("Nuevo Nombre", updated!.Name);
        Assert.Equal("Maple", updated.PreferredCue); // No cambió
    }

    [Fact]
    public async Task Delete_RemovesPlayer()
    {
        var context = TestDbContext.Create();
        var service = new PlayerService(context, NullLoggerFactory.Instance.CreateLogger<PlayerService>());
        var keycloakId = Guid.NewGuid().ToString();

        var player = await service.Create(new CreatePlayerDto
        {
            Name = "A borrar",
            ProfilePictureUrl = "url"
        }, keycloakId);

        var deleted = await service.Delete(player.Id);
        var found = await service.GetByKeycloakId(keycloakId);

        Assert.True(deleted);
        Assert.Null(found);
    }
}