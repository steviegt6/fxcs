using System;
using System.Runtime.InteropServices;

namespace Tomat.FXCS.Bindings;

[StructLayout(LayoutKind.Sequential)]
public struct Lpcstr : IDisposable
{
    public readonly bool IsNull => data == nint.Zero;

    internal readonly string? DebugDisplayString => IsNull ? null : Marshal.PtrToStringAnsi(data);

    private nint data;

    public Lpcstr(nint data)
    {
        this.data = data;
    }

    public Lpcstr(string? str)
    {
        data = str is null ? 0 : Marshal.StringToHGlobalAnsi(str);
    }

    public void Dispose()
    {
        if (IsNull)
        {
            return;
        }

        Marshal.FreeHGlobal(data);
        data = nint.Zero;
    }

    public readonly override string ToString()
    {
        if (IsNull)
        {
            return "";
        }

        return Marshal.PtrToStringAnsi(data) ?? "";
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct Lpcwstr : IDisposable
{
    public readonly bool IsNull => data == nint.Zero;

    internal readonly string? DebugDisplayString => IsNull ? null : Marshal.PtrToStringUni(data);

    private nint data;

    public Lpcwstr(nint data)
    {
        this.data = data;
    }

    public Lpcwstr(string? str)
    {
        data = str is null ? 0 : Marshal.StringToHGlobalUni(str);
    }

    public void Dispose()
    {
        if (IsNull)
        {
            return;
        }

        Marshal.FreeHGlobal(data);
        data = nint.Zero;
    }

    public readonly override string ToString()
    {
        if (IsNull)
        {
            return "";
        }

        return Marshal.PtrToStringUni(data) ?? "";
    }
}
