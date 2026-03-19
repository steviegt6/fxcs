using System;
using System.Collections.Generic;
using System.IO;

namespace Tomat.FXCS;

public sealed class HelpRequestedException() : Exception("help requested");

/// <summary>
///     Target classification, derived from the /T string.
/// </summary>
public enum TargetKind
{
    Shader,  // vs_, ps_, cs_, gs_, ds_, hs_
    Effects, // fx_
    Library, // lib_
    RootSig, // rootsig_
}

/// <summary>
///     Program options.
/// </summary>
public sealed class Options
{
    // Mode flags
    public bool DumpBin { get; set; }         // /dumpbin
    public bool PreprocessOnly { get; set; }  // /P
    public bool Compress { get; set; }        // /compress
    public bool Decompress { get; set; }      // /decompress
    public bool NoLogo { get; set; }          // /nologo
    public bool VerboseIncludes { get; set; } // /Vi

    // Shader target 
    public string Target { get; set; } = "vs_2_0";
    public string EntryPoint { get; set; } = "main";
    public TargetKind TargetKind { get; set; } = TargetKind.Shader;

    // Compilation flags
    public D3DCompileFlags CompileFlags { get; set; } = D3DCompileFlags.OptimizationLevel1;
    public D3DEffectFlags EffectFlags { get; set; } = D3DEffectFlags.None;
    public D3DStripFlags StripFlags { get; set; } = D3DStripFlags.None;

    // /O level tracking — we need to detect duplicate /O flags.
    public bool OptLevelSet { get; set; }

    // Output paths (null = not requested)
    public string? OutObject { get; set; }     // /Fo
    public string? OutLibrary { get; set; }    // /Fl
    public string? OutHeader { get; set; }     // /Fh
    public string? OutAssembly { get; set; }   // /Fc  (disasm only)
    public string? OutListing { get; set; }    // /Fx  (disasm + hex)
    public string? OutPreprocess { get; set; } // /P
    public string? OutPdb { get; set; }        // /Fd
    public string? OutError { get; set; }      // /Fe

    // Naming
    public string? VarName { get; set; } // /Vn

    // Listing display options
    public bool ColorCode { get; set; }          // /Cc
    public bool InstructionNumbers { get; set; } // /Ni
    public bool InstructionOffsets { get; set; } // /No
    public bool HexLiterals { get; set; }        // /Lx

    // Root-signature operations
    public string? SetRootSig { get; set; }      // /setrootsignature
    public string? ExtractRootSig { get; set; }  // /extractrootsignature
    public string? VerifyRootSig { get; set; }   // /verifyrootsignature
    public string? ForceRootSigVer { get; set; } // /force_rootsig_ver

    // Private data
    public string? SetPrivate { get; set; } // /setprivate
    public string? GetPrivate { get; set; } // /getprivate

    // -- UAV matching (partially implemented; parsed but not yet acted on) --
    // NOTE: /mergeUAVs and /matchUAVs require D3DCompile2 secondary-data
    // semantics that are not publicly documented.  The flags are parsed and
    // validated; the actual merging is a known gap.
    public bool MergeUavs { get; set; }     // /mergeUAVs
    public bool MatchUavs { get; set; }     // /matchUAVs
    public string? ShTemplate { get; set; } // /shtemplate

    // Preprocessor macros
    public List<(string Name, string Value)> Macros { get; } = [];

    // Include search paths (the first entry is always CWD, added at startup)
    public List<string> IncludePaths { get; } = [];

    // Input files
    public List<string> InputFiles { get; } = [];

    /// <summary>
    ///     True when the target does not use an explicit entry point.
    /// </summary>
    public bool TargetHasNoEntryPoint => TargetKind is TargetKind.Effects or TargetKind.Library or TargetKind.RootSig;

    /// <summary>
    ///     Builds <see cref="D3DDisassembleFlags" /> from the display options.
    /// </summary>
    public D3DDisassembleFlags DisassembleFlags
    {
        get
        {
            var f = D3DDisassembleFlags.EnableDefaultValuePrints;
            if (ColorCode)
            {
                f |= D3DDisassembleFlags.EnableColorCode;
            }

            if (InstructionNumbers)
            {
                f |= D3DDisassembleFlags.EnableInstructionNumbering;
            }

            if (InstructionOffsets)
            {
                f |= D3DDisassembleFlags.EnableInstructionOffset;
            }

            if (HexLiterals)
            {
                f |= D3DDisassembleFlags.PrintHexLiterals;
            }

            return f;
        }
    }
}

/// <summary>
///     Parses out command line options into an <see cref="Options" /> object.
/// </summary>
public static class OptionsParser
{
    // A lightweight ref-cursor over a string span.
    private ref struct TokenCursor(ReadOnlySpan<string> tokens)
    {
        private int pos;
        private readonly ReadOnlySpan<string> tokens = tokens;

        public bool HasMore => pos < tokens.Length;

        public string? Peek()
        {
            return HasMore ? tokens[pos] : null;
        }

        public string Next()
        {
            return tokens[pos++];
        }

        public string? TryNext()
        {
            return HasMore ? tokens[pos++] : null;
        }

        /// <summary>Advance and return the next token as a required parameter.</summary>
        public string? RequireParam(string optionName)
        {
            var p = TryNext();
            if (p is null)
            {
                Console.Error.WriteLine($"fxc: '/{optionName}' option requires a parameter");
            }

            return p;
        }
    }

    /// <summary>
    ///     Parses <paramref name="args" /> into <paramref name="opts" />.
    ///     <br />
    ///     Returns <see langword="false" /> on a fatal parse error, or throws
    ///     <see cref="HelpRequestedException" /> when <c>/?</c> or <c>/help</c>
    ///     are encountered.
    /// </summary>
    public static bool Parse(string[] args, Options opts)
    {
        var cursor = new TokenCursor(args.AsSpan());
        return ParseTokens(ref cursor, opts);
    }

    private static bool ParseTokens(ref TokenCursor cursor, Options opts)
    {
        while (cursor.HasMore)
        {
            var arg = cursor.Next();

            // Response file
            if (arg.StartsWith('@'))
            {
                var rfPath = NormalizePath(arg[1..]);
                if (!HandleResponseFile(rfPath, opts))
                {
                    return false;
                }

                continue;
            }

            // Plain file (not a switch)
            if (arg.Length == 0 || (arg[0] != '-' && arg[0] != '/'))
            {
                opts.InputFiles.Add(NormalizePath(arg));
                continue;
            }

            // Strip leading switch character and parse
            var sw = arg.AsSpan(1);
            if (!ParseSwitch(sw, ref cursor, opts))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ParseSwitch(ReadOnlySpan<char> sw, ref TokenCursor cursor, Options opts)
    {
        // Help
        if (sw.Equals("?", StringComparison.Ordinal) || sw.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            throw new HelpRequestedException();
        }

        // Long word options

        if (MatchesLong(sw, "dumpbin"))
        {
            opts.DumpBin = true;
            return true;
        }

        if (TryGetLongParam(sw, "setrootsignature", ref cursor, out var setRs))
        {
            if (setRs is null)
            {
                return false;
            }

            opts.SetRootSig = NormalizePath(setRs);
            return true;
        }

        if (TryGetLongParam(sw, "extractrootsignature", ref cursor, out var extractRs))
        {
            if (extractRs is null)
            {
                return false;
            }

            opts.ExtractRootSig = NormalizePath(extractRs);
            return true;
        }

        if (TryGetLongParam(sw, "verifyrootsignature", ref cursor, out var verifyRs))
        {
            if (verifyRs is null)
            {
                return false;
            }

            opts.VerifyRootSig = NormalizePath(verifyRs);
            return true;
        }

        if (MatchesLong(sw, "compress"))
        {
            opts.Compress = true;
            return true;
        }

        if (MatchesLong(sw, "decompress"))
        {
            opts.Decompress = true;
            return true;
        }

        if (TryGetLongParam(sw, "shtemplate", ref cursor, out var sht))
        {
            if (sht is null)
            {
                return false;
            }

            opts.ShTemplate = NormalizePath(sht);
            return true;
        }

        if (MatchesLong(sw, "mergeUAVs"))
        {
            opts.MergeUavs = true;
            return true;
        }

        if (MatchesLong(sw, "matchUAVs"))
        {
            opts.MatchUavs = true;
            return true;
        }

        if (MatchesLong(sw, "res_may_alias"))
        {
            opts.CompileFlags |= D3DCompileFlags.ResourcesMayAlias;
            return true;
        }

        if (MatchesLong(sw, "enable_unbounded_descriptor_tables"))
        {
            opts.CompileFlags |= D3DCompileFlags.EnableUnboundedDescriptorTables;
            return true;
        }

        if (MatchesLong(sw, "all_resources_bound"))
        {
            opts.CompileFlags |= D3DCompileFlags.AllResourcesBound;
            return true;
        }

        if (TryGetLongParam(sw, "getprivate", ref cursor, out var getPriv))
        {
            if (getPriv is null)
            {
                return false;
            }

            opts.GetPrivate = NormalizePath(getPriv);
            return true;
        }

        if (TryGetLongParam(sw, "setprivate", ref cursor, out var setPriv))
        {
            if (setPriv is null)
            {
                return false;
            }

            opts.SetPrivate = NormalizePath(setPriv);
            return true;
        }

        if (TryGetLongParam(sw, "force_rootsig_ver", ref cursor, out var frsv))
        {
            if (frsv is null)
            {
                return false;
            }

            opts.ForceRootSigVer = frsv;
            return true;
        }

        if (MatchesLong(sw, "nologo"))
        {
            opts.NoLogo = true;
            return true;
        }

        // Single/double character options

        if (sw.Length == 0)
        {
            Console.Error.WriteLine("fxc: empty option");
            return false;
        }

        var c0 = sw[0];
        var c1 = sw.Length > 1 ? sw[1] : '\0';
        // var c2 = sw.Length > 2 ? sw[2] : '\0';

        switch (c0)
        {
            case 'C' or 'c':
            {
                if (sw.Equals("Cc", StringComparison.OrdinalIgnoreCase))
                {
                    opts.ColorCode = true;
                    return true;
                }

                break;
            }

            case 'D' or 'd':
            {
                // /D name[=value] or /D:name=value
                if (c0 == 'D' && c1 is '\0' or ':')
                {
                    var defn = c1 == ':' ? sw[2..].ToString() : cursor.RequireParam("D");
                    if (defn is null)
                    {
                        return false;
                    }

                    return ParseMacroDefinition(defn, ref cursor, opts);
                }

                if (c0 == 'D' && c1 != '\0')
                {
                    // /Dname or /Dname=value (no separator)
                    return ParseMacroDefinition(sw[1..].ToString(), ref cursor, opts);
                }

                break;
            }

            case 'E' or 'e':
            {
                if (c0 == 'E' || (c0 == 'e' && c1 == '\0'))
                {
                    var ep = ExtractParam(sw, 1, "E", ref cursor);
                    if (ep is null)
                    {
                        return false;
                    }

                    opts.EntryPoint = ep;
                    return true;
                }

                break;
            }

            case 'F' or 'f':
            {
                return ParseFSwitch(sw, ref cursor, opts);
            }

            case 'G' or 'g':
            {
                return ParseGSwitch(sw, opts);
            }

            case 'I' or 'i':
            {
                if (c0 == 'I')
                {
                    var inc = ExtractParam(sw, 1, "I", ref cursor);
                    if (inc is null)
                    {
                        return false;
                    }

                    opts.IncludePaths.Add(NormalizePath(inc));
                    return true;
                }

                break;
            }

            case 'L' or 'l':
            {
                if (sw.Equals("Lx", StringComparison.OrdinalIgnoreCase))
                {
                    opts.HexLiterals = true;
                    return true;
                }

                break;
            }

            case 'N' or 'n':
            {
                if (sw.Equals("Ni", StringComparison.OrdinalIgnoreCase))
                {
                    opts.InstructionNumbers = true;
                    return true;
                }

                if (sw.Equals("No", StringComparison.OrdinalIgnoreCase))
                {
                    opts.InstructionOffsets = true;
                    return true;
                }

                break;
            }

            case 'O' or 'o':
            {
                return ParseOSwitch(sw, opts);
            }

            case 'P' or 'p':
            {
                if (sw.Length == 1 || (sw.Length >= 2 && (c1 == ':' || sw.Length == 1)))
                {
                    var pout = ExtractParam(sw, 1, "P", ref cursor);
                    if (pout is null)
                    {
                        return false;
                    }

                    opts.PreprocessOnly = true;
                    opts.OutPreprocess = NormalizePath(pout);
                    return true;
                }

                break;
            }

            case 'Q' or 'q':
            {
                return ParseQSwitch(sw, opts);
            }

            case 'T' or 't':
                if (c0 == 'T')
                {
                    var target = ExtractParam(sw, 1, "T", ref cursor);
                    if (target is null)
                    {
                        return false;
                    }

                    opts.Target = target;
                    opts.TargetKind = ClassifyTarget(target);

                    // Library targets have no entrypoint; clear the default.
                    if (opts.TargetHasNoEntryPoint)
                    {
                        opts.EntryPoint = string.Empty;
                    }

                    return true;
                }

                break;

            case 'V' or 'v':
            {
                return ParseVSwitch(sw, ref cursor, opts);
            }

            case 'W' or 'w':
            {
                if (sw.Equals("WX", StringComparison.OrdinalIgnoreCase))
                {
                    opts.CompileFlags |= D3DCompileFlags.WarningsAreErrors;
                    return true;
                }

                break;
            }

            case 'Z' or 'z':
            {
                return ParseZSwitch(sw, opts);
            }
        }

        Console.Error.WriteLine($"fxc: unknown or invalid option '/{sw}'");
        return false;
    }

    private static bool ParseFSwitch(ReadOnlySpan<char> sw, ref TokenCursor cursor, Options opts)
    {
        if (sw.Length < 2)
        {
            Console.Error.WriteLine("fxc: unknown option '/F'");
            return false;
        }

        var kind = sw[1];
        var path = ExtractParam(sw, 2, $"F{kind}", ref cursor);
        if (path is null)
        {
            return false;
        }

        path = NormalizePath(path);

        switch (kind)
        {
            case 'o':
                opts.OutObject = path;
                return true;

            case 'l':
                opts.OutLibrary = path;
                return true;

            case 'h':
                opts.OutHeader = path;
                return true;

            case 'c':
                opts.OutAssembly = path;
                return true;

            case 'x':
                opts.OutListing = path;
                return true;

            case 'd':
                opts.OutPdb = path;
                return true;

            case 'e':
                opts.OutError = path;
                return true;

            default:
                Console.Error.WriteLine($"fxc: unknown option '/F{kind}'");
                return false;
        }
    }

    private static bool ParseGSwitch(ReadOnlySpan<char> sw, Options opts)
    {
        if (sw.Equals("Gpp", StringComparison.OrdinalIgnoreCase))
        {
            opts.CompileFlags |= D3DCompileFlags.PartialPrecision;
            return true;
        }

        if (sw.Equals("Gfa", StringComparison.OrdinalIgnoreCase))
        {
            opts.CompileFlags |= D3DCompileFlags.AvoidFlowControl;
            return true;
        }

        if (sw.Equals("Gfp", StringComparison.OrdinalIgnoreCase))
        {
            opts.CompileFlags |= D3DCompileFlags.PreferFlowControl;
            return true;
        }

        if (sw.Equals("Gdp", StringComparison.OrdinalIgnoreCase))
        {
            opts.EffectFlags |= D3DEffectFlags.AllowSlowOps;
            return true;
        }

        if (sw.Equals("Ges", StringComparison.OrdinalIgnoreCase))
        {
            opts.CompileFlags |= D3DCompileFlags.EnableStrictness;
            return true;
        }

        if (sw.Equals("Gec", StringComparison.OrdinalIgnoreCase))
        {
            opts.CompileFlags |= D3DCompileFlags.EnableBackwardsCompatibility;
            return true;
        }

        if (sw.Equals("Gis", StringComparison.OrdinalIgnoreCase))
        {
            opts.CompileFlags |= D3DCompileFlags.IeeeStrictness;
            return true;
        }

        if (sw.Equals("Gch", StringComparison.OrdinalIgnoreCase))
        {
            opts.EffectFlags |= D3DEffectFlags.ChildEffect;
            return true;
        }

        Console.Error.WriteLine($"fxc: unknown option '/{sw}'");
        return false;
    }

    private static bool ParseOSwitch(ReadOnlySpan<char> sw, Options opts)
    {
        if (sw.Length < 2)
        {
            Console.Error.WriteLine("fxc: /O requires a level or 'd'/'p'");
            return false;
        }

        var c1 = sw[1];

        if (c1 == 'd')
        {
            // /Od — disable optimisation
            opts.CompileFlags |= D3DCompileFlags.SkipOptimization;
            opts.OptLevelSet = true;
            return true;
        }

        if (c1 == 'p')
        {
            // /Op — disable preshaders
            opts.CompileFlags |= D3DCompileFlags.NoPreshader;
            return true;
        }

        if (c1 >= '0' && c1 <= '3')
        {
            if (opts.OptLevelSet)
            {
                Console.Error.WriteLine("fxc: optimization level (/O#) set multiple times");
                return false;
            }

            opts.OptLevelSet = true;

            // Clear any existing opt-level bits before setting the new ones.
            opts.CompileFlags &= ~(D3DCompileFlags.OptimizationLevel0 |
                                   D3DCompileFlags.OptimizationLevel3);
            opts.CompileFlags |= c1 switch
            {
                '0' => D3DCompileFlags.OptimizationLevel0,
                '1' => D3DCompileFlags.OptimizationLevel1,
                '2' => D3DCompileFlags.OptimizationLevel2,
                '3' => D3DCompileFlags.OptimizationLevel3,
                _ => D3DCompileFlags.OptimizationLevel1,
            };
            return true;
        }

        Console.Error.WriteLine($"fxc: unknown option '/{sw}'");
        return false;
    }

    private static bool ParseQSwitch(ReadOnlySpan<char> sw, Options opts)
    {
        if (sw.Equals("Qstrip_reflect", StringComparison.OrdinalIgnoreCase))
        {
            opts.StripFlags |= D3DStripFlags.ReflectionData;
            return true;
        }

        if (sw.Equals("Qstrip_debug", StringComparison.OrdinalIgnoreCase))
        {
            opts.StripFlags |= D3DStripFlags.DebugInfo;
            return true;
        }

        if (sw.Equals("Qstrip_priv", StringComparison.OrdinalIgnoreCase))
        {
            opts.StripFlags |= D3DStripFlags.PrivateData;
            return true;
        }

        if (sw.Equals("Qstrip_rootsignature", StringComparison.OrdinalIgnoreCase))
        {
            opts.StripFlags |= D3DStripFlags.RootSignature;
            return true;
        }

        Console.Error.WriteLine($"fxc: unknown option '/{sw}'");
        return false;
    }

    private static bool ParseVSwitch(ReadOnlySpan<char> sw, ref TokenCursor cursor, Options opts)
    {
        if (sw.Equals("Vd", StringComparison.OrdinalIgnoreCase))
        {
            opts.CompileFlags |= D3DCompileFlags.SkipValidation;
            return true;
        }

        if (sw.Equals("Vi", StringComparison.OrdinalIgnoreCase))
        {
            opts.VerboseIncludes = true;
            return true;
        }

        if (sw.Length >= 2 && (sw[1] == 'n' || sw[1] == 'N'))
        {
            var vn = ExtractParam(sw, 2, "Vn", ref cursor);
            if (vn is null)
            {
                return false;
            }

            opts.VarName = vn;
            return true;
        }

        Console.Error.WriteLine($"fxc: unknown option '/{sw}'");
        return false;
    }

    private static bool ParseZSwitch(ReadOnlySpan<char> sw, Options opts)
    {
        if (sw.Equals("Zi", StringComparison.OrdinalIgnoreCase))
        {
            opts.CompileFlags |= D3DCompileFlags.Debug;
            return true;
        }

        if (sw.Equals("Zss", StringComparison.OrdinalIgnoreCase))
        {
            opts.CompileFlags |= D3DCompileFlags.DebugNameForSource;
            return true;
        }

        if (sw.Equals("Zsb", StringComparison.OrdinalIgnoreCase))
        {
            opts.CompileFlags |= D3DCompileFlags.DebugNameForBinary;
            return true;
        }

        if (sw.Equals("Zpr", StringComparison.OrdinalIgnoreCase))
        {
            opts.CompileFlags |= D3DCompileFlags.PackMatrixRowMajor;
            return true;
        }

        if (sw.Equals("Zpc", StringComparison.OrdinalIgnoreCase))
        {
            opts.CompileFlags |= D3DCompileFlags.PackMatrixColumnMajor;
            return true;
        }

        Console.Error.WriteLine($"fxc: unknown option '/{sw}'");
        return false;
    }

    private static bool ParseMacroDefinition(string defn, ref TokenCursor cursor, Options opts)
    {
        if (opts.Macros.Count >= 256)
        {
            Console.Error.WriteLine($"fxc: too many macros defined ({opts.Macros.Count})");
            return false;
        }

        var eq = defn.IndexOf('=');
        var name = defn;
        var value = "1";

        if (eq >= 0)
        {
            name = defn[..eq];
            value = defn[(eq + 1)..];

            // If the value is empty, consume the next token
            // (e.g., /D FOO = bar)
            if (value.Length == 0)
            {
                var next = cursor.TryNext();
                value = next ?? "1";
            }
        }

        opts.Macros.Add((name, value));
        return true;
    }

    private static bool HandleResponseFile(string path, Options opts)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fxc: unable to open response file '{path}': {ex.Message}");
            return false;
        }

        var tokens = TokenizeResponseFile(text);
        var sub = new TokenCursor(tokens.AsSpan());
        return ParseTokens(ref sub, opts);
    }

    private static string[] TokenizeResponseFile(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            // Skip whitespace
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            var quoted = text[i] == '"';
            if (quoted)
            {
                i++;
            }

            var start = i;
            while (i < text.Length)
            {
                if (quoted && text[i] == '"')
                {
                    break;
                }

                if (!quoted && char.IsWhiteSpace(text[i]))
                {
                    break;
                }

                i++;
            }

            tokens.Add(text[start..i]);
            if (quoted && i < text.Length)
            {
                i++; // skip closing quote
            }
        }

        return [.. tokens];
    }

    /// <summary>
    ///     Extracts a parameter value from either the remainder of the current
    ///     token (after <paramref name="offset" />) or from the next token.
    ///     <br />
    ///     Handles optional ':' separator.
    /// </summary>
    private static string? ExtractParam(
        ReadOnlySpan<char> sw,
        int offset,
        string optionName,
        ref TokenCursor cursor
    )
    {
        if (offset >= sw.Length)
        {
            return cursor.RequireParam(optionName);
        }

        // Skip optional colon
        var start = (sw[offset] == ':') ? offset + 1 : offset;
        if (start < sw.Length)
        {
            return sw[start..].ToString();
        }

        return cursor.RequireParam(optionName);
    }

    /// <summary>
    ///     Matches a long option name case-insensitively.
    /// </summary>
    private static bool MatchesLong(ReadOnlySpan<char> sw, string name)
    {
        return sw.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Matches a long option that takes a parameter.
    ///     The parameter may follow immediately after the name (with optional ':'),
    ///     or be the next token.
    /// </summary>
    private static bool TryGetLongParam(
        ReadOnlySpan<char> sw,
        string name,
        ref TokenCursor cursor,
        out string? value
    )
    {
        if (!sw.StartsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            return false;
        }

        var rest = name.Length;
        if (rest == sw.Length)
        {
            // Name only — parameter is next token.
            value = cursor.RequireParam(name);
            return true;
        }

        if (sw[rest] == ':' || sw[rest] == ' ')
        {
            value = rest + 1 < sw.Length ? sw[(rest + 1)..].ToString() : cursor.RequireParam(name);
            return true;
        }

        value = null;
        return false;
    }

    private static TargetKind ClassifyTarget(string target)
    {
        return target.StartsWith("lib_", StringComparison.OrdinalIgnoreCase) ? TargetKind.Library :
            target.StartsWith("rootsig_", StringComparison.OrdinalIgnoreCase) ? TargetKind.RootSig :
            target.StartsWith("fx_", StringComparison.OrdinalIgnoreCase) ? TargetKind.Effects :
            TargetKind.Shader;
    }

    /// <summary>Replaces forward slashes with backslashes for Win32 APIs.</summary>
    internal static string NormalizePath(string p)
    {
        return p.Contains('/') ? p.Replace('/', '\\') : p;
    }
}
