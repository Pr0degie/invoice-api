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
    string? DefaultSenderAddress,
    string? TaxNumber,
    string? VatId,
    bool IsSmallBusiness,
    string? Street,
    string? PostalCode,
    string? City,
    string? Country,
    string? Iban,
    string? Bic,
    string? BankName);

// PATCH semantics: null = leave unchanged, "" = clear the field.
public record UpdateProfileDto(
    [MinLength(2), MaxLength(200)] string? Name,
    [MaxLength(200)] string? DefaultSenderName,
    [MaxLength(200)] string? DefaultSenderAddress,
    [MaxLength(50)] string? TaxNumber = null,
    [MaxLength(20)] string? VatId = null,
    bool? IsSmallBusiness = null,
    [MaxLength(200)] string? Street = null,
    [MaxLength(20)] string? PostalCode = null,
    [MaxLength(100)] string? City = null,
    [MaxLength(100)] string? Country = null,
    [MaxLength(34)] string? Iban = null,
    [MaxLength(11)] string? Bic = null,
    [MaxLength(100)] string? BankName = null);

public record RefreshRequestDto([Required] string RefreshToken);

public record ChangePasswordDto(
    [Required] string CurrentPassword,
    [Required, MinLength(8)] string NewPassword);
