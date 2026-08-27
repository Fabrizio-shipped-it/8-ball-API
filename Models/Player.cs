namespace PoolManager.Models;

public class Player
{
    public int Id { get; set; }
    public string KeycloakId { get; set; } = string.Empty;      ///Equivalente a Auth0_ID
    public string Name { get; set; } = string.Empty;

    /// Cantidad de partidas ganadas. Es un contador persistido.
    /// El "ranking" (la posición) NO se guarda: se calcula a partir de este valor,
    /// porque una posición depende de todos los demás jugadores y guardarla
    /// obligaría a reescribir toda la tabla en cada victoria.
    public int Wins { get; set; } = 0;

    public string? PreferredCue { get; set; }

    /// Key del objeto en S3 (ej: "players/12/9f3a-foto.jpg"), NO la URL completa.
    /// Guardar la URL filtraba el nombre del bucket y la región al cliente,
    /// y además era inútil porque el bucket es privado.
    public string? ProfilePictureKey { get; set; }

    // Navegación: partidas donde es jugador 1
    public ICollection<Match> MatchesAsPlayer1 { get; set; } = new List<Match>();
    // Navegación: partidas donde es jugador 2
    public ICollection<Match> MatchesAsPlayer2 { get; set; } = new List<Match>();
}
