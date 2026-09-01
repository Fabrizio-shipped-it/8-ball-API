using PoolManager.Data;
using PoolManager.DTOs;
using PoolManager.Models;

namespace PoolManager.Services;

/// <summary>
///  Lógica de negocio de jugadores. El "ranking" que se expone es una posición
///  calculada a partir de Wins, no un campo guardado.
///
///  Este servicio no sabe qué base de datos hay abajo: habla contra
///  IRepositorioDatos. Cambiar de motor no toca este archivo.
/// </summary>
public class PlayerService
{
    private readonly IRepositorioDatos _datos;
    private readonly ILogger<PlayerService> _logger;

    public PlayerService(IRepositorioDatos datos, ILogger<PlayerService> logger)
    {
        _datos = datos;
        _logger = logger;
    }

    // Busca o crea un jugador a partir del token de Keycloak (auto-registro en primer login)
    public async Task<PlayerResponseDto> GetOrCreateFromToken(string keycloakId, string name)
    {
        var player = await _datos.ObtenerJugadorPorKeycloakId(keycloakId);

        if (player == null)
        {
            player = new Player
            {
                KeycloakId = keycloakId,
                Name = name,
                ProfilePictureKey = null // todavía no subió foto
            };
            // Dos requests simultáneos del mismo usuario nuevo (por ejemplo, un front
            // que dispara varias llamadas al iniciar sesión) intentan insertar dos
            // veces. El repositorio detecta la colisión y devuelve false en vez de
            // dejar que explote como un 500.
            var insertado = await _datos.IntentarInsertarJugador(player);

            if (!insertado)
            {
                // Ganó el otro request: se relee la fila que quedó.
                player = await _datos.ObtenerJugadorPorKeycloakId(keycloakId)
                    ?? throw new InvalidOperationException(
                        "El insert del jugador fue rechazado por unicidad pero no se encontró la fila existente.");

                _logger.LogInformation(
                    "Auto-registro concurrente resuelto para el jugador {PlayerId}", player.Id);
            }
        }

        return await MapToDto(player);
    }

    /// Resuelve el Id interno del jugador a partir del sub del token.
    /// Lo usan los controllers para chequear pertenencia sin traer la entidad entera.
    public Task<int?> GetIdByKeycloakId(string keycloakId) =>
        _datos.ObtenerIdJugadorPorKeycloakId(keycloakId);

    // GET /players/me
    public async Task<PlayerResponseDto?> GetByKeycloakId(string keycloakId)
    {
        var player = await _datos.ObtenerJugadorPorKeycloakId(keycloakId);
        return player == null ? null : await MapToDto(player);
    }

    // GET /players (admin) con filtro opcional por nombre
    public async Task<List<PlayerResponseDto>> GetAll(string? nameFilter)
    {
        var players = await _datos.ListarJugadores(nameFilter);

        // El ranking depende de TODOS los jugadores, no solo de los filtrados.
        var allWins = await _datos.ListarVictoriasDeTodos();

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

        _datos.AgregarJugador(player);
        await _datos.GuardarCambios();
        return await MapToDto(player);
    }

    // PATCH /players/me
    // profilePictureKey ya viene validada por el controller (pertenencia + existencia en S3).
    public async Task<PlayerResponseDto?> Update(string keycloakId, UpdatePlayerDto dto)
    {
        var player = await _datos.ObtenerJugadorPorKeycloakId(keycloakId);
        if (player == null) return null;

        if (dto.Name != null) player.Name = dto.Name;
        if (dto.PreferredCue != null) player.PreferredCue = dto.PreferredCue;
        if (dto.ProfilePictureKey != null) player.ProfilePictureKey = dto.ProfilePictureKey;

        await _datos.GuardarCambios();
        return await MapToDto(player);
    }

    // DELETE /players/:id (admin)
    //
    // Devuelve el motivo del fallo para que el controller elija el status code.
    // Antes devolvía solo bool: si el jugador tenía partidas, la FK con
    // DeleteBehavior.Restrict hacía explotar el guardado con DbUpdateException
    // y el usuario recibía un 500 sin explicación.
    public async Task<(bool Success, string? Error, bool Conflict)> Delete(int id)
    {
        var player = await _datos.ObtenerJugadorPorId(id);
        if (player == null)
            return (false, "Jugador no encontrado", false);

        var partidas = await _datos.ContarPartidasDelJugador(id);

        if (partidas > 0)
            return (false,
                $"No se puede eliminar: el jugador tiene {partidas} partida(s) asociada(s). " +
                "Eliminá primero sus partidas.",
                true);

        _datos.EliminarJugador(player);
        await _datos.GuardarCambios();
        _logger.LogInformation("Jugador eliminado: {PlayerId} - {PlayerName}", player.Id, player.Name);
        return (true, null, false);
    }

    /// True si esa key está referenciada como foto de perfil de algún jugador.
    /// Permite que cualquiera vea la foto ACTUAL de otro (aparecen en los listados
    /// de partidas), pero no keys arbitrarias ni versiones viejas del bucket.
    public Task<bool> IsPublishedProfileKey(string key) =>
        _datos.AlgunJugadorUsaLaFoto(key);

    // --- Ranking ---

    /// Posición 1 = el que más ganó. Los empates comparten posición
    /// (dos jugadores con 3 victorias son ambos #1, y el siguiente es #3).
    private static int RankFor(int wins, IReadOnlyCollection<int> allWins)
        => allWins.Count(w => w > wins) + 1;

    private async Task<PlayerResponseDto> MapToDto(Player player)
    {
        var better = await _datos.ContarJugadoresConMasVictoriasQue(player.Wins);
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
