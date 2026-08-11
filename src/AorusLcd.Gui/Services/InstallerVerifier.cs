using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
