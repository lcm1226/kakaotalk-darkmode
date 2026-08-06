using System.Runtime.InteropServices;
using System.Text;

namespace KakaoTalkGpuInvertSpike;

internal static class ShaderCompiler
{
    public static byte[] Compile(string source, string entryPoint, string target)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var result = D3DCompile(
            sourceBytes,
            (nuint)sourceBytes.Length,
            "GpuInvert.hlsl",
            nint.Zero,
            nint.Zero,
            entryPoint,
            target,
            0,
            0,
            out var code,
            out var errors);

        try
        {
            if (result < 0)
            {
                var message = errors == nint.Zero
                    ? $"Shader compilation failed with HRESULT 0x{result:X8}."
                    : Encoding.UTF8.GetString(ReadBlob(errors)).TrimEnd('\0', '\r', '\n');
                Marshal.ThrowExceptionForHR(result, new nint(-1));
                throw new InvalidOperationException(message);
            }

            return ReadBlob(code);
        }
        catch (COMException exception) when (errors != nint.Zero)
        {
            var message = Encoding.UTF8.GetString(ReadBlob(errors)).TrimEnd('\0', '\r', '\n');
            throw new InvalidOperationException(message, exception);
        }
        finally
        {
            if (errors != nint.Zero)
            {
                _ = Marshal.Release(errors);
            }

            if (code != nint.Zero)
            {
                _ = Marshal.Release(code);
            }
        }
    }

    private static unsafe byte[] ReadBlob(nint blob)
    {
        var vtable = *(nint**)blob;
        var getBufferPointer = (delegate* unmanaged[Stdcall]<nint, nint>)vtable[3];
        var getBufferSize = (delegate* unmanaged[Stdcall]<nint, nuint>)vtable[4];
        var source = getBufferPointer(blob);
        var size = checked((int)getBufferSize(blob));
        var bytes = new byte[size];
        Marshal.Copy(source, bytes, 0, size);
        return bytes;
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        [In] byte[] sourceData,
        nuint sourceDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string sourceName,
        nint defines,
        nint include,
        [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
        [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1,
        uint flags2,
        out nint code,
        out nint errorMessages);
}
