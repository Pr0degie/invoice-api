using FluentAssertions;
using InvoiceApi.Services;

namespace InvoiceApi.Tests.Auth;

public class PasswordHasherTests
{
    private readonly BCryptPasswordHasher _sut = new();

    [Fact]
    public void Hash_ProducesCost12Hash()
    {
        var hash = _sut.Hash("some-password");

        // BCrypt encodes the cost in the hash: $2a$12$... (or 2b/2y variants)
        hash.Should().MatchRegex(@"^\$2[aby]\$12\$");
        _sut.Verify("some-password", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_StillAcceptsLegacyCost11Hash()
    {
        // Hashes created before the workfactor was pinned to 12 used the
        // library default (cost 11). BCrypt encodes the cost in the hash,
        // so they must keep verifying unchanged.
        var legacyHash = BCrypt.Net.BCrypt.HashPassword("legacy-password", workFactor: 11);
        legacyHash.Should().MatchRegex(@"^\$2[aby]\$11\$");

        _sut.Verify("legacy-password", legacyHash).Should().BeTrue();
        _sut.Verify("wrong-password", legacyHash).Should().BeFalse();
    }
}
