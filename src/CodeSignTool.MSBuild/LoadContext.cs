#if NETCOREAPP

namespace Ancora.MSBuild
{
	using System.IO;
	using System.Reflection;
	using System.Runtime.Loader;

	public class LoadContext : AssemblyLoadContext
    {
        public static readonly LoadContext Instance = new LoadContext();

        public const string RuntimePath = "./runtimes";

        protected override Assembly Load(AssemblyName assemblyName)
        {
            var path = Path.Combine(Path.GetDirectoryName(typeof(LoadContext).Assembly.Location), assemblyName.Name + ".dll");
            return File.Exists(path)
                ? this.LoadFromAssemblyPath(path)
                : Default.LoadFromAssemblyName(assemblyName);
        }
	}
}

#endif