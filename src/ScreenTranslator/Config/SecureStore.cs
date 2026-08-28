using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ScreenTranslator.Infrastructure;

namespace ScreenTranslator.Config;

/// <summary>
/// Encrypts secrets (API keys) with Windows DPAPI, scoped to the current user account.
/// The ciphertext is what lands in config.json — the plaintext key never touches disk.
/// A copied config.json is useless on another machine or under another user, which is
/// the intended trade-off.
///
/// P/Invokes crypt32 directly rather than pulling in the System.Security.Cryptography
/// .ProtectedData package, so the app has zero NuGet dependencies.
/// </summary>
public static class SecureStore
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const string Description = "ScreenTranslator API key";

    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        try
        {
            var cipher = Crypt(Encoding.UTF8.GetBytes(plaintext), protect: true);
            return Convert.ToBase64String(cipher);
        }
        catch (Exception ex)
        {
            Log.Error("加密 API key 失败", ex);
            return "";
        }
    }

    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try
        {
            var plain = Crypt(Convert.FromBase64String(protectedBase64), protect: false);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            // Typical cause: config.json was copied from another machine/user account.
            Log.Warn($"解密 API key 失败（通常是配置从别的电脑或账户复制过来的）: {ex.Message}");
            return "";
        }
    }

    private static byte[] Crypt(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        var pin = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = input.Length;
            inBlob.pbData = pin.AddrOfPinnedObject();

            var ok = protect
                ? CryptProtectData(ref inBlob, Description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out outBlob);

            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (pin.IsAllocated) pin.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
