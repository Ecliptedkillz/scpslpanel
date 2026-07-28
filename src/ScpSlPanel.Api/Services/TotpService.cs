using System.Security.Cryptography;

namespace ScpSlPanel.Api.Services;

public sealed class TotpService
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return EncodeBase32(bytes);
    }

    public bool Verify(string secret, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6) return false;
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        return Enumerable.Range(-1, 3).Any(offset =>
            FixedTimeEquals(CreateCode(secret, counter + offset), code.Trim()));
    }

    private static string CreateCode(string secret, long counter)
    {
        var key = DecodeBase32(secret);
        var bytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(bytes);
        var offset = hash[^1] & 0xf;
        var value = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8) | (hash[offset + 3] & 0xff);
        return (value % 1_000_000).ToString("D6");
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left), System.Text.Encoding.ASCII.GetBytes(right));

    private static string EncodeBase32(byte[] data)
    {
        var output = new System.Text.StringBuilder();
        var buffer = 0;
        var bits = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5) { output.Append(Alphabet[(buffer >> (bits -= 5)) & 31]); }
        }
        if (bits > 0) output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    private static byte[] DecodeBase32(string value)
    {
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var character in value.Trim().ToUpperInvariant())
        {
            var index = Alphabet.IndexOf(character);
            if (index < 0) continue;
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits -= 8))); buffer &= (1 << bits) - 1; }
        }
        return bytes.ToArray();
    }
}
