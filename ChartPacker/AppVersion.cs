using System.Reflection;

namespace ChartPacker
{
    /// <summary>The program version, as set by <c>&lt;Version&gt;</c> in ChartPacker.csproj.</summary>
    internal static class AppVersion
    {
        /// <summary>The semantic version, e.g. <c>1.2.0</c> or <c>1.2.0-beta.1</c>.</summary>
        public static string Semantic { get; } =
            typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    }
}
