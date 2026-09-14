using System.Security.Cryptography;
using System.Text;

namespace Whisper.Server.Security;

public readonly record struct PasswordHash(byte[] Hash, byte[] Salt);

public interface IPasswordHasher
{
    PasswordHash Hash(string password);

    bool Verify(string password, byte[] expectedHash, byte[] salt);
}

public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    public const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public PasswordHash Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        return new PasswordHash(Derive(password, salt), salt);
    }

    public bool Verify(string password, byte[] expectedHash, byte[] salt)
    {
        if (expectedHash.Length == 0 || salt.Length == 0)
        {
            return false;
        }

        var actual = Derive(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    private static byte[] Derive(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(
        Encoding.UTF8.GetBytes(password),
        salt,
        Iterations,
        HashAlgorithmName.SHA256,
        HashSize);
}
