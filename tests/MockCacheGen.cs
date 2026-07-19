using System;
using System.IO;
using System.Reflection;
using System.Text;

[assembly: AssemblyTitle("VLC plugin cache generator test fixture")]
[assembly: AssemblyVersion("3.0.23.0")]
[assembly: AssemblyFileVersion("3.0.23.0")]

internal static class MockCacheGen
{
    private static int Main(string[] args)
    {
        if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fail-cache")))
            return 10;
        if (args.Length != 1 || !Directory.Exists(args[0]))
            return 2;

        string plugin = Path.Combine(args[0], "access", "libtorrent_plugin.dll");
        string cache = Path.Combine(args[0], "plugins.dat");
        string contents = File.Exists(plugin)
            ? "mock plugin cache\0libtorrent_plugin.dll\0"
            : "mock plugin cache\0";
        File.WriteAllBytes(cache, Encoding.ASCII.GetBytes(contents));
        return 0;
    }
}
