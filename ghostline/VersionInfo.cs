using System.Reflection;

namespace ghostline
{
    // Версия приложения из атрибутов сборки: "1.0.124+2026.10.08" (номер — version.txt, дата сборки).
    public static class VersionInfo
    {
        public static string Informational { get; } =
            typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        public static string Version => Informational.Split('+')[0];

        // "2026-10-08"; пусто, если дата сборки неизвестна (отладочная сборка)
        public static string BuildDate => Informational.Contains('+') ? Informational.Split('+')[1].Replace('.', '-') : "";

        public const string ProjectUrl = "https://github.com/rsvln/ghostline";
    }
}
