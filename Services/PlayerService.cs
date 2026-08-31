using Microsoft.EntityFrameworkCore;
using PoolManager.Data;
using PoolManager.DTOs;
using PoolManager.Models;

namespace PoolManager.Services;

/// <summary>
///  Lógica de negocio de jugadores. El "ranking" que se expone es una posición
///  calculada a partir de Wins, no un campo guardado.
/// </summary>
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
                ProfilePictureKey = null // todavía no subió foto
            };
            _context.Players.Add(player);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Dos requests simultáneos del mismo usuario nuevo (por ejemplo, un
                // front que dispara varias llamadas al iniciar sesión) intentan
                // insertar dos veces. El índice único de KeycloakId rechaza el
                // segundo y eso salía como 500. Acá se descarta el insert perdedor
                // y se relee la fila que ganó la carrera.
                _context.Entry(player).State = EntityState.Detached;

                player = await _context.Players.FirstOrDefaultAsync(p => p.KeycloakId == keycloakId);

                if (player == null) throw; // no era una colisión de unicidad

                _logger.LogInformation(
                    "Auto-registro concurrente resuelto para el jugador {PlayerId}", player.Id);
            }
        }

        return await MapToDto(player);
    }

    /// Resuelve el Id interno del jugador a partir del sub del token.
    /// Lo usan los controllers para chequear pertenencia sin traer la entidad entera.
    public async Task<int?> GetIdByKeycloakId(string keycloakId)
    {
        return await _context.Players
            .Where(p => p.KeycloakId == keycloakId)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync();
    }

    // GET /players/me
    public async Task<PlayerResponseDto?> GetByKeycloakId(string keycloakId)
    {
        var player = await _context.Players.FirstOrDefaultAsync(p => p.KeycloakId == keycloakId);
        return player == null ? null : await MapToDto(player);
    }

    // GET /players (admin) con filtro opcional por nombre
    public async Task<List<PlayerResponseDto>> GetAll(string? nameFilter)
    {
        var query = _context.Players.AsQueryable();

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            var needle = nameFilter.ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(needle));
        }

        var players = await query.ToListAsync();

        // El ranking depende de TODOS los jugadores, no solo de los filtrados.
        var allWins = await _context.Players.Select(p => p.Wins).ToListAsync();

        return players
            .Select(p => MapToDto(p, RankFor(p.Wins, allWins)))
            .ToList();
    }

    // POST /players (admin)
    public async Task<PlayerResponseDto> Create(CreatePlayerDto dto, string keycloakId)
    {
        var player = new Player
        {
            KeycloakId = keycloakId,
            Name = dto.Name,
            PreferredCue = dto.PreferredCue,
            ProfilePictureKey = null
        };

        _context.Players.Add(player);
        await _context.SaveChangesAsync();
        return await MapToDto(player);
    }

    // PATCH /players/me
    // profilePictureKey ya viene validada por el controller (pertenencia + existencia en S3).
    public async Task<PlayerResponseDto?> Update(string keycloakId, UpdatePlayerDto dto)
    {
        var player = await _context.Players.FirstOrDefaultAsync(p => p.KeycloakId == keycloakId);
        if (player == null) return null;

        if (dto.Name != null) player.Name = dto.Name;
        if (dto.PreferredCue != null) player.PreferredCue = dto.PreferredCue;
        if (dto.ProfilePictureKey != null) player.ProfilePictureKey = dto.ProfilePictureKey;

        await _context.SaveChangesAsync();
        return await MapToDto(player);
    }

    // DELETE /players/:id (admin)
    //
    // Devuelve el motivo del fallo para que el controller elija el status code.
    // Antes devolvía solo bool: si el jugador tenía partidas, la FK con
    // DeleteBehavior.Restrict hacía explotar SaveChanges con DbUpdateException
    // y el usuario recibía un 500 sin explicación.
    public async Task<(bool Success, string? Error, bool Conflict)> Delete(int id)
    {
        var player = await _context.Players.FindAsync(id);
        if (player == null)
            return (false, "Jugador no encontrado", false);

        var partidas = await _context.Matches
            .CountAsync(m => m.Player1Id == id || m.Player2Id == id);

        if (partidas > 0)
            return (false,
                $"No se puede eliminar: el jugador tiene {partidas} partida(s) asociada(s). " +
                "Eliminá primero sus partidas.",
                true);

        _context.Players.Remove(player);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Jugador eliminado: {PlayerId} - {PlayerName}", player.Id, player.Name);
        return (true, null, false);
    }

    /// True si esa key está referenciada como foto de perfil de algún jugador.
    /// Permite que cualquiera vea la foto ACTUAL de otro (aparecen en los listados
    /// de partidas), pero no keys arbitrarias ni versiones viejas del bucket.
    public async Task<bool> IsPublishedProfileKey(string key)
    {
        return await _context.Players.AnyAsync(p => p.ProfilePictureKey == key);
    }

    // --- Ranking ---

    /// Posición 1 = el que más ganó. Los empates comparten posición
    /// (dos jugadores con 3 victorias son ambos #1, y el siguiente es #3).
    private static int RankFor(int wins, IReadOnlyCollection<int> allWins)
        => allWins.Count(w => w > wins) + 1;

    private async Task<PlayerResponseDto> MapToDto(Player player)
    {
        var better = await _context.Players.CountAsync(p => p.Wins > player.Wins);
        return MapToDto(player, better + 1);
    }

    private static PlayerResponseDto MapToDto(Player player, int ranking)
    {
        return new PlayerResponseDto
        {
            Id = player.Id,
            Name = player.Name,
            Wins = player.Wins,
            Ranking = ranking,
            PreferredCue = player.PreferredCue,
            ProfilePictureKey = player.ProfilePictureKey
        };
    }
}
