using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace DeskMadeline.Mac;

internal static class MacCelesteInstall
{
    internal static string ResourcesAt(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (string candidate in new[]
        {
            path,
            Path.Combine(path, "Contents", "Resources"),
            Path.Combine(path, "Celeste.app", "Contents", "Resources"),
        })
            if (CelesteInstall.IsInstall(candidate)) return candidate;
        return null;
    }

    internal static List<string> Candidates()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string steam = Path.Combine(home, "Library", "Application Support", "Steam");
        var libraries = new List<string> { steam };
        try
        {
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var paths = new List<string>();
        foreach (string library in libraries)
            paths.Add(Path.Combine(library, "steamapps", "common", "Celeste", "Celeste.app", "Contents", "Resources"));
        paths.Add(Path.Combine(home, "Applications", "Celeste.app", "Contents", "Resources"));
        paths.Add("/Applications/Celeste.app/Contents/Resources");
        return paths;
    }
}
