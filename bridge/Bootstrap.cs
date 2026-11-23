using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LxMusic.Bridge.Online
{
    internal static class Bootstrap
    {
        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
            return Run(args);
        }

        // Register the resolver before the JIT touches any third-party types.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args) => Program.Run(args);

        private static Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            using (var resource = typeof(Bootstrap).Assembly.GetManifestResourceStream("LxMusic.Dependencies." + name + ".dll"))
            {
                if (resource == null) return null;
                using (var bytes = new MemoryStream())
                {
                    resource.CopyTo(bytes);
                    return Assembly.Load(bytes.ToArray());
                }
            }
        }
    }
}
