using System.ComponentModel.DataAnnotations;

namespace PoolManager.DTOs;

public class CreatePlayerDto
{
    [Required(ErrorMessage = "El nombre es obligatorio")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "El nombre debe tener entre 2 y 100 caracteres")]
    public string Name { get; set; } = string.Empty;

    [StringLength(50)]
    public string? PreferredCue { get; set; }

    [Required(ErrorMessage = "La foto de perfil es obligatoria")]
    public string ProfilePictureUrl { get; set; } = string.Empty;
}

public class UpdatePlayerDto
{
    [StringLength(100, MinimumLength = 2)]
    public string? Name { get; set; }

    [StringLength(50)]
    public string? PreferredCue { get; set; }

    public string? ProfilePictureUrl { get; set; }
}

public class PlayerResponseDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Ranking { get; set; }
    public string? PreferredCue { get; set; }
    public string ProfilePictureUrl { get; set; } = string.Empty;
}