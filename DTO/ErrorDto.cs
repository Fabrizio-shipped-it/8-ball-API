namespace PoolManager.DTOs;

/// <summary>
/// Formato único de error de la API: { "error": "mensaje" }.
///
/// Existe como tipo concreto —y no como objeto anónimo— para poder declararlo
/// en los [ProducesResponseType] y que Swagger documente la forma real de las
/// respuestas de error, no solo el código de estado.
/// </summary>
/// <param name="Error">Mensaje apto para mostrarle al usuario final.</param>
public record ErrorDto(string Error);
