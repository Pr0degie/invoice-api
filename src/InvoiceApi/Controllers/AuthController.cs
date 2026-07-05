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
    /// <summary>Register a new user. Sends an e-mail verification link; no session is
    /// issued — the account must be verified before it can log in.</summary>
    [HttpPost("register")]
    [EnableRateLimiting("auth-ip")]
    [ProducesResponseType<MessageResponseDto>(StatusCodes.Status201Created)]
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

    /// <summary>Log in and receive tokens. Unverified accounts get 403 email_not_verified.</summary>
    [HttpPost("login")]
    [EnableRateLimiting("auth-ip")]
    [ProducesResponseType<AuthResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Login([FromBody] LoginDto dto, CancellationToken ct)
        => Ok(await authService.LoginAsync(dto, ct));

    /// <summary>Request a password-reset link. Always 200 with a generic message —
    /// never reveals whether the address is registered.</summary>
    [HttpPost("forgot-password")]
    [EnableRateLimiting("auth-ip")]
    [ProducesResponseType<MessageResponseDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto, CancellationToken ct)
        => Ok(await authService.ForgotPasswordAsync(dto, ct));

    /// <summary>Redeem a reset token and set a new password. Revokes all refresh tokens.</summary>
    [HttpPost("reset-password")]
    [EnableRateLimiting("auth-ip")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto, CancellationToken ct)
    {
        await authService.ResetPasswordAsync(dto, ct);
        return NoContent();
    }

    /// <summary>Redeem an e-mail-verification token, unblocking login.</summary>
    [HttpPost("verify-email")]
    [EnableRateLimiting("auth-ip")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailDto dto, CancellationToken ct)
    {
        await authService.VerifyEmailAsync(dto, ct);
        return NoContent();
    }

    /// <summary>Re-send the verification link. Always 200 with a generic message —
    /// never reveals whether the address is registered or already verified.</summary>
    [HttpPost("resend-verification")]
    [EnableRateLimiting("auth-ip")]
    [ProducesResponseType<MessageResponseDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ResendVerification([FromBody] ResendVerificationDto dto, CancellationToken ct)
        => Ok(await authService.ResendVerificationAsync(dto, ct));

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

        // DeletedAt set = anonymized account (ADR 0005) — dead, not a zombie profile.
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null || user.DeletedAt is not null)
            return Unauthorized();

        return Ok(new UserDto(user.Id, user.Email, user.Name, user.CreatedAt,
            user.DefaultSenderName, user.DefaultSenderAddress,
            user.TaxNumber, user.VatId, user.IsSmallBusiness,
            user.Street, user.PostalCode, user.City, user.Country, user.Phone,
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

    /// <summary>
    /// Delete the current user's account. Accounts without numbered invoices are hard-deleted
    /// (cascades to drafts and refresh tokens). Accounts owning numbered invoices are anonymized
    /// instead — the invoices and their archives stay under § 147 AO retention (ADR 0005).
    /// Returns 204 in both cases.
    /// </summary>
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
