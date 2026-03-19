using System;
using Tomat.FXCS.Bindings;

namespace Tomat.FXCS;

/// <summary>
///     Validates <see cref="Options"/>.
/// </summary>
public static class Validator
{
    /// <summary>
    ///     Validates the parsed options for mutually exclusive combinations and
    ///     missing required values.  Writes to stderr and returns false on
    ///     error.
    /// </summary>
    public static bool Validate(Options opts)
    {
        if (opts.InputFiles.Count == 0)
        {
            Console.Error.WriteLine("fxc: no files specified, use /? to get usage information");
            return false;
        }

        // /Zss and /Zsb are mutually exclusive
        var hasZss = opts.CompileFlags.HasFlag(D3DCompileFlags.DebugNameForSource);
        var hasZsb = opts.CompileFlags.HasFlag(D3DCompileFlags.DebugNameForBinary);
        if (hasZss && hasZsb)
        {
            Console.Error.WriteLine("fxc: cannot specify both /Zss and /Zsb");
            return false;
        }

        // /Zpr and /Zpc are mutually exclusive
        var hasZpr = opts.CompileFlags.HasFlag(D3DCompileFlags.PackMatrixRowMajor);
        var hasZpc = opts.CompileFlags.HasFlag(D3DCompileFlags.PackMatrixColumnMajor);
        if (hasZpr && hasZpc)
        {
            Console.Error.WriteLine(
                "fxc: cannot specify /Zpr and /Zpc together, use /? to get usage information"
            );
            return false;
        }

        // /Gfa and /Gfp are mutually exclusive
        var hasGfa = opts.CompileFlags.HasFlag(D3DCompileFlags.AvoidFlowControl);
        var hasGfp = opts.CompileFlags.HasFlag(D3DCompileFlags.PreferFlowControl);
        if (hasGfa && hasGfp)
        {
            Console.Error.WriteLine(
                "fxc: cannot specify /Gfa and /Gfp together, use /? to get usage information"
            );
            return false;
        }

        // /Ges and /Gec are mutually exclusive
        var hasGes = opts.CompileFlags.HasFlag(D3DCompileFlags.EnableStrictness);
        var hasGec = opts.CompileFlags.HasFlag(D3DCompileFlags.EnableBackwardsCompatibility);
        if (hasGes && hasGec)
        {
            Console.Error.WriteLine("fxc: strictness and compatibility mode are mutually exclusive:");
            Console.Error.WriteLine("  For DX9 compatibility mode, use /Gec");
            Console.Error.WriteLine("  For regular DX10 shaders and effects, use regular mode");
            Console.Error.WriteLine("  For clean future-proof DX10 shaders and effects, use strict mode (/Ges)");
            return false;
        }

        // /P must be used alone - no object/header/listing output alongside it
        if (opts.PreprocessOnly)
        {
            if (opts.OutObject != null
             || opts.OutHeader != null
             || opts.OutAssembly != null
             || opts.OutListing != null)
            {
                Console.Error.WriteLine(
                    "fxc: cannot preprocess to file and compile at the same time"
                );
                return false;
            }
        }

        // /compress and /decompress are mutually exclusive
        if (opts is { Compress: true, Decompress: true })
        {
            Console.Error.WriteLine(
                "fxc: compression and decompression cannot be combined"
            );
            return false;
        }

        // compress/decompress cannot combine with strip
        if ((opts.Compress || opts.Decompress) && opts.StripFlags != D3DStripFlags.None)
        {
            Console.Error.WriteLine(
                "fxc: compression and decompression cannot be combined with stripping"
            );
            return false;
        }

        // compress/decompress cannot combine with disassembly output
        if ((opts.Compress || opts.Decompress)
         && (opts.OutAssembly != null || opts.OutListing != null))
        {
            Console.Error.WriteLine(
                "fxc: compression and decompression cannot be combined with disassembly"
            );
            return false;
        }

        // /decompress cannot output a header file
        if (opts is { Decompress: true, OutHeader: not null })
        {
            Console.Error.WriteLine(
                "fxc: decompression cannot be combined with /Fh"
            );
            return false;
        }

        // library target cannot have an explicit entry point
        if (opts.TargetKind == TargetKind.Library && !string.IsNullOrEmpty(opts.EntryPoint))
        {
            Console.Error.WriteLine(
                "fxc: cannot specify entry point for a library " +
                "(mark library entry points with the export keyword)"
            );
            return false;
        }

        // /shtemplate requires /mergeUAVs or /matchUAVs
        if (opts is { ShTemplate: not null, MergeUavs: false, MatchUavs: false })
        {
            Console.Error.WriteLine(
                "fxc: /shtemplate can only be used when merging/matching resources, " +
                "use /? to get usage information"
            );
            return false;
        }

        // /mergeUAVs and /matchUAVs are mutually exclusive
        if (opts.MergeUavs && opts.MatchUavs)
        {
            Console.Error.WriteLine(
                "fxc: merge/match resources set multiple times, use /? to get usage information"
            );
            return false;
        }

        // /mergeUAVs or /matchUAVs requires a template
        if ((opts.MergeUavs || opts.MatchUavs) && opts.ShTemplate == null)
        {
            Console.Error.WriteLine(
                "fxc: merging/matching resources requires a template shader to be given " +
                "with /shtemplate, use /? to get usage information"
            );
            return false;
        }

        return true;
    }
}
