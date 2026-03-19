using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomat.FXCS.Bindings;

#region Enums
[Flags]
public enum D3DCompileFlags : uint
{
    None = 0,
    Debug = 1 << 0,
    SkipValidation = 1 << 1,
    SkipOptimization = 1 << 2,
    PackMatrixRowMajor = 1 << 3,
    PackMatrixColumnMajor = 1 << 4,
    PartialPrecision = 1 << 5,
    ForceVsSoftwareNoOpt = 1 << 6,
    ForcePsSoftwareNoOpt = 1 << 7,
    NoPreshader = 1 << 8,
    AvoidFlowControl = 1 << 9,
    PreferFlowControl = 1 << 10,
    EnableStrictness = 1 << 11,
    EnableBackwardsCompatibility = 1 << 12,
    IeeeStrictness = 1 << 13,
    OptimizationLevel0 = 1 << 14,
    OptimizationLevel1 = 0, // default
    OptimizationLevel2 = (1 << 14) | (1 << 15),
    OptimizationLevel3 = 1 << 15,
    WarningsAreErrors = 1 << 18,
    ResourcesMayAlias = 1 << 19,
    EnableUnboundedDescriptorTables = 1 << 20,
    AllResourcesBound = 1 << 21,

    // ??
    DebugNameForSource = 1 << 22,
    DebugNameForBinary = 1 << 23,

    // TODO: secdata?
}

[Flags]
public enum D3DEffectFlags : uint
{
    None = 0,
    ChildEffect = 1 << 0,  // D3DCOMPILE_EFFECT_CHILD_EFFECT
    AllowSlowOps = 1 << 1, // D3DCOMPILE_EFFECT_ALLOW_SLOW_OPS (/Gdp)
}

[Flags]
public enum D3DStripFlags : uint
{
    None = 0,
    ReflectionData = 0x01,
    DebugInfo = 0x02,
    TestBlobs = 0x04,
    PrivateData = 0x08,
    RootSignature = 0x10,
}

[Flags]
public enum D3DDisassembleFlags : uint
{
    None = 0,
    EnableColorCode = 0x01,
    EnableDefaultValuePrints = 0x02,
    EnableInstructionNumbering = 0x04,
    EnableInstructionCycle = 0x08,
    DisableDebugInfo = 0x10,
    EnableInstructionOffset = 0x20,
    InstructionOnly = 0x40,
    PrintHexLiterals = 0x80,
}

public enum D3DBlobPart
{
    InputSignatureBlob = 0,
    OutputSignatureBlob = 1,
    InputAndOutputSignatureBlob = 2,
    PatchConstantSignatureBlob = 3,
    AllSignatureBlob = 4,
    DebugInfo = 5,
    LegacyShader = 6,
    XnaPrepassShader = 7,
    XnaShader = 8,
    Pdb = 9,
    PrivateData = 10,
    RootSignature = 11,
    DebugName = 12,

    // Test parts are only produced by special compiler versions and so are
    // usually not present in shaders.
    TestAlternateShader = 0x8000,
    TestCompileDetails,
    TestCompilePerf,
    TestCompileReport,
}
#endregion

#region COM interface
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ID3DBlobVtbl
{
    // IUnknown
    public delegate* unmanaged[Stdcall]<ID3DBlob*, Guid*, void**, int> QueryInterface;

    public delegate* unmanaged[Stdcall]<ID3DBlob*, uint> AddRef;

    public delegate* unmanaged[Stdcall]<ID3DBlob*, uint> Release;

    // ID3DBlob
    public delegate* unmanaged[Stdcall]<ID3DBlob*, void*> GetBufferPointer;

    public delegate* unmanaged[Stdcall]<ID3DBlob*, nuint> GetBufferSize;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct ID3DBlob
{
    public ID3DBlobVtbl* lpVtbl;

    public void* GetBufferPointer()
    {
        return lpVtbl->GetBufferPointer((ID3DBlob*)Unsafe.AsPointer(ref this));
    }

    public nuint GetBufferSize()
    {
        return lpVtbl->GetBufferSize((ID3DBlob*)Unsafe.AsPointer(ref this));
    }

    public uint Release()
    {
        return lpVtbl->Release((ID3DBlob*)Unsafe.AsPointer(ref this));
    }

    /// <summary>
    ///     Copies blob contents into a managed byte array.
    /// </summary>
    public byte[] ToArray()
    {
        var sz = GetBufferSize();
        if (sz == 0)
        {
            return [];
        }

        var arr = new byte[(int)sz];
        new ReadOnlySpan<byte>(GetBufferPointer(), (int)sz).CopyTo(arr);
        return arr;
    }

    /// <summary>
    ///     Returns blob contents as a managed string (null-terminated UTF-8).
    /// </summary>
    public string ToUtf8String()
    {
        var sz = GetBufferSize();
        if (sz == 0)
        {
            return string.Empty;
        }

        // Drop trailing null terminator if present
        var len = (int)sz;
        var ptr = (byte*)GetBufferPointer();
        if (len > 0 && ptr[len - 1] == 0)
        {
            len--;
        }

        return System.Text.Encoding.UTF8.GetString(ptr, len);
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct D3DShaderMacro
{
    public Lpcstr Name;
    public Lpcstr Definition;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct D3DShaderData
{
    public void* BytecodePtr;
    public nuint BytecodeLength;

    public Span<byte> Bytecode
    {
        get { return new(BytecodePtr, (int)BytecodeLength); }
    }
}
#endregion

public static unsafe class D3DCompiler
{
    // TODO: Once we start patching, we'll probably only tolerate a very
    //       specific binary we'll distribute.
    private static readonly string[] candidate_dlls =
    [
        "d3dcompiler_47.dll",
        "d3dcompiler_46.dll",
        "d3dcompiler_43.dll",
        "d3dcompiler.dll",
    ];

    // The name of whichever DLL was actually loaded, for diagnostics.
    public static string? LoadedDllName { get; private set; }

    public static string? LoadedDllPath { get; private set; }

    public static delegate* unmanaged[Stdcall]<
        void*, nuint,           // pSrcData, SrcDataSize
        byte*,                  // pSourceName
        D3DShaderMacro*, void*, // pDefines, pInclude
        byte*, byte*,           // pEntrypoint, pTarget
        uint, uint, uint,       // Flags1, Flags2, SecondaryDataFlags
        void*, nuint,           // pSecondaryData, SecondaryDataSize
        ID3DBlob**, ID3DBlob**, // ppCode, ppErrorMsgs
        int                     // HRESULT
        > Compile2 { get; private set; }

    public static delegate* unmanaged[Stdcall]<
        void*, nuint,
        byte*,
        D3DShaderMacro*, void*,
        ID3DBlob**, ID3DBlob**,
        int
        > Preprocess { get; private set; }

    public static delegate* unmanaged[Stdcall]<
        void*, nuint, uint, byte*, ID3DBlob**, int
        > Disassemble { get; private set; }

    public static delegate* unmanaged[Stdcall]<
        void*, nuint, uint, ID3DBlob**, int>
        StripShader { get; private set; }

    public static delegate* unmanaged[Stdcall]<
        void*, nuint, D3DBlobPart, uint, ID3DBlob**, int>
        GetBlobPart { get; private set; }

    public static delegate* unmanaged[Stdcall]<
        void*, nuint, D3DBlobPart, uint, void*, nuint, ID3DBlob**, int>
        SetBlobPart { get; private set; }

    public static delegate* unmanaged[Stdcall]<char*, ID3DBlob**, int>
        ReadFileToBlob { get; private set; }

    public static delegate* unmanaged[Stdcall]<ID3DBlob*, char*, int, int>
        WriteBlobToFile { get; private set; }

    public static delegate* unmanaged[Stdcall]<uint, D3DShaderData*, uint, ID3DBlob**, int>
        CompressShaders { get; private set; }

    public static delegate* unmanaged[Stdcall]<
        void*, nuint,
        uint, uint, uint*, uint,
        ID3DBlob**, uint*,
        int>
        DecompressShaders { get; private set; }

    public static delegate* unmanaged[Stdcall]<nuint, ID3DBlob**, int>
        CreateBlob { get; private set; }
    
    /// <summary>
    /// Loads the first available d3dcompiler DLL and resolves all entry points.
    /// Returns false and writes to stderr if loading fails.
    /// </summary>
    public static bool Load()
    {
        var handle = nint.Zero;
        foreach (var candidate in candidate_dlls)
        {
            if (!NativeLibrary.TryLoad(candidate, out handle))
            {
                continue;
            }

            LoadedDllName = candidate;
            break;
        }

        var moduleCollection = Process.GetCurrentProcess().Modules;
        var modules = new ProcessModule[moduleCollection.Count];
        {
            moduleCollection.CopyTo(modules, 0);
        }

        LoadedDllPath = modules.FirstOrDefault(x => x.ModuleName.Equals(LoadedDllName, StringComparison.OrdinalIgnoreCase))?.FileName ?? LoadedDllName;

        if (handle == 0)
        {
            Console.Error.WriteLine(
                "fxc: error: could not load d3dcompiler_47.dll (or any fallback).\n" +
                "  On Wine: place d3dcompiler_47.dll in the prefix or use winetricks d3dcompiler_47."
            );
            return false;
        }

        // Console.Error.WriteLine($"fxc: using {LoadedDllName}");

        // | instead of || to take note of all missing exports instead of just
        // the first failure.
        if (!TryResolve(handle, "D3DCompile2", out var compile2)
          | !TryResolve(handle, "D3DPreprocess", out var preprocess)
          | !TryResolve(handle, "D3DDisassemble", out var disassemble)
          | !TryResolve(handle, "D3DStripShader", out var stripShader)
          | !TryResolve(handle, "D3DGetBlobPart", out var getBlobPart)
          | !TryResolve(handle, "D3DSetBlobPart", out var setBlobPart)
          | !TryResolve(handle, "D3DReadFileToBlob", out var readFileToBlob)
          | !TryResolve(handle, "D3DWriteBlobToFile", out var writeBlobToFile)
          | !TryResolve(handle, "D3DCompressShaders", out var compressShaders)
          | !TryResolve(handle, "D3DDecompressShaders", out var decompressShaders)
          | !TryResolve(handle, "D3DCreateBlob", out var createBlob))
        {
            return false;
        }

        Compile2 = (delegate* unmanaged[Stdcall]<void*, nuint, byte*, D3DShaderMacro*, void*, byte*, byte*, uint, uint, uint, void*, nuint, ID3DBlob**, ID3DBlob**, int>)compile2;
        Preprocess = (delegate* unmanaged[Stdcall]<void*, nuint, byte*, D3DShaderMacro*, void*, ID3DBlob**, ID3DBlob**, int>)preprocess;
        Disassemble = (delegate* unmanaged[Stdcall]<void*, nuint, uint, byte*, ID3DBlob**, int>)disassemble;
        StripShader = (delegate* unmanaged[Stdcall]<void*, nuint, uint, ID3DBlob**, int>)stripShader;
        GetBlobPart = (delegate* unmanaged[Stdcall]<void*, nuint, D3DBlobPart, uint, ID3DBlob**, int>)getBlobPart;
        SetBlobPart = (delegate* unmanaged[Stdcall]<void*, nuint, D3DBlobPart, uint, void*, nuint, ID3DBlob**, int>)setBlobPart;
        ReadFileToBlob = (delegate* unmanaged[Stdcall]<char*, ID3DBlob**, int>)readFileToBlob;
        WriteBlobToFile = (delegate* unmanaged[Stdcall]<ID3DBlob*, char*, int, int>)writeBlobToFile;
        CompressShaders = (delegate* unmanaged[Stdcall]<uint, D3DShaderData*, uint, ID3DBlob**, int>)compressShaders;
        DecompressShaders = (delegate* unmanaged[Stdcall]<void*, nuint, uint, uint, uint*, uint, ID3DBlob**, uint*, int>)decompressShaders;
        CreateBlob = (delegate* unmanaged[Stdcall]<nuint, ID3DBlob**, int>)createBlob;
        return true;
    }

    private static bool TryResolve(nint lib, string name, out nint ptr)
    {
        if (NativeLibrary.TryGetExport(lib, name, out ptr))
        {
            return true;
        }

        Console.Error.WriteLine($"fxc: missing export: {name}");
        ptr = nint.Zero;
        return false;
    }
}
