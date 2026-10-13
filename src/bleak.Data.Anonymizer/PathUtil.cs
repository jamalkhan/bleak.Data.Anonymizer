namespace bleak.Data.Anonymizer;

/// <summary>
/// Filesystem path helpers shared across the engine. Mirrors the ETL engine's <c>PathUtil</c> so both
/// behave identically (leading <c>~</c> expansion + config-relative resolution).
/// </summary>
public static class PathUtil
{
    /// <summary>Expands a leading <c>~</c> to the current user's home directory; other paths pass through.</summary>
    public static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~')
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return path;
        }

        if (path.Length == 1)
        {
            return home;
        }
        if (path[1] == '/' || path[1] == '\\')
        {
            return Path.Combine(home, path[2..]);
        }
        return path;
    }

    /// <summary>Expands <c>~</c>, then resolves a relative path against <paramref name="baseDirectory"/>.</summary>
    public static string Resolve(string path, string baseDirectory)
    {
        var expanded = ExpandHome(path);
        return Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(Path.Combine(baseDirectory, expanded));
    }
}
