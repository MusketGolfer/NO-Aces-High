using System;
using System.IO;

namespace NOAcesHigh;

/// <summary>
/// Puts the squadron guide (README.md) and the _Template squadron into the user's squadron folder, copied from the
/// plugin's own AceSquadrons folder, the first time (never over a copy the user has edited). Folders starting with "_"
/// are not loaded, so the template stays out of the game until it is copied and renamed.
/// </summary>
internal static class SquadronGuide
{
    internal static void EnsureUserFolder()
    {
        try
        {
            string shipped = Path.Combine(Path.GetDirectoryName(typeof(SquadronGuide).Assembly.Location) ?? "", "AceSquadrons");
            string user = Squadrons.UserFolder;
            if (!Directory.Exists(shipped)) return;
            Directory.CreateDirectory(user);
            CopyIfNewer(Path.Combine(shipped, "README.md"), Path.Combine(user, "README.md")); // the guide follows mod updates
            string template = Path.Combine(shipped, "_Template");
            if (Directory.Exists(template))
                foreach (var file in Directory.GetFiles(template, "*", SearchOption.AllDirectories))
                    CopyIfMissing(file, Path.Combine(user, "_Template", file.Substring(template.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Could not set up the squadron guide and template: " + ex.Message);
        }
    }

    private static void CopyIfNewer(string from, string to)
    {
        if (File.Exists(to) && File.Exists(from) && File.GetLastWriteTimeUtc(from) > File.GetLastWriteTimeUtc(to)) File.Copy(from, to, true);
        else CopyIfMissing(from, to);
    }

    private static void CopyIfMissing(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to) ?? ".");
        File.Copy(from, to);
    }
}
