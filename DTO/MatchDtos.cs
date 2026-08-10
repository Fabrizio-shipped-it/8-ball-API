using System.ComponentModel.DataAnnotations;

namespace PoolManager.DTOs;

public class CreateMatchDto
{
    [Required]
    public int Player1Id { get; set; }

    [Required]
    public int Player2Id { get; set; }

    [Required(ErrorMessage = "La hora de inicio es obligatoria")]
    public DateTime StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    [Range(1, 100, ErrorMessage = "El número de mesa debe estar entre 1 y 100")]
    public int? TableNumber { get; set; }
}

public class UpdateMatchDto
{
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? WinnerId { get; set; }

    [Range(1, 100)]
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