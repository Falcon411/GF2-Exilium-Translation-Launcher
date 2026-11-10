namespace Gf2ExiliumTranslationLauncher;

internal static class LauncherPaths
{
    public static string ExecutableDirectory { get; } = GetExecutableDirectory();

    private static string GetExecutableDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return string.IsNullOrWhiteSpace(baseDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(baseDirectory);
    }
}
