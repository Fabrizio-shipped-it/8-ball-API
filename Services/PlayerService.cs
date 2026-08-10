using Microsoft.EntityFrameworkCore;
using PoolManager.Data;
using PoolManager.DTOs;
using PoolManager.Models;

namespace PoolManager.Services;



/// <summary>
///  Este archivo principalmente se encarga de la logica del negocio, mayormente vamos a manejar los datos de tipo DTO y Player.
/// </summary>
/// 



public class PlayerService
{
    private readonly AppDbContext _context;
    private readonly ILogger<PlayerService> _logger;

    public PlayerService(AppDbContext context, ILogger<PlayerService> logger)
    {
        _context = context;
        _logger = logger;
    }



    // Busca o crea un jugador a partir del token de Keycloak (auto-registro en primer login)
    public async Task<PlayerResponseDto> GetOrCreateFromToken(string keycloakId, string name)
    {
        var player = await _context.Players.FirstOrDefaultAsync(p => p.KeycloakId == keycloakId);

        if (player == null)
        {
            player = new Player
            {
                KeycloakId = keycloakId,
                Name = name,
                ProfilePictureUrl = "pending" // Se actualiza después con S3
            };
            _context.Players.Add(player);
            await _context.SaveChangesAsync();
        }

        return MapToDto(player);
    }



    // GET /players/me
    public async Task<PlayerResponseDto?> GetByKeycloakId(string keycloakId)
    {
        var player = await _context.Players.FirstOrDefaultAsync(p => p.KeycloakId == keycloakId);
        return player == null ? null : MapToDto(player);
    }

    // GET /players (admin) con filtro opcional por nombre
    public async Task<List<PlayerResponseDto>> GetAll(string? nameFilter)
    {
        var query = _context.Players.AsQueryable();

        if (!string.IsNullOrWhiteSpace(nameFilter))
            query = query.Where(p => p.Name.ToLower().Contains(nameFilter.ToLower()));

        var players = await query.ToListAsync();
        return players.Select(MapToDto).ToList();
    }

    // POST /players (admin)
    public async Task<PlayerResponseDto> Create(CreatePlayerDto dto, string keycloakId)
    {
        var player = new Player
        {
            KeycloakId = keycloakId,
            Name = dto.Name,
            PreferredCue = dto.PreferredCue,
            ProfilePictureUrl = dto.ProfilePictureUrl
        };

        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return MapToDto(player);
    }

    // PATCH /players/me
    public async Task<PlayerResponseDto?> Update(string keycloakId, UpdatePlayerDto dto)
    {
        var player = await _context.Players.FirstOrDefaultAsync(p => p.KeycloakId == keycloakId);
        if (player == null) return null;

        if (dto.Name != null) player.Name = dto.Name;
        if (dto.PreferredCue != null) player.PreferredCue = dto.PreferredCue;
        if (dto.ProfilePictureUrl != null) player.ProfilePictureUrl = dto.ProfilePictureUrl;

        await _context.SaveChangesAsync();
        return MapToDto(player);
    }

    // DELETE /players/:id (admin)
    public async Task<bool> Delete(int id)
    {
        var player = await _context.Players.FindAsync(id);
        if (player == null) return false;

        _context.Players.Remove(player);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Jugador eliminado: {PlayerId} - {PlayerName}", player.Id, player.Name);
        return true;
    }

    private static PlayerResponseDto MapToDto(Player player)
    {
        return new PlayerResponseDto
        {
            Id = player.Id,
            Name = player.Name,
            Ranking = player.Ranking,
            PreferredCue = player.PreferredCue,
            ProfilePictureUrl = player.ProfilePictureUrl
        };
    }
}