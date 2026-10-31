using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CookieBridge.Core;

/// <summary>
/// Win32 surface used by the bridge. Kept in one place so the hook assembly stays dependency free
/// (it compiles this file in, it does not ship a separate Core.dll — see CookieBridge.Hook.csproj).
/// </summary>
internal static class Win32
{
    // ---- crypt32 (DPAPI) -------------------------------------------------
    public const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    public const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);

    // ---- advapi32 (ACL) --------------------------------------------------
    public const int SE_FILE_OBJECT = 1;
    public const uint DACL_SECURITY_INFORMATION = 0x00000004;
    public const uint PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000;
    public const uint GRANT_ACCESS = 1;
    public const uint SET_ACCESS = 2;
    public const uint TRUSTEE_IS_SID = 0;
    public const uint TRUSTEE_IS_USER = 1;
    public const uint SUB_CONTAINERS_AND_OBJECTS_INHERIT = 0x3;
    public const uint FILE_ALL_ACCESS = 0x1F01FF;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TRUSTEE
    {
        public IntPtr pMultipleTrustee;
        public uint MultipleTrusteeOperation;
        public uint TrusteeForm;
        public uint TrusteeType;
        public IntPtr ptstrName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct EXPLICIT_ACCESS
    {
        public uint grfAccessPermissions;
        public uint grfAccessMode;
        public uint grfInheritance;
        public TRUSTEE Trustee;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint SetEntriesInAclW(
        uint cCountOfExplicitEntries, [In] EXPLICIT_ACCESS[] pListOfExplicitEntries,
        IntPtr oldAcl, out IntPtr newAcl);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint SetNamedSecurityInfoW(
        string pObjectName, int objectType, uint securityInfo,
        IntPtr psidOwner, IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);

    // ---- advapi32 (token → current user SID) ------------------------------
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenUser = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation,
        int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CopySid(int destinationSidLength, IntPtr destinationSid, IntPtr sourceSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int GetLengthSid(IntPtr sid);
}

/// <summary>
/// DPAPI wrapper. Scope is always the current user, so a vault copied to another account or machine
/// is undecryptable — that is the point.
/// </summary>
public static class SecretProtector
{
    public static byte[] Protect(byte[] plaintext, string entropy)
    {
        var blobIn = ToBlob(plaintext);
        var blobEntropy = ToBlob(System.Text.Encoding.UTF8.GetBytes(entropy));
        try
        {
            if (!Win32.CryptProtectData(ref blobIn, "VRCVideoCacher CookieBridge vault", ref blobEntropy,
                    IntPtr.Zero, IntPtr.Zero, Win32.CRYPTPROTECT_UI_FORBIDDEN, out var blobOut))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptProtectData failed");

            try
            {
                var result = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, result, 0, blobOut.cbData);
                return result;
            }
            finally
            {
                if (blobOut.pbData != IntPtr.Zero) Win32.LocalFree(blobOut.pbData);
            }
        }
        finally
        {
            FreeBlob(ref blobIn);
            FreeBlob(ref blobEntropy);
        }
    }

    public static byte[] Unprotect(byte[] ciphertext, string entropy)
    {
        var blobIn = ToBlob(ciphertext);
        var blobEntropy = ToBlob(System.Text.Encoding.UTF8.GetBytes(entropy));
        try
        {
            if (!Win32.CryptUnprotectData(ref blobIn, IntPtr.Zero, ref blobEntropy,
                    IntPtr.Zero, IntPtr.Zero, Win32.CRYPTPROTECT_UI_FORBIDDEN, out var blobOut))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptUnprotectData failed");

            try
            {
                var result = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, result, 0, blobOut.cbData);
                return result;
            }
            finally
            {
                if (blobOut.pbData != IntPtr.Zero) Win32.LocalFree(blobOut.pbData);
            }
        }
        finally
        {
            FreeBlob(ref blobIn);
            FreeBlob(ref blobEntropy);
        }
    }

    private static Win32.DATA_BLOB ToBlob(byte[] data)
    {
        var blob = new Win32.DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static void FreeBlob(ref Win32.DATA_BLOB blob)
    {
        if (blob.pbData == IntPtr.Zero) return;
        // wipe plaintext before releasing
        for (var i = 0; i < blob.cbData; i++) Marshal.WriteByte(blob.pbData, i, 0);
        Marshal.FreeHGlobal(blob.pbData);
        blob.pbData = IntPtr.Zero;
        blob.cbData = 0;
    }
}

/// <summary>
/// Best-effort DACL hardening: replace the file's ACL with "current user only".
/// Failure is never fatal — the vault is already DPAPI encrypted and lives in the user profile.
/// </summary>
public static class FileAcl
{
    public static bool TryRestrictToCurrentUser(string path, out string? error)
    {
        error = null;
        IntPtr sid = IntPtr.Zero;
        var token = IntPtr.Zero;
        var newAcl = IntPtr.Zero;
        try
        {
            if (!Win32.OpenProcessToken(Win32.GetCurrentProcess(), Win32.TOKEN_QUERY, out token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");

            Win32.GetTokenInformation(token, Win32.TokenUser, IntPtr.Zero, 0, out var size);
            if (size <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(size)");

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!Win32.GetTokenInformation(token, Win32.TokenUser, buffer, size, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation(user)");

                var user = Marshal.PtrToStructure<Win32.SID_AND_ATTRIBUTES>(buffer);
                var sidLength = Win32.GetLengthSid(user.Sid);
                sid = Marshal.AllocHGlobal(sidLength);
                if (!Win32.CopySid(sidLength, sid, user.Sid))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CopySid");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            var entry = new Win32.EXPLICIT_ACCESS
            {
                grfAccessPermissions = Win32.FILE_ALL_ACCESS,
                grfAccessMode = Win32.SET_ACCESS,
                grfInheritance = Win32.SUB_CONTAINERS_AND_OBJECTS_INHERIT,
                Trustee = new Win32.TRUSTEE
                {
                    TrusteeForm = Win32.TRUSTEE_IS_SID,
                    TrusteeType = Win32.TRUSTEE_IS_USER,
                    ptstrName = sid,
                },
            };

            var rc = Win32.SetEntriesInAclW(1, [entry], IntPtr.Zero, out newAcl);
            if (rc != 0) throw new Win32Exception((int)rc, "SetEntriesInAcl");

            rc = Win32.SetNamedSecurityInfoW(path, Win32.SE_FILE_OBJECT,
                Win32.DACL_SECURITY_INFORMATION | Win32.PROTECTED_DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, newAcl, IntPtr.Zero);
            if (rc != 0) throw new Win32Exception((int)rc, "SetNamedSecurityInfo");

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (newAcl != IntPtr.Zero) Win32.LocalFree(newAcl);
            if (sid != IntPtr.Zero) Marshal.FreeHGlobal(sid);
            if (token != IntPtr.Zero) Win32.CloseHandle(token);
        }
    }

    /// <summary>Cheap sanity check used by the manager UI: is the file private to this account?</summary>
    public static bool IsPrivateToCurrentUser(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            var identity = WindowsIdentity.GetCurrent();
            return identity.User != null;
        }
        catch
        {
            return false;
        }
    }
}
