using Microsoft.EntityFrameworkCore;
using PoolManager.Models;

namespace PoolManager.Data;

/// <summary>
/// Implementación del repositorio sobre Entity Framework Core + PostgreSQL.
///
/// Es el ÚNICO archivo del proyecto que menciona EF Core o escribe consultas.
/// Todo lo específico del motor —Include, FirstOrDefaultAsync, el estado de las
/// entidades— queda encerrado acá adentro.
/// </summary>
public class RepositorioEfCore : IRepositorioDatos
{
    private readonly AppDbContext _context;

    public RepositorioEfCore(AppDbContext context)
    {
        _context = context;
    }

    // ---------------------------------------------------------------
    // Jugadores — lectura
    // ---------------------------------------------------------------

    public Task<Player?> ObtenerJugadorPorKeycloakId(string keycloakId) =>
        _context.Players.FirstOrDefaultAsync(p => p.KeycloakId == keycloakId);

    public Task<int?> ObtenerIdJugadorPorKeycloakId(string keycloakId) =>
        _context.Players
            .Where(p => p.KeycloakId == keycloakId)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync();

    public async Task<Player?> ObtenerJugadorPorId(int id) =>
        await _context.Players.FindAsync(id);

    public Task<List<Player>> ListarJugadores(string? filtroNombre)
    {
        var query = _context.Players.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filtroNombre))
        {
            var aguja = filtroNombre.ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(aguja));
        }

        return query.ToListAsync();
    }

    public Task<List<int>> ListarVictoriasDeTodos() =>
        _context.Players.Select(p => p.Wins).ToListAsync();

    public Task<int> ContarJugadoresConMasVictoriasQue(int victorias) =>
        _context.Players.CountAsync(p => p.Wins > victorias);

    public Task<bool> AlgunJugadorUsaLaFoto(string key) =>
        _context.Players.AnyAsync(p => p.ProfilePictureKey == key);

    // ---------------------------------------------------------------
    // Jugadores — escritura
    // ---------------------------------------------------------------

    public void AgregarJugador(Player jugador) => _context.Players.Add(jugador);

    public void EliminarJugador(Player jugador) => _context.Players.Remove(jugador);

    public async Task<bool> IntentarInsertarJugador(Player jugador)
    {
        _context.Players.Add(jugador);

        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (EsViolacionDeUnicidad(ex))
        {
            // El insert perdedor queda pendiente en el contexto; si no se lo
            // desasocia, vuelve a intentarse en el siguiente guardado y falla de nuevo.
            _context.Entry(jugador).State = EntityState.Detached;
            return false;
        }
    }

    /// 23505 es el código de PostgreSQL para "violación de restricción única".
    /// Es conocimiento del motor y por eso vive únicamente en este archivo.
    private static bool EsViolacionDeUnicidad(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    // ---------------------------------------------------------------
    // Partidas — lectura
    // ---------------------------------------------------------------

    public async Task<Match?> ObtenerPartidaPorId(int id) =>
        await _context.Matches.FindAsync(id);

    public Task<Match?> ObtenerPartidaCompleta(int id) =>
        _context.Matches
            .Include(m => m.Player1)
            .Include(m => m.Player2)
            .Include(m => m.Winner)
            .FirstOrDefaultAsync(m => m.Id == id);

    public Task<List<Match>> ListarPartidas(int? soloDelJugador, DateTime? desde, DateTime? hasta)
    {
        var query = _context.Matches
            .Include(m => m.Player1)
            .Include(m => m.Player2)
            .Include(m => m.Winner)
            .AsQueryable();

        if (soloDelJugador.HasValue)
        {
            var id = soloDelJugador.Value;
            query = query.Where(m => m.Player1Id == id || m.Player2Id == id);
        }

        // Rango en vez de comparar la parte de fecha: así el motor puede usar
        // el índice IX_Matches_StartTime, que con una función sobre la columna
        // quedaría inutilizado.
        if (desde.HasValue && hasta.HasValue)
        {
            var d = desde.Value;
            var h = hasta.Value;
            query = query.Where(m => m.StartTime >= d && m.StartTime < h);
        }

        return query.ToListAsync();
    }

    public Task<int> ContarPartidasDelJugador(int jugadorId) =>
        _context.Matches.CountAsync(m => m.Player1Id == jugadorId || m.Player2Id == jugadorId);

    public Task<Match?> BuscarPartidaSolapadaDeJugadores(
        int jugador1Id, int jugador2Id, DateTime inicio, DateTime fin, int? excluirPartidaId)
    {
        var query = _context.Matches.AsQueryable();

        if (excluirPartidaId.HasValue)
        {
            var excluir = excluirPartidaId.Value;
            query = query.Where(m => m.Id != excluir);
        }

        // La condición de solapamiento va escrita inline: EF Core traduce el árbol
        // de expresión a SQL, y una llamada a un método propio no sabe traducirla.
        // Dos intervalos se solapan si cada uno empieza antes de que el otro termine.
        // Una partida sin hora de fin se asume de una hora.
        return query
            .Where(m =>
                (m.Player1Id == jugador1Id || m.Player2Id == jugador1Id ||
                 m.Player1Id == jugador2Id || m.Player2Id == jugador2Id)
                && m.StartTime < fin
                && (m.EndTime == null ? m.StartTime.AddHours(1) : m.EndTime.Value) > inicio)
            .FirstOrDefaultAsync();
    }

    public Task<Match?> BuscarPartidaSolapadaEnMesa(
        int numeroDeMesa, DateTime inicio, DateTime fin, int? excluirPartidaId)
    {
        var query = _context.Matches.Where(m => m.TableNumber == numeroDeMesa);

        if (excluirPartidaId.HasValue)
        {
            var excluir = excluirPartidaId.Value;
            query = query.Where(m => m.Id != excluir);
        }

        return query
            .Where(m =>
                m.StartTime < fin
                && (m.EndTime == null ? m.StartTime.AddHours(1) : m.EndTime.Value) > inicio)
            .FirstOrDefaultAsync();
    }

    // ---------------------------------------------------------------
    // Partidas — escritura
    // ---------------------------------------------------------------

    public void AgregarPartida(Match partida) => _context.Matches.Add(partida);

    public void EliminarPartida(Match partida) => _context.Matches.Remove(partida);

    // ---------------------------------------------------------------
    // Unidad de trabajo
    // ---------------------------------------------------------------

    public Task GuardarCambios() => _context.SaveChangesAsync();

    public Task<bool> PuedeConectar(CancellationToken cancellationToken = default) =>
        _context.Database.CanConnectAsync(cancellationToken);
}
