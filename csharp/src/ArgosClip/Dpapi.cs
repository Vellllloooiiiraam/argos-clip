using System.Runtime.InteropServices;
using System.Text;

namespace ArgosClip;

/// <summary>
/// Criptografia local via DPAPI do Windows (CryptProtectData/CryptUnprotectData).
/// A chave fica amarrada ao usuário da máquina: nenhum outro usuário/máquina
/// consegue ler o arquivo de config. Sem dependências externas (P/Invoke).
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CryptprotectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Devolve o texto cifrado em Base64 (ou null se vazio/erro).</summary>
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var input = Encoding.UTF8.GetBytes(plain);
        var inPtr = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inPtr, input.Length);
            var inBlob = new DataBlob { cbData = input.Length, pbData = inPtr };
            if (!CryptProtectData(ref inBlob, "ArgosClip", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptprotectUiForbidden, out var outBlob))
            {
                return null;
            }
            try
            {
                var outBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                return Convert.ToBase64String(outBytes);
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
        }
    }

    /// <summary>Decifra o Base64 produzido por <see cref="Protect"/> (ou null).</summary>
    public static string? Unprotect(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        byte[] input;
        try
        {
            input = Convert.FromBase64String(base64);
        }
        catch
        {
            return null;
        }

        var inPtr = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inPtr, input.Length);
            var inBlob = new DataBlob { cbData = input.Length, pbData = inPtr };
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptprotectUiForbidden, out var outBlob))
            {
                return null;
            }
            try
            {
                var outBytes = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
                return Encoding.UTF8.GetString(outBytes);
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
        }
    }
}
