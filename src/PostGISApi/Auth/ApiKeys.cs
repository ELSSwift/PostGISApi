using System.Security.Cryptography;
using System.Text;

namespace PostGISApi.Auth;

public static class ApiKeys
{
    public const string Prefix = "pgapi_";

    /// <summary>Creates a new random key (256 bit).</summary>
    public static string Generate() =>
        Prefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 hex. Keys are high entropy, so an unsalted hash is sufficient.</summary>
    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static bool HashEquals(string hashHex, string candidateHex) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hashHex), Encoding.ASCII.GetBytes(candidateHex));

    public static string DisplayPrefix(string key) => key.Length <= 12 ? key : key[..12];

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
