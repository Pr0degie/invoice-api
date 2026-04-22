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

public record UserDto(Guid Id, string Email, string Name, DateTime CreatedAt);

public record RefreshRequestDto([Required] string RefreshToken);
