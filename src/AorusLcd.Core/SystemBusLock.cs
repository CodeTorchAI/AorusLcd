using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace AorusLcd.Core;

/// <summary>Global mutex serializing GUI/service access to GPU I2C 0x61; a scoped ACL lets the LocalSystem service and the unelevated GUI share it without granting the world control of the ACL. In normal operation the service holds a lifetime handle so the object is created and owned by LocalSystem.</summary>
[SupportedOSPlatform("windows")]
public sealed class SystemBusLock : IDisposable
{
    private const string MutexName = @"Global\AorusLcdBusLock";

    // Rights the non-creating process needs: wait on (Synchronize) and release (Modify) the mutex.
    private const MutexRights ShareRights = MutexRights.Synchronize | MutexRights.Modify;

    private readonly Mutex _mutex;

    public SystemBusLock() => _mutex = OpenOrCreate();

    private static Mutex OpenOrCreate()
    {
        var security = new MutexSecurity();
        // LocalSystem (the service) and administrators own the mutex outright, including its ACL.
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        // The unelevated interactive GUI runs as an Authenticated User: enough to wait on and
        // release the bus lock, but not to rewrite the ACL or take ownership.
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            ShareRights,
            AccessControlType.Allow));

        try
        {
            return MutexAcl.Create(initiallyOwned: false, MutexName, out _, security);
        }
        catch (UnauthorizedAccessException)
        {
            // MutexAcl.Create always requests FullControl, which the ACL withholds from
            // Authenticated Users. When the service created the mutex first, an unelevated
            // GUI lands here, so reopen with only the rights the ACL grants us.
            return MutexAcl.OpenExisting(MutexName, ShareRights);
        }
    }

    /// <summary>Acquire the bus mutex; dispose returned handle to release, with timeout for multi-second uploads.</summary>
    public IDisposable Acquire(int timeoutMs = 60000)
    {
        bool acquired;
        try
        {
            acquired = _mutex.WaitOne(timeoutMs);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner crashed mid-operation; we now hold the mutex.
            acquired = true;
        }
        if (!acquired)
        {
            throw new TimeoutException("Timed out waiting for the AorusLcd bus lock.");
        }
        return new Releaser(_mutex);
    }

    public void Dispose() => _mutex.Dispose();

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                mutex.ReleaseMutex();
            }
        }
    }
}
