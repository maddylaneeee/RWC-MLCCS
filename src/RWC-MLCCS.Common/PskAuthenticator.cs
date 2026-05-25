using System.Security.Cryptography;
using System.Text;

namespace RWC_MLCCS.Common;

public static class PskAuthenticator
{
    public static string CreateNonce()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public static string CreateResponse(string sharedSecret, string nonce, string clientId)
    {
        var key = Encoding.UTF8.GetBytes(sharedSecret);
        var payload = Encoding.UTF8.GetBytes($"{nonce}|{clientId}");
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(payload));
    }

    public static bool VerifyResponse(string sharedSecret, string nonce, string clientId, string response)
    {
        var expected = CreateResponse(sharedSecret, nonce, clientId);
        var expectedBytes = Convert.FromBase64String(expected);

        try
        {
            var actualBytes = Convert.FromBase64String(response);
            return actualBytes.Length == expectedBytes.Length
                   && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
