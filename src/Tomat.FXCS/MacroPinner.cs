using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Tomat.FXCS.Bindings;

namespace Tomat.FXCS;

/// <summary>
///     Converts a managed macro list into a null-termined array of
///     <see cref="D3DShaderMacro" /> objects in unmanaged memory with pinned
///     strings, suitable for passing to d3dcompiler.
/// </summary>
internal sealed unsafe class MacroPinner : IDisposable
{
    private readonly nint block; // D3DShaderMacro[]
    private readonly GCHandle[] pins;

    public MacroPinner(List<(string Name, string Value)> macros)
    {
        var n = macros.Count;
        // n entries + 1 sentinel {null, null}
        block = (nint)NativeMemory.AllocZeroed((nuint)((n + 1) * sizeof(D3DShaderMacro)));
        pins = new GCHandle[n * 2];

        var ptr = Ptr;
        for (var i = 0; i < n; i++)
        {
            var nameBytes = Compiler.Utf8Null(macros[i].Name);
            var valueBytes = Compiler.Utf8Null(macros[i].Value);

            pins[i * 2] = GCHandle.Alloc(nameBytes, GCHandleType.Pinned);
            pins[i * 2 + 1] = GCHandle.Alloc(valueBytes, GCHandleType.Pinned);

            ptr[i].Name = (byte*)pins[i * 2].AddrOfPinnedObject();
            ptr[i].Definition = (byte*)pins[i * 2 + 1].AddrOfPinnedObject();
        }

        // Sentinel is already zero'd by AllocZeroed.
    }

    public D3DShaderMacro* Ptr => (D3DShaderMacro*)block;

    public void Dispose()
    {
        foreach (var pin in pins)
        {
            if (pin.IsAllocated)
            {
                pin.Free();
            }
        }

        NativeMemory.Free((void*)block);
    }
}
