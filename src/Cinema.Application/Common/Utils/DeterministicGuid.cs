using System.Security.Cryptography;
using System.Text;

namespace Cinema.Application.Common.Utils;

public static class DeterministicGuid
{
    public static Guid Create(string input)
    {
        if (string.IsNullOrEmpty(input))
            throw new ArgumentNullException(nameof(input));

        byte[] inputBytes = Encoding.UTF8.GetBytes(input);
        byte[] hashBytes = MD5.HashData(inputBytes);

        // Set version to 3 (0011) - MD5-based UUID
        hashBytes[7] = (byte)((hashBytes[7] & 0x0F) | 0x30);

        // Set variant to RFC 4122 (10xx)
        hashBytes[8] = (byte)((hashBytes[8] & 0x3F) | 0x80);

        return new Guid(hashBytes);
    }
}
