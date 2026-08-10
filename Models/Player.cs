namespace PoolManager.Models;

public class Player
{
    public int Id { get; set; }
    public string KeycloakId { get; set; } = string.Empty;      ///Equivalente a Auth0_ID
    public string Name { get; set; } = string.Empty;
    public int Ranking { get; set; } = 0;
    public string? PreferredCue { get; set; }
    public string ProfilePictureUrl { get; set; } = string.Empty;

    // Navegación: partidas donde es jugador 1
    public ICollection<Match> MatchesAsPlayer1 { get; set; } = new List<Match>();
    // Navegación: partidas donde es jugador 2
    public ICollection<Match> MatchesAsPlayer2 { get; set; } = new List<Match>();
}