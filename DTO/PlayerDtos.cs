using System.ComponentModel.DataAnnotations;

namespace PoolManager.DTOs;

public class CreatePlayerDto
{
    [Required(ErrorMessage = "El nombre es obligatorio")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "El nombre debe tener entre 2 y 100 caracteres")]
    public string Name { get; set; } = string.Empty;

    [StringLength(50)]
    public string? PreferredCue { get; set; }
}

public class UpdatePlayerDto
{
    [StringLength(100, MinimumLength = 2)]
    public string? Name { get; set; }

    [StringLength(50)]
    public string? PreferredCue { get; set; }

    /// Key de S3, no URL. Se valida que pertenezca al jugador que hace el request.
    [StringLength(300)]
    public string? ProfilePictureKey { get; set; }
}

public class PlayerResponseDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// Partidas ganadas.
    public int Wins { get; set; }

    /// Posición calculada: 1 = el que más ganó. Empates comparten posición.
    public int Ranking { get; set; }

    public string? PreferredCue { get; set; }

    /// Key de S3. Para verla, el cliente la manda a GET /storage/download-url.
    public string? ProfilePictureKey { get; set; }
}
