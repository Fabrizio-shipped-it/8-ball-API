using PoolManager.Models;

namespace PoolManager.Data;

/// <summary>
/// Único punto de contacto con la base de datos.
///
/// Los servicios de negocio (PlayerService, MatchService) dependen de esta
/// interfaz y no del motor. Si mañana se cambia PostgreSQL por otra cosa, se
/// escribe una implementación nueva de esta interfaz y ningún servicio se toca.
///
/// Los métodos están nombrados por lo que significan en el dominio
/// ("buscar un conflicto de horario") y no por cómo se resuelven en SQL. Eso es
/// lo que evita que las consultas se filtren hacia la capa de negocio.
///
/// Sobre GuardarCambios: varias operaciones modifican más de una fila y tienen
/// que caer juntas o no caer (declarar un ganador toca la partida y los
/// contadores de dos jugadores). Por eso el guardado es explícito y separado:
/// el servicio decide dónde termina la transacción.
/// </summary>
public interface IRepositorioDatos
{
    // ---------------------------------------------------------------
    // Jugadores — lectura
    // ---------------------------------------------------------------

    /// El jugador asociado a una identidad de Keycloak, o null si nunca se registró.
    Task<Player?> ObtenerJugadorPorKeycloakId(string keycloakId);

    /// Solo el Id interno. Se usa en los chequeos de pertenencia, donde traer la
    /// entidad completa sería desperdicio.
    Task<int?> ObtenerIdJugadorPorKeycloakId(string keycloakId);

    Task<Player?> ObtenerJugadorPorId(int id);

    /// Todos los jugadores, con filtro opcional por nombre (parcial, sin distinguir mayúsculas).
    Task<List<Player>> ListarJugadores(string? filtroNombre);

    /// Las victorias de todos los jugadores. El ranking es una posición relativa,
    /// así que necesita el universo completo aunque el listado esté filtrado.
    Task<List<int>> ListarVictoriasDeTodos();

    /// Cuántos jugadores ganaron más partidas que este número. Es el cálculo de posición.
    Task<int> ContarJugadoresConMasVictoriasQue(int victorias);

    /// True si algún jugador tiene esa key como foto de perfil actual.
    Task<bool> AlgunJugadorUsaLaFoto(string key);

    // ---------------------------------------------------------------
    // Jugadores — escritura
    // ---------------------------------------------------------------

    void AgregarJugador(Player jugador);
    void EliminarJugador(Player jugador);

    /// Inserta el jugador y confirma en la base, en una sola operación.
    ///
    /// Devuelve <c>false</c> si otro proceso ya insertó un jugador con el mismo
    /// KeycloakId. Esa carrera ocurre cuando un front dispara varias llamadas al
    /// iniciar sesión. Detectarla exige leer el código de error del motor, así que
    /// se resuelve acá adentro: el servicio solo recibe un sí o un no.
    ///
    /// Cualquier otro fallo de escritura se propaga como excepción.
    Task<bool> IntentarInsertarJugador(Player jugador);

    // ---------------------------------------------------------------
    // Partidas — lectura
    // ---------------------------------------------------------------

    /// La partida sola, sin los jugadores cargados.
    Task<Match?> ObtenerPartidaPorId(int id);

    /// La partida con sus dos jugadores y el ganador ya cargados.
    Task<Match?> ObtenerPartidaCompleta(int id);

    /// Partidas con sus jugadores cargados.
    /// <param name="soloDelJugador">Si viene un Id, limita a las partidas donde ese jugador participa.</param>
    /// <param name="desde">Inicio del rango de fechas, inclusive. Null para no filtrar.</param>
    /// <param name="hasta">Fin del rango, exclusive.</param>
    Task<List<Match>> ListarPartidas(int? soloDelJugador, DateTime? desde, DateTime? hasta);

    /// Cuántas partidas tiene asociadas un jugador, como jugador 1 o 2.
    Task<int> ContarPartidasDelJugador(int jugadorId);

    /// Primera partida que se solapa en el tiempo con alguno de estos dos jugadores.
    /// Null si no hay ninguna.
    Task<Match?> BuscarPartidaSolapadaDeJugadores(
        int jugador1Id, int jugador2Id, DateTime inicio, DateTime fin, int? excluirPartidaId);

    /// Primera partida que ocupa esa mesa en un horario que se solapa. Null si la mesa está libre.
    Task<Match?> BuscarPartidaSolapadaEnMesa(
        int numeroDeMesa, DateTime inicio, DateTime fin, int? excluirPartidaId);

    // ---------------------------------------------------------------
    // Partidas — escritura
    // ---------------------------------------------------------------

    void AgregarPartida(Match partida);
    void EliminarPartida(Match partida);

    // ---------------------------------------------------------------
    // Unidad de trabajo
    // ---------------------------------------------------------------

    /// Confirma en la base todos los cambios acumulados, en una sola transacción.
    Task GuardarCambios();

    /// Verifica que la base esté accesible. Lo usa el chequeo de readiness.
    Task<bool> PuedeConectar(CancellationToken cancellationToken = default);
}
