using System;
using System.IO;
using System.Text;

namespace Tomat.FXCS;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (!D3DCompiler.Load())
        {
            return 1;
        }

        var opts = new Options();
        try
        {
            if (!OptionsParser.Parse(args, opts))
            {
                return 1;
            }
        }
        catch (HelpRequestedException)
        {
            Usage.PrintHelp(D3DCompiler.LoadedDllPath!);
            return 0;
        }

        if (!opts.NoLogo)
        {
            Usage.PrintBanner(D3DCompiler.LoadedDllPath!);
        }

        var errorSink = Console.Error;
        var errorFile = default(StreamWriter);

        if (opts.OutError is not null)
        {
            try
            {
                errorFile = new StreamWriter(opts.OutError, append: false, Encoding.UTF8);
                errorSink = errorFile;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"fxc: could not open error file '{opts.OutError}': {ex.Message}");
            }
        }

        try
        {
            if (!Validator.Validate(opts))
            {
                return 1;
            }

            return Dispatch(opts, errorSink);
        }
        finally
        {
            errorFile?.Flush();
            errorFile?.Dispose();
        }
    }

    private static int Dispatch(Options opts, TextWriter errorSink)
    {
        if (opts.Compress)
        {
            return Compiler.Compress(opts, errorSink);
        }

        if (opts.Decompress)
        {
            return Compiler.Decompress(opts, errorSink);
        }

        if (opts.PreprocessOnly)
        {
            var rc = 0;
            foreach (var f in opts.InputFiles)
            {
                rc |= Compiler.Preprocess(f, opts, errorSink);
            }

            return rc;
        }

        // Regular compilation.
        {
            var rc = 0;
            foreach (var f in opts.InputFiles)
            {
                rc |= Compiler.Compile(f, opts, errorSink);
            }

            return rc;
        }
    }
}
