using RWC_MLCCS.Common;

namespace RWC_MLCCS.Tests;

public sealed class AuthTests
{
    private const string TestKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";

    [Fact]
    public void AuthProofMatchesIndependentVector()
    {
        var proof = BrokerCrypto.CreateAuthProof(
            TestKey, "device", "dev-1", "key-1", "broker-nonce", "client-nonce", 1784764800000);

        Assert.Equal("bd8T4wxpmg0__8ifbvDnDa6O0MVZYz7RZHkz-dN-NCQ", proof);
    }

    [Fact]
    public void Base64UrlRejectsWrongLengthAndNonCanonicalInput()
    {
        Assert.Equal(32, Base64Url.Decode(TestKey, 32).Length);
        Assert.Throws<FormatException>(() => Base64Url.Decode(TestKey + "="));
        Assert.Throws<FormatException>(() => Base64Url.Decode(TestKey, 31));
    }
}
