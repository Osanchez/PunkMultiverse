using System.IO;
using System.Reflection;

namespace PunkMultiverse
{
    /// <summary>This DLL's own folder (BepInEx/plugins/PunkMultiverse/) — home for config
    /// and session cache. Falls back to plugins/&lt;AssemblyName&gt; under dev hot-reload, where
    /// the assembly is loaded from bytes and Location is empty.</summary>
    internal static class ModFolder
    {
        internal static string Dir
        {
            get
            {
                var asm = Assembly.GetExecutingAssembly();
                var loc = asm.Location;
                return string.IsNullOrEmpty(loc)
                    ? Path.Combine(BepInEx.Paths.PluginPath, asm.GetName().Name)
                    : Path.GetDirectoryName(loc);
            }
        }
    }
}
