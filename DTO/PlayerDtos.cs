namespace PoolManager.DTOs;


/// <summary>
///  Lo que son DTO's en general, sirven para usar ciertos datos del modelo y no todos.
/// Refiriendonos a que estos van a ser los campos que se van a poder recibir o devolver, y no campos sensibles como "KeycloakId".
/// El DTO conceptualmente es como las "vistas" en bases de datos. Son los mismos datos pero filtrados, permitiendo ver solo lo [necesario].
/// </summary>


///     Define que datos puede mandar el usuario al crear
public class CreatePlayerDto
{
    public string Name { get; set; } = string.Empty;
    public string? PreferredCue { get; set; }
    public string ProfilePictureUrl { get; set; } = string.Empty;
}

///     Define que puede modificar (todo nullable porque puede actualizar solo un campo)

public class UpdatePlayerDto
{
    public string? Name { get; set; }
    public string? PreferredCue { get; set; }
    public string? ProfilePictureUrl { get; set; }
}


///     Define los campos que se pueden devolver. Es preferible devolver esto y no todo el objeto Player.
public class PlayerResponseDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Ranking { get; set; }
    public string? PreferredCue { get; set; }
    public string ProfilePictureUrl { get; set; } = string.Empty;
}