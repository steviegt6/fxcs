using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Tomat.FXCS;

/// <summary>
///     High-level operations that call into d3dcompiler.
/// </summary>
public static unsafe class Compiler
{
    public static int Compress(Options opts, TextWriter errorSink)
    {
        // Read all input blobs into managed byte arrays.
        var shaders = new List<(byte[] data, string path)>(opts.InputFiles.Count);
        var totalSize = 0L;

        foreach (var f in opts.InputFiles)
        {
            var data = ReadFileOrDie(f, out _);
            if (data is null)
            {
                return 1;
            }

            shaders.Add((data, f));
            totalSize += data.Length;
        }

        // Build a D3D_SHADER_DATA[] in unmanaged memory.
        var n = shaders.Count;
        var sd = (D3DShaderData*)NativeMemory.Alloc((nuint)(n * sizeof(D3DShaderData)));

        // Pin all arrays for the duration of the call.
        var pins = new GCHandle[n];
        try
        {
            for (var i = 0; i < n; i++)
            {
                pins[i] = GCHandle.Alloc(shaders[i].data, GCHandleType.Pinned);
                sd[i].BytecodePtr = (void*)pins[i].AddrOfPinnedObject();
                sd[i].BytecodeLength = (nuint)shaders[i].data.Length;
            }

            ID3DBlob* outBlob = null;
            var hr = D3DCompiler.CompressShaders((uint)n, sd, 1, &outBlob);

            if (hr < 0)
            {
                errorSink.WriteLine("fxc: compression failed");
                return 1;
            }

            var compSize = (long)outBlob->GetBufferSize();
            var ratio = totalSize > 0 ? (double)totalSize / compSize : 1.0;
            Console.WriteLine(
                $"fxc: Compressed {n} items totalling {totalSize} bytes " +
                $"to {compSize} bytes, {ratio:F2}x compression"
            );

            var outPath = opts.OutObject ?? opts.InputFiles[0];
            var result = WriteBlobToPath(
                outBlob,
                outPath,
                errorSink,
                $"fxc: compressed output written to {outPath}"
            );
            outBlob->Release();
            return result;
        }
        finally
        {
            foreach (var pin in pins)
            {
                if (pin.IsAllocated)
                {
                    pin.Free();
                }
            }

            NativeMemory.Free(sd);
        }
    }

    public static int Decompress(Options opts, TextWriter errorSink)
    {
        var srcFile = opts.InputFiles[0];
        var src = ReadFileOrDie(srcFile, out _);
        if (src is null)
        {
            return 1;
        }

        // How many shaders are in the compressed blob?
        uint countInBlob = 0;
        fixed (byte* pSrc = src)
        {
            D3DCompiler.DecompressShaders(
                pSrc,
                (nuint)src.Length,
                0,
                0,
                null,
                0,
                null,
                &countInBlob
            );
        }

        // Remaining input file tokens are output filenames.
        var requestedCount = opts.InputFiles.Count - 1;
        var decompCount = requestedCount > 0 ? (uint)requestedCount : countInBlob;

        var outShaders = (ID3DBlob**)NativeMemory.AllocZeroed((nuint)(decompCount * sizeof(nint)));

        var totalOut = 0u;
        int hr;
        fixed (byte* pSrc = src)
        {
            hr = D3DCompiler.DecompressShaders(
                pSrc,
                (nuint)src.Length,
                decompCount,
                0,
                null,
                0,
                outShaders,
                &totalOut
            );
        }

        if (hr < 0)
        {
            errorSink.WriteLine($"fxc: unable to decompress '{srcFile}'");
            NativeMemory.Free(outShaders);
            return 1;
        }

        if (totalOut == decompCount)
        {
            Console.WriteLine($"fxc: Decompressed {totalOut} items");
        }
        else if (totalOut < decompCount)
        {
            Console.WriteLine(
                $"fxc: Decompressed all {totalOut} compressed items, " +
                $"ignored {decompCount - totalOut} extra filespecs"
            );
        }
        else
        {
            Console.WriteLine($"fxc: Decompressed first {decompCount} items out of {totalOut}");
        }

        var exitCode = 0;
        for (uint i = 0; i < totalOut; i++)
        {
            var outPath = (i + 1 < (uint)opts.InputFiles.Count)
                ? opts.InputFiles[(int)(i + 1)]
                : $"shader_{i}.cso";

            exitCode |= WriteBlobToPath(outShaders[i], outPath, errorSink, null);
            outShaders[i]->Release();
        }

        NativeMemory.Free(outShaders);
        return exitCode;
    }

    public static int Preprocess(
        string inFile,
        Options opts,
        TextWriter errorSink
    )
    {
        var src = ReadFileOrDie(inFile, out _);
        if (src is null)
        {
            return 1;
        }

        using var inc = BuildIncludeHandler(opts);
        using var mac = new MacroPinner(opts.Macros);

        inc.SetSourceFileDirectory(Path.GetDirectoryName(Path.GetFullPath(inFile))!);

        ID3DBlob* code = null;
        ID3DBlob* errors = null;

        int hr;
        fixed (byte* pSrc = src)
        {
            fixed (byte* pSrcName = Utf8Null(inFile))
            {
                hr = D3DCompiler.Preprocess(
                    pSrc,
                    (nuint)src.Length,
                    pSrcName,
                    mac.Ptr,
                    inc.NativePtr,
                    &code,
                    &errors
                );
            }
        }

        PrintErrors(errors, errorSink);
        if (errors is not null)
        {
            errors->Release();
        }

        if (hr < 0)
        {
            errorSink.WriteLine("fxc: preprocessing failed; no output produced");
            return 1;
        }

        // Write output
        var outPath = opts.OutPreprocess!;
        try
        {
            using var fs = File.Open(outPath, FileMode.Create, FileAccess.Write);
            var span = new ReadOnlySpan<byte>(code->GetBufferPointer(), (int)code->GetBufferSize());
            fs.Write(span);
        }
        catch (Exception ex)
        {
            errorSink.WriteLine($"fxc: failed writing preprocessed output to '{outPath}': {ex.Message}");
            code->Release();
            return 1;
        }
        finally
        {
            if (code is not null)
            {
                code->Release();
            }
        }

        return 0;
    }

    public static int Compile(
        string inFile,
        Options opts,
        TextWriter errorSink
    )
    {
        ID3DBlob* code = null;

        if (opts.DumpBin)
        {
            // /dumpbin: read pre-compiled blob directly, wrap in an ID3DBlob.
            var raw = ReadFileOrDie(inFile, out _);
            if (raw is null)
            {
                return 1;
            }

            var hr = D3DCompiler.CreateBlob((nuint)raw.Length, &code);
            if (hr < 0)
            {
                errorSink.WriteLine($"fxc: failed to create blob (hr=0x{hr:X8})");
                return 1;
            }

            raw.CopyTo(new Span<byte>(code->GetBufferPointer(), raw.Length));
        }
        else
        {
            // Normal compilation.
            var src = ReadFileOrDie(inFile, out _);
            if (src is null)
            {
                return 1;
            }

            using var inc = BuildIncludeHandler(opts);
            using var mac = new MacroPinner(opts.Macros);

            inc.SetSourceFileDirectory(Path.GetDirectoryName(Path.GetFullPath(inFile))!);

            ID3DBlob* errors = null;

            // Entry point: empty for effects/libraries/rootsig targets.
            var epStr = opts.TargetHasNoEntryPoint ? null : opts.EntryPoint;

            int hr;
            fixed (byte* pSrc = src)
            fixed (byte* pSrcName = Utf8Null(inFile))
            fixed (byte* pTarget = Utf8Null(opts.Target))
            fixed (byte* pEntryPoint = epStr is not null ? Utf8Null(epStr) : null)
            {
                hr = D3DCompiler.Compile2(
                    pSrc,
                    (nuint)src.Length,
                    pSrcName,
                    mac.Ptr,
                    inc.NativePtr,
                    pEntryPoint,
                    pTarget,
                    (uint)opts.CompileFlags,
                    (uint)opts.EffectFlags,
                    0, // SecondaryDataFlags — not used outside /mergeUAVs
                    null,
                    0, // SecondaryData — see NOTE in Options.cs
                    &code,
                    &errors
                );
            }

            PrintErrors(errors, errorSink);
            if (errors is not null)
            {
                errors->Release();
            }

            if (hr < 0)
            {
                errorSink.WriteLine("fxc: compilation failed; no code produced");
                return 1;
            }
        }

        // Post-compilation:
        // Each step may replace `code` with a new blob; the previous one is
        // released before reassignment.

        try
        {
            return RunPostCompile(inFile, opts, errorSink, ref code);
        }
        finally
        {
            if (code is not null)
            {
                code->Release();
                code = null;
            }
        }
    }

    private static int RunPostCompile(
        string inFile,
        Options opts,
        TextWriter errorSink,
        ref ID3DBlob* code
    )
    {
        // /setrootsignature
        if (opts.SetRootSig is not null)
        {
            var rsData = ReadFileOrDie(opts.SetRootSig, out _);
            if (rsData is null)
            {
                return 1;
            }

            ID3DBlob* newCode = null;
            int hr;
            fixed (byte* pRs = rsData)
            {
                hr = D3DCompiler.SetBlobPart(
                    code->GetBufferPointer(),
                    code->GetBufferSize(),
                    D3DBlobPart.RootSignature,
                    0,
                    pRs,
                    (nuint)rsData.Length,
                    &newCode
                );
            }

            if (hr < 0)
            {
                errorSink.WriteLine("fxc: unable to set root signature");
                return 1;
            }

            code->Release();
            code = newCode;
        }

        // /extractrootsignature — save root sig and exit
        if (opts.ExtractRootSig is not null)
        {
            ID3DBlob* rsBlob = null;
            var hr = D3DCompiler.GetBlobPart(
                code->GetBufferPointer(),
                code->GetBufferSize(),
                D3DBlobPart.RootSignature,
                0,
                &rsBlob
            );

            if (hr < 0 || rsBlob is null)
            {
                errorSink.WriteLine("fxc: specified file does not contain a root signature");
                return 1;
            }

            var result = WriteBlobToPath(
                rsBlob,
                opts.ExtractRootSig,
                errorSink,
                $"fxc: root signature extracted; see {opts.ExtractRootSig}"
            );
            rsBlob->Release();
            return result;
        }

        // /Qstrip_*
        if (opts.StripFlags != D3DStripFlags.None)
        {
            ID3DBlob* stripped = null;
            var hr = D3DCompiler.StripShader(
                code->GetBufferPointer(),
                code->GetBufferSize(),
                (uint)opts.StripFlags,
                &stripped
            );

            if (hr >= 0 && stripped is not null)
            {
                code->Release();
                code = stripped;
            }
            else
            {
                errorSink.WriteLine("fxc: strip operation failed");
                // Non-fatal — continue with unstripped blob.
            }
        }

        // /setprivate
        if (opts.SetPrivate is not null)
        {
            var privData = ReadFileOrDie(opts.SetPrivate, out _);
            if (privData is null)
            {
                errorSink.WriteLine(
                    $"fxc: failed to open private data file: {opts.SetPrivate}"
                );
                // Non-fatal — continue without private data.
            }
            else
            {
                ID3DBlob* newCode = null;
                int hr;
                fixed (byte* pPriv = privData)
                {
                    hr = D3DCompiler.SetBlobPart(
                        code->GetBufferPointer(),
                        code->GetBufferSize(),
                        D3DBlobPart.PrivateData,
                        0,
                        pPriv,
                        (nuint)privData.Length,
                        &newCode
                    );
                }

                if (hr >= 0 && newCode is not null)
                {
                    code->Release();
                    code = newCode;
                }
                else
                {
                    errorSink.WriteLine(
                        "fxc: unable to set private data, continuing without it"
                    );
                }
            }
        }

        // /getprivate
        if (opts.GetPrivate is not null)
        {
            ID3DBlob* privBlob = null;
            var hr = D3DCompiler.GetBlobPart(
                code->GetBufferPointer(),
                code->GetBufferSize(),
                D3DBlobPart.PrivateData,
                0,
                &privBlob
            );

            if (hr < 0 || privBlob is null)
            {
                errorSink.WriteLine("fxc: shader does not have private data");
            }
            else
            {
                WriteBlobToPath(
                    privBlob,
                    opts.GetPrivate,
                    errorSink,
                    $"fxc: compilation private data save succeeded; see {opts.GetPrivate}"
                );
                privBlob->Release();
            }
        }

        // /Fd — PDB extraction
        if (opts.OutPdb is not null)
        {
            ExtractPdb(code, opts.OutPdb, errorSink);
        }

        // Inspect shader version DWORD for output extension selection.
        var shaderVersion = code->GetBufferSize() >= 4
            ? *(uint*)code->GetBufferPointer()
            : 0;

        // /Fc — assembly listing
        if (opts.OutAssembly is not null)
        {
            WriteDisassembly(
                code,
                opts.OutAssembly,
                opts.DisassembleFlags,
                hexDump: false,
                errorSink
            );
        }

        // /Fx — assembly + hex listing
        if (opts.OutListing is not null)
        {
            WriteDisassembly(
                code,
                opts.OutListing,
                opts.DisassembleFlags,
                hexDump: true,
                errorSink
            );
        }

        // /Fh — header file
        if (opts.OutHeader is not null)
        {
            var r = WriteHeader(code, opts.OutHeader, opts.VarName, shaderVersion, errorSink);
            if (r == 0)
            {
                Console.WriteLine($"fxc: compilation header save succeeded; see {opts.OutHeader}");
            }
        }

        // /Fo or /Fl or derived default
        var objPath = opts.OutObject ?? opts.OutLibrary;
        if (objPath is null && opts.OutHeader is null &&
            opts.OutAssembly is null && opts.OutListing is null)
        {
            objPath = DeriveOutputPath(inFile, shaderVersion, opts);
        }

        if (objPath is not null)
        {
            var r = WriteBlobToPath(
                code,
                objPath,
                errorSink,
                $"fxc: compilation object save succeeded; see {objPath}"
            );
            if (r != 0)
            {
                return r;
            }
        }

        return 0;
    }

    private static void ExtractPdb(
        ID3DBlob* code,
        string pdbArg,
        TextWriter errorSink
    )
    {
        // If pdbArg ends with '\', we derive the filename from the debug-name
        // blob embedded in the shader.
        var pdbPath = pdbArg;

        if (pdbArg.EndsWith('\\') || pdbArg.EndsWith('/'))
        {
            var derivedName = GetDebugName(code);
            if (derivedName is not null)
            {
                pdbPath = Path.Combine(pdbArg, derivedName);
            }
        }

        ID3DBlob* pdbBlob = null;
        var hr = D3DCompiler.GetBlobPart(
            code->GetBufferPointer(),
            code->GetBufferSize(),
            D3DBlobPart.Pdb,
            0,
            &pdbBlob
        );

        if (hr >= 0 && pdbBlob is not null)
        {
            WriteBlobToPath(
                pdbBlob,
                pdbPath,
                errorSink,
                $"fxc: compilation PDB save succeeded; see {pdbPath}"
            );
            pdbBlob->Release();
        }
        else
        {
            // 0x887A0001 = DXGI_ERROR_NOT_FOUND — shader model too old.
            errorSink.WriteLine(
                hr == unchecked((int)0x887A0001)
                    ? "fxc: /Fd requires a shader model 4 or higher target."
                    : "fxc: /Fd specified, but no PDB data was found in the shader. " +
                      "Use /Zi to generate debug information."
            );
        }
    }

    /// <summary>
    ///     Reads the D3D_BLOB_DEBUG_NAME part and returns the embedded
    ///     filename, or null if not present.
    ///     <br />
    ///     <br />
    ///     The layout of the debug-name blob is:
    ///     <br />
    ///     <c>uint16 Flags, uint16 NameLength, char[NameLength+1] Name, padding</c>
    /// </summary>
    private static string? GetDebugName(ID3DBlob* code)
    {
        ID3DBlob* nameBlob = null;
        var hr = D3DCompiler.GetBlobPart(
            code->GetBufferPointer(),
            code->GetBufferSize(),
            D3DBlobPart.DebugName,
            0,
            &nameBlob
        );

        if (hr < 0 || nameBlob is null)
        {
            return null;
        }

        try
        {
            var sz = nameBlob->GetBufferSize();
            if (sz < 4)
            {
                return null;
            }

            var ptr = (byte*)nameBlob->GetBufferPointer();
            var nameLen = *(ushort*)(ptr + 2);
            if (sz < (nuint)(4 + nameLen))
            {
                return null;
            }

            return Encoding.UTF8.GetString(ptr + 4, nameLen);
        }
        finally
        {
            nameBlob->Release();
        }
    }

    private static void WriteDisassembly(
        ID3DBlob* code,
        string outPath,
        D3DDisassembleFlags flags,
        bool hexDump,
        TextWriter errorSink
    )
    {
        ID3DBlob* disasm = null;
        var hr = D3DCompiler.Disassemble(
            code->GetBufferPointer(),
            code->GetBufferSize(),
            (uint)flags,
            null,
            &disasm
        );

        if (hr < 0 || disasm is null)
        {
            errorSink.WriteLine($"fxc: disassembly failed writing '{outPath}'");
            return;
        }

        try
        {
            using var fs = File.Open(outPath, FileMode.Create, FileAccess.Write);
            using var writer = new StreamWriter(fs, Encoding.UTF8, leaveOpen: true);

            // Write disassembly text
            writer.Write(disasm->ToUtf8String());

            if (hexDump)
            {
                WriteHexDump(writer, code);
            }

            writer.Flush();
        }
        catch (Exception ex)
        {
            errorSink.WriteLine($"fxc: failed writing assembly to '{outPath}': {ex.Message}");
        }
        finally
        {
            disasm->Release();
        }
    }

    private static void WriteHexDump(StreamWriter w, ID3DBlob* code)
    {
        var dwords = (uint*)code->GetBufferPointer();
        var size = code->GetBufferSize();
        var count = (int)(size / 4);

        w.WriteLine();
        w.WriteLine();

        for (var i = 0; i < count; i += 4)
        {
            w.Write($"// {i * 4:x4}:  ");

            // Hex columns
            for (var j = i; j < i + 4; j++)
            {
                w.Write(j < count ? $"{dwords[j]:x8}  " : "            ");
            }

            // ASCII columns
            for (var j = i; j < i + 4 && j < count; j++)
            {
                var d = dwords[j];
                for (var b = 0; b < 4; b++)
                {
                    var ch = (byte)((d >> (b * 8)) & 0xFF);
                    w.Write(ch >= 0x20 ? (char)ch : '.');
                }
            }

            w.WriteLine();
        }
    }

    private static int WriteHeader(
        ID3DBlob* code,
        string outPath,
        string? varName,
        uint shaderVersion,
        TextWriter errorSink
    )
    {
        try
        {
            using var writer = new StreamWriter(outPath, append: false, Encoding.UTF8);

            // Attempt disassembly as a comment block.
            ID3DBlob* disasm = null;
            var hr = D3DCompiler.Disassemble(
                code->GetBufferPointer(),
                code->GetBufferSize(),
                (uint)D3DDisassembleFlags.EnableDefaultValuePrints,
                null,
                &disasm
            );

            if (hr >= 0 && disasm is not null)
            {
                writer.WriteLine("#if 0");
                writer.Write(disasm->ToUtf8String());
                writer.WriteLine("#endif");
                writer.WriteLine();
                disasm->Release();
            }

            // Variable name
            var vn = varName ?? DeriveVarName(shaderVersion);

            writer.WriteLine($"const BYTE {vn}[] =");
            writer.WriteLine("{");

            var data = new ReadOnlySpan<byte>(
                code->GetBufferPointer(),
                (int)code->GetBufferSize()
            );

            for (var i = 0; i < data.Length; i++)
            {
                if (i % 6 == 0)
                {
                    writer.Write("    ");
                }

                writer.Write($" 0x{data[i]:x2}");
                if (i + 1 < data.Length)
                {
                    writer.Write(',');
                }

                if (i % 6 == 5 || i + 1 == data.Length)
                {
                    writer.WriteLine();
                }
            }

            writer.WriteLine("};");
            return 0;
        }
        catch (Exception ex)
        {
            errorSink.WriteLine($"fxc: failed writing header to '{outPath}': {ex.Message}");
            return 1;
        }
    }

    private static string DeriveVarName(uint shaderVersion)
    {
        var type = (shaderVersion >> 16) & 0xFFFF;
        var major = (shaderVersion >> 8) & 0xFF;
        var minor = shaderVersion & 0xFF;
        return type switch
        {
            0xFFFE => $"vs{major}{minor}_",
            0xFFFF => $"ps{major}{minor}_",
            _ => "g_",
        };
    }

    private static string DeriveOutputPath(string inFile, uint shaderVersion, Options opts)
    {
        string ext;

        if (opts.DumpBin)
        {
            ext = ".dcs";
        }
        else
        {
            var type = (shaderVersion >> 16) & 0xFFFF;
            ext = type switch
            {
                0xFFFE => ".vso",
                0xFFFF => ".pso",
                _ when shaderVersion == 0xFFFFFFFF => ".fxo",
                _ => ".cso",
            };
        }

        return ReplaceExtension(inFile, ext);
    }

    private static string ReplaceExtension(string path, string newExt)
    {
        // Find the last dot that is after the last directory separator.
        var lastSep = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        var dot = path.LastIndexOf('.');
        var @base = (dot > lastSep) ? dot : path.Length;
        return string.Concat(path.AsSpan(0, @base), newExt);
    }

    /// <summary>
    ///     Writes <paramref name="blob" /> to a file using D3DWriteBlobToFile,
    ///     which handles the Win32 path correctly under Wine.
    /// </summary>
    private static int WriteBlobToPath(
        ID3DBlob* blob,
        string path,
        TextWriter errorSink,
        string? successMessage
    )
    {
        int hr;
        fixed (char* pPath = path)
        {
            hr = D3DCompiler.WriteBlobToFile(blob, pPath, 1 /* overwrite */);
        }

        if (hr < 0)
        {
            errorSink.WriteLine($"fxc: failed writing '{path}'");
            return 1;
        }

        if (successMessage is null)
        {
            return 0;
        }

        Console.WriteLine(successMessage);
        return 0;
    }

    // TODO: We can do some filtering on our side here.
    /// <summary>
    ///     Writes the text content of an errors blob to the given sink.
    /// </summary>
    private static void PrintErrors(ID3DBlob* errors, TextWriter sink)
    {
        if (errors is null)
        {
            return;
        }

        var sz = errors->GetBufferSize();
        if (sz < 2)
        {
            return;
        }

        // The blob is null-terminated UTF-8.
        var ptr = (byte*)errors->GetBufferPointer();
        var len = (int)sz;
        if (ptr[len - 1] == 0)
        {
            len--;
        }

        sink.Write(Encoding.UTF8.GetString(ptr, len));
        sink.Flush();
    }

    /// <summary>
    ///     Reads a file entirely into a byte array.
    ///     <br />
    ///     Writes an error message and returns null on failure.
    /// </summary>
    private static byte[]? ReadFileOrDie(string path, out long size)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            size = data.Length;
            return data;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fxc: failed to open file: {path} ({ex.Message})");
            size = 0;
            return null;
        }
    }

    private static IncludeHandler BuildIncludeHandler(Options opts)
    {
        var includeHandler = new IncludeHandler();
        foreach (var p in opts.IncludePaths)
        {
            includeHandler.AddSystemPath(p);
        }

        return includeHandler;
    }

    internal static byte[] Utf8Null(string s)
    {
        var len = Encoding.UTF8.GetByteCount(s);
        var bytes = new byte[len + 1];
        {
            Encoding.UTF8.GetBytes(s, bytes);
        }
        bytes[len] = 0;
        return bytes;
    }
}
