using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RomM.Saves
{
    /// <summary>
    /// RetroArch's battery saves. The location comes out of retroarch.cfg rather than being fixed,
    /// so the file is found by reading the emulator's own configuration and then, if that path is
    /// empty, by looking for the save the emulator already wrote.
    ///
    /// The save is named after the ROM, not after any platform identifier, which is why this
    /// handler needs no title id and works for every core.
    /// </summary>
    internal sealed class RetroArchSaveHandler : ISaveHandler
    {
        private readonly string _roamingConfigDir;

        public RetroArchSaveHandler()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroArch"))
        {
        }

        /// <param name="roamingConfigDir">
        /// Where an installed (non-portable) RetroArch keeps its configuration. Injectable so the
        /// discovery order can be tested without the result depending on whether the machine
        /// running the tests happens to have RetroArch installed.
        /// </param>
        internal RetroArchSaveHandler(string roamingConfigDir)
        {
            _roamingConfigDir = roamingConfigDir;
        }

        public string EmulatorTag => "retroarch";

        public bool CanHandle(Emulator emulator)
        {
            if (emulator == null)
                return false;

            if (string.Equals(emulator.BuiltInConfigId, "retroarch", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(emulator.Name) &&
                emulator.Name.IndexOf("retroarch", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (!string.IsNullOrEmpty(emulator.InstallDir) &&
                File.Exists(Path.Combine(emulator.InstallDir, "retroarch.exe")))
                return true;

            return false;
        }

        public SaveTarget ResolveTarget(SaveTargetRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.ContentPath))
                    return null;

                var cfgPath = FindConfig(request.Emulator, request.Profile, request.ConfigPathOverride, out var baseDir);
                if (cfgPath == null)
                {
                    // Without the config there is no way to know where saves go, and guessing is
                    // worse than stopping: an empty config resolves to the content directory, which
                    // silently files real save data inside the user's ROM library and reports
                    // success. "savefile_directory is empty" and "no config was found" look the
                    // same downstream but mean opposite things, so the second one has to end here.
                    request.Logger?.Info(
                        $"[SaveSync] Could not find retroarch.cfg for '{request.Emulator.Name}' " +
                        $"(install dir: {Describe(request.Emulator.InstallDir)}). Set the retroarch.cfg " +
                        "path in the RomM settings to sync this game's saves.");
                    return null;
                }

                var cfg = RetroArchConfig.Parse(File.ReadAllText(cfgPath));
                var saveRoot = RetroArchConfig.ResolveSaveBaseDirectory(cfg, request.ContentPath, baseDir);

                // With sort_savefiles_enable the save sits in a folder named after the running core.
                // Resolving without that name only matters once a download has to create the file:
                // it would land beside the core folders instead of inside the right one, where
                // RetroArch never looks, and the game would start over on a save that is present.
                var coreName = MatchExistingCoreFolder(saveRoot, ResolveCoreName(request.Profile));

                var expectedPath = RetroArchConfig.ResolveSaveFilePath(cfg, request.ContentPath, coreName, baseDir);
                if (string.IsNullOrEmpty(expectedPath))
                    return null;

                // A configured path can still come out relative -- ":\saves" with no base directory
                // to expand it against leaves "saves". Combining that with a file name yields
                // something that resolves against Playnite's working directory, which is nobody's
                // save folder, so it is another case of not knowing rather than a usable answer.
                if (!Path.IsPathRooted(expectedPath))
                {
                    request.Logger?.Info(
                        $"[SaveSync] Save path for '{request.Emulator.Name}' resolved to the relative " +
                        $"'{expectedPath}', which cannot be anchored (base directory: " +
                        $"{Describe(baseDir)}). Set the retroarch.cfg path in the RomM settings.");
                    return null;
                }

                // The configured path is where RetroArch *would* write. When nothing is there, the
                // save may still exist under a subfolder we did not model, so fall back to
                // searching for it by ROM name before assuming there is none.
                var existing = File.Exists(expectedPath)
                    ? expectedPath
                    : FindExistingSave(saveRoot, Path.GetFileNameWithoutExtension(request.ContentPath));

                return new FileSaveTarget(EmulatorTag, expectedPath, existing);
            }
            catch (Exception ex)
            {
                request.Logger?.Error(ex, $"[SaveSync] Failed to resolve RetroArch save path for {request.Game?.Name}.");
                return null;
            }
        }

        /// <summary>
        /// The core a profile runs, as far as it can be told from Playnite. Built-in RetroArch
        /// profiles are named after the core ("mGBA"); a custom profile carries it in the libretro
        /// argument (`-L "cores\mgba_libretro.dll"`). Null when neither yields anything, which
        /// leaves the per-core folder out of the path exactly as before.
        /// </summary>
        internal static string ResolveCoreName(EmulatorProfile profile)
        {
            var builtIn = profile as BuiltInEmulatorProfile;
            if (builtIn != null)
                return string.IsNullOrWhiteSpace(builtIn.Name) ? null : builtIn.Name.Trim();

            var custom = profile as CustomEmulatorProfile;
            if (custom != null)
                return CoreFromArguments(custom.Arguments);

            return null;
        }

        private static readonly Regex LibretroArgument =
            new Regex(@"-L\s+""?(?<path>[^""\s]+)""?", RegexOptions.IgnoreCase);

        private static string CoreFromArguments(string arguments)
        {
            if (string.IsNullOrEmpty(arguments))
                return null;

            var match = LibretroArgument.Match(arguments);
            if (!match.Success)
                return null;

            var name = Path.GetFileNameWithoutExtension(match.Groups["path"].Value);
            if (string.IsNullOrEmpty(name))
                return null;

            if (name.EndsWith("_libretro", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - "_libretro".Length);

            return name.Length == 0 ? null : name;
        }

        /// <summary>
        /// RetroArch names the folder after the core's own display name, which is not always how
        /// Playnite spells it — a profile can yield "mgba" where the folder on disk is "mGBA".
        /// Where a matching folder already exists its spelling wins, so a download joins the saves
        /// RetroArch is already writing instead of creating a near-duplicate beside them.
        /// </summary>
        private static string MatchExistingCoreFolder(string saveRoot, string coreName)
        {
            if (string.IsNullOrEmpty(coreName) || string.IsNullOrEmpty(saveRoot) || !Directory.Exists(saveRoot))
                return coreName;

            try
            {
                var match = Directory.EnumerateDirectories(saveRoot)
                    .Select(Path.GetFileName)
                    .FirstOrDefault(n => string.Equals(n, coreName, StringComparison.OrdinalIgnoreCase));

                return match ?? coreName;
            }
            catch
            {
                return coreName;
            }
        }

        /// <summary>
        /// Locates retroarch.cfg, and reports the directory RetroArch treats as its base -- what
        /// the leading ':' in values like ":\saves" expands against. The two are the same for a
        /// portable install, where the config sits beside the executable, but not for an installed
        /// one, where the config lives under %AppData% while ':' still means the program folder.
        ///
        /// Order matters. The Playnite entry's install directory is only correct when the entry
        /// points at RetroArch itself; a profile that launches through a wrapper script points it
        /// at the script's folder, and the config is then nowhere near it. So an explicit setting
        /// wins, then the executable the profile actually runs, and the install directory is
        /// consulted after both.
        /// </summary>
        private string FindConfig(
            Emulator emulator, EmulatorProfile profile, string overridePath, out string retroArchBaseDir)
        {
            retroArchBaseDir = emulator.InstallDir;

            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            {
                retroArchBaseDir = Path.GetDirectoryName(overridePath);
                return overridePath;
            }

            var fromExecutable = ExecutableDirectory(profile);
            if (fromExecutable != null)
            {
                var beside = Path.Combine(fromExecutable, "retroarch.cfg");
                if (File.Exists(beside))
                {
                    retroArchBaseDir = fromExecutable;
                    return beside;
                }
            }

            if (!string.IsNullOrEmpty(emulator.InstallDir))
            {
                var inInstall = Path.Combine(emulator.InstallDir, "retroarch.cfg");
                if (File.Exists(inInstall))
                {
                    retroArchBaseDir = emulator.InstallDir;
                    return inInstall;
                }
            }

            // The roaming config belongs to an installed RetroArch, whose ':' is the program folder
            // rather than this one, so the base directory is left as the entry's install directory.
            if (string.IsNullOrEmpty(_roamingConfigDir))
                return null;

            var roaming = Path.Combine(_roamingConfigDir, "retroarch.cfg");
            return File.Exists(roaming) ? roaming : null;
        }

        /// <summary>
        /// The folder of the executable a custom profile runs, when that executable is RetroArch
        /// itself. A profile that launches a wrapper (a script, a shell) is deliberately not
        /// followed: where it ends up is that script's business and not something to infer.
        /// </summary>
        private static string ExecutableDirectory(EmulatorProfile profile)
        {
            var custom = profile as CustomEmulatorProfile;
            if (custom == null || string.IsNullOrWhiteSpace(custom.Executable))
                return null;

            if (!string.Equals(Path.GetFileName(custom.Executable), "retroarch.exe",
                    StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(custom.Executable));
                return Directory.Exists(directory) ? directory : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Describe(string value)
        {
            return string.IsNullOrEmpty(value) ? "<not set>" : value;
        }

        private static string FindExistingSave(string baseDir, string contentName)
        {
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir) || string.IsNullOrEmpty(contentName))
                return null;

            try
            {
                return Directory
                    .EnumerateFiles(baseDir, contentName + RetroArchConfig.SaveExtension, SearchOption.AllDirectories)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }
}
