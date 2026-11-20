using System.Runtime.InteropServices;

namespace Cellular.Core;

public enum MbnRegisterMode { None = 0, Automatic = 1, Manual = 2 }

/// <summary>
/// Network registration control via the Windows Mobile Broadband COM API (mbnapi).
/// netsh and WinRT can't do this; <c>IMbnRegistration::SetRegisterMode(Automatic)</c> is what
/// recovers the XMM7360 when it gets stuck in a Searching/Deregistered loop (2026-11-20 incident).
/// </summary>
public static class MbnRegistration
{
    public static MbnRegisterMode? GetRegisterMode()
    {
        var reg = GetRegistration();
        if (reg is null) return null;
        try { reg.GetRegisterMode(out var mode); return (MbnRegisterMode)mode; }
        finally { Marshal.ReleaseComObject(reg); }
    }

    /// <summary>MBN_DATA_CLASS flags used as the "preferred data class" when registering.</summary>
    public const uint DataClassAny = 0, DataClass3G = 0x04 | 0x08 | 0x10 /* UMTS|HSDPA|HSUPA */, DataClassLte = 0x20;

    public static uint? GetCurrentDataClass()
    {
        var reg = GetRegistration();
        if (reg is null) return null;
        try { reg.GetCurrentDataClass(out var dc); return dc; }
        finally { Marshal.ReleaseComObject(reg); }
    }

    /// <summary>Asks the modem to (re)register automatically on its home network. Returns the request id.</summary>
    public static uint RegisterAutomatic() => RegisterAutomatic(DataClassAny);

    /// <param name="preferredDataClass">MBN_DATA_CLASS preference (0 = let the modem choose).</param>
    public static uint RegisterAutomatic(uint preferredDataClass)
    {
        var reg = GetRegistration() ?? throw new ModemException("No mobile broadband interface found.");
        try
        {
            reg.SetRegisterMode(1 /* MBN_REGISTER_MODE_AUTOMATIC */, null, preferredDataClass, out var requestId);
            return requestId;
        }
        catch (COMException ex)
        {
            throw new ModemException($"Registration request failed (0x{ex.HResult:X8}).", ex);
        }
        finally { Marshal.ReleaseComObject(reg); }
    }

    /// <summary>Re-registers and waits until the modem reports Home/Roaming or the timeout passes.</summary>
    /// <remarks>
    /// Observed 2026-11-21: a request sent while the modem reports "Deregistered" right after a failed USSD is
    /// ignored, but the same request sent once it is back in "Searching" registers within ~2 s. So the request is
    /// repeated every <paramref name="retryEvery"/> until registered.
    /// </remarks>
    public static async Task<bool> RecoverAsync(TimeSpan timeout, CancellationToken ct = default, TimeSpan? retryEvery = null)
    {
        var interval = retryEvery ?? TimeSpan.FromSeconds(15);
        var until = DateTime.UtcNow + timeout;
        var nextRequest = DateTime.MinValue;
        while (DateTime.UtcNow < until)
        {
            if (DateTime.UtcNow >= nextRequest)
            {
                await Task.Run(RegisterAutomatic, ct);
                nextRequest = DateTime.UtcNow + interval;
            }
            await Task.Delay(1500, ct);
            var s = await ModemStatusReader.ReadAsync(ct);
            if (s.IsRegistered) return true;
        }
        return false;
    }

    private static IMbnRegistration? GetRegistration()
    {
        var mgr = (IMbnInterfaceManager)new MbnInterfaceManagerClass();
        try
        {
            mgr.GetInterfaces(out var arr);
            if (arr is not { Length: > 0 }) return null;
            var first = arr.GetValue(0)!;
            for (int i = 1; i < arr.Length; i++) Marshal.ReleaseComObject(arr.GetValue(i)!);
            return (IMbnRegistration)first; // QueryInterface on IMbnInterface
        }
        catch (COMException) { return null; }
        finally { Marshal.ReleaseComObject(mgr); }
    }

    [ComImport, Guid("BDFEE05B-4418-11DD-90ED-001C257CCFF1")]
    private class MbnInterfaceManagerClass;

    [ComImport, Guid("DCBBBAB6-201B-4BBB-AAEE-338E368AF6FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMbnInterfaceManager
    {
        void GetInterface([MarshalAs(UnmanagedType.LPWStr)] string interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object mbnInterface);
        void GetInterfaces([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)] out Array mbnInterfaces);
    }

    [ComImport, Guid("DCBBBAB6-2009-4BBB-AAEE-338E368AF6FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMbnRegistration
    {
        void GetRegisterState(out int registerState);
        void GetRegisterMode(out int registerMode);
        void GetProviderID([MarshalAs(UnmanagedType.BStr)] out string providerId);
        void GetProviderName([MarshalAs(UnmanagedType.BStr)] out string providerName);
        void GetRoamingText([MarshalAs(UnmanagedType.BStr)] out string roamingText);
        void GetAvailableDataClasses(out uint availableDataClasses);
        void GetCurrentDataClass(out uint currentDataClass);
        void GetRegistrationNetworkError(out uint registrationNetworkError);
        void GetPacketAttachNetworkError(out uint packetAttachNetworkError);
        void SetRegisterMode(int regMode, [MarshalAs(UnmanagedType.LPWStr)] string? providerId, uint dataClass, out uint requestId);
    }
}
