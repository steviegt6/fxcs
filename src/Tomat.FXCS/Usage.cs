using System;

namespace Tomat.FXCS;

internal static class Usage
{
    private const string help_text =
        """
        Usage: fxc <options> <files>

           /?, /help           print this message
           /T <profile>        target profile
           /E <name>           entrypoint name
           /I <include>        additional include path
           /Vi                 display details about the include process

           /O{0,1,2,3}         optimization level 0..3.  1 is default
           /Od                 disable optimizations
           /Op                 disable preshaders
           /WX                 treat warnings as errors
           /Vd                 disable validation

           /Zi                 enable debugging information
           /Zss                debug name with source information
           /Zsb                debug name with only binary information
           /Zpr                pack matrices in row-major order
           /Zpc                pack matrices in column-major order

           /Gpp                force partial precision
           /Gfa                avoid flow control constructs
           /Gfp                prefer flow control constructs
           /Gdp                disable effect performance mode
           /Ges                enable strict mode
           /Gec                enable backwards compatibility mode
           /Gis                force IEEE strictness
           /Gch                compile as a child effect for FX 4.x targets

           /Fo <file>          output object file
           /Fl <file>          output a library
           /Fh <file>          output header file containing object code
           /Fc <file>          output assembly code listing file
           /Fx <file>          output assembly code and hex listing file
           /Fd <file>          extract shader PDB and write to given file
                               (if <file> ends with '\', PDB name is derived from shader)
           /Fe <file>          output warnings and errors to a specific file
           /Vn <name>          use <name> as variable name in header file

           /P <file>           preprocess to file (must be used alone)

           /D <id>=<text>      define macro
           /nologo             suppress copyright message

           /Cc                 output color coded assembly listings
           /Ni                 output instruction numbers in assembly listings
           /No                 output instruction byte offset in assembly listings
           /Lx                 output hexadecimal literals

           /Qstrip_reflect     strip reflection data from 4_0+ shader bytecode
           /Qstrip_debug       strip debug information from 4_0+ shader bytecode
           /Qstrip_priv        strip private data from 4_0+ shader bytecode
           /Qstrip_rootsignature  strip root signature from shader bytecode

           /setrootsignature <file>      attach root signature to shader bytecode
           /extractrootsignature <file>  extract root signature from shader bytecode
           /verifyrootsignature <file>   verify shader bytecode against root signature
           /force_rootsig_ver <profile>  force root signature version (rootsig_1_1 if omitted)

           /setprivate <file>  private data to add to compiled shader blob
           /getprivate <file>  save private data from shader blob

           /shtemplate <file>  template shader file for merging/matching resources
           /mergeUAVs          merge UAV slots of template shader and current shader
           /matchUAVs          match template shader UAV slots in current shader
                               NOTE: /mergeUAVs and /matchUAVs are parsed but the actual
                               merging is not yet implemented (requires undocumented
                               D3DCompile2 secondary-data semantics).

           /res_may_alias      assume that UAVs/SRVs may alias for cs_5_0+
           /enable_unbounded_descriptor_tables  enables unbounded descriptor tables
           /all_resources_bound  enable aggressive flattening in SM5.1+

           /dumpbin            load a binary file rather than compiling
           /compress           compress DX10 shader bytecode from files
           /decompress         decompress bytecode from first file; remaining files
                               are output destinations (auto-named if omitted)

           @<file>             options response file

           <profile>:
              cs_4_0 cs_4_1 cs_5_0 cs_5_1
              ds_5_0 ds_5_1
              gs_4_0 gs_4_1 gs_5_0 gs_5_1
              hs_5_0 hs_5_1
              ps_2_0 ps_2_a ps_2_b ps_2_sw ps_3_0 ps_3_sw
              ps_4_0 ps_4_0_level_9_0 ps_4_0_level_9_1 ps_4_0_level_9_3
              ps_4_1 ps_5_0 ps_5_1
              vs_1_1 vs_2_0 vs_2_a vs_2_sw vs_3_0 vs_3_sw
              vs_4_0 vs_4_0_level_9_0 vs_4_0_level_9_1 vs_4_0_level_9_3
              vs_4_1 vs_5_0 vs_5_1
              lib_4_0 lib_4_0_level_9_0 lib_4_0_level_9_1 lib_4_0_level_9_3
              lib_4_1 lib_5_0
              fx_2_0 fx_4_0 fx_4_1 fx_5_0
              rootsig_1_0 rootsig_1_1

        """;

    private const string shim_compiler_version = "10.1";

    public static void PrintBanner(string compilerPath)
    {
        Console.WriteLine($"Microsoft (R) Direct3D Shader Compiler {shim_compiler_version} (using {compilerPath})");
        Console.WriteLine("Copyright (C) 2013 Microsoft. All rights reserved.");
        Console.WriteLine("Copyright (C) 2026 Tomat");
        Console.WriteLine();
    }

    public static void PrintHelp(string compilerPath)
    {
        PrintBanner(compilerPath);
        Console.Write(help_text);
    }
}
