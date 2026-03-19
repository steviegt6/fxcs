using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomat.FXCS;

public sealed unsafe class IncludeHandler : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ID3DIncludeVtbl
    {
        public delegate* unmanaged[Stdcall]<
            NativeInstance*,
            uint,   // IncludeType
            byte*,  // pFileName
            void*,  // pParentData
            void**, // ppData
            uint*,  // pBytes
            int>    // HRESULT
            Open;

        public delegate* unmanaged[Stdcall]<
            NativeInstance*,
            void*, // pData
            int>   // HRESULT
            Close;
    }

    // The struct we hand to d3dcompiler.  Its first field must be a pointer
    // to a vtable, matching the C++ object layout.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInstance
    {
        public ID3DIncludeVtbl* pVtbl;

        // We stash a GCHandle cookie here so the unmanaged callbacks can
        // recover the managed IncludeHandler instance.
        public nint gcHandleCookie;
    }

    private static readonly ID3DIncludeVtbl* vtbl;

    // Tracks buffers handed out to d3dcompiler that have not yet been Close()d.
    private readonly Dictionary<nint, nint> openBuffers = []; // ppData value -> GCHandle

    private readonly List<string> searchPaths = [];
    private bool disposed;

    private NativeInstance* nativeInstance;
    private GCHandle selfHandle;

    static IncludeHandler()
    {
        // Allocate the vtable in unmanaged memory so it stays pinned forever.
        vtbl = (ID3DIncludeVtbl*)NativeMemory.AllocZeroed((nuint)sizeof(ID3DIncludeVtbl));
        vtbl->Open = &NativeOpen;
        vtbl->Close = &NativeClose;
    }

    public IncludeHandler()
    {
        nativeInstance = (NativeInstance*)NativeMemory.AllocZeroed((nuint)sizeof(NativeInstance));
        selfHandle = GCHandle.Alloc(this);
        nativeInstance->pVtbl = vtbl;
        nativeInstance->gcHandleCookie = GCHandle.ToIntPtr(selfHandle);
    }

    /// <summary>
    ///     The raw pointer passed to D3DCompile2 / D3DPreprocess as pInclude.
    /// </summary>
    public void* NativePtr => nativeInstance;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        // Free any buffers that were never Close()d (shouldn't happen in normal
        // use).
        foreach (var cookie in openBuffers.Values)
        {
            GCHandle.FromIntPtr(cookie).Free();
        }

        openBuffers.Clear();

        if (selfHandle.IsAllocated)
        {
            selfHandle.Free();
        }

        if (nativeInstance != null)
        {
            NativeMemory.Free(nativeInstance);
            nativeInstance = null;
        }
    }

    public void AddSearchPath(string path)
    {
        searchPaths.Add(path);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int NativeOpen(
        NativeInstance* self,
        uint includeType,
        byte* pFileNameUtf8,
        void* pParentData,
        void** ppData,
        uint* pBytes
    )
    {
        var handler = (IncludeHandler)GCHandle.FromIntPtr(self->gcHandleCookie).Target!;
        return handler.Open(pFileNameUtf8, ppData, pBytes);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int NativeClose(NativeInstance* self, void* pData)
    {
        var handler = (IncludeHandler)GCHandle.FromIntPtr(self->gcHandleCookie).Target!;
        return handler.Close(pData);
    }

    private int Open(byte* pFileNameUtf8, void** ppData, uint* pBytes)
    {
        const int s_ok = 0;
        const int e_fail = unchecked((int)0x80004005);

        var fileName = Marshal.PtrToStringUTF8((nint)pFileNameUtf8) ?? string.Empty;

        foreach (var dir in searchPaths)
        {
            var fullPath = string.IsNullOrEmpty(dir)
                ? fileName
                : Path.Combine(dir, fileName);

            if (!File.Exists(fullPath))
            {
                continue;
            }

            try
            {
                var data = File.ReadAllBytes(fullPath);
                // Pin the array and record the handle so Close() can free it.
                var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                var ptr = (void*)pin.AddrOfPinnedObject();
                openBuffers[(nint)ptr] = GCHandle.ToIntPtr(pin);
                *ppData = ptr;
                *pBytes = (uint)data.Length;
                return s_ok;
            }
            catch
            {
                // Try the next path.
            }
        }

        return e_fail;
    }

    private int Close(void* pData)
    {
        const int s_ok = 0;
        if (openBuffers.Remove((nint)pData, out var cookie))
        {
            GCHandle.FromIntPtr(cookie).Free();
        }

        return s_ok;
    }
}
