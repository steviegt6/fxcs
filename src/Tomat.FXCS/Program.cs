using Tomat.FXCS.Bindings;

namespace Tomat.FXCS;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (!D3DCompiler.Load())
        {
            return 1;
        }

        Usage.PrintHelp(D3DCompiler.LoadedDllPath!);
        return 0;
    }
}
