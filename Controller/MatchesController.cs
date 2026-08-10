using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PoolManager.DTOs;
using PoolManager.Services;

namespace PoolManager.Controllers;

[ApiController]
[Route("[controller]")]
[Authorize]
public class MatchesController : ControllerBase
{
    private readonly MatchService _matchService;

    public MatchesController(MatchService matchService)
    {
        _matchService = matchService;
    }

    // POST /matches
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMatchDto dto)
    {
        var (match, error) = await _matchService.Create(dto);

        if (error != null)
        {
            // Si es double-booking, 409. Si no, 400.
            var statusCode = error.Contains("horario") ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
            return StatusCode(statusCode, new { error });
        }

        return CreatedAtAction(nameof(GetById), new { id = match!.Id }, match);
    }

    // GET /matches
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? date, [FromQuery] string? status)
    {
        var matches = await _matchService.GetAll(date, status);
        return Ok(matches);
    }

    // GET /matches/:id
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var match = await _matchService.GetById(id);
        if (match == null) return NotFound();
        return Ok(match);
    }

    // PATCH /matches/:id
    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateMatchDto dto)
    {
        var (match, error) = await _matchService.Update(id, dto);

        if (error != null)
        {
            if (error.Contains("no encontrado")) return NotFound(new { error });
            if (error.Contains("horario")) return Conflict(new { error });
            return BadRequest(new { error });
        }

        return Ok(match);
    }

    // DELETE /matches/:id
    [HttpDelete("{id}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var (success, error) = await _matchService.Delete(id);

        if (error != null)
        {
            if (error.Contains("no encontrado")) return NotFound(new { error });
            return Conflict(new { error });
        }

        return NoContent();
    }
}