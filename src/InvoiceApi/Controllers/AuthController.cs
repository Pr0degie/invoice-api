using System.Security.Claims;
using InvoiceApi.Data;
using InvoiceApi.Exceptions;
using InvoiceApi.Models.Dtos;
using InvoiceApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceApi.Controllers;

[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController(IAuthService authService, AppDbContext db) : ControllerBase
{
    /// <summary>Register a new user.</summary>
    [HttpPost("register")]
    [ProducesResponseType<AuthResponseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto, CancellationToken ct)
    {
        try
        {
            var result = await authService.RegisterAsync(dto, ct);
            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (ValidationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Log in and receive tokens.</summary>
    [HttpPost("login")]
    [ProducesResponseType<AuthResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginDto dto, CancellationToken ct)
    {
        try
        {
            return Ok(await authService.LoginAsync(dto, ct));
        }
        catch (UnauthorizedException)
        {
            return Unauthorized(new { error = "Invalid credentials." });
        }
    }

    /// <summary>Refresh an access token using a refresh token.</summary>
    [HttpPost("refresh")]
    [ProducesResponseType<AuthResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequestDto dto, CancellationToken ct)
    {
        try
        {
            return Ok(await authService.RefreshAsync(dto.RefreshToken, ct));
        }
        catch (UnauthorizedException)
        {
            return Unauthorized(new { error = "Invalid or expired refresh token." });
        }
    }

    /// <summary>Revoke a refresh token (logout).</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody] RefreshRequestDto dto, CancellationToken ct)
    {
        await authService.RevokeRefreshTokenAsync(dto.RefreshToken, ct);
        return NoContent();
    }

    /// <summary>Get current user info.</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var sub = User.FindFirstValue("sub");
        if (sub is null || !Guid.TryParse(sub, out var userId))
            return Unauthorized();

        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
            return Unauthorized();

        return Ok(new UserDto(user.Id, user.Email, user.Name, user.CreatedAt));
    }
}
