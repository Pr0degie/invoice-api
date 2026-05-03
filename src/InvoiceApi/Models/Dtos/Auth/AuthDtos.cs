using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Models.Dtos;

public record RegisterDto(
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password,
    [Required, MinLength(2)] string Name
);

public record LoginDto(
    [Required, EmailAddress] string Email,
    [Required] string Password
);

public record AuthResponseDto(
    string Token,
    string RefreshToken,
    DateTime ExpiresAt,
    UserDto User
);

public record UserDto(
    Guid Id,
    string Email,
    string Name,
    DateTime CreatedAt,
    string? DefaultSenderName,
    string? DefaultSenderAddress);

public record UpdateProfileDto(
    [MinLength(2), MaxLength(200)] string? Name,
    [MaxLength(200)] string? DefaultSenderName,
    [MaxLength(200)] string? DefaultSenderAddress);

public record RefreshRequestDto([Required] string RefreshToken);

public record ChangePasswordDto(
    [Required] string CurrentPassword,
    [Required, MinLength(8)] string NewPassword);
