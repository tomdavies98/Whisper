using FluentAssertions;
using Whisper.Server.Security;
using Xunit;

namespace Whisper.Tests.Server;

public class PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        _hasher.Verify("correct horse battery staple", hash.Hash, hash.Salt).Should().BeTrue();
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        _hasher.Verify("Correct horse battery staple", hash.Hash, hash.Salt).Should().BeFalse();
    }

    [Fact]
    public void Verify_EmptyPasswordAgainstRealHash_ReturnsFalse()
    {
        var hash = _hasher.Hash("something");

        _hasher.Verify(string.Empty, hash.Hash, hash.Salt).Should().BeFalse();
    }

    [Fact]
    public void Verify_UnseededCredentials_ReturnsFalse()
    {
        // Guards the window before the database is seeded: an empty hash must never match.
        _hasher.Verify(string.Empty, [], []).Should().BeFalse();
        _hasher.Verify("anything", [], []).Should().BeFalse();
    }

    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentSaltsAndHashes()
    {
        var first = _hasher.Hash("same password");
        var second = _hasher.Hash("same password");

        first.Salt.Should().NotEqual(second.Salt);
        first.Hash.Should().NotEqual(second.Hash);
        _hasher.Verify("same password", second.Hash, second.Salt).Should().BeTrue();
    }

    [Fact]
    public void Verify_HashWithForeignSalt_ReturnsFalse()
    {
        var first = _hasher.Hash("same password");
        var second = _hasher.Hash("same password");

        _hasher.Verify("same password", first.Hash, second.Salt).Should().BeFalse();
    }

    [Fact]
    public void Hash_ProducesSixteenByteSaltAndThirtyTwoByteHash()
    {
        var hash = _hasher.Hash("whatever");

        hash.Salt.Should().HaveCount(16);
        hash.Hash.Should().HaveCount(32);
    }
}
