namespace PoolManager.Models;

public class Match
{
    public int Id { get; set; }
    public int Player1Id { get; set; }
    public int Player2Id { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? WinnerId { get; set; }
    public int? TableNumber { get; set; }

    // Navegación
    public Player Player1 { get; set; } = null!; //le dice al compilador "sé que esto es null ahora, pero EF Core lo va a llenar"
    public Player Player2 { get; set; } = null!;
    public Player? Winner { get; set; }
}