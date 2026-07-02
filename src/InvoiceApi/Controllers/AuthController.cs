using System.Security.Claims;
using InvoiceApi.Data;
using InvoiceApi.Exceptions;
using InvoiceApi.Models.Dtos;
using InvoiceApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvoiceApi.Controllers;

[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController(IAuthService authService, AppDbContext db) : ControllerBase
{
    /// <summary>Register a new user.</summary>
    [HttpPost("register")]
    [EnableRateLimiting("auth-ip")]
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
    [EnableRateLimiting("auth-ip")]
    [ProducesResponseType<AuthResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginDto dto, CancellationToken ct)
        => Ok(await authService.LoginAsync(dto, ct));

    /// <summary>Refresh an access token using a refresh token.</summary>
    [HttpPost("refresh")]
    [EnableRateLimiting("auth-ip")]
    [ProducesResponseType<AuthResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequestDto dto, CancellationToken ct)
        => Ok(await authService.RefreshAsync(dto.RefreshToken, ct));

    /// <summary>Revoke a refresh token (logout).</summary>
    [HttpPost("logout")]
    [EnableRateLimiting("auth-ip")]
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

        return Ok(new UserDto(user.Id, user.Email, user.Name, user.CreatedAt,
            user.DefaultSenderName, user.DefaultSenderAddress,
            user.TaxNumber, user.VatId, user.IsSmallBusiness,
            user.Street, user.PostalCode, user.City, user.Country,
            user.Iban, user.Bic, user.BankName));
    }

    /// <summary>Update name and/or sender defaults for the current user.</summary>
    [HttpPatch("me")]
    [Authorize]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateProfileDto dto, CancellationToken ct)
    {
        var sub = User.FindFirstValue("sub");
        if (sub is null || !Guid.TryParse(sub, out var userId))
            return Unauthorized();

        return Ok(await authService.UpdateProfileAsync(userId, dto, ct));
    }

    /// <summary>Change the current user's password. Revokes all existing refresh tokens.</summary>
    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting("auth-ip")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto, CancellationToken ct)
    {
        var sub = User.FindFirstValue("sub");
        if (sub is null || !Guid.TryParse(sub, out var userId))
            return Unauthorized();

        await authService.ChangePasswordAsync(userId, dto, ct);
        return NoContent();
    }

    /// <summary>Delete the current user's account. Cascades to invoices and refresh tokens.</summary>
    [HttpDelete("me")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteMe(CancellationToken ct)
    {
        var sub = User.FindFirstValue("sub");
        if (sub is null || !Guid.TryParse(sub, out var userId))
            return Unauthorized();

        await authService.DeleteAccountAsync(userId, ct);
        return NoContent();
    }
}
