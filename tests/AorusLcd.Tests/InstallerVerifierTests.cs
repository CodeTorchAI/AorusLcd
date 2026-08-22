using AorusLcd.Gui.Services;

namespace AorusLcd.Tests;

/// <summary>Publisher-pinning decision rules for in-app updates (<see cref="InstallerVerifier.ApplyPublisherPin"/>).</summary>
public class InstallerVerifierTests
{
    private const string Publisher = "CN=CodeTorch.ai, O=CodeTorch.ai, C=US";

    [Theory]
    [InlineData(InstallerSignature.Trusted)]
    [InlineData(InstallerSignature.Indeterminate)]
    [InlineData(InstallerSignature.NotSigned)]
    [InlineData(InstallerSignature.Invalid)]
    public void ApplyPublisherPin_PassesVerdictThroughWhenReferenceIsUnsigned(InstallerSignature verdict)
    {
        // The running app is unsigned (dev build), so identity can't be pinned: the plain
        // verdict stands and the download's signer is not consulted.
        var actualRead = false;

        var withNull = InstallerVerifier.ApplyPublisherPin(verdict, null, () => { actualRead = true; return Publisher; });
        var withEmpty = InstallerVerifier.ApplyPublisherPin(verdict, "", () => { actualRead = true; return Publisher; });

        Assert.Equal(verdict, withNull);
        Assert.Equal(verdict, withEmpty);
        Assert.False(actualRead);
    }

    [Theory]
    [InlineData(InstallerSignature.Invalid)]
    [InlineData(InstallerSignature.NotSigned)]
    public void ApplyPublisherPin_RejectsUnverifiedDownloadWithoutReadingPublisher(InstallerSignature verdict)
    {
        // With a signed app, only a verified chain (Trusted/Indeterminate) is eligible. An untrusted
        // or NotSigned file is blocked outright - even a matching signer subject on an otherwise
        // unverified file must not make it launchable - and the signer is not consulted.
        var actualRead = false;
        var result = InstallerVerifier.ApplyPublisherPin(verdict, Publisher, () =>
        {
            actualRead = true;
            return Publisher; // matching subject, but the chain isn't verified
        });

        Assert.Equal(InstallerSignature.Invalid, result);
        Assert.False(actualRead);
    }

    [Fact]
    public void ApplyPublisherPin_KeepsTrustedForMatchingPublisherCaseInsensitively()
    {
        var result = InstallerVerifier.ApplyPublisherPin(
            InstallerSignature.Trusted, Publisher, () => Publisher.ToUpperInvariant());

        Assert.Equal(InstallerSignature.Trusted, result);
    }

    [Fact]
    public void ApplyPublisherPin_KeepsIndeterminateWhenPublisherMatches()
    {
        // Identity is proven; only the revocation status is unknown, so the caller can still
        // ask the user to confirm rather than hard-blocking.
        var result = InstallerVerifier.ApplyPublisherPin(
            InstallerSignature.Indeterminate, Publisher, () => Publisher);

        Assert.Equal(InstallerSignature.Indeterminate, result);
    }

    [Theory]
    [InlineData(InstallerSignature.Trusted)]
    [InlineData(InstallerSignature.Indeterminate)]
    public void ApplyPublisherPin_RejectsDifferentPublisher(InstallerSignature verdict)
    {
        var result = InstallerVerifier.ApplyPublisherPin(
            verdict, Publisher, () => "CN=Someone Else, O=Attacker, C=US");

        Assert.Equal(InstallerSignature.Invalid, result);
    }

    [Theory]
    [InlineData(InstallerSignature.Trusted)]
    [InlineData(InstallerSignature.Indeterminate)]
    public void ApplyPublisherPin_RejectsVerifiedDownloadWithUnreadableSigner(InstallerSignature verdict)
    {
        // A verified chain whose signer subject can't be read while the app is signed => reject.
        var result = InstallerVerifier.ApplyPublisherPin(verdict, Publisher, () => null);

        Assert.Equal(InstallerSignature.Invalid, result);
    }
}
