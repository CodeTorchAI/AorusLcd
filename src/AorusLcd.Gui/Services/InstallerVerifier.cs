using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AorusLcd.Gui.Services;

/// <summary>Authenticode verdict for a downloaded installer.</summary>
public enum InstallerSignature
{
    /// <summary>Signed with a valid signature that chains to a trusted root.</summary>
    Trusted,

    /// <summary>No Authenticode signature is present.</summary>
    NotSigned,

    /// <summary>A signature is present, but its revocation status could not be checked.</summary>
    Indeterminate,

    /// <summary>A signature is present but is tampered, expired, or untrusted.</summary>
    Invalid,
}

/// <summary>Verifies a downloaded installer's Authenticode signature via WinVerifyTrust before it is launched.</summary>
[SupportedOSPlatform("windows")]
public static class InstallerVerifier
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdSaferFlag = 0x100;
    private const uint TrustNoSignature = 0x800B0100; // TRUST_E_NOSIGNATURE
    private const uint TrustSubjectFormUnknown = 0x800B0003; // provider cannot parse the file's form
    private const uint TrustProviderUnknown = 0x800B0001; // no trust provider for this file
    private const uint CertERevocationFailure = 0x800B010E; // revocation server offline/unreachable

    // WINTRUST_ACTION_GENERIC_VERIFY_V2
    private static Guid _genericVerify = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>Verify the file's Authenticode signature and trust chain.</summary>
    public static InstallerSignature Verify(string filePath)
    {
        var fileInfo = new WintrustFileInfo
        {
            CbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            FilePath = filePath,
        };
        IntPtr pFile = Marshal.AllocHGlobal((int)fileInfo.CbStruct);
        IntPtr pData = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WintrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeWholeChain,
                UnionChoice = WtdChoiceFile,
                FileInfoPtr = pFile,
                StateAction = WtdStateActionVerify,
                ProvFlags = WtdSaferFlag,
            };
            pData = Marshal.AllocHGlobal((int)data.CbStruct);
            Marshal.StructureToPtr(data, pData, false);

            uint status = WinVerifyTrust(IntPtr.Zero, ref _genericVerify, pData);
            // Capture the last error now: TRUST_E_NOSIGNATURE needs it to tell a genuinely
            // unsigned file from one the provider could not parse, and the close call below
            // would otherwise clobber it.
            int lastError = Marshal.GetLastWin32Error();

            // Always release the provider's per-verification state.
            data.StateAction = WtdStateActionClose;
            Marshal.StructureToPtr(data, pData, true);
            _ = WinVerifyTrust(IntPtr.Zero, ref _genericVerify, pData);

            return status switch
            {
                0 => InstallerSignature.Trusted,
                // WinVerifyTrust also returns TRUST_E_NOSIGNATURE when the provider cannot process
                // the file, not only for genuinely unsigned files. Per the documented pattern,
                // disambiguate via the last error: only a true "no signature" result is NotSigned;
                // a signature the provider could not validate is blocked as Invalid.
                TrustNoSignature => IsGenuinelyUnsigned(lastError)
                    ? InstallerSignature.NotSigned
                    : InstallerSignature.Invalid,
                // Signed but not fully verified: offline or blocked revocation checks must not be trusted.
                CertERevocationFailure => InstallerSignature.Indeterminate,
                _ => InstallerSignature.Invalid,
            };
        }
        finally
        {
            if (pData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pData);
            }
            Marshal.DestroyStructure<WintrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr data);

    /// <summary>The Authenticode signer's subject (distinguished name) for a signed file, or
    /// <c>null</c> when the file is unsigned or its signer can't be read. Used to pin an update
    /// to the same publisher that signed the currently running app. The subject DN (not the
    /// thumbprint) is intentional: it keeps auto-update working across a legitimate certificate
    /// renewal, while Authenticode's trusted-chain requirement plus CA organization validation
    /// make a different certificate bearing the same organization DN impractical to obtain.</summary>
    public static string? GetPublisher(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return null; // no reference to read (e.g. an empty ProcessPath)
        }
        try
        {
            // CreateFromSignedFile is the only managed way to read a PE's Authenticode signer;
            // X509CertificateLoader (the SYSLIB0057 replacement) only loads standalone cert
            // files, not the signer embedded in a signed executable, so suppress it here.
#pragma warning disable SYSLIB0057
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch (CryptographicException)
        {
            return null; // unsigned, or the signature couldn't be read
        }
    }

    /// <summary>Trust verdict for <paramref name="filePath"/> that additionally requires it to be
    /// signed by the same publisher as <paramref name="referenceSignedFile"/> (the running app).
    /// A valid Authenticode chain alone is not enough - an installer signed by a different
    /// publisher is rejected as <see cref="InstallerSignature.Invalid"/>. When the reference app
    /// is itself unsigned (a dev build) identity can't be pinned, so the plain trust verdict is
    /// returned unchanged.</summary>
    public static InstallerSignature VerifyMatchesPublisher(string filePath, string referenceSignedFile)
        => ApplyPublisherPin(Verify(filePath), GetPublisher(referenceSignedFile), () => GetPublisher(filePath));

    /// <summary>Decision core for <see cref="VerifyMatchesPublisher"/>, split out so the publisher
    /// pinning rules are unit-testable without Authenticode fixtures. <paramref name="actualPublisher"/>
    /// is evaluated only when a comparison is actually needed.</summary>
    internal static InstallerSignature ApplyPublisherPin(InstallerSignature trustVerdict,
        string? expectedPublisher, Func<string?> actualPublisher)
    {
        if (string.IsNullOrEmpty(expectedPublisher))
        {
            return trustVerdict; // running app is unsigned; there's nothing to pin against
        }
        // The app is signed, so a genuine update is a signed installer from the same publisher.
        // Anything that isn't a verified chain is rejected outright - including NotSigned, which
        // Verify() also returns for a file the trust provider couldn't parse, so a matching signer
        // subject on an otherwise-unverified file must not make it launchable. Only a chain that is
        // Trusted, or Indeterminate solely because revocation couldn't be checked, is eligible, and
        // then only when its signer matches.
        if (trustVerdict is not (InstallerSignature.Trusted or InstallerSignature.Indeterminate))
        {
            return InstallerSignature.Invalid;
        }
        return string.Equals(expectedPublisher, actualPublisher(), StringComparison.OrdinalIgnoreCase)
            ? trustVerdict
            : InstallerSignature.Invalid;
    }

    /// <summary>A TRUST_E_NOSIGNATURE result is only a true "unsigned" verdict for these last-error codes;
    /// any other last error means the provider could not validate a signature that is present.</summary>
    private static bool IsGenuinelyUnsigned(int lastError) => (uint)lastError
        is TrustNoSignature or TrustSubjectFormUnknown or TrustProviderUnknown;

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint CbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr KnownFileHandle;
        public IntPtr KnownSubjectGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint CbStruct;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPtr;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
