using Microsoft.EntityFrameworkCore;
using PoolManager.Data;
using PoolManager.DTOs;
using PoolManager.Models;

namespace PoolManager.Services;

/// Clasificación del error, para que el controller elija el status code
/// sin tener que adivinar leyendo el texto del mensaje.
public enum MatchError
{
    None,
    NotFound,
    Validation,
    Conflict,
    Forbidden
}

public class MatchService
{
    private readonly AppDbContext _context;
    private readonly ILogger<MatchService> _logger;

    public MatchService(AppDbContext context, ILogger<MatchService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<(MatchResponseDto? Match, string? Error, MatchError Kind)> Create(
        CreateMatchDto dto, int callerPlayerId, bool isAdmin)
    {
        // Un jugador solo puede agendar partidas en las que participa.
        // Sin esto cualquiera podía crear partidas entre terceros y, de paso,
        // bloquearles la agenda vía el chequeo de double-booking.
        if (!isAdmin && dto.Player1Id != callerPlayerId && dto.Player2Id != callerPlayerId)
            return (null, "Solo podés crear partidas en las que participás", MatchError.Forbidden);

        var player1 = await _context.Players.FindAsync(dto.Player1Id);
        var player2 = await _context.Players.FindAsync(dto.Player2Id);

        if (player1 == null || player2 == null)
            return (null, "Uno o ambos jugadores no existen", MatchError.Validation);

        if (dto.Player1Id == dto.Player2Id)
            return (null, "Un jugador no puede jugar contra sí mismo", MatchError.Validation);

        var errorHorario = ValidarHorario(dto.StartTime, dto.EndTime, exigirFutura: true);
        if (errorHorario != null)
            return (null, errorHorario, MatchError.Validation);

        // Calcular EndTime estimado si no se provee (default: 1 hora)
        var endTime = dto.EndTime ?? dto.StartTime.AddHours(1);

        // Verificar double-booking de jugadores
        var conflict = await HasConflict(dto.Player1Id, dto.Player2Id, dto.StartTime, endTime);
        if (conflict != null)
            return (null, conflict, MatchError.Conflict);

        // Verificar double-booking de mesa
        var conflictoMesa = await HasTableConflict(dto.TableNumber, dto.StartTime, endTime);
        if (conflictoMesa != null)
            return (null, conflictoMesa, MatchError.Conflict);

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

        return (await MapToDto(match), null, MatchError.None);
    }

    /// includeAll solo lo puede pedir un admin. Para el resto, el listado
    /// se limita a las partidas donde el usuario participa.
    public async Task<List<MatchResponseDto>> GetAll(
        string? date, string? status, int callerPlayerId, bool isAdmin, bool includeAll)
    {
        var query = _context.Matches
            .Include(m => m.Player1)
            .Include(m => m.Player2)
            .Include(m => m.Winner)
            .AsQueryable();

        if (!(isAdmin && includeAll))
            query = query.Where(m => m.Player1Id == callerPlayerId || m.Player2Id == callerPlayerId);

        // Filtro por fecha, como rango.
        //
        // Antes era `m.StartTime.Date == parsedDate.Date`, que aplica una función
        // sobre la columna y por lo tanto NO puede usar el índice IX_Matches_StartTime
        // (que existe justamente para esto). Un rango sí lo aprovecha.
        if (DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsedDate))
        {
            var desde = DateTime.SpecifyKind(parsedDate.Date, DateTimeKind.Utc);
            var hasta = desde.AddDays(1);
            query = query.Where(m => m.StartTime >= desde && m.StartTime < hasta);
        }

        var matches = await query.ToListAsync();

        // Filtro por status (se aplica en memoria porque es calculado)
        if (!string.IsNullOrWhiteSpace(status))
        {
            matches = matches.Where(m => GetStatus(m) == status.ToLower()).ToList();
        }

        return matches.Select(MapToDtoFromLoaded).ToList();
    }

    public async Task<(MatchResponseDto? Match, MatchError Kind)> GetById(int id, int callerPlayerId, bool isAdmin)
    {
        var match = await _context.Matches
            .Include(m => m.Player1)
            .Include(m => m.Player2)
            .Include(m => m.Winner)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (match == null)
            return (null, MatchError.NotFound);

        // Se devuelve 404 y no 403 a propósito: un 403 confirmaría que el match
        // existe, que es justamente lo que no queremos revelar.
        if (!isAdmin && match.Player1Id != callerPlayerId && match.Player2Id != callerPlayerId)
            return (null, MatchError.NotFound);

        return (MapToDtoFromLoaded(match), MatchError.None);
    }

    public async Task<(MatchResponseDto? Match, string? Error, MatchError Kind)> Update(
        int id, UpdateMatchDto dto, int callerPlayerId, bool isAdmin)
    {
        var match = await _context.Matches
            .Include(m => m.Player1)
            .Include(m => m.Player2)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (match == null)
            return (null, "Match no encontrado", MatchError.NotFound);

        // Solo los participantes (o un admin) pueden tocar la partida.
        // Sin esto cualquier usuario autenticado podía declararse ganador
        // de un match ajeno.
        if (!isAdmin && match.Player1Id != callerPlayerId && match.Player2Id != callerPlayerId)
            return (null, "Match no encontrado", MatchError.NotFound);

        // Se calcula cómo quedaría el match DESPUÉS de aplicar los cambios y se
        // valida eso, en vez de validar campo por campo. Antes solo se revisaba
        // cuando cambiaba StartTime: mover la partida de mesa, o acortar el
        // EndTime, se colaba sin ninguna verificación.
        var nuevoStart = dto.StartTime ?? match.StartTime;
        var nuevoEnd = dto.EndTime ?? match.EndTime;
        var nuevaMesa = dto.TableNumber ?? match.TableNumber;

        var cambiaHorario = dto.StartTime.HasValue || dto.EndTime.HasValue;
        var cambiaMesa = dto.TableNumber.HasValue;

        if (cambiaHorario)
        {
            // Al reprogramar no se exige fecha futura: un admin puede estar
            // corrigiendo los datos de una partida que ya se jugó.
            var errorHorario = ValidarHorario(nuevoStart, nuevoEnd, exigirFutura: false);
            if (errorHorario != null)
                return (null, errorHorario, MatchError.Validation);
        }

        if (cambiaHorario || cambiaMesa)
        {
            var endEfectivo = nuevoEnd ?? nuevoStart.AddHours(1);

            var conflict = await HasConflict(match.Player1Id, match.Player2Id, nuevoStart, endEfectivo, id);
            if (conflict != null)
                return (null, conflict, MatchError.Conflict);

            var conflictoMesa = await HasTableConflict(nuevaMesa, nuevoStart, endEfectivo, id);
            if (conflictoMesa != null)
                return (null, conflictoMesa, MatchError.Conflict);
        }

        match.StartTime = nuevoStart;
        if (dto.EndTime.HasValue) match.EndTime = dto.EndTime.Value;
        if (dto.TableNumber.HasValue) match.TableNumber = dto.TableNumber.Value;

        if (dto.WinnerId.HasValue)
        {
            // Validar que el ganador sea uno de los dos jugadores
            if (dto.WinnerId != match.Player1Id && dto.WinnerId != match.Player2Id)
                return (null, "El ganador debe ser uno de los jugadores del match", MatchError.Validation);

            await ReassignWinner(match, dto.WinnerId.Value);
        }

        await _context.SaveChangesAsync();
        return (await MapToDto(match), null, MatchError.None);
    }

    public async Task<(bool Success, string? Error, MatchError Kind)> Delete(int id)
    {
        var match = await _context.Matches.FindAsync(id);
        if (match == null)
            return (false, "Match no encontrado", MatchError.NotFound);

        // Solo se puede borrar si no empezó
        if (match.StartTime <= DateTime.UtcNow && match.EndTime == null)
            return (false, "No se puede eliminar un match en curso", MatchError.Conflict);

        // Si tenía ganador, hay que devolverle la victoria antes de borrar.
        if (match.WinnerId.HasValue)
        {
            var previous = await _context.Players.FindAsync(match.WinnerId.Value);
            if (previous != null && previous.Wins > 0) previous.Wins -= 1;
        }

        _context.Matches.Remove(match);
        await _context.SaveChangesAsync();
        return (true, null, MatchError.None);
    }

    /// Cambia el ganador de un match manteniendo el contador de victorias consistente.
    /// El bug anterior era que solo se hacía Wins += 1 al nuevo ganador y nunca se
    /// le restaba al anterior: corregir un resultado dejaba a los dos jugadores
    /// con una victoria fantasma cada uno.
    private async Task ReassignWinner(Match match, int newWinnerId)
    {
        if (match.WinnerId == newWinnerId) return;

        if (match.WinnerId.HasValue)
        {
            var previous = await _context.Players.FindAsync(match.WinnerId.Value);
            if (previous != null && previous.Wins > 0)
            {
                previous.Wins -= 1;
                _logger.LogInformation(
                    "Match #{MatchId}: se revierte la victoria de {PlayerId}", match.Id, previous.Id);
            }
        }

        var winner = await _context.Players.FindAsync(newWinnerId);
        if (winner != null) winner.Wins += 1;

        match.WinnerId = newWinnerId;
    }

    // --- Validación de horarios ---

    /// Reglas que antes no existían: se podía crear una partida que terminaba
    /// antes de empezar, o agendarla en 2019.
    private static string? ValidarHorario(DateTime start, DateTime? end, bool exigirFutura)
    {
        if (end.HasValue && end.Value <= start)
            return "La hora de fin debe ser posterior a la de inicio";

        if (end.HasValue && (end.Value - start) > TimeSpan.FromHours(12))
            return "Una partida no puede durar más de 12 horas";

        if (exigirFutura && start < DateTime.UtcNow.AddMinutes(-5))
            return "No se puede agendar una partida en el pasado";

        return null;
    }

    // --- Double-booking ---

    /// Dos partidas no pueden compartir mesa en horarios que se solapan.
    /// El chequeo anterior solo miraba jugadores, así que la mesa 5 podía tener
    /// tres partidas simultáneas — en un club de pool eso es imposible.
    private async Task<string?> HasTableConflict(
        int? tableNumber, DateTime start, DateTime end, int? excludeMatchId = null)
    {
        // Sin mesa asignada no hay nada que reservar.
        if (!tableNumber.HasValue) return null;

        var query = _context.Matches.Where(m => m.TableNumber == tableNumber.Value);

        if (excludeMatchId.HasValue)
            query = query.Where(m => m.Id != excludeMatchId.Value);

        var conflicting = await query
            .Where(m =>
                m.StartTime < end &&
                (m.EndTime == null ? m.StartTime.AddHours(1) : m.EndTime.Value) > start)
            .FirstOrDefaultAsync();

        return conflicting == null
            ? null
            : $"La mesa {tableNumber.Value} ya está ocupada en ese horario (Match #{conflicting.Id})";
    }

    /// HasConflict: si el match existente empieza antes de que el nuevo termine, Y el match existente termina después de que el nuevo empiece,
    ///  hay solapamiento. El parámetro excludeMatchId sirve para cuando actualizás un match ya que no queremos que se detecte conflicto consigo mismo.
    private async Task<string?> HasConflict(int player1Id, int player2Id, DateTime start, DateTime end, int? excludeMatchId = null)
    {
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
    private static MatchResponseDto MapToDtoFromLoaded(Match match)
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
