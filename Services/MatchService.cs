using Microsoft.EntityFrameworkCore;
using PoolManager.Data;
using PoolManager.DTOs;
using PoolManager.Models;

namespace PoolManager.Services;

public class MatchService
{
    private readonly AppDbContext _context;
    private readonly ILogger<MatchService> _logger;

    public MatchService(AppDbContext context, ILogger<MatchService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<(MatchResponseDto? Match, string? Error)> Create(CreateMatchDto dto)
    {
        // Validar que los jugadores existan
        var player1 = await _context.Players.FindAsync(dto.Player1Id);
        var player2 = await _context.Players.FindAsync(dto.Player2Id);

        if (player1 == null || player2 == null)
            return (null, "Uno o ambos jugadores no existen");

        if (dto.Player1Id == dto.Player2Id)
            return (null, "Un jugador no puede jugar contra sí mismo");

        // Calcular EndTime estimado si no se provee (default: 1 hora)
        var endTime = dto.EndTime ?? dto.StartTime.AddHours(1);

        // Verificar double-booking
        var conflict = await HasConflict(dto.Player1Id, dto.Player2Id, dto.StartTime, endTime);
        if (conflict != null)
            return (null, conflict);

        var match = new Match
        {
            Player1Id = dto.Player1Id,
            Player2Id = dto.Player2Id,
            StartTime = dto.StartTime,
            EndTime = dto.EndTime,
            TableNumber = dto.TableNumber
        };

        _context.Matches.Add(match);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Match creado: #{MatchId} - Jugador {P1} vs {P2}", match.Id, match.Player1Id, match.Player2Id);

        return (await MapToDto(match), null);
    }

    public async Task<List<MatchResponseDto>> GetAll(string? date, string? status)
    {
        var query = _context.Matches
            .Include(m => m.Player1)
            .Include(m => m.Player2)
            .Include(m => m.Winner)
            .AsQueryable();

        // Filtro por fecha
        if (DateTime.TryParse(date, out var parsedDate))
            query = query.Where(m => m.StartTime.Date == parsedDate.Date);

        var matches = await query.ToListAsync();

        // Filtro por status (se aplica en memoria porque es calculado)
        if (!string.IsNullOrWhiteSpace(status))
        {
            matches = matches.Where(m => GetStatus(m) == status.ToLower()).ToList();
        }

        var result = new List<MatchResponseDto>();
        foreach (var match in matches)
            result.Add(MapToDtoFromLoaded(match));

        return result;
    }

    public async Task<MatchResponseDto?> GetById(int id)
    {
        var match = await _context.Matches
            .Include(m => m.Player1)
            .Include(m => m.Player2)
            .Include(m => m.Winner)
            .FirstOrDefaultAsync(m => m.Id == id);

        return match == null ? null : MapToDtoFromLoaded(match);
    }

    public async Task<(MatchResponseDto? Match, string? Error)> Update(int id, UpdateMatchDto dto)
    {
        var match = await _context.Matches
            .Include(m => m.Player1)
            .Include(m => m.Player2)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (match == null)
            return (null, "Match no encontrado");

        // Si cambia StartTime, verificar double-booking de nuevo
        if (dto.StartTime.HasValue)
        {
            var endTime = dto.EndTime ?? match.EndTime ?? dto.StartTime.Value.AddHours(1);
            var conflict = await HasConflict(match.Player1Id, match.Player2Id, dto.StartTime.Value, endTime, id);
            if (conflict != null)
                return (null, conflict);

            match.StartTime = dto.StartTime.Value;
        }

        if (dto.EndTime.HasValue) match.EndTime = dto.EndTime.Value;
        if (dto.TableNumber.HasValue) match.TableNumber = dto.TableNumber.Value;

        if (dto.WinnerId.HasValue)
        {
            // Validar que el ganador sea uno de los dos jugadores
            if (dto.WinnerId != match.Player1Id && dto.WinnerId != match.Player2Id)
                return (null, "El ganador debe ser uno de los jugadores del match");

            match.WinnerId = dto.WinnerId.Value;

            // Actualizar ranking del ganador
            var winner = await _context.Players.FindAsync(dto.WinnerId.Value);
            if (winner != null)
            {
                winner.Ranking += 1;
            }
        }

        await _context.SaveChangesAsync();
        return (await MapToDto(match), null);
    }

    public async Task<(bool Success, string? Error)> Delete(int id)
    {
        var match = await _context.Matches.FindAsync(id);
        if (match == null)
            return (false, "Match no encontrado");

        // Solo se puede borrar si no empezó
        if (match.StartTime <= DateTime.UtcNow && match.EndTime == null)
            return (false, "No se puede eliminar un match en curso");

        _context.Matches.Remove(match);
        await _context.SaveChangesAsync();
        return (true, null);
    }

    // --- Double-booking ---
    /// HasConflict: si el match existente empieza antes de que el nuevo termine, Y el match existente termina después de que el nuevo empiece,
    ///  hay solapamiento. El parámetro excludeMatchId sirve para cuando actualizás un match ya que no queremos que se detecte conflicto consigo mismo.
    /// 
    private async Task<string?> HasConflict(int player1Id, int player2Id, DateTime start, DateTime end, int? excludeMatchId = null)
    {
        // Busca si alguno de los dos jugadores tiene un match que se solape en el rango [start, end]
        var query = _context.Matches.AsQueryable();

        if (excludeMatchId.HasValue)
            query = query.Where(m => m.Id != excludeMatchId.Value);

        var conflicting = await query
            .Where(m =>
                (m.Player1Id == player1Id || m.Player2Id == player1Id ||
                 m.Player1Id == player2Id || m.Player2Id == player2Id)
                &&
                m.StartTime < end &&
                (m.EndTime == null ? m.StartTime.AddHours(1) : m.EndTime.Value) > start
            )
            .FirstOrDefaultAsync();

        if (conflicting == null) return null;

        // Identificar quién tiene el conflicto
        var conflictPlayerId = (conflicting.Player1Id == player1Id || conflicting.Player2Id == player1Id)
            ? player1Id : player2Id;

        return $"El jugador {conflictPlayerId} ya tiene un match programado en ese horario (Match #{conflicting.Id})";
    }

    private static string GetStatus(Match match)
    {
        var now = DateTime.UtcNow;
        if (match.WinnerId != null || (match.EndTime != null && match.EndTime <= now))
            return "completed";
        if (match.StartTime <= now)
            return "ongoing";
        return "upcoming";
    }

    // Cuando el match ya tiene Include cargado
    private MatchResponseDto MapToDtoFromLoaded(Match match)
    {
        return new MatchResponseDto
        {
            Id = match.Id,
            Player1Id = match.Player1Id,
            Player1Name = match.Player1.Name,
            Player2Id = match.Player2Id,
            Player2Name = match.Player2.Name,
            StartTime = match.StartTime,
            EndTime = match.EndTime,
            WinnerId = match.WinnerId,
            WinnerName = match.Winner?.Name,
            TableNumber = match.TableNumber,
            Status = GetStatus(match)
        };
    }

    // Cuando necesitamos cargar los jugadores
    private async Task<MatchResponseDto> MapToDto(Match match)
    {
        var loaded = await _context.Matches
            .Include(m => m.Player1)
            .Include(m => m.Player2)
            .Include(m => m.Winner)
            .FirstAsync(m => m.Id == match.Id);

        return MapToDtoFromLoaded(loaded);
    }
}