namespace PoolManager.DTOs;


/// <summary>
/// El MatchResponseDto incluye los nombres de los jugadores (no solo IDs), así el cliente no necesita hacer requests extra para saber quién juega. 
/// Y el campo Status lo calculamos nosotros (upcoming/ongoing/completed) en vez de guardarlo en la DB.
/// </summary>
public class CreateMatchDto
{
    public int Player1Id { get; set; }
    public int Player2Id { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? TableNumber { get; set; }
}

public class UpdateMatchDto
{
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? WinnerId { get; set; }
    public int? TableNumber { get; set; }
}

public class MatchResponseDto
{
    public int Id { get; set; }
    public int Player1Id { get; set; }
    public string Player1Name { get; set; } = string.Empty;
    public int Player2Id { get; set; }
    public string Player2Name { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? WinnerId { get; set; }
    public string? WinnerName { get; set; }
    public int? TableNumber { get; set; }
    public string Status { get; set; } = string.Empty;
}