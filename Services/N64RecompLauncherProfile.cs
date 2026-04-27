using GitHubLauncher.Core.Services;

namespace N64RecompLauncher.Services
{
    public sealed class N64RecompLauncherProfile : LauncherProfile 
    {
        public static N64RecompLauncherProfile Instance { get; } = new();

        public override string DisplayName => "N64Recomp Launcher";
        public override string ApplicationId => "N64RecompLauncher";
        public override string Repository => "SirDiabo/N64RecompLauncher";
        public override string ExecutableName => "N64RecompLauncher";
        public override string DefaultInstallFolderName => "RecompiledGames";
        public override string UserAgent => "N64Recomp-Launcher/1.0";
        public override string CliUserAgent => "N64RecompLauncher-CLI";
        public override string UpdaterUserAgent => "N64RecompLauncher-Updater";
        public override string SteamTag => "N64 Recomp Launcher";

        public override (List<object> standard, List<object> experimental, List<object> custom) GetDefaultGamesData()
        {
            var standard = new List<object>
    {
        new { name = "Zelda 64",
            repository = "Zelda64Recomp/Zelda64Recomp",
            folderName = "Zelda64Recompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/Zelda64Recomp/Zelda64Recomp/refs/heads/dev/icons/512.png" },

        new { name = "Goemon 64",
            repository = "klorfmorf/Goemon64Recomp",
            folderName = "Goemon64Recompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/klorfmorf/Goemon64Recomp/refs/heads/dev/icons/512.png" },

        new { name = "Mario Kart 64",
            repository = "sonicdcer/MarioKart64Recomp",
            folderName = "MarioKart64Recompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/sonicdcer/MarioKart64Recomp/refs/heads/main/icons/512.png" },

        new { name = "Dinosaur Planet",
            repository = "DinosaurPlanetRecomp/dino-recomp",
            folderName = "DinoPlanetRecompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/DinosaurPlanetRecomp/dino-recomp/refs/heads/main/icons/64.png" },

        new { name = "Dr. Mario 64",
            repository = "theboy181/drmario64_recomp_plus",
            folderName = "drmario64_recomp",
            gameIconUrl  = "https://raw.githubusercontent.com/theboy181/drmario64_recomp_plus/refs/heads/main/icons/512.png" },

        new { name = "Duke Nukem: Zero Hour",
            repository = "sonicdcer/DNZHRecomp",
            folderName = "DNZHRecompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/sonicdcer/DNZHRecomp/refs/heads/main/icons/512.png" },

        new { name = "Star Fox 64",
            repository = "sonicdcer/Starfox64Recomp",
            folderName = "Starfox64Recompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/sonicdcer/Starfox64Recomp/refs/heads/main/icons/512.png" },

        new  { name = "Banjo 64",
            repository = "BanjoRecomp/BanjoRecomp",
            folderName = "BanjoRecompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/BanjoRecomp/BanjoRecomp/refs/heads/main/icons/app.png" },
            
        new  { name = "Bomberman 64",
            repository = "RevoSucks/BM64Recomp",
            folderName = "BM64Recompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/RevoSucks/BM64Recomp/refs/heads/master/icons/512.png" },
    };

            var experimental = new List<object>
    {
        new { name = "Chameleon Twist",
            repository = "Rainchus/ChameleonTwist1-JP-Recomp",
            folderName = "ChameleonTwistRecompiled",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/c1f22f4c38899f51f1ed3ce20120bbd9.png" },

        new { name = "Mega Man 64",
            repository = "MegaMan64Recomp/MegaMan64Recompiled",
            folderName = "MegaMan64Recompiled",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/850618e22f83f152773d2a3e51168812.png" },

        new { name = "Quest 64",
            repository = "Rainchus/Quest64-Recomp",
            folderName = "Quest64Recompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/Rainchus/Quest64-Recomp/refs/heads/main/icons/512.png" },
    };

            var custom = new List<object>
    {
        new { name = "Zelda OoT (Ship of Harkinian)",
            repository = "harbourmasters/shipwright",
            folderName = "harbourmasters.shipofharkinian",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/d1cd0a8c9b28f58703a097d5a25534e3/32/256x256.png" },

        new { name = "Zelda MM (2 Ship 2 Harkinian)",
            repository = "harbourmasters/2ship2harkinian",
            folderName = "harbourmasters.2ship2harkinian",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/6c7dbdd98cd70f67f102524761f3b4d2/24/256x256.png" },

        new { name = "Star Fox 64 (Starship)",
            repository = "harbourmasters/starship",
            folderName = "harbourmasters.starship",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/dc2ee2a5add7154447a4644326e33386/32/256x256.png" },

        new { name = "Mario Kart 64 (SpaghettiKart)",
            repository = "harbourmasters/spaghettikart",
            folderName = "harbourmasters.spaghettikart",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon_thumb/5e5e0bd5ad7c2ca72b0c5ff8b6debbba.png" },

        new { name = "Super Mario 64 (Ghostship)",
            repository = "harbourmasters/ghostship",
            folderName = "harbourmasters.ghostship",
            gameIconUrl  = "https://raw.githubusercontent.com/HarbourMasters/Ghostship/refs/heads/develop/nx-logo.jpg" },

        new { name = "Perfect Dark",
            repository = "fgsfdsfgs/perfect_dark",
            folderName = "fgsfdsfgs.perfect_dark",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/64314c17210c549a854f1f1c7adce8b6/32/256x256.png" },

        new { name = "SM64 CoopDX",
            repository = "coop-deluxe/sm64coopdx",
            folderName = "coop-deluxe.sm64coopdx",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon_thumb/e3dd863ef4277e82f712a5bd8fefe7d7.png" }
    };

            return (standard, experimental, custom);
        }
    }
}
