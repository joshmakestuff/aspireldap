using System.Security.Cryptography;
using System.Text;

namespace Aspire.Hosting.ApplicationModel.Seeding;

/// <summary>
/// Hashes seeded <c>userPassword</c> values as <c>{SSHA}</c> (salted SHA-1, RFC 2307 style)
/// so the directory stores a hash at rest instead of the cleartext. slapd verifies binds
/// against <c>{SSHA}</c> natively with no extra modules (unlike <c>{SSHA256}</c>+, which
/// need pw-sha2), and the scheme guards the stored value — SHA-1's collision weakness is
/// irrelevant to preimage-resistance of a salted password hash in a dev directory.
/// </summary>
internal static class LdapPasswordHasher
{
    private const int SaltLength = 8;

    /// <summary>
    /// Returns the value to store in <c>userPassword</c> for a seeded user: the
    /// <c>{SSHA}</c> hash of <paramref name="password"/>, or the value verbatim when it
    /// already carries an RFC 3112-style scheme prefix (<c>{SSHA}...</c>, <c>{CRYPT}...</c>)
    /// — hashing an already-hashed value would break the user's bind.
    /// </summary>
    public static string ToUserPasswordValue(string password)
        => HasSchemePrefix(password) ? password : HashSsha(password);

    /// <summary>
    /// <c>{SSHA}</c>: base64(SHA1(password + salt) + salt) with a fresh random salt,
    /// matching <c>slappasswd -h {SSHA}</c> output.
    /// </summary>
    public static string HashSsha(string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var buffer = new byte[passwordBytes.Length + SaltLength];
        passwordBytes.CopyTo(buffer, 0);
        RandomNumberGenerator.Fill(buffer.AsSpan(passwordBytes.Length));

        var digestAndSalt = new byte[SHA1.HashSizeInBytes + SaltLength];
        SHA1.HashData(buffer, digestAndSalt);
        buffer.AsSpan(passwordBytes.Length).CopyTo(digestAndSalt.AsSpan(SHA1.HashSizeInBytes));

        return $"{{SSHA}}{Convert.ToBase64String(digestAndSalt)}";
    }

    /// <summary>
    /// True when the value starts with an RFC 3112 scheme prefix: <c>{</c>, one or more
    /// scheme characters (letters, digits, <c>-</c>, <c>.</c>, <c>/</c> as in <c>{X-...}</c>
    /// or crypt variants), then <c>}</c>.
    /// </summary>
    private static bool HasSchemePrefix(string password)
    {
        if (password.Length < 3 || password[0] != '{')
        {
            return false;
        }
        var close = password.IndexOf('}', StringComparison.Ordinal);
        if (close < 2)
        {
            return false;
        }
        for (var i = 1; i < close; i++)
        {
            var c = password[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '/'))
            {
                return false;
            }
        }
        return true;
    }
}
