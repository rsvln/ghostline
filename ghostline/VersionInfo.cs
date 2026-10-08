using System.Reflection;

namespace ghostline
{
    // App version from the assembly attributes: "1.1.0+2026.10.08" (version.txt and the build date).
    public static class VersionInfo
    {
        public static string Informational { get; } =
            typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        public static string Version => Informational.Split('+')[0];

        // "2026-10-08"; empty when the build date is unknown
        public static string BuildDate => Informational.Contains('+') ? Informational.Split('+')[1].Replace('.', '-') : "";

        public const string ProjectUrl = "https://github.com/rsvln/ghostline";
    }
}
