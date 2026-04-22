using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using InvoiceApi.Models;
using Microsoft.IdentityModel.Tokens;

namespace InvoiceApi.Services;

public interface IJwtTokenService
{
    string GenerateAccessToken(User user);
    string GenerateRefreshToken();
    ClaimsPrincipal? ValidateToken(string token);
}

public class JwtTokenService(IConfiguration config) : IJwtTokenService
{
    public string GenerateAccessToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var minutes = config.GetValue("Jwt:AccessTokenMinutes", 15);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("name", user.Name),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(minutes),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        var bytes = new byte[64];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]!));

        try
        {
            return new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = config["Jwt:Issuer"],
                ValidAudience = config["Jwt:Audience"],
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.Zero
            }, out _);
        }
        catch
        {
            return null;
        }
    }
}
