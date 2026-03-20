using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomat.FXCS;

public enum D3DIncludeType : uint
{
    Local = 0,  // #include "file"
    System = 1, // #include <file>
}

public sealed unsafe class IncludeHandler : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ID3DIncludeVtbl
    {
        public delegate* unmanaged[Stdcall]<
            NativeInstance*,
            D3DIncludeType, // IncludeType
            byte*,          // pFileName
            void*,          // pParentData
            void**,         // ppData
            uint*,          // pBytes
            int>            // HRESULT
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

    // Pinned buffers handed to d3dcompiler, keyed by pointer so Close() can
    // release the GCHandle.
    private readonly Dictionary<nint, nint> openBuffers = [];

    // Explicit paths added via /I, searched for both local and system includes
    // after the directory stack is exhausted.
    private readonly List<string> systemPaths = [];

    // Stack of directories of currently-open include files.
    // Bottom = directory of the top-level source file (set by the caller).
    // Each successful Open() pushes; each Close() pops.
    private readonly Stack<string> dirStack = new();

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

    /// <summary>
    ///     Sets the directory of the top-level source file.
    ///     <br />
    ///     Call this once before each compilation that uses this handler.
    /// </summary>
    public void SetSourceFileDirectory(string directory)
    {
        dirStack.Clear();
        if (!string.IsNullOrEmpty(directory))
        {
            dirStack.Push(directory);
        }
    }

    /// <summary>
    /// Adds a path that's searched for system includes and as a fallback for
    /// local includes not found on the directory stack.  Corresponds to /I.
    /// </summary>
    public void AddSystemPath(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            systemPaths.Add(path);
        }
    }

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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int NativeOpen(
        NativeInstance* self,
        D3DIncludeType includeType,
        byte* pFileNameUtf8,
        void* pParentData,
        void** ppData,
        uint* pBytes
    )
    {
        var handler = (IncludeHandler)GCHandle.FromIntPtr(self->gcHandleCookie).Target!;
        return handler.Open(includeType, pFileNameUtf8, ppData, pBytes);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int NativeClose(NativeInstance* self, void* pData)
    {
        var handler = (IncludeHandler)GCHandle.FromIntPtr(self->gcHandleCookie).Target!;
        return handler.Close(pData);
    }

    private int Open(D3DIncludeType includeType, byte* pFileName, void** ppData, uint* pBytes)
    {
        const int s_ok = 0;
        const int error_file_not_found = 0x00000002;

        // d3dcompiler passes an ANSI (CP_ACP) string, not UTF-8.
        var fileName = Marshal.PtrToStringAnsi((nint)pFileName) ?? string.Empty;

        // Normalize separators so paths from Wine/Unix environments resolve.
        if (fileName.Contains('/'))
        {
            fileName = fileName.Replace('/', '\\');
        }

        string? foundDir;
        if (includeType == D3DIncludeType.Local)
        {
            // Walk the directory stack from the most-recently opened file
            // outward to the top-level source directory.
            foreach (var dir in dirStack)
            {
                if (TryReadFile(dir, fileName, ppData, pBytes, out foundDir))
                {
                    goto found;
                }
            }
        }

        // For system includes, or local includes not found on the stack,
        // try each explicitly added search path.
        foreach (var path in systemPaths)
        {
            if (TryReadFile(path, fileName, ppData, pBytes, out foundDir))
            {
                goto found;
            }
        }

        return error_file_not_found;

    found:
        // Push the resolved file's directory so its own nested includes
        // resolve relative to it.
        dirStack.Push(foundDir!);
        return s_ok;
    }

    private int Close(void* pData)
    {
        const int s_ok = 0;
        if (openBuffers.Remove((nint)pData, out var cookie))
        {
            GCHandle.FromIntPtr(cookie).Free();
        }

        // Pop the directory we pushed when this file was opened.
        if (dirStack.Count > 0)
        {
            dirStack.Pop();
        }

        return s_ok;
    }

    /// <summary>
    ///     Tries to find and read <paramref name="fileName"/> under
    ///     <paramref name="directory"/>.  On success, pins the buffer, writes
    ///     ppData/pBytes, records the pin handle, and returns the file's
    ///     containing directory via <paramref name="fileDirectory"/>.
    /// </summary>
    private bool TryReadFile(
        string directory,
        string fileName,
        void** ppData,
        uint* pBytes,
        out string? fileDirectory
    )
    {
        fileDirectory = null;
        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));

        if (!File.Exists(fullPath))
        {
            return false;
        }

        try
        {
            var data = Compiler.ModifyIncomingFileData(File.ReadAllText(fullPath));

            var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            var ptr = (void*)pin.AddrOfPinnedObject();

            openBuffers[(nint)ptr] = GCHandle.ToIntPtr(pin);
            *ppData = ptr;
            *pBytes = (uint)data.Length;

            fileDirectory = Path.GetDirectoryName(fullPath) ?? directory;
            return true;
        }
        catch (Exception)
        {
            // IO errors, permissions problems, etc. — try the next path.
            return false;
        }
    }
}
