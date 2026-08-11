using System;
using System.IO;
using Playnite.SDK.Models;
using RomM.Saves;
using Xunit;

namespace RomM.Tests
{
    /// <summary>
    /// Finding retroarch.cfg, and what happens when it cannot be found. The Playnite emulator entry
    /// is not a dependable pointer to RetroArch: a profile that launches through a wrapper script
    /// leaves the install directory pointing at the script's folder, and frontends that generate
    /// those entries (EmuDeck among them) rewrite them wholesale, so a correction made there is
    /// undone the next time they run.
    /// </summary>
    public class RetroArchConfigDiscoveryTests : IDisposable
    {
        private readonly string _root;
        private readonly string _retroArchDir;
        private readonly string _romDir;
        private readonly string _romPath;

        public RetroArchConfigDiscoveryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            _retroArchDir = Path.Combine(_root, "RetroArch");
            _romDir = Path.Combine(_root, "roms", "snes");
            Directory.CreateDirectory(_retroArchDir);
            Directory.CreateDirectory(_romDir);

            _romPath = Path.Combine(_romDir, "Some Game.zip");
            File.WriteAllText(_romPath, "rom");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string WriteConfig(string directory)
        {
            var path = Path.Combine(directory, "retroarch.cfg");
            File.WriteAllText(path,
                "savefile_directory = \":\\saves\"\n" +
                "sort_savefiles_enable = \"false\"\n");
            Directory.CreateDirectory(Path.Combine(directory, "saves"));
            return path;
        }

        private SaveTarget Resolve(Emulator emulator, EmulatorProfile profile = null, string overridePath = null)
        {
            // An empty roaming directory, so a RetroArch installed on the machine running these
            // tests cannot satisfy the lookup and change what they prove.
            var handler = new RetroArchSaveHandler(Path.Combine(_root, "no-roaming-retroarch"));

            return handler.ResolveTarget(new SaveTargetRequest
            {
                Game = new Game { Name = "Some Game" },
                Emulator = emulator,
                Profile = profile,
                ContentPath = _romPath,
                ConfigPathOverride = overridePath,
            });
        }

        private string ExpectedSave => Path.Combine(_retroArchDir, "saves", "Some Game.srm");

        // The failure this guards against is silent and destructive: with no config the resolver
        // used to fall through to "saves live beside the content", which files a real save inside
        // the ROM library, reports success, and leaves the emulator reading an empty directory.
        [Fact]
        public void Refuses_to_resolve_when_no_config_can_be_found()
        {
            var emulator = new Emulator { Name = "RetroArch", InstallDir = Path.Combine(_root, "launchers") };
            Directory.CreateDirectory(emulator.InstallDir);

            Assert.Null(Resolve(emulator));
        }

        [Fact]
        public void Does_not_write_saves_into_the_rom_directory_when_the_config_is_missing()
        {
            var emulator = new Emulator { Name = "RetroArch", InstallDir = Path.Combine(_root, "launchers") };
            Directory.CreateDirectory(emulator.InstallDir);

            Resolve(emulator);

            Assert.False(File.Exists(Path.Combine(_romDir, "Some Game.srm")));
        }

        [Fact]
        public void Uses_the_config_beside_the_install_directory()
        {
            WriteConfig(_retroArchDir);
            var emulator = new Emulator { Name = "RetroArch", InstallDir = _retroArchDir };

            var target = Resolve(emulator);

            Assert.NotNull(target);
            Assert.Contains(Path.Combine("RetroArch", "saves"), target.DescribeLocation());
        }

        // A wrapper-script setup: the install directory points at the launcher, so the only honest
        // way to find the config is the user saying where it is.
        [Fact]
        public void Override_wins_when_the_install_directory_points_elsewhere()
        {
            var configPath = WriteConfig(_retroArchDir);
            var launchers = Path.Combine(_root, "launchers");
            Directory.CreateDirectory(launchers);
            var emulator = new Emulator { Name = "RetroArch", InstallDir = launchers };

            var target = Resolve(emulator, overridePath: configPath);

            Assert.NotNull(target);
            Assert.Equal(ExpectedSave, target.DescribeLocation());
        }

        // The ':' in savefile_directory expands against RetroArch's own folder, so the override has
        // to set the base directory too -- resolving it against the launcher folder would put the
        // save somewhere RetroArch never reads.
        [Fact]
        public void Override_also_anchors_the_base_directory_token()
        {
            var configPath = WriteConfig(_retroArchDir);
            var launchers = Path.Combine(_root, "launchers");
            Directory.CreateDirectory(launchers);
            var emulator = new Emulator { Name = "RetroArch", InstallDir = launchers };

            var target = Resolve(emulator, overridePath: configPath);

            Assert.DoesNotContain("launchers", target.DescribeLocation());
        }

        [Fact]
        public void Falls_back_to_the_executable_the_profile_runs()
        {
            WriteConfig(_retroArchDir);
            var exe = Path.Combine(_retroArchDir, "retroarch.exe");
            File.WriteAllText(exe, "exe");
            var launchers = Path.Combine(_root, "launchers");
            Directory.CreateDirectory(launchers);

            var emulator = new Emulator { Name = "RetroArch", InstallDir = launchers };
            var profile = new CustomEmulatorProfile { Executable = exe, Arguments = "\"{ImagePath}\"" };

            var target = Resolve(emulator, profile);

            Assert.NotNull(target);
            Assert.Equal(ExpectedSave, target.DescribeLocation());
        }

        // Following a wrapper would mean guessing what the script does with its arguments. Where it
        // ends up is the script's business, so the profile is simply not a signal in that case.
        [Fact]
        public void Does_not_follow_a_wrapper_executable()
        {
            WriteConfig(_retroArchDir);
            var launchers = Path.Combine(_root, "launchers");
            Directory.CreateDirectory(launchers);

            var emulator = new Emulator { Name = "RetroArch", InstallDir = launchers };
            var profile = new CustomEmulatorProfile
            {
                Executable = "powershell",
                Arguments = "-File \"" + Path.Combine(launchers, "retroarch.ps1") + "\" -L snes9x_libretro.dll",
            };

            Assert.Null(Resolve(emulator, profile));
        }

        [Fact]
        public void Ignores_an_override_that_does_not_exist()
        {
            WriteConfig(_retroArchDir);
            var emulator = new Emulator { Name = "RetroArch", InstallDir = _retroArchDir };

            var target = Resolve(emulator, overridePath: Path.Combine(_root, "nope", "retroarch.cfg"));

            Assert.NotNull(target);
            Assert.Equal(ExpectedSave, target.DescribeLocation());
        }
    }
}
