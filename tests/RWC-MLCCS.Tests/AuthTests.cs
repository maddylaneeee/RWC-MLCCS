using RWC_MLCCS.Common;

namespace RWC_MLCCS.Tests;

public sealed class AuthTests
{
    [Fact]
    public void PskResponseVerifiesWithMatchingSecret()
    {
        var nonce = PskAuthenticator.CreateNonce();
        var response = PskAuthenticator.CreateResponse("secret", nonce, "client-a");

        Assert.True(PskAuthenticator.VerifyResponse("secret", nonce, "client-a", response));
        Assert.False(PskAuthenticator.VerifyResponse("wrong", nonce, "client-a", response));
        Assert.False(PskAuthenticator.VerifyResponse("secret", nonce, "client-b", response));
    }
}
