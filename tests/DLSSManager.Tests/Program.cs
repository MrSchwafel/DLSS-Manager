using System.Security.Cryptography;
using System.Text;
using DLSSManager.Core;

var tests = new (string Name, Action<Sandbox> Run)[]
{
    ("Spielebibliothek speichert Bearbeitungen, Ignore-Status und bewusst leere Listen", s =>
    {
        var service = new GameLibraryService(s.Storage);
        Check(service.Read() is null);
        var entry = new GameLibraryEntry(Guid.NewGuid(), "Spiel", s.Installation.Root, "play.exe", "version.dll", "game.ico", true);
        service.Save([entry]);
        Check(new GameLibraryService(s.Storage).Read()!.SequenceEqual([entry]));
        var renamed = entry with { Title = "Neuer Titel" };
        service.Save([renamed]);
        Check(service.Read()!.Single() == renamed);
        service.Save([]);
        Check(service.Read() is { Length: 0 });
    }),
    ("Vorhandene Einstellungen aktivieren Launcher-Scan und Anti-Cheat-Filter standardmäßig", s =>
    {
        var library = new GameLibraryService(s.Storage);
        Check(library.ReadSettings() is { ScanLaunchersOnStartup: true, ExcludeAntiCheatGames: true });
        Directory.CreateDirectory(s.Storage);
        File.WriteAllText(Path.Combine(s.Storage, "settings-v1.json"), """{"ScanFolders":[]}""");
        Check(library.ReadSettings() is { ScanLaunchersOnStartup: true, ExcludeAntiCheatGames: true });
        library.SaveSettings(new([], ScanLaunchersOnStartup: false, ExcludeAntiCheatGames: false));
        Check(library.ReadSettings() is { ScanLaunchersOnStartup: false, ExcludeAntiCheatGames: false });
    }),
    ("Update-Einstellungen sind standardmäßig aus und werden unabhängig gespeichert", s =>
    {
        var library = new GameLibraryService(s.Storage);
        var settings = library.ReadSettings();
        Check(settings is { AutoUpdateDlls: false, AutoUpdateUnlocker: false, AutoUpdateOptiScaler: false, AutoUpdateIntervalMinutes: 60 });
        library.SaveSettings(settings with { AutoUpdateDlls = true, AutoUpdateOptiScaler = true, AutoUpdateIntervalMinutes = 15 });
        Check(library.ReadSettings() is { AutoUpdateDlls: true, AutoUpdateUnlocker: false, AutoUpdateOptiScaler: true, AutoUpdateIntervalMinutes: 15 });
    }),
    ("DLSSdb ordnet eindeutige Titel zu und leitet Frame-Empfehlungen ab", _ =>
    {
        var db = RtxDatabase.Parse("""
            {"data":[
              {"name":"Éclipse II","type":"Game","dlss super resolution":"NV, T","dlss multi frame generation":"NV, 4X","ray tracing":"Path Tracing"},
              {"name":"Éclipse","type":"Game","dlss multi frame generation":"NV, 2X"},
              {"name":"Collision!","type":"Game","dlaa":"Yes"},
              {"name":"Collision","type":"Game","dlss frame generation":"Yes"},
              {"name":"Tool","type":"App","dlaa":"Yes"}
            ]}
            """);
        var game = db.Find("Eclipse II: Complete Edition");
        Check(game is { RecommendedFrames: 3, Transformer: true } &&
            game.Features.HasFlag(RtxFeature.SR | RtxFeature.MFG | RtxFeature.RT | RtxFeature.PathTracing));
        Check(db.Find("Eclipse")?.RecommendedFrames == 1);
        Check(db.Find("Eclipse III") is null && db.Find("Collision") is null && db.Find("Tool") is null);
    }),
    ("DLSSdb verwendet ETag und behält den vollständigen Cache bei defekter Antwort", s =>
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            data = Enumerable.Range(0, 120).Select(n => new { name = $"Fixture {n}", type = "Game" })
        });
        using var handler = new RtxHandler(json);
        using var http = new HttpClient(handler);
        var service = new RtxDatabaseService(http, s.Storage);
        Check(service.ReadCached() is null);
        Check(service.RefreshAsync().GetAwaiter().GetResult() is { Database.Count: 120, Offline: false });
        Check(service.RefreshAsync().GetAwaiter().GetResult() is { Database.Count: 120, Offline: false } && handler.Conditional);
        Check(service.RefreshAsync().GetAwaiter().GetResult() is { Database.Count: 120, Offline: true });
        Check(service.ReadCached()?.Count == 120);
        Throws<OperationCanceledException>(() => service.RefreshAsync(new(true)).GetAwaiter().GetResult());
    }),
    ("INI-Editor validiert Expertenwerte", _ =>
    {
        Check(ModIniService.ParseExpert("# Hinweis\r\n XeSSFG | Mode = a=b\n").Single() == new IniSetting("XeSSFG", "Mode", "a=b"));
        foreach (var invalid in new[] { "fehlendes Trennzeichen", "[DLSSG]|Key=3", "DLSSG|#Kommentiert=3", "DLSSG|a|b=3" })
            Throws<ArgumentException>(() => ModIniService.ParseExpert(invalid));
    }),
    ("SM86-INI erhält UTF-16, Formatierung und unbekannte Daten und ist wiederherstellbar", s =>
    {
        const string before = "; Grüße\r\n[DLSSG]\r\n Optimized = 2  ; Hinweis\r\nUnknown=Bleibt\n[Other]\r\nX=1";
        const string after = "; Grüße\r\n[DLSSG]\r\n Optimized = 3  ; Hinweis\r\nUnknown=Bleibt\n[Other]\r\nX=1\r\n[Expert]\r\nFlag=a=b";
        byte[] original = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(before)];
        byte[] expected = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(after)];
        IniSetting[] changes = [new("dlssg", "optimized", "3"), new("Expert", "Flag", "a=b")];
        Check(ModIniService.ReadValues(original).Contains(new("DLSSG", "Optimized", "2")));
        Check(ModIniService.Apply(original, changes).SequenceEqual(expected));
        Check(ModIniService.Apply(expected, changes).SequenceEqual(expected));
        byte[] legacy = [.. Encoding.Latin1.GetBytes("; Grüße\n[DLSSG]\nOptimized=2\n")];
        Check(Encoding.Latin1.GetString(ModIniService.Apply(legacy, changes)).StartsWith("; Grüße\n", StringComparison.Ordinal));

        s.GameFile("Bin/play.exe", "fixture");
        var target = s.GameFile("Bin/dlssg_sm86.ini", "");
        var game = s.Installation with { Executables = ["Bin/play.exe"] };
        var work = Path.Combine(s.Storage, "ini-work");
        var tx = s.Transactions();
        File.WriteAllBytes(target, original);
        var input = ModIniService.Read(game, "Bin/play.exe");
        var id = ModIniService.Save(tx, game, "Bin/play.exe", input, changes, work)!.Value;
        Check(File.ReadAllBytes(target).SequenceEqual(expected));
        Throws<IOException>(() => ModIniService.Save(tx, game, "Bin/play.exe", input, changes, work));
        Check(ModIniService.Save(tx, game, "Bin/play.exe", expected, changes, work) is null);
        tx.Rollback(game, id);
        Check(File.ReadAllBytes(target).SequenceEqual(original));
        Check(Directory.GetFiles(work).Length == 0);
    }),
    ("SM86-Zuordnung, Update und Fallback überstehen Neustart und erhalten INI", s =>
    {
        var stock = Path.Combine(Environment.SystemDirectory, "version.dll");
        ModPackage Make(string folder, string marker)
        {
            foreach (var name in new[] { "version.dll", "dlssg_sm86/backend/sm86_backend.dll",
                         "dlssg_sm86/kernels/fg_kernels.dll", "dlssg_sm86/runtime/nvngx_dlssg.dll" })
            {
                var path = s.FixtureFile(folder + "/" + name, "");
                File.Copy(stock, path, overwrite: true);
                File.AppendAllText(path, marker);
            }
            s.FixtureFile(folder + "/dlssg_sm86.ini", marker);
            return ModPackage.Read(Path.Combine(s.Fixtures, folder), "version.dll");
        }

        s.GameFile("Bin/play.exe", "fixture");
        s.GameFile("Bin/other.exe", "fixture");
        var fg = s.GameFile("nvngx_dlssg.dll", "");
        File.Copy(stock, fg, overwrite: true);
        var game = s.Installation with { Executables = ["Bin/play.exe"] };
        const string exe = "Bin/play.exe";
        var firstPackage = Make("first", "one");
        var secondPackage = Make("second", "two");
        var snapshots = Path.Combine(s.Storage, "mod-snapshots");
        var service = new ManagedModService(s.Transactions(), snapshots);
        var first = service.Install(game, exe, "nvngx_dlssg.dll", firstPackage, "0.4.0", "0.4.0")!.Value;
        service = new ManagedModService(s.Transactions(), snapshots);
        Check(service.History(game, "Bin/other.exe").Single().Id == first);
        var nested = game with { Root = Path.Combine(game.Root, "Bin") };
        Check(service.History(nested, "play.exe").Single().Id == first);
        Check(service.Install(game, exe, "nvngx_dlssg.dll", firstPackage, "0.4.0", "0.4.0") is null);
        Check(service.Install(game, exe, "nvngx_dlssg.dll", firstPackage, "0.4.0", "0.4.0", newerOnly: true) is null);
        s.GameFile("Bin/dlssg_sm86.ini", "custom");
        var second = service.Install(game, exe, "nvngx_dlssg.dll", secondPackage, "0.4.1", "0.4.1", newerOnly: true)!.Value;
        Check(service.History(game, exe).Select(j => j.Id).SequenceEqual([second, first]));
        Check(s.ReadGame("Bin/dlssg_sm86.ini") == "custom");
        var fallback = service.Restore(game, exe, "nvngx_dlssg.dll", first)!.Value;
        Check(service.History(game, exe)[0].Mod is { UpdatesPaused: true, Version.ReleaseTag: "0.4.0" });
        Check(HashFile(Path.Combine(game.Root, "Bin/version.dll")) == firstPackage.Files[0].Sha256);
        Check(service.Install(game, exe, "nvngx_dlssg.dll", secondPackage, "0.4.1", "0.4.1", newerOnly: true) is null);
        Throws<IOException>(() => s.Transactions().Rollback(game, second));
        var resume = service.ResumeUpdates(game, exe)!.Value;
        Check(service.History(game, exe)[0].Mod is { UpdatesPaused: false, Version.ReleaseTag: "0.4.0" });
        s.Transactions().Rollback(game, resume);
        s.Transactions().Rollback(game, fallback);
        Check(service.History(game, exe)[0].Id == second);
        Check(service.Remove(game, exe) is not null);
        Check(!File.Exists(Path.Combine(game.Root, "Bin/version.dll")) && s.ReadGame("Bin/dlssg_sm86.ini") == "custom");
        Check(service.History(game, exe)[0].Mod is { Version: null });
    }),
    ("Mod-INI-Schema und Profile erhalten Vorlagen, Kommentare und die Überschreibungsreihenfolge", s =>
    {
        var profiles = new ModIniProfiles(Path.Combine(s.Storage, "profiles"), recommendedFrames: 5);
        var mod = IniMod.Sm86;
        var root = s.Installation.Root;
        var original = ModIniProfiles.Defaults(mod);
        var schema = ModIniSchema.Read(mod, original);
        var engine = schema.Single(f => f.Default.Matches("FrameGeneration", "Engine"));
        Check(engine.Description.Contains("XeSS-FG") && engine.Choices.Select(c => c.Value).SequenceEqual(["DLSS", "XeSS"]));
        var opti = ModIniSchema.Read(IniMod.OptiScaler, ModIniProfiles.Defaults(IniMod.OptiScaler));
        Check(opti.Length > 200 && opti.Count(f => f.Important) == 29);
        Check(opti.Single(f => f.Default.Matches("Sharpness", "Sharpness")).Number is { Min: 0, Max: 1.3, AllowsAuto: true });
        Check(opti.Single(f => f.Default.Matches("FSRFG", "FPTVarianceFactor")).Number is { Min: 0, Max: 1 });
        Check(opti.Single(f => f.Default.Matches("FSRFG", "AllowAsync")).Choices.Select(c => c.Value).SequenceEqual(["auto", "true", "false"]));
        Check(ModIniService.ReadValues(profiles.Merge(mod, root, original)).Contains(new("FrameGeneration", "MaxGeneratedFrames", "5")));

        profiles.Save(mod, null, new([new("Logging", "Level", "2"), new("FrameGeneration", "MaxGeneratedFrames", "4")], []));
        profiles.Save(mod, root, new([new("Logging", "Level", "0"), new("FrameGeneration", "Engine", "XeSS")],
            [new("logging", "level", "3"), new("Runtime", "Path", @"C:\Runtime\nvngx_dlssg.dll")]));
        var reloaded = new ModIniProfiles(Path.Combine(s.Storage, "profiles"), recommendedFrames: 5);
        var merged = ModIniService.ReadValues(reloaded.Merge(mod, root, original));
        Check(merged.Contains(new("Logging", "Level", "3")) && merged.Contains(new("FrameGeneration", "MaxGeneratedFrames", "3")));
        Check(merged.Contains(new("Runtime", "Path", @"C:\Runtime\nvngx_dlssg.dll")));
        reloaded.Save(mod, root, IniProfile.Empty);
        Check(!reloaded.Read(mod, root).HasOverrides);
        merged = ModIniService.ReadValues(reloaded.Merge(mod, root, original));
        Check(merged.Contains(new("Logging", "Level", "2")) && merged.Contains(new("FrameGeneration", "MaxGeneratedFrames", "4")));
        Throws<ArgumentException>(() => profiles.Save(mod, root, new([], [new("FrameGeneration", "Optimized", "2")])));
        Throws<ArgumentException>(() => profiles.Save(mod, root, new([], [new("DLSSG", "Preset", "Auto")])));

        var legacy = Encoding.UTF8.GetBytes("[FrameGeneration]\n; Historical comment\nOptimized=2\n[DLSSG]\nPreset=Auto\n");
        var normalized = Encoding.UTF8.GetString(ModIniProfiles.Merge(mod, legacy, IniProfile.Empty, IniProfile.Empty));
        Check(normalized == "[FrameGeneration]\n; Historical comment\n[DLSSG]\nPreset=A\n");

        var source = Encoding.UTF8.GetBytes("[A]\nValue=1\n\n; Section B heading\n[B]\n; Keep this help\nValue=2");
        var output = Encoding.UTF8.GetString(ModIniService.Apply(source, [new("a", "Added", "yes")]));
        Check(output == "[A]\nValue=1\nAdded=yes\n\n; Section B heading\n[B]\n; Keep this help\nValue=2");
    }),
    ("SM86 schreibt neue Paketbasis mit Profilen bei Update, Fallback und Neuinstallation", s =>
    {
        var stock = Path.Combine(Environment.SystemDirectory, "version.dll");
        var fg = s.GameFile("nvngx_dlssg.dll", "");
        File.Copy(stock, fg, overwrite: true);
        s.GameFile("fixture.exe", "");
        var game = s.Installation;
        var profileRoot = Path.Combine(s.Storage, "profiles");
        var profiles = new ModIniProfiles(profileRoot, recommendedFrames: 5);
        var service = new ManagedModService(s.Transactions(), Path.Combine(s.Storage, "snapshots"), profiles);
        var first = Make("first", "v1");
        var second = Make("second", "v2");
        var firstId = service.Install(game, "fixture.exe", "nvngx_dlssg.dll", first, "v1", "1.0")!.Value;
        Check(s.ReadGame("dlssg_sm86.ini")!.Contains("MaxGeneratedFrames=5"));
        profiles.Save(IniMod.Sm86, null, new([new("Logging", "Level", "2")], []));
        profiles.Save(IniMod.Sm86, game.Root, new([new("FrameGeneration", "MaxGeneratedFrames", "4")], []));
        service.Install(game, "fixture.exe", "nvngx_dlssg.dll", second, "v2", "2.0", newerOnly: true);
        Check(s.ReadGame("dlssg_sm86.ini") is "; v2\n[FrameGeneration]\nEngine=DLSS\nMaxGeneratedFrames=4\n[Logging]\nLevel=2\nNewKey=v2\n");
        service.Restore(game, "fixture.exe", "nvngx_dlssg.dll", firstId);
        Check(s.ReadGame("dlssg_sm86.ini")!.Contains("NewKey=v1"));
        Check(Encoding.UTF8.GetString(service.InstalledIniBase(game, "fixture.exe")!).Contains("MaxGeneratedFrames=3"));
        service.Remove(game, "fixture.exe");
        Check(s.ReadGame("dlssg_sm86.ini") is null && new ModIniProfiles(profileRoot).Read(IniMod.Sm86, game.Root).HasOverrides);
        service.Install(game, "fixture.exe", "nvngx_dlssg.dll", second, "v2", "2.0");
        Check(s.ReadGame("dlssg_sm86.ini")!.Contains("MaxGeneratedFrames=4"));

        ModPackage Make(string folder, string version)
        {
            foreach (var name in new[] { "version.dll", "dlssg_sm86/backend/sm86_backend.dll",
                "dlssg_sm86/kernels/fg_kernels.dll", "dlssg_sm86/runtime/nvngx_dlssg.dll" })
                File.Copy(stock, s.FixtureFile(folder + "/" + name, ""), overwrite: true);
            s.FixtureFile(folder + "/dlssg_sm86.ini", $"; {version}\n[FrameGeneration]\nEngine=DLSS\nMaxGeneratedFrames=3\n[Logging]\nLevel=1\nNewKey={version}\n");
            return ModPackage.Read(Path.Combine(s.Fixtures, folder), "version.dll");
        }
    }),
    ("SM86-Download prüft Archivhash, Cache und Pfadausbruch", s =>
    {
        byte[] BuildPackage(bool unsafePath)
        {
            using var memory = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var path in new[] { "version.dll", "dlssg_sm86/backend/sm86_backend.dll",
                             "dlssg_sm86/kernels/fg_kernels.dll", "dlssg_sm86/runtime/nvngx_dlssg.dll" })
                {
                    using var target = archive.CreateEntry(path).Open();
                    using var source = File.OpenRead(Path.Combine(Environment.SystemDirectory, "version.dll"));
                    source.CopyTo(target);
                }
                using (var ini = new StreamWriter(archive.CreateEntry("dlssg_sm86.ini").Open()))
                    ini.Write("[General]");
                if (unsafePath) archive.CreateEntry("../escape.dll");

            }
            return memory.ToArray();
        }

        using var handler = new ModReleaseHandler(BuildPackage(false));
        using var http = new HttpClient(handler);
        var service = new ModDownloadService(http, s.Storage);
        var first = service.GetLatestAsync().GetAwaiter().GetResult();
        Check(first.Tag == "0.4.1" && first.Proxies.SequenceEqual(["version.dll"]));
        var cached = service.GetLatestAsync().GetAwaiter().GetResult();
        Check(handler.Downloads == 1 && File.Exists(Path.Combine(cached.Directory, "version.dll")));
        File.WriteAllText(Directory.GetFiles(s.Storage, "*.zip").Single(), "corrupt");
        var unsafeArchive = BuildPackage(true);
        handler.DigestOverride = Convert.ToHexString(SHA256.HashData(unsafeArchive));
        Throws<IOException>(() => service.GetLatestAsync().GetAwaiter().GetResult());
        Check(handler.Downloads == 2 && Directory.GetFiles(s.Storage, "*.tmp").Length == 0);
        handler.DigestOverride = null;
        handler.Archive = unsafeArchive;
        Throws<IOException>(() => service.GetLatestAsync().GetAwaiter().GetResult());
        var directories = Directory.GetDirectories(s.Storage);
        if (File.Exists(Path.Combine(s.Storage, "escape.dll")) || directories.Length != 2)
            throw new InvalidOperationException($"Cache-Verzeichnisse: {string.Join(", ", directories)}");
    }),
    ("SM86 installiert und repariert, schützt fremde Proxys und erhält angepasste INIs", s =>
    {
        var stock = Path.Combine(Environment.SystemDirectory, "version.dll");
        var packageRoot = Path.Combine(s.Fixtures, "sm86");
        foreach (var relative in new[] { "version.dll", "dlssg_sm86/backend/sm86_backend.dll",
                     "dlssg_sm86/kernels/fg_kernels.dll", "dlssg_sm86/runtime/nvngx_dlssg.dll" })
        {
            var file = s.FixtureFile("sm86/" + relative, "");
            File.Copy(stock, file, overwrite: true);
        }
        s.FixtureFile("sm86/dlssg_sm86.ini", "[General]\nEnabled=1");
        s.GameFile("Bin/play.exe", "fixture");
        var fg = s.GameFile("nvngx_dlssg.dll", "");
        File.Copy(stock, fg, overwrite: true);
        var game = s.Installation with { Executables = ["Bin/play.exe"] };
        var package = ModPackage.Read(packageRoot, "version.dll");
        Check(ModPackage.ReadProxies(packageRoot).SequenceEqual(["version.dll"]));
        var service = new ModInstallationService(s.Transactions());
        var proxy = s.GameFile("Bin/version.dll", "fremd");
        Throws<IOException>(() => service.Install(game, "Bin/play.exe", "nvngx_dlssg.dll", package));
        Check(File.ReadAllText(proxy) == "fremd");
        File.Delete(proxy);
        Check(service.Install(game, "Bin/play.exe", "nvngx_dlssg.dll", package) is not null);
        Check(service.Install(game, "Bin/play.exe", "nvngx_dlssg.dll", package, package) is null);
        var backend = Path.Combine(game.Root, "Bin/dlssg_sm86/backend/sm86_backend.dll");
        File.Delete(backend);
        s.GameFile("Bin/dlssg_sm86/user.txt", "Benutzerdatei");
        s.GameFile("Bin/dlssg_sm86.ini", "benutzerdefiniert");
        Check(service.Install(game, "Bin/play.exe", "nvngx_dlssg.dll", package, package) is not null);
        Check(File.Exists(backend));
        s.GameFile("Bin/version.dll", "fremd");
        Check(service.Uninstall(game, "Bin/play.exe", package) is not null);
        Check(!File.Exists(backend) && File.ReadAllText(proxy) == "fremd");
        Check(s.ReadGame("Bin/dlssg_sm86.ini") == "benutzerdefiniert");
        Check(s.ReadGame("Bin/dlssg_sm86/user.txt") == "Benutzerdatei");
    }),
    ("Rollback entfernt leere Transaktionsordner und erhält fremde Dateien", s =>
    {
        var payload = s.FixtureFile("payload", "fixture");
        var tx = s.Transactions();
        var id = tx.Apply(s.Installation,
            [new("one/two/file.dll", null, payload, Hash("fixture")),
             new("other/file.dll", null, payload, Hash("fixture"))], createTargetDirectories: true);
        s.GameFile("one/user.txt", "behalten");
        tx.Rollback(s.Installation, id);
        Check(!Directory.Exists(Path.Combine(s.Installation.Root, "one/two")));
        Check(!Directory.Exists(Path.Combine(s.Installation.Root, "other")));
        Check(s.ReadGame("one/user.txt") == "behalten");
    }),
    ("GitHub-Releaseprüfung sendet Clientkennung und meldet Rate-Limits", _ =>
    {
        using var handler = new ReleaseHandler();
        using var http = new HttpClient(handler);
        var update = UpdateService.CheckManagerAsync(http, "1.0.0", ReleaseChannel.Stable).GetAwaiter().GetResult();
        Check(update?.Tag == "v1.2.0" && handler.ValidRequest);
        Throws<IOException>(() => UpdateService.CheckManagerAsync(http, "1.0.0", ReleaseChannel.Stable)
            .GetAwaiter().GetResult());
    }),
    ("Manager-Releases beachten Kanal, Preview-Rang und Downgrade-Schutz", _ =>
    {
        ReleaseCandidate[] candidates =
        [
            new("v1.1.0-beta.9", false, true), new("v1.1.0-beta.10", false, true),
            new("v1.1.0-rc", false, true), new("v1.0.0", false, false),
            new("v2.0.0", true, false), new("v3.0.0-beta", false, false),
            new("v4.0.0", false, true), new("v99.0.0-alpha", false, true)
        ];
        Check(UpdateService.SelectUpdate("v1.0.0", ReleaseChannel.Beta, candidates)?.Tag == "v1.1.0-rc");
        Check(UpdateService.SelectUpdate("v1.1.0-beta.9", ReleaseChannel.Beta, candidates.Take(2))?.Tag == "v1.1.0-beta.10");
        Check(UpdateService.SelectUpdate("v1.0.0", ReleaseChannel.Stable, candidates) is null);
        Check(UpdateService.SelectUpdate("1.1.0-rc", ReleaseChannel.Stable,
            [.. candidates, new("1.1.0.0", false, false)])?.Tag == "1.1.0.0");
        Check(UpdateService.SelectUpdate("1.1.0.0", ReleaseChannel.Beta, candidates) is null);
    }),
    ("DLL-Katalog filtert, sortiert und verwendet den letzten gültigen ETag-Cache", s =>
    {
        var valid = new[] { CatalogItem("3.9"), CatalogItem("3.10") };
        var manifest = System.Text.Json.JsonSerializer.Serialize(new
        {
            dlss = new[] { valid[0], valid[1], CatalogItem("4.0", dev: true), CatalogItem("5.0", host: "fixture.invalid") },
            dlss_g = valid,
            dlss_d = valid
        });
        using var handler = new CatalogHandler(manifest);
        using var http = new HttpClient(handler);
        var catalog = new DllCatalogService(http, s.Storage);
        var initial = catalog.RefreshAsync().GetAwaiter().GetResult();
        Check(initial is { Entries.Length: 6, Notice: null });
        Check(initial.Entries.First(e => e.Kind == "SR").Version == new Version(3, 10, 0, 0));
        Check(catalog.RefreshAsync().GetAwaiter().GetResult().Notice is null && handler.Conditional);
        Check(catalog.RefreshAsync().GetAwaiter().GetResult().Notice is not null);
        Check(catalog.ReadCached().SequenceEqual(initial.Entries));
    }),
    ("Numerische DLL-Versionen und Updateentscheidungen", _ =>
    {
        Check(DllVersionService.IsUpgrade("3.9", "3.10.0"));
        Check(!DllVersionService.IsUpgrade("3.10", "3.9"));
        Check(!DllVersionService.IsUpgrade("3.10", "3.10.0.0"));
        Check(!DllVersionService.IsUpgrade(null, "3.10"));
        Check(DllVersionService.Parse("3.1-beta") is null && DllVersionService.Parse("1.2.3.4.5") is null);
    }),
    ("PE-Inventar unterscheidet DLL, Proxy-Kandidat und ungültigen Inhalt", s =>
    {
        var dll = s.GameFile("nvngx_dlss.dll", "");
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), dll, overwrite: true);
        var bogus = s.GameFile("nvngx_dlssd.dll", "falscher Inhalt");
        s.GameFile("dxgi.dll", "fremd");
        s.GameFile("dlssg_sm86/backend/sm86_backend.dll", "fixture");
        var scanner = new GameScanner(s.Storage);
        var options = new ScanOptions([new("Spiel", s.Installation.Root, "Manuell")], [], []);
        var findings = scanner.Scan(options).Games.Single().Dlls;
        var result = findings.Single(d => d.Kind == "SR").Metadata!;
        Check(result.IsDll && result.Machine == System.Reflection.PortableExecutable.Machine.Amd64);
        Check(result.FileVersion is not null && result.ProductVersion is not null && result.Sha256 == HashFile(dll));
        Check(findings.Single(d => d.Path == bogus).Metadata is { IsDll: false });
        Check(findings.Single(d => d.Kind == "Proxy").Metadata is { IsDll: false });
        Check(findings.Single(d => d.Kind == "SM86").ModComponent);
        Check(scanner.Scan(options).Metrics.CacheHits == 4);
        Throws<OperationCanceledException>(() => DllDetectionService.Inspect(dll, new(true)));
    }),
    ("Geprüfter Download, Cache-Reparatur und manipuliertes Archiv", s =>
    {
        var dll = Path.Combine(Environment.SystemDirectory, "version.dll");
        using var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("pkg/nvngx_dlss.dll").Open())
        using (var source = File.OpenRead(dll))
            source.CopyTo(entry);
        var archive = memory.ToArray();
        using var handler = new PackageHandler(archive);
        using var client = new HttpClient(handler);
        var package = new DllPackage(new("https://fixture.invalid/package.zip"), "nvngx_dlss.dll",
            Convert.ToHexString(SHA256.HashData(archive)), HashFile(dll));
        var service = new DllDownloadService(client, s.Storage);
        var cached = service.GetAsync(package).GetAwaiter().GetResult();
        Check(service.GetAsync(package).GetAwaiter().GetResult() == cached && handler.Requests == 1);
        File.WriteAllText(cached, "corrupt");
        Throws<IOException>(() => service.GetAsync(package with { ArchiveSha256 = Hash("other") }).GetAwaiter().GetResult());
        Check(File.ReadAllText(cached) == "corrupt" && Directory.GetDirectories(s.Storage).Length == 0);
        Check(service.GetAsync(package).GetAwaiter().GetResult() == cached && handler.Requests == 3);
        Check(HashFile(cached) == package.DllSha256);
    }),
    ("DLL-Tausch erhält das erste Original und erlaubt Wiederherstellung und Rollback", s =>
    {
        const string name = "nvngx_dlssg.dll";
        var target = s.GameFile(name, "");
        var stock = Path.Combine(Environment.SystemDirectory, "version.dll");
        File.Copy(stock, target, overwrite: true);
        var payload = s.FixtureFile("payload.dll", "");
        File.Copy(stock, payload, overwrite: true);
        File.AppendAllText(payload, "fixture");
        var initial = HashFile(target);
        var update = HashFile(payload);
        var tx = s.Transactions();
        var replacement = new DllReplacementService(tx);
        Throws<IOException>(() => replacement.Replace(s.Installation, name, Hash("stale"), payload, update));
        Check(!File.Exists(target + ".bak"));
        var first = replacement.Replace(s.Installation, name, initial, payload, update)!.Value;
        Check(HashFile(target + ".bak") == initial && HashFile(target) == update);
        Check(replacement.Replace(s.Installation, name, update, payload, update) is null);
        File.AppendAllText(payload, "second");
        var secondHash = HashFile(payload);
        var second = replacement.Replace(s.Installation, name, update, payload, secondHash)!.Value;
        Check(HashFile(target + ".bak") == initial && HashFile(target) == secondHash);
        var restore = replacement.RestoreOriginal(s.Installation, name, secondHash, initial)!.Value;
        Check(HashFile(target) == initial && HashFile(target + ".bak") == initial);
        tx.Rollback(s.Installation, restore);
        tx.Rollback(s.Installation, second);
        tx.Rollback(s.Installation, first);
        Check(HashFile(target) == initial && !File.Exists(target + ".bak"));
    }),
    ("Original-Backup löschen schützt aktive DLL, fremde Backups und geänderte Sicherungen", s =>
    {
        const string name = "nvngx_dlss.dll";
        var target = s.GameFile(name, "active");
        var backup = new FileInfo(s.GameFile(name + ".bak", "original"));
        var other = s.GameFile("nvngx_dlssg.dll.bak", "other");
        var tx = s.Transactions();
        var size = backup.Length;
        var saved = backup.LastWriteTimeUtc;
        Throws<IOException>(() => tx.DeleteOriginalBackup(s.Installation, name, size + 1, saved));
        Throws<IOException>(() => tx.DeleteOriginalBackup(s.Installation, @"..\foreign.dll", size, saved));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Throws<OperationCanceledException>(() => tx.DeleteOriginalBackup(s.Installation, name, size, saved, canceled.Token));
        Check(File.Exists(backup.FullName));
        tx.DeleteOriginalBackup(s.Installation, name, size, saved);
        tx.DeleteOriginalBackup(s.Installation, name, size, saved);
        Check(!File.Exists(backup.FullName) && File.ReadAllText(target) == "active" && File.ReadAllText(other) == "other");
    }),
    ("Batch-Tausch prüft alle Ziele vor dem Schreiben und sichert XeSS und FSR", s =>
    {
        var stock = Path.Combine(Environment.SystemDirectory, "version.dll");
        const string xess = "libxess.dll";
        const string fsr = "amd_fidelityfx_dx12.dll";
        var first = s.GameFile(xess, "");
        var second = s.GameFile(fsr, "");
        var source = s.FixtureFile(xess, "");
        foreach (var path in new[] { first, second, source }) File.Copy(stock, path, overwrite: true);
        File.AppendAllText(source, "fixture");
        var local = new LocalDllCatalog(Path.Combine(s.Storage, "catalog"));
        var entry = local.Import(source, CancellationToken.None);
        Check(local.Read(CancellationToken.None).Single() == entry);
        Check(DllDownloadService.VerifyCatalogFile(entry.LocalPath!, entry).Sha256 == entry.Sha256);

        var original = HashFile(first);
        var changes = new DllReplacementService(s.Transactions());
        var update = new DllReplacementService.Update(xess, original, entry.LocalPath!, entry.Sha256!);
        Throws<IOException>(() => changes.ReplaceAll(s.Installation, [update, update with { RelativePath = fsr, CurrentHash = Hash("stale") }]));
        Check(HashFile(first) == original && !File.Exists(first + ".bak"));
        Check(changes.ReplaceAll(s.Installation, [update, update with { RelativePath = fsr }]) is not null);
        Check(HashFile(first + ".bak") == original && HashFile(second + ".bak") == original);
        Check(changes.RestoreAll(s.Installation, [(xess, entry.Sha256!, original), (fsr, entry.Sha256!, original)]) is not null);
        Check(HashFile(first) == original && HashFile(second) == original);
    }),
    ("OptiScaler beansprucht nur freie Proxy-Pfade und entfernt eigene Dateien anhand des Journals", s =>
    {
        var source = s.FixtureFile("OptiScaler.dll", "");
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), source, overwrite: true);
        s.GameFile("fixture.exe", "");
        var occupied = s.GameFile("version.dll", "Fremdproxy");
        var local = new LocalDllCatalog(Path.Combine(s.Storage, "catalog"));
        var package = local.Import(source, CancellationToken.None);
        var service = new OptiScalerService(s.Transactions());
        Throws<IOException>(() => service.Install(s.Installation, "fixture.exe", package, "version.dll", CancellationToken.None));
        Check(File.ReadAllText(occupied) == "Fremdproxy");
        var id = service.Install(s.Installation, "fixture.exe", package, "dxgi.dll", CancellationToken.None);
        Check(service.Installed(s.Installation.Root)?.Id == id);
        Throws<IOException>(() => service.Install(s.Installation, "fixture.exe", package, "winmm.dll", CancellationToken.None));
        service.Remove(s.Installation, id);
        Check(service.Installed(s.Installation.Root) is null && !File.Exists(Path.Combine(s.Installation.Root, "dxgi.dll")));
    }),
    ("OptiScaler entfernt den Proxy und bewahrt eine bearbeitete Konfiguration", s =>
    {
        s.GameFile("fixture.exe", "");
        var source = s.FixtureFile("OptiScaler.dll", "");
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), source, overwrite: true);
        var package = new LocalDllCatalog(Path.Combine(s.Storage, "catalog")).Import(source, CancellationToken.None);
        var support = Directory.CreateDirectory(package.LocalPath + ".files");
        File.WriteAllText(Path.Combine(support.FullName, "OptiScaler.ini"), "[Upscalers]\nDx12Upscaler=auto");
        var service = new OptiScalerService(s.Transactions());
        var id = service.Install(s.Installation, "fixture.exe", package, "d3d12.dll", CancellationToken.None);
        s.GameFile("OptiScaler.ini", "[Upscalers]\nDx12Upscaler=xess");
        service.Remove(s.Installation, id);
        Check(s.ReadGame("OptiScaler.ini")?.Contains("xess") == true);
        Check(!File.Exists(Path.Combine(s.Installation.Root, "d3d12.dll")));
    }),
    ("OptiScaler aktualisiert Paket und INI atomar und behält Spielprofile bei der Neuinstallation", s =>
    {
        var game = s.Installation;
        s.GameFile("fixture.exe", "");
        var original = "[Upscalers]\nDx12Upscaler=old-custom";
        s.GameFile("OptiScaler.ini", original);
        var catalog = new LocalDllCatalog(Path.Combine(s.Storage, "catalog"));
        var profiles = new ModIniProfiles(Path.Combine(s.Storage, "profiles"));
        var service = new OptiScalerService(s.Transactions(), profiles);
        var first = Make("first", "v1");
        var second = Make("second", "v2");
        var mod = IniMod.OptiScaler;
        profiles.Save(mod, null, new([new("Upscalers", "Dx12Upscaler", "xess")], []));
        var id = service.Install(game, "fixture.exe", first, "dxgi.dll", CancellationToken.None);
        Check(s.ReadGame("OptiScaler.ini")!.Contains("Dx12Upscaler=xess"));
        profiles.Save(mod, game.Root, new([new("Upscalers", "Dx12Upscaler", "dlss")], []));
        Check(service.Installed(game.Root)?.ComponentReleaseTag == "v1.0");
        Throws<IOException>(() => service.Install(game, "fixture.exe", second, "version.dll", CancellationToken.None, Guid.NewGuid()));
        var update = service.Install(game, "fixture.exe", second, "version.dll", CancellationToken.None, id);
        Check(service.Installed(game.Root)?.ComponentReleaseTag == "v2.0");
        Check(update != id && service.Installed(game.Root)?.Id == update && s.ReadGame("dxgi.dll") is null);
        Check(s.ReadGame("OptiScaler.ini") is "; v2\n[Upscalers]\nDx12Upscaler=dlss\nNewKey=v2\n");
        var hash = service.Installed(game.Root)!.IniBase!;
        Check(Encoding.UTF8.GetString(profiles.ReadOptiBase(hash)).Contains("Dx12Upscaler=auto"));
        profiles.Save(mod, game.Root, IniProfile.Empty);
        ModIniService.Restore(s.Transactions(), game, "fixture.exe", mod, profiles.ReadOptiBase(hash), profiles,
            Path.Combine(s.Storage, "ini-work"), CancellationToken.None);
        Check(s.ReadGame("OptiScaler.ini")!.Contains("Dx12Upscaler=xess"));
        profiles.Save(mod, game.Root, new([], [new("Upscalers", "Dx12Upscaler", "ffx")]));
        service.Remove(game, update);
        Check(service.Installed(game.Root) is null && s.ReadGame("version.dll") is null && s.ReadGame("OptiScaler.ini") == original);
        service.Install(game, "fixture.exe", second, "version.dll", CancellationToken.None);
        Check(s.ReadGame("OptiScaler.ini")!.Contains("Dx12Upscaler=ffx"));

        DllCatalogEntry Make(string folder, string marker)
        {
            var source = s.FixtureFile(folder + "/OptiScaler.dll", "");
            File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), source, overwrite: true);
            File.AppendAllText(source, marker);
            var package = catalog.Import(source, CancellationToken.None, $"OptiScaler {marker}.0", DateTimeOffset.UnixEpoch);
            var support = Directory.CreateDirectory(package.LocalPath + ".files");
            File.WriteAllText(Path.Combine(support.FullName, "OptiScaler.ini"), $"; {marker}\n[Upscalers]\nDx12Upscaler=auto\nNewKey={marker}\n");
            return package;
        }
    }),
    ("Launcher-Scan wählt GTAIV statt Starthelfer und lässt mehrdeutige Treffer offen", _ =>
    {
        var root = new ScanRoot("Grand Theft Auto IV: The Complete Edition", @"F:\Games\Grand Theft Auto IV", "Steam");
        var game = new ScanGame(root, null, [@"F:\Games\Grand Theft Auto IV\Redistributables\Social-Club-Setup.exe",
            @"F:\Games\Grand Theft Auto IV\GTAIV\PlayGTAIV.exe", @"F:\Games\Grand Theft Auto IV\GTAIV\GTAIV.exe",
            @"F:\Games\Grand Theft Auto IV\GTAIV\gtaEncoder.exe"], []);
        Check(GameExecutableSelection.FromScan(game) == game.Alternatives[2]);
        Check(GameExecutableSelection.FromScan(game with { Executable = game.Alternatives[1] }) == game.Alternatives[1]);
        Check(GameExecutableSelection.FromScan(game with { Alternatives = ["Sniper5_vulkan.exe", "Sniper5_dx12.exe"] }) is null);
    }),
    ("Launcher-Scan wählt eurotrucks2.exe statt des Sync-Helfers", _ =>
    {
        var game = new ScanGame(new("Euro Truck Simulator 2", @"F:\Games\Euro Truck Simulator 2", "Steam"), null,
            [@"F:\Games\Euro Truck Simulator 2\ets2sync_helper.exe",
             @"F:\Games\Euro Truck Simulator 2\bin\win_x64\eurotrucks2.exe"], []);
        Check(GameExecutableSelection.FromScan(game) == game.Alternatives[1]);
        Check(GameExecutableSelection.FromScan(game with { Alternatives = [game.Alternatives[0]] }) is null);
    }),
    ("Launcher-Scan bevorzugt die Spiel-Binaries von LS25 und GTA Definitive Edition", _ =>
    {
        var farm = new ScanGame(new("Farming Simulator 25", @"F:\Games\FS25", "Steam"), null,
            ["dedicatedServer.exe", "FarmingSimulator2025.exe", @"x64\FarmingSimulator2025Game.exe"], []);
        Check(GameExecutableSelection.FromScan(farm) == farm.Alternatives[2]);
        foreach (var (bootstrap, runtime) in new[] { ("PlayGTA3.exe", "LibertyCity.exe"),
                     ("PlayGTAViceCity.exe", "ViceCity.exe"), ("PlayGTASanAndreas.exe", "SanAndreas.exe") })
        {
            var gta = new ScanGame(new("GTA Definitive Edition", @"F:\Games\GTA", "Steam"), null,
                [bootstrap, @"Gameface\Binaries\Win64\" + runtime, @"Engine\Extras\Redist\UE4PrereqSetup_x64.exe"], []);
            Check(GameExecutableSelection.FromScan(gta) == gta.Alternatives[1]);
        }
    }),
    ("Xbox-Erkennung überspringt Spielstände und Ordner ohne Spielkonfiguration", s =>
    {
        s.FixtureFile("XboxGames/GameSave/wgs/data", "save");
        s.FixtureFile("XboxGames/Other/readme.txt", "metadata");
        s.FixtureFile("XboxGames/Game/Content/MicrosoftGame.config", """<Game><ExecutableList><Executable Name="Game.exe"/></ExecutableList></Game>""");
        var games = WindowsLauncherDiscovery.XboxGames(Path.Combine(s.Fixtures, "XboxGames"), _ => { }, default).ToArray();
        Check(games is [{ Title: "Game", Executable: "Game.exe" }]);
    }),
    ("Launcher-Pfadsegmente und DLL-Versionsformat", _ =>
    {
        Check(LauncherLabels.FromPath(@"D:\STEAMLIBRARY\common\Game") == "Steam");
        Check(LauncherLabels.FromPath("D:/Epic Games/Game") == "Epic");
        Check(LauncherLabels.FromPath(@"D:\FakeXboxGames\Game") == "Lokal");
        Check(DllVersionService.Parse("310, 9, 16, 0") == new Version(310, 9, 16, 0));
    }),
    ("Laufwerksinventar trennt Spiele und ordnet Anti-Cheat nur dem betroffenen Ordner zu", s =>
    {
        s.GameFile("First/fixture.exe", "");
        s.GameFile("First/bin/nvngx_dlss.dll", "fixture");
        s.GameFile("First/EasyAntiCheat/EasyAntiCheat.exe", "fixture");
        s.GameFile("Second/fixture.exe", "");
        s.GameFile("Second/libxess.dll", "fixture");
        var root = new ScanRoot("Drive", s.Installation.Root, "Laufwerk");
        var scanned = new GameScanner(s.Storage).Scan(new([root], [], ["EasyAntiCheat.exe"]));
        Check(scanned.Games.Length == 2);
        var first = scanned.Games.Single(g => g.Root.Title == "First");
        var second = scanned.Games.Single(g => g.Root.Title == "Second");
        Check(first.AntiCheat!.SequenceEqual(["Easy Anti-Cheat"]) && second.AntiCheat is { Length: 0 });
        Throws<IOException>(() => GameScanner.RequireModifiable(first.Root.Path, CancellationToken.None));
        GameScanner.RequireModifiable(first.Root.Path, CancellationToken.None, first.AntiCheat);
        s.GameFile("First/ACE-BASE.sys", "fixture");
        Throws<IOException>(() => GameScanner.RequireModifiable(first.Root.Path, CancellationToken.None, first.AntiCheat));
        GameScanner.RequireModifiable(second.Root.Path, CancellationToken.None);
    }),
    ("Dateien ersetzen, anlegen, entfernen und wiederherstellen", s =>
    {
        s.GameFile("a.dll", "vorher");
        s.GameFile("c.ini", "behalten");
        var payload = s.FixtureFile("payload", "nachher");
        var tx = s.Transactions();
        var id = tx.Apply(s.Installation, [new("a.dll", Hash("vorher"), payload, Hash("nachher")),
            new("b.dll", null, payload, Hash("nachher")), new("c.ini", Hash("behalten"), null, null)]);
        Check(s.ReadGame("a.dll") == "nachher" && s.ReadGame("b.dll") == "nachher" && s.ReadGame("c.ini") is null);
        tx.Rollback(s.Installation, id);
        Check(s.ReadGame("a.dll") == "vorher" && s.ReadGame("b.dll") is null && s.ReadGame("c.ini") == "behalten");
        tx.Rollback(s.Installation, id);
    }),
    ("Falscher Inhalt, Pfadausbruch und NTFS-Streams werden abgelehnt", s =>
    {
        s.GameFile("a.dll", "vorher");
        var payload = s.FixtureFile("payload", "nachher");
        var tx = s.Transactions();
        foreach (var name in new[] { "a.dll", @"..\outside", "a.dll:stream", "NUL.txt", "a.dll." })
            Throws<IOException>(() => tx.Apply(s.Installation, [new(name, null, payload, Hash("nachher"))]));
        Check(s.ReadGame("a.dll") == "vorher" && tx.Pending().Length == 0);
    }),
    ("Exklusive Dateisperre verhindert Sicherung und Austausch", s =>
    {
        var target = s.GameFile("a.dll", "vorher");
        var payload = s.FixtureFile("payload", "nachher");
        using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Throws<IOException>(() => s.Transactions().Apply(s.Installation,
                [new("a.dll", Hash("vorher"), payload, Hash("nachher"))]));
        Check(s.ReadGame("a.dll") == "vorher");
    }),
    ("Zweiter Zugriff während Transaktion wird blockiert", s =>
    {
        var payload = s.FixtureFile("payload", "nachher");
        var guard = new TestGuard { OnCheck = n =>
        {
            if (n == 2) Throws<IOException>(() => s.Transactions().Pending());
        }};
        s.Transactions(guard).Apply(s.Installation, [new("a.dll", null, payload, Hash("nachher"))]);
        Check(s.ReadGame("a.dll") == "nachher");
    }),
    ("Abbruch nach erster Änderung und Wiederanlauf mit neuem Dienst", s =>
    {
        s.GameFile("a.dll", "vorher");
        var payload = s.FixtureFile("payload", "nachher");
        var guard = new TestGuard { OnCheck = n => { if (n >= 5) throw new IOException("Spiel läuft"); } };
        Throws<AggregateException>(() => s.Transactions(guard).Apply(s.Installation,
            [new("a.dll", Hash("vorher"), payload, Hash("nachher")), new("b.dll", null, payload, Hash("nachher"))]));
        Check(s.ReadGame("a.dll") == "nachher" && s.ReadGame("b.dll") is null);
        var restart = s.Transactions();
        var pending = restart.Pending().Single();
        restart.Rollback(s.Installation, pending.Id);
        Check(s.ReadGame("a.dll") == "vorher" && s.ReadGame("b.dll") is null && restart.Pending().Length == 0);
        Check(!Directory.EnumerateFiles(s.Installation.Root, "*.tmp").Any());
    }),
    ("Rollback schützt Fremdänderungen", s =>
    {
        var payload = s.FixtureFile("payload", "nachher");
        var tx = s.Transactions();
        var id = tx.Apply(s.Installation, [new("a.dll", null, payload, Hash("nachher"))]);
        s.GameFile("a.dll", "fremd");
        Throws<IOException>(() => tx.Rollback(s.Installation, id));
        Check(s.ReadGame("a.dll") == "fremd");
    }),
    ("Steam-App 730 wird auch ohne lokale Anti-Cheat-Datei als VAC-geschützt erkannt", s =>
    {
        s.FixtureFile("steam/steamapps/appmanifest_730.acf", """ "AppState" { "installdir" "Counter-Strike Global Offensive" "name" "Counter-Strike 2" } """);
        s.FixtureFile("steam/steamapps/common/Counter-Strike Global Offensive/game/bin/win64/cs2.exe", "fixture");
        var steam = Path.Combine(s.Fixtures, "steam");
        var roots = LauncherDiscovery.Steam(steam, _ => { }, default).ToArray();
        var game = new GameScanner(s.Storage).Scan(new(roots, [], [])).Games.Single();
        Check(game.AntiCheat is ["VAC"]);
    }),
    ("Steam mit zwei Bibliotheken und Epic mit geprüfter Startdatei", s =>
    {
        static string Valve(string value) => value.Replace("\\", "\\\\");
        var steam = Path.Combine(s.Fixtures, "steam");
        var second = Path.Combine(s.Fixtures, "second");
        s.FixtureFile("steam/steamapps/libraryfolders.vdf", "\"libraryfolders\" { \"1\" { \"path\" \"" + Valve(second) + "\" } }");
        s.FixtureFile("steam/steamapps/appmanifest_228980.acf", """ "AppState" { "installdir" "Steamworks Shared" "name" "Steamworks Common Redistributables" } """);
        s.FixtureFile("steam/steamapps/common/Steamworks Shared/_CommonRedist/setup.exe", "fixture");
        s.FixtureFile("steam/steamapps/appmanifest_431960.acf", """ "AppState" { "installdir" "wallpaper_engine" "name" "Wallpaper Engine" } """);
        s.FixtureFile("steam/steamapps/appmanifest_388080.acf", """ "AppState" { "installdir" "Borderless Gaming" "name" "Borderless Gaming" } """);
        s.FixtureFile("steam/steamapps/appmanifest_900.acf", """ "AppState" { "installdir" "Utility" "name" "Utility" "type" "tool" } """);
        s.FixtureFile("steam/steamapps/appmanifest_1.acf", """ "AppState" { "installdir" "Shared" "name" "Shared" } """);
        s.FixtureFile("second/steamapps/appmanifest_2.acf", """ "AppState" { "installdir" "Shared" "name" "Shared" } """);
        s.FixtureFile("steam/steamapps/common/Shared/nvngx_dlss.dll", "fixture");
        s.FixtureFile("second/steamapps/common/Shared/nvngx_dlssg.dll", "fixture");
        var exe = s.GameFile("Bin/play.exe", "fixture");
        s.FixtureFile("epic/test.item", System.Text.Json.JsonSerializer.Serialize(new
        {
            InstallLocation = s.Installation.Root, DisplayName = "Epic Fixture", LaunchExecutable = "Bin/play.exe"
        }));
        var warnings = new List<string>();
        var roots = LauncherDiscovery.Steam(steam, warnings.Add, default)
            .Concat(LauncherDiscovery.Epic(Path.Combine(s.Fixtures, "epic"), warnings.Add, default)).ToArray();
        var result = new GameScanner(s.Storage).Scan(new(roots, [], []));
        Check(warnings.Count == 0 && result.Games.Length == 3 && result.Metrics.MetadataReads == 2);
        Check(result.Games.Single(g => g.Root.Launcher == "Epic").Executable == Path.GetFullPath(exe));
    }),
    ("Scanner überspringt Ausschlüsse und Junctions, meldet Offline-Pfad, Cache und Abbruch", s =>
    {
        var dll = s.GameFile("Bin/nvngx_dlss.dll", "SR");
        s.GameFile("Bin/dlssg_sm86/nvngx_dlssg.dll", "Mod");
        s.GameFile("Bin/play.exe", "fixture");
        s.GameFile("Bin/blocked.exe", "fixture");
        s.GameFile("Skipped/nvngx_dlssd.dll", "RR");
        s.FixtureFile("nvngx_dlssd.dll", "outside");
        if (OperatingSystem.IsWindows())
        {
            var link = Path.Combine(s.Installation.Root, "junction");
            using var command = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/d /c mklink /J \"{link}\" \"{s.Fixtures}\"") { CreateNoWindow = true, RedirectStandardOutput = true })!;
            command.WaitForExit();
            Check(command.ExitCode == 0);
        }
        var options = new ScanOptions([new("Fixture", s.Installation.Root, "Manuell"),
            new("Offline", Path.Combine(s.Installation.Root, "offline"), "Steam")],
            [Path.Combine(s.Installation.Root, "Skipped")], ["blocked.exe"]);
        var scanner = new GameScanner(s.Storage);
        using var abort = new CancellationTokenSource();
        Throws<OperationCanceledException>(() => scanner.Scan(options, new InlineProgress(_ => abort.Cancel()), abort.Token));
        Check(!File.Exists(Path.Combine(s.Storage, "scan-cache-v1.json")));
        var cold = scanner.Scan(options);
        var warm = scanner.Scan(options);
        Check(cold.Metrics.MetadataReads == 2 && warm.Metrics.CacheHits == 2 && warm.Metrics.MetadataReads == 0);
        Check(warm.Games.Single().Dlls.Count(d => d.ModComponent) == 1 && warm.Games.Single().Dlls.Length == 2);
        Check(warm.Games.Single().Alternatives.Length == 1 && warm.Warnings.Length == 1);
        File.AppendAllText(dll, "changed");
        Check(scanner.Scan(options).Metrics.MetadataReads == 1);
    }),
    ("Windows-Prozessprüfung blockiert eine laufende Fixture-EXE", s =>
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "fixture-" + Guid.NewGuid().ToString("N") + ".exe";
        var exe = Path.Combine(s.Installation.Root, name);
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-n 20 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true })!;
        try { Throws<IOException>(() => new WindowsInstallationGuard().RequireClosed(s.Installation with { Executables = [name] })); }
        finally { if (!process.HasExited) process.Kill(); process.WaitForExit(); }
    }),
    ("Treiber-Einstellungen nutzen sichere Defaults und bleiben abwärtskompatibel", s =>
    {
        var library = new GameLibraryService(s.Storage);
        Check(library.ReadSettings() is { SilentDriverInstall: false, DeleteDriverInstallerAfterInstall: true });
        Directory.CreateDirectory(s.Storage);
        File.WriteAllText(Path.Combine(s.Storage, "settings-v1.json"), """{"ScanFolders":[]}""");
        var legacy = library.ReadSettings();
        Check(legacy is { SilentDriverInstall: false, DeleteDriverInstallerAfterInstall: true });
        library.SaveSettings(legacy with { SilentDriverInstall = true, DeleteDriverInstallerAfterInstall = false });
        Check(library.ReadSettings() is { SilentDriverInstall: true, DeleteDriverInstallerAfterInstall: false });
    }),
    ("WHQL- und Hotfix-Suche lösen direkte Installer-Links, Release-Seiten und Zusammenfassungen auf", s =>
    {
        using var handler = new NvidiaDriverHandler();
        using var http = new HttpClient(handler, disposeHandler: true);
        var whql = NvidiaDriverLookup.WhqlAsync(http, "NVIDIA GeForce RTX 4080").GetAwaiter().GetResult()!;
        Check(whql.DriverType == NvidiaDriverType.WHQL && !whql.IsHotfix && whql.Version == "621.40");
        Check(whql.DirectInstallerUrl == "https://us.download.nvidia.com/Windows/621.40/621.40-desktop-win10-win11-64bit-international-dch-whql.exe");
        Check(whql.ReleasePageUrl == "https://www.nvidia.com/en-us/drivers/details/288687/");
        Check(whql.ReleaseSummary is { Length: > 0 } && whql.ReleaseSummary.Contains("Game Ready", StringComparison.Ordinal));

        var (hotfixDesktop, checkedOk) = NvidiaDriverLookup.HotfixAsync(
            http, new Version(621, 0), whql.ParsedVersion, "NVIDIA GeForce RTX 4080").GetAwaiter().GetResult();
        Check(checkedOk && hotfixDesktop is { DriverType: NvidiaDriverType.Hotfix, IsHotfix: true, Version: "621.45" });
        Check(hotfixDesktop!.DirectInstallerUrl == "https://international.download.nvidia.com/Windows/621.45/621.45-geforce-desktop-win10-win11-64bit-international-dch-hf.exe");
        Check(hotfixDesktop.ReleasePageUrl == "https://nvidia.custhelp.com/app/answers/detail/a_id/5940");
        Check(hotfixDesktop.ReleaseSummary is { Length: > 0 } && hotfixDesktop.ReleaseSummary.Contains("•", StringComparison.Ordinal));

        var (hotfixNotebook, _) = NvidiaDriverLookup.HotfixAsync(
            http, new Version(621, 0), whql.ParsedVersion, "NVIDIA GeForce RTX 4080 Laptop GPU").GetAwaiter().GetResult();
        Check(hotfixNotebook!.DirectInstallerUrl == "https://international.download.nvidia.com/Windows/621.45/621.45-geforce-notebook-win10-win11-64bit-international-dch-hf.exe");

        var unresolved = hotfixDesktop with { DirectInstallerUrl = null };
        var resolved = NvidiaDriverLookup.ResolveDirectInstallerUrlAsync(http, unresolved, isNotebook: false).GetAwaiter().GetResult();
        Check(resolved.DirectInstallerUrl == hotfixDesktop.DirectInstallerUrl);
    }),
    ("Treiber-Download streamt .part nach .exe, nutzt den Cache und prüft PE-/MZ-Signatur sowie Mindestgröße", s =>
    {
        if (!OperatingSystem.IsWindows()) return;
        var peBytes = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "ping.exe"));
        using var handler = new NvidiaDriverHandler { InstallerPayload = peBytes };
        using var mockHttp = new HttpClient(handler);
        var cacheDir = Path.Combine(s.Storage, "downloads", "drivers");
        var service = new NvidiaDriverService(mockHttp, cacheDir, minInstallerBytes: peBytes.Length);

        var whql = new DriverUpdateInfo(
            NvidiaDriverType.WHQL,
            "621.40",
            new Version(621, 40),
            "https://us.download.nvidia.com/Windows/621.40/621.40-desktop-win10-win11-64bit-international-dch-whql.exe",
            "https://www.nvidia.com/en-us/drivers/details/288687/");

        var reports = new List<DriverDownloadProgress>();
        var progress = new SyncProgress<DriverDownloadProgress>(p => reports.Add(p));
        var downloaded = service.DownloadAsync(whql, progress: progress).GetAwaiter().GetResult();
        Check(Path.GetFileName(downloaded) == "NVIDIA-WHQL-621.40.exe" && File.Exists(downloaded));
        Check(!File.Exists(downloaded + ".part"));
        Check(reports.Count >= 2 && reports[^1].Percent == 100);
        Check(handler.DownloadRequests == 1 && handler.SawInstallerReferer);

        // Second download hits cache without hitting HTTP.
        var cached = service.DownloadAsync(whql).GetAwaiter().GetResult();
        Check(cached == downloaded && handler.DownloadRequests == 1);

        // Default 50 MB minimum rejects tiny PE fixture.
        var strictService = new NvidiaDriverService(mockHttp, cacheDir);
        Throws<InvalidDataException>(() => strictService.VerifyInstaller(downloaded));
        Check(service.GetCachedInstaller(whql) == downloaded);

        // 32-bit SFX bootstrapper stub (Machine.I386, e.g. NVIDIA Hotfix SFX) is accepted; DLLs are rejected.
        var sysWow64Ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "ping.exe");
        if (File.Exists(sysWow64Ping))
        {
            var sfxService = new NvidiaDriverService(mockHttp, cacheDir, minInstallerBytes: new FileInfo(sysWow64Ping).Length);
            sfxService.VerifyInstaller(sysWow64Ping);
        }
        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        if (File.Exists(kernel32))
        {
            var dllService = new NvidiaDriverService(mockHttp, cacheDir, minInstallerBytes: new FileInfo(kernel32).Length);
            Throws<InvalidDataException>(() => dllService.VerifyInstaller(kernel32));
        }

        // HTML payload & HTTP 404 reject cleanly and leave no .part behind.
        var hotfix = new DriverUpdateInfo(
            NvidiaDriverType.Hotfix,
            "621.45",
            new Version(621, 45),
            "https://international.download.nvidia.com/Windows/621.45/html-error.exe",
            "https://nvidia.custhelp.com/app/answers/detail/a_id/5940");
        Throws<InvalidDataException>(() => service.DownloadAsync(hotfix).GetAwaiter().GetResult());
        Check(!File.Exists(service.GetInstallerPath(hotfix)) && !File.Exists(service.GetInstallerPath(hotfix) + ".part"));

        var missing = hotfix with { DirectInstallerUrl = "https://international.download.nvidia.com/Windows/621.45/missing.exe" };
        Throws<HttpRequestException>(() => service.DownloadAsync(missing).GetAwaiter().GetResult());
        Check(!File.Exists(service.GetInstallerPath(missing) + ".part"));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        var canceling = hotfix with { DirectInstallerUrl = "https://international.download.nvidia.com/Windows/621.45/cancel.exe" };
        Throws<OperationCanceledException>(() => service.DownloadAsync(canceling, token: cts.Token).GetAwaiter().GetResult());
        Check(!File.Exists(service.GetInstallerPath(canceling)) && !File.Exists(service.GetInstallerPath(canceling) + ".part"));
    }),
    ("Treiber-Cache bereinigt nach Installation, bei neuer Version, nach 24h und manuell", s =>
    {
        if (!OperatingSystem.IsWindows()) return;
        var peBytes = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "ping.exe"));
        using var mockHttp = new HttpClient(new NvidiaDriverHandler());
        var cacheDir = Path.Combine(s.Storage, "downloads", "drivers");
        Directory.CreateDirectory(cacheDir);
        var service = new NvidiaDriverService(mockHttp, cacheDir, minInstallerBytes: peBytes.Length);

        var oldExe = Path.Combine(cacheDir, "NVIDIA-WHQL-610.00.exe");
        var currentExe = Path.Combine(cacheDir, "NVIDIA-WHQL-621.40.exe");
        var hotfixExe = Path.Combine(cacheDir, "NVIDIA-Hotfix-621.45.exe");
        var stalePart = Path.Combine(cacheDir, "NVIDIA-WHQL-620.00.exe.part");
        var freshTmp = Path.Combine(cacheDir, "NVIDIA-Hotfix-621.45.exe.tmp");
        File.WriteAllBytes(oldExe, peBytes);
        File.WriteAllBytes(currentExe, peBytes);
        File.WriteAllBytes(hotfixExe, peBytes);
        File.WriteAllBytes(stalePart, [1, 2, 3]);
        File.WriteAllBytes(freshTmp, [4, 5, 6]);
        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(stalePart, now.AddHours(-25).UtcDateTime);
        File.SetLastWriteTimeUtc(freshTmp, now.AddHours(-1).UtcDateTime);

        service.CleanupOnStartup(new Version(621, 40), deleteInstalledMatch: true, now: now);
        Check(!File.Exists(oldExe) && !File.Exists(currentExe) && !File.Exists(stalePart));
        Check(File.Exists(hotfixExe) && File.Exists(freshTmp));

        File.WriteAllBytes(currentExe, peBytes);
        service.CleanupOutdatedInstallers(new Version(620, 00), new Version(621, 45), deleteInstalledMatch: true);
        Check(!File.Exists(currentExe) && File.Exists(hotfixExe));

        var hotfixInfo = new DriverUpdateInfo(NvidiaDriverType.Hotfix, "621.45", new Version(621, 45));
        Check(!service.CleanupAfterInstall(hotfixInfo, new Version(621, 40), autoDeleteEnabled: true) && File.Exists(hotfixExe));
        Check(!service.CleanupAfterInstall(hotfixInfo, new Version(621, 45), autoDeleteEnabled: false) && File.Exists(hotfixExe));
        Check(service.CleanupAfterInstall(hotfixInfo, new Version(621, 45), autoDeleteEnabled: true) && !File.Exists(hotfixExe));

        File.WriteAllBytes(currentExe, peBytes);
        var expectedSize = service.GetCacheSizeBytes();
        Check(expectedSize == new FileInfo(currentExe).Length + new FileInfo(freshTmp).Length);
        var (freed, count) = service.ClearCache();
        Check(freed == expectedSize && count == 2 && service.GetCacheSizeBytes() == 0);
    }),
    ("Treiber-Historie ermittelt Vorgänger-WHQL und -Hotfix und bevorzugt jüngeren Hotfix", _ =>
    {
        using var handler = new NvidiaDriverHandler();
        using var http = new HttpClient(handler, disposeHandler: true);

        var historyOn62140 = NvidiaDriverLookup.HistoryAsync(http, "NVIDIA GeForce RTX 4080", new Version(621, 40)).GetAwaiter().GetResult();
        Check(historyOn62140.HotfixChecked);
        Check(historyOn62140.AvailableUpdate(new Version(621, 40))?.Version == "621.45");
        Check(historyOn62140.PreviousDrivers.PreviousWhqlDriver is { Version: "620.80", IsHotfix: false, ReleaseDate: "04.11.2026" });
        Check(historyOn62140.PreviousDrivers.PreviousHotfixDriver is
        {
            Version: "620.95",
            IsHotfix: true,
            HotfixPageUrl: "https://nvidia.custhelp.com/app/answers/detail/a_id/5910",
            DirectInstallerUrl: "https://international.download.nvidia.com/Windows/620.95/620.95-geforce-desktop-win10-win11-64bit-international-dch-hf.exe"
        });
        Check(historyOn62140.PreviousDrivers.PreferredDriver?.Version == "620.95");
        Check(historyOn62140.PreviousDrivers.Drivers.Select(d => d.Version).SequenceEqual(["620.95", "620.80", "619.90"]));

        var historyOn62095 = NvidiaDriverLookup.HistoryAsync(http, "NVIDIA GeForce RTX 4080 Laptop GPU", new Version(620, 95)).GetAwaiter().GetResult();
        Check(historyOn62095.PreviousDrivers.PreviousHotfixDriver is null);
        Check(historyOn62095.PreviousDrivers.PreviousWhqlDriver?.Version == "620.80");
        Check(historyOn62095.PreviousDrivers.PreferredDriver?.Version == "620.80");
        Check(historyOn62095.PreviousDrivers.Drivers.Select(d => d.Version).SequenceEqual(["620.80", "619.90"]));
    }),
    ("Lokaler Cache reichert Vorgänger-Optionen an und funktioniert auch offline", s =>
    {
        if (!OperatingSystem.IsWindows()) return;
        var peBytes = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "ping.exe"));
        using var mockHttp = new HttpClient(new NvidiaDriverHandler());
        var cacheDir = Path.Combine(s.Storage, "downloads", "drivers");
        Directory.CreateDirectory(cacheDir);
        var service = new NvidiaDriverService(mockHttp, cacheDir, minInstallerBytes: peBytes.Length);

        var cachedWhqlPath = Path.Combine(cacheDir, "NVIDIA-WHQL-620.80.exe");
        var cachedHotfixPath = Path.Combine(cacheDir, "NVIDIA-Hotfix-620.95.exe");
        var newerCachedPath = Path.Combine(cacheDir, "NVIDIA-WHQL-621.40.exe");
        File.WriteAllBytes(cachedWhqlPath, peBytes);
        File.WriteAllBytes(cachedHotfixPath, peBytes);
        File.WriteAllBytes(newerCachedPath, peBytes);

        var online = new PreviousDriverOptions(
            PreviousWhqlDriver: new DriverUpdateInfo(NvidiaDriverType.WHQL, "620.80", new Version(620, 80), ReleaseDate: "04.11.2026"),
            PreviousHotfixDriver: new DriverUpdateInfo(NvidiaDriverType.Hotfix, "620.50", new Version(620, 50)),
            AllPreviousDrivers:
            [
                new DriverUpdateInfo(NvidiaDriverType.WHQL, "620.80", new Version(620, 80), ReleaseDate: "04.11.2026"),
                new DriverUpdateInfo(NvidiaDriverType.Hotfix, "620.50", new Version(620, 50)),
                new DriverUpdateInfo(NvidiaDriverType.WHQL, "619.90", new Version(619, 90))
            ]);
        var enriched = service.EnrichPreviousDrivers(online, new Version(621, 40));
        Check(enriched.PreviousWhqlDriver is { Version: "620.80", IsCached: true, ReleaseDate: "04.11.2026" });
        Check(enriched.PreviousHotfixDriver is { Version: "620.95", IsCached: true });
        Check(enriched.PreferredDriver?.Version == "620.95");
        Check(enriched.Drivers.Select(d => d.Version).SequenceEqual(["620.95", "620.80", "620.50", "619.90"]));

        var offline = service.EnrichPreviousDrivers(new PreviousDriverOptions(), new Version(621, 40));
        Check(offline.PreviousWhqlDriver is { Version: "620.80", IsCached: true });
        Check(offline.PreviousHotfixDriver is { Version: "620.95", IsCached: true });

        var parsedHotfix = NvidiaDriverService.ParseLocalInstallerInfo(@"C:\Temp\621.24-geforce-desktop-win10-win11-64bit-international-dch-hf.exe");
        Check(parsedHotfix is { Version: "621.24", IsHotfix: true, IsCached: true });
        var parsedWhql = NvidiaDriverService.ParseLocalInstallerInfo(@"C:\Temp\621.14-desktop-win10-win11-64bit-international-dch-whql.exe");
        Check(parsedWhql is { Version: "621.14", IsHotfix: false, IsCached: true });
    }),
    ("Clean-Install-Parameter, DDU-Erkennung, Settings-Roundtrip und Downgrade-Cache-Schutz", s =>
    {
        if (!OperatingSystem.IsWindows()) return;
        Check(NvidiaDriverService.BuildInstallerArguments(silentInstall: false, cleanInstall: true) == "");
        Check(NvidiaDriverService.BuildInstallerArguments(silentInstall: true, cleanInstall: true) == "-s -clean -noreboot");
        Check(NvidiaDriverService.BuildInstallerArguments(silentInstall: true, cleanInstall: false) == "-s -noreboot");

        var dduExe = s.FixtureFile("Tools/DDU/Display Driver Uninstaller.exe", "fixture");
        Check(NvidiaDriverService.FindDduExecutable(dduExe, s.Storage) == Path.GetFullPath(dduExe));
        Check(NvidiaDriverService.FindDduExecutable(Path.Combine(s.Fixtures, "missing.exe"), s.Storage) is null);

        var library = new GameLibraryService(s.Storage);
        var pending = new PendingDriverDowngrade("NVIDIA-WHQL-620.80.exe", "620.80", RetainInCacheAfterInstall: false);
        library.SaveSettings(library.ReadSettings() with { PendingDowngrade = pending, DduExecutablePath = dduExe });
        var reloaded = library.ReadSettings();
        Check(reloaded.PendingDowngrade == pending && reloaded.DduExecutablePath == dduExe);

        var peBytes = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "ping.exe"));
        using var mockHttp = new HttpClient(new NvidiaDriverHandler());
        var cacheDir = Path.Combine(s.Storage, "downloads", "drivers");
        Directory.CreateDirectory(cacheDir);
        var service = new NvidiaDriverService(mockHttp, cacheDir, minInstallerBytes: peBytes.Length);

        var protectedExe = Path.Combine(cacheDir, "NVIDIA-WHQL-620.80.exe");
        var unprotectedOldExe = Path.Combine(cacheDir, "NVIDIA-WHQL-619.90.exe");
        File.WriteAllBytes(protectedExe, peBytes);
        File.WriteAllBytes(unprotectedOldExe, peBytes);

        service.CleanupOnStartup(new Version(621, 40), deleteInstalledMatch: true, protectedInstallerFileName: "NVIDIA-WHQL-620.80.exe");
        Check(File.Exists(protectedExe) && !File.Exists(unprotectedOldExe));

        service.CleanupOutdatedInstallers(new Version(621, 40), new Version(621, 45), deleteInstalledMatch: true, protectedInstallerFileName: "NVIDIA-WHQL-620.80.exe");
        Check(File.Exists(protectedExe));

        var downgradeTarget = new DriverUpdateInfo(NvidiaDriverType.WHQL, "620.80", new Version(620, 80));
        Check(!service.CleanupAfterDowngrade(downgradeTarget, new Version(621, 40), autoDeleteEnabled: true) && File.Exists(protectedExe));
        Check(!service.CleanupAfterDowngrade(downgradeTarget, new Version(620, 80), autoDeleteEnabled: false) && File.Exists(protectedExe));
        Check(service.CleanupAfterDowngrade(downgradeTarget, new Version(620, 80), autoDeleteEnabled: true) && !File.Exists(protectedExe));
    }),
    ("Release-Notes-Bereinigung dekodiert mehrfache URL-Encodings, HTML-Entities und Tags sauber", _ =>
    {
        var raw = "%3Cb%3EGame%20Ready%3C%2Fb%3E%09%26rsquo%3B%3Cbr%20%2F%3E%3Cul%3E%3Cli%3EFix%2050%25%20crash%20in%20%26ldquo%3BDX12%26rdquo%3B%3C%2Fli%3E%3C%2Ful%3E";
        var cleaned = NvidiaDriverLookup.CleanReleaseNotes(raw, preferBulletList: true)!;
        Check(!cleaned.Contains("%20", StringComparison.Ordinal));
        Check(!cleaned.Contains("%3C", StringComparison.OrdinalIgnoreCase));
        Check(!cleaned.Contains("%09", StringComparison.OrdinalIgnoreCase));
        Check(!cleaned.Contains("&rsquo;", StringComparison.OrdinalIgnoreCase));
        Check(!cleaned.Contains("<b>", StringComparison.OrdinalIgnoreCase) && !cleaned.Contains("<li>", StringComparison.OrdinalIgnoreCase));
        Check(cleaned.Contains("50% crash", StringComparison.Ordinal));
        Check(cleaned.Contains("• Fix 50% crash", StringComparison.Ordinal));

        var general = NvidiaDriverLookup.CleanReleaseNotes(raw, preferBulletList: false)!;
        Check(general.Contains("Game Ready ’", StringComparison.Ordinal));
        Check(general.Contains("• Fix 50% crash in “DX12”", StringComparison.Ordinal));
    }),
    ("Hotfix-Scanner nutzt Fallback-Kette, CDN-HEAD-Probe, 6h-Cache, 304 und 403/429-Sperre", s =>
    {
        var cachePath = Path.Combine(s.Storage, "cache", "hotfix_feed_cache.json");
        var now = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var handler = new FallbackHotfixHandler();
        using var http = new HttpClient(handler, disposeHandler: true);

        // 1. Forum 429 (Retry-After: 3600s), CustHelp 403, TechPowerUp returns Hotfix 622.15 without exe -> CDN HEAD resolves exe
        var first = HotfixFeedScanner.ScanAsync(http, isNotebook: false, cachePath, forceRefresh: false, clock: () => now).GetAwaiter().GetResult();
        Check(first.Checked && !first.FromCache && first.SourceName == "techpowerup");
        Check(first.Drivers.Length == 1);
        Check(first.Drivers[0] is { Version: "622.15", IsHotfix: true } &&
              first.Drivers[0].DirectInstallerUrl == "https://international.download.nvidia.com/Windows/622.15hf/622.15-desktop-notebook-win10-win11-64bit-international-dch.hf.exe");
        Check(first.Drivers[0].ReleaseSummary is { Length: > 0 } && !first.Drivers[0].ReleaseSummary!.Contains("%20", StringComparison.Ordinal));
        Check(File.Exists(cachePath));

        var requestsAfterFirst = handler.TotalRequests;

        // 2. Within 6h TTL -> 0 external HTTP requests
        now = now.AddHours(3);
        var second = HotfixFeedScanner.ScanAsync(http, isNotebook: false, cachePath, forceRefresh: false, clock: () => now).GetAwaiter().GetResult();
        Check(second.Checked && second.FromCache && second.Drivers.Length == 1 && second.Drivers[0].Version == "622.15");
        Check(handler.TotalRequests == requestsAfterFirst);

        // 3. Manual refresh cooldown (< 60s) -> 0 external HTTP requests
        handler.ReturnNotModified = true;
        now = now.AddHours(4); // TTL expired (7h elapsed)
        var manualFirst = HotfixFeedScanner.ScanAsync(http, isNotebook: false, cachePath, forceRefresh: true, clock: () => now).GetAwaiter().GetResult();
        Check(manualFirst.Checked && manualFirst.Drivers.Length == 1);
        var requestsAfterManual = handler.TotalRequests;

        now = now.AddSeconds(20);
        var manualTooSoon = HotfixFeedScanner.ScanAsync(http, isNotebook: false, cachePath, forceRefresh: true, clock: () => now).GetAwaiter().GetResult();
        Check(manualTooSoon.Drivers.Length == 1 && handler.TotalRequests == requestsAfterManual);

        // 4. All sources blocked (403/429) without cache -> Checked == false and BlockedUntilUtc persisted >= 30m
        var blockedCachePath = Path.Combine(s.Storage, "cache", "hotfix_blocked_cache.json");
        var blockedNow = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var allBlockedHandler = new FallbackHotfixHandler { BlockAll = true };
        using var blockedHttp = new HttpClient(allBlockedHandler, disposeHandler: true);
        var blockedResult = HotfixFeedScanner.ScanAsync(blockedHttp, isNotebook: false, blockedCachePath, forceRefresh: false, clock: () => blockedNow).GetAwaiter().GetResult();
        Check(!blockedResult.Checked && blockedResult.Drivers.Length == 0);
        var doc = System.Text.Json.JsonSerializer.Deserialize<HotfixFeedCacheDocument>(File.ReadAllText(blockedCachePath))!;
        Check(doc.Sources is not null && doc.Sources["techpowerup"].BlockedUntilUtc >= blockedNow.AddMinutes(30));

        // Next call immediately skips blocked sources -> 0 HTTP requests
        var requestsWhenBlocked = allBlockedHandler.TotalRequests;
        var skippedResult = HotfixFeedScanner.ScanAsync(blockedHttp, isNotebook: false, blockedCachePath, forceRefresh: false, clock: () => blockedNow.AddMinutes(5)).GetAwaiter().GetResult();
        Check(!skippedResult.Checked && allBlockedHandler.TotalRequests == requestsWhenBlocked);
    }),
    ("Hotfix-Scanner findet 6xx.xx über mehrere Quellen, erfasst SourceStatuses und invalidiert altes Schema", s =>
    {
        // 1. Parser-Checks: GeForce Forum (Sticky + JSON), TechPowerUp (Badge + verschachtelte Divs), Guru3D
        var forumHtml = """
            <script>window.__STATE__ = {"threads":[{"title":"GeForce Hotfix Display Driver version 622.85","url":"/en-us/geforce/forums/game-ready-drivers/13/555555/geforce-hotfix-622-85/"}]};</script>
            <div class="sticky-wrapper"><div class="pinned-inner">
              <a href="/en-us/geforce/forums/game-ready-drivers/13/555000/geforce-hotfix-621-90/">[Sticky] GeForce Hotfix Display Driver version 621.90</a>
              <span>Nov 18, 2026</span>
            </div></div>
            """;
        var forumParsed = HotfixFeedScanner.ParseNvidiaForumHotfixes(forumHtml);
        Check(forumParsed.Select(d => d.Version).SequenceEqual(["622.85", "621.90"]));

        var tpuHtml = """
            <div class="item">
              <div class="title-wrap">
                <a href="/download/nvidia-geforce-graphics-drivers/623-10/">NVIDIA GeForce 623.10</a>
                <span class="badge">Hotfix</span>
              </div>
              <div class="meta">Nov 25, 2026</div>
            </div>
            """;
        var tpuParsed = HotfixFeedScanner.ParseCatalogHotfixes(tpuHtml, "https://www.techpowerup.com");
        Check(tpuParsed.Length == 1 && tpuParsed[0].Version == "623.10");

        var guruHtml = """
            <article>
              <a href="/download/nvidia-geforce-621-95-hotfix-driver-download/">NVIDIA GeForce 621.95 Hotfix</a>
              <span>Nov 21, 2026</span>
            </article>
            """;
        var guruParsed = HotfixFeedScanner.ParseCatalogHotfixes(guruHtml, "https://www.guru3d.com");
        Check(guruParsed.Length == 1 && guruParsed[0].Version == "621.95");

        // 2. GitHub-First: Stale v4 Cache (nur 576.26) wird verworfen -> GitHub Feed liefert 623.20 und schaltet Fallbacks kurz
        var cachePath = Path.Combine(s.Storage, "cache", "hotfix_feed_cache.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var now = new DateTimeOffset(2026, 11, 27, 14, 30, 0, TimeSpan.Zero);
        File.WriteAllText(cachePath, """
            {
              "SchemaVersion": 4,
              "FetchedAtUtc": "2026-11-27T14:00:00+00:00",
              "SourceName": "nvidia-custhelp",
              "Entries": [
                { "DriverType": 1, "Version": "576.26", "ParsedVersion": "576.26" }
              ]
            }
            """);

        var multiHandler = new MultiSourceHotfixHandler();
        using var multiHttp = new HttpClient(multiHandler, disposeHandler: true);
        var options = new HotfixFeedOptions(GoogleSearchApiKey: "test-key", GoogleSearchCx: "test-cx");
        var fresh = HotfixFeedScanner.ScanAsync(multiHttp, isNotebook: false, cachePath, forceRefresh: false, clock: () => now, options: options).GetAwaiter().GetResult();

        Check(fresh.Checked && !fresh.FromCache);
        Check(fresh.Drivers.Select(d => d.Version).SequenceEqual(["623.20"]));
        Check(fresh.SourceStatuses is { Length: 1 });
        Check(fresh.SourceStatuses!.Single() is { SourceName: "GitHub Feed", Status: HotfixSourceFetchStatus.Success, FoundCount: 1 });

        var savedDoc = System.Text.Json.JsonSerializer.Deserialize<HotfixFeedCacheDocument>(File.ReadAllText(cachePath))!;
        Check(savedDoc.SchemaVersion == HotfixFeedScanner.CurrentSchemaVersion);
        Check(savedDoc.SourceStatuses is { Length: 1 });

        // 3. Cache-Hit liefert SourceStatuses mit
        var cached = HotfixFeedScanner.ScanAsync(multiHttp, isNotebook: false, cachePath, forceRefresh: false, clock: () => now.AddMinutes(10), options: options).GetAwaiter().GetResult();
        Check(cached.FromCache && cached.SourceStatuses is { Length: 1 } && cached.Drivers[0].Version == "623.20");

        // 4. Automatischer Fallback auf Google Suche + TechPowerUp, wenn GitHub Feed nicht erreichbar ist (404)
        multiHandler.FailGitHubFeed = true;
        var fallbackCachePath = Path.Combine(s.Storage, "cache", "hotfix_fallback_cache.json");
        var fallback = HotfixFeedScanner.ScanAsync(multiHttp, isNotebook: false, fallbackCachePath, forceRefresh: true, clock: () => now.AddHours(1), bypassCooldown: true, options: options).GetAwaiter().GetResult();
        Check(fallback.Checked && !fallback.FromCache);
        Check(fallback.Drivers.Select(d => d.Version).SequenceEqual(["623.15", "623.10", "623.05"]));
        Check(fallback.SourceStatuses is { Length: 3 });
        Check(fallback.SourceStatuses!.Single(x => x.SourceName == "GitHub Feed") is { Status: HotfixSourceFetchStatus.Success, FoundCount: 0 });
        Check(fallback.SourceStatuses!.Single(x => x.SourceName == "Google Suche") is { Status: HotfixSourceFetchStatus.Success, FoundCount: 2 });
        Check(fallback.SourceStatuses!.Single(x => x.SourceName == "TechPowerUp") is { Status: HotfixSourceFetchStatus.Success, FoundCount: 1 });

        // 5. 304 NotModified auf TechPowerUp -> CacheHit
        multiHandler.ReturnNotModifiedForTpu = true;
        var refreshed = HotfixFeedScanner.ScanAsync(multiHttp, isNotebook: false, fallbackCachePath, forceRefresh: true, clock: () => now.AddHours(2), bypassCooldown: true, options: options).GetAwaiter().GetResult();
        Check(refreshed.Checked && refreshed.SourceStatuses!.Single(x => x.SourceName == "TechPowerUp") is { Status: HotfixSourceFetchStatus.CacheHit, FoundCount: 1 });

        // 6. LatestHotfix (> installed) vs PreviousHotfix (< installed) Resolution + hotfixes.json Schema + Settings Roundtrip
        var resolved = HotfixFeedScanner.ResolveHotfixes(fallback.Drivers, new Version(621, 40));
        Check(resolved.LatestHotfix?.Version == "623.15");
        Check(resolved.PreviousHotfix is null);

        var resolvedNoUpdate = HotfixFeedScanner.ResolveHotfixes(fallback.Drivers, new Version(624, 00));
        Check(resolvedNoUpdate.LatestHotfix is null && resolvedNoUpdate.PreviousHotfix?.Version == "623.15");

        var ghSchemaDrivers = HotfixFeedScanner.ParseJsonFeedHotfixes("""
            {
              "schemaVersion": 1,
              "updatedAtUtc": "2026-11-27T00:00:00Z",
              "hotfixes": [
                {
                  "version": "621.35",
                  "series": "600",
                  "releaseDate": "2026-11-10",
                  "title": "GeForce Hotfix Display Driver version 621.35",
                  "directUrl": "https://international.download.nvidia.com/Windows/621.35hf/621.35-desktop-notebook-win10-win11-64bit-international-dch.hf.exe",
                  "desktopOnlyUrl": "https://international.download.nvidia.com/Windows/621.35hf/621.35-desktop-win10-win11-64bit-international-dch.hf.exe",
                  "pageUrl": "https://nvidia.custhelp.com/app/answers/detail/a_id/5930",
                  "forumUrl": "https://www.nvidia.com/en-us/geforce/forums/game-ready-drivers/13/554000/",
                  "summary": "Fixes nvlog.nvlgstg excessive writes.",
                  "sha256": "abc123"
                },
                {
                  "version": "576.26",
                  "series": "500",
                  "releaseDate": "2025-04-28",
                  "title": "GeForce Hotfix Display Driver version 576.26",
                  "desktopOnlyUrl": "https://international.download.nvidia.com/Windows/576.26hf/576.26-desktop-win10-win11-64bit-international-dch.hf.exe",
                  "forumUrl": "https://www.nvidia.com/en-us/geforce/forums/game-ready-drivers/13/550000/",
                  "summary": "Fixes older bug."
                }
              ]
            }
            """);
        Check(ghSchemaDrivers.Length == 2);
        Check(ghSchemaDrivers[0] is { Version: "621.35", ReleasePageUrl: "https://nvidia.custhelp.com/app/answers/detail/a_id/5930" } &&
              ghSchemaDrivers[0].DirectInstallerUrl!.Contains("621.35-desktop-notebook", StringComparison.Ordinal));
        Check(ghSchemaDrivers[1] is { Version: "576.26", ReleasePageUrl: "https://www.nvidia.com/en-us/geforce/forums/game-ready-drivers/13/550000/" } &&
              ghSchemaDrivers[1].DirectInstallerUrl!.Contains("576.26-desktop-win10", StringComparison.Ordinal));
        var resolved62140 = HotfixFeedScanner.ResolveHotfixes(ghSchemaDrivers, new Version(621, 40));
        Check(resolved62140.LatestHotfix is null && resolved62140.PreviousHotfix?.Version == "621.35");

        var rssParsed = HotfixFeedScanner.ParseStructuredFeedHotfixes("""
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0"><channel>
              <item>
                <title>NVIDIA GeForce Hotfix Driver 624.05</title>
                <link>https://www.guru3d.com/download/geforce-624-05-hotfix/</link>
                <pubDate>Thu, 26 Nov 2026 12:00:00 GMT</pubDate>
                <description>Fixes flicker in DX12.</description>
              </item>
            </channel></rss>
            """);
        Check(rssParsed.Length == 1 && rssParsed[0] is { Version: "624.05", ReleaseDate: "26.11.2026" });

        var lib = new GameLibraryService(s.Storage);
        lib.SaveSettings(new ManagerSettings([], CustomHotfixFeedUrl: "https://example.com/hotfixes.json", GoogleSearchApiKey: "key1", GoogleSearchCx: "cx1"));
        Check(lib.ReadSettings() is { CustomHotfixFeedUrl: "https://example.com/hotfixes.json", GoogleSearchApiKey: "key1", GoogleSearchCx: "cx1" });
    }),
    ("Google-Translate-Client übersetzt, cached pro Version/Sprache und fällt bei Fehler nahtlos zurück", s =>
    {
        var translateCachePath = Path.Combine(s.Storage, "cache", "translations.json");
        var handler = new FallbackHotfixHandler();
        using var http = new HttpClient(handler, disposeHandler: true);
        var translator = new DriverSummaryTranslator(http, translateCachePath);

        // English client does not trigger translation requests
        var enResult = translator.TranslateAsync("622.15", "• Fixed DX12 crash", "en").GetAwaiter().GetResult();
        Check(enResult == "• Fixed DX12 crash" && handler.TranslateRequests == 0);

        // German client translates and caches on disk
        var deResult = translator.TranslateAsync("622.15", "• Fixed DX12 crash", "de").GetAwaiter().GetResult();
        Check(deResult == "• Behobener DX12-Absturz" && handler.TranslateRequests == 1);

        var deCached = translator.TranslateAsync("622.15", "• Fixed DX12 crash", "de").GetAwaiter().GetResult();
        Check(deCached == "• Behobener DX12-Absturz" && handler.TranslateRequests == 1);

        // Fresh instance loads persisted disk cache without HTTP
        var translatorReloaded = new DriverSummaryTranslator(http, translateCachePath);
        var deDisk = translatorReloaded.TranslateAsync("622.15", "• Fixed DX12 crash", "de").GetAwaiter().GetResult();
        Check(deDisk == "• Behobener DX12-Absturz" && handler.TranslateRequests == 1);

        // HTTP 429 falls back silently to cleaned English text
        handler.RateLimitTranslate = true;
        var fallback = translator.TranslateAsync("622.20", "• Fixed flicker", "de").GetAwaiter().GetResult();
        Check(fallback == "• Fixed flicker");

        // Settings roundtrip for new translation fields
        var library = new GameLibraryService(s.Storage);
        Check(library.ReadSettings() is { TranslateDriverSummaries: false, GoogleTranslateApiKey: null });
        library.SaveSettings(library.ReadSettings() with { TranslateDriverSummaries = true, GoogleTranslateApiKey = "test-key" });
        Check(library.ReadSettings() is { TranslateDriverSummaries: true, GoogleTranslateApiKey: "test-key" });
    })
};

if (args.SequenceEqual(["--rtx-smoke"]))
{
    using var sandbox = new Sandbox();
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    var service = new RtxDatabaseService(http, sandbox.Storage);
    var (database, offline) = await service.RefreshAsync();
    var game = database.Find("007 First Light");
    Check(!offline && database.Count >= 100 && game?.RecommendedFrames == 5 && service.ReadCached()?.Count == database.Count);
    Console.WriteLine($"DLSSdb · {database.Count} eindeutige Titelschlüssel · {game!.Title}: {game.RecommendedFrames} Frames");
    return 0;
}

if (args.SequenceEqual(["--sm86-smoke"]))
{
    using var sandbox = new Sandbox();
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    var service = new ModDownloadService(http, sandbox.Storage);
    var package = await service.GetLatestAsync();
    Console.WriteLine($"SM86 {package.Tag} · {string.Join(", ", package.Proxies)}");
    return 0;
}

if (args.SequenceEqual(["--catalog-smoke"]))
{
    using var sandbox = new Sandbox();
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    var entries = (await new DllCatalogService(http, sandbox.Storage).RefreshAsync()).Entries;
    var service = new DllDownloadService(http, Path.Combine(sandbox.Storage, "dlls"));
    foreach (var entry in entries.DistinctBy(e => e.Kind))
    {
        var path = await service.GetAsync(entry);
        Console.WriteLine($"{entry.Kind} {entry.Version} · {HashFile(path)}");
    }
    Console.WriteLine($"{entries.Length} Katalogeinträge");
    return 0;
}

if (args.SequenceEqual(["--benchmark"]))
{
    using var s = new Sandbox();
    var roots = new List<ScanRoot>();
    for (var game = 0; game < 64; game++)
    {
        var relative = $"Game{game}";
        roots.Add(new(relative, Path.Combine(s.Installation.Root, relative), "Steam"));
        s.GameFile($"{relative}/Bin/play.exe", "synthetic executable");
        s.GameFile($"{relative}/Bin/nvngx_dlss.dll", "synthetic DLL");
        s.GameFile($"{relative}/Bin/nvngx_dlssg.dll", "synthetic DLL");
        for (var folder = 0; folder < 10; folder++)
            for (var file = 0; file < 12; file++)
                s.GameFile($"{relative}/Content/{folder}/asset{file}.dat", "fixture");
    }
    var scanner = new GameScanner(s.Storage);
    var options = new ScanOptions(roots, [], []);
    for (var i = 0; i < 6; i++)
    {
        var scan = scanner.Scan(options);
        Console.WriteLine($"{(i == 0 ? "empty-cache" : "repeat")}: {System.Text.Json.JsonSerializer.Serialize(scan.Metrics)}");
    }
    return 0;
}

var failures = 0;
foreach (var (name, run) in tests)
{
    using var sandbox = new Sandbox();
    try { run(sandbox); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} Tests erfolgreich.");
return failures == 0 ? 0 : 1;

static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
static object CatalogItem(string version, bool dev = false, string host = "dlss-swapper-downloads.beeradmoore.com") =>
    new
    {
        version, is_dev_file = dev, is_signature_valid = true,
        download_url = $"https://{host}/test.zip",
        zip_md5_hash = new string('1', 32), md5_hash = new string('2', 32)
    };

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static void Check(bool condition)
{
    if (!condition) throw new InvalidOperationException("Erwartung nicht erfüllt.");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Erwartete Ausnahme: {typeof(T).Name}");
}

sealed class TestGuard : IInstallationGuard
{
    private int calls;
    public Action<int>? OnCheck { get; init; }
    public void RequireClosed(Installation installation) => OnCheck?.Invoke(++calls);
}

sealed class InlineProgress(Action<ScanProgress> onProgress) : IProgress<ScanProgress>
{
    public void Report(ScanProgress value) => onProgress(value);
}

sealed class Sandbox : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dlss-manager-tests", Guid.NewGuid().ToString("N"));
    public string Fixtures => Path.Combine(root, "fixtures");
    public string Storage => Path.Combine(root, "storage");
    public Installation Installation => new(Path.Combine(root, "installation"), ["fixture.exe"]);

    public Sandbox()
    {
        Directory.CreateDirectory(Fixtures);
        Directory.CreateDirectory(Installation.Root);
    }

    public FileTransactions Transactions(IInstallationGuard? guard = null) => new(Storage, guard ?? new TestGuard());
    public string FixtureFile(string name, string contents) => Write(Fixtures, name, contents);
    public string GameFile(string name, string contents) => Write(Installation.Root, name, contents);
    public string? ReadGame(string name)
    {
        var path = Path.Combine(Installation.Root, name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
    private static string Write(string folder, string name, string contents)
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }
    public void Dispose()
    {
        var link = Path.Combine(Installation.Root, "junction");
        if (SafePaths.Attributes(link) is { } attr && attr.HasFlag(FileAttributes.ReparsePoint))
        {
            using var command = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/d /c rmdir \"{link}\"") { CreateNoWindow = true })!;
            command.WaitForExit();
            if (command.ExitCode != 0) throw new IOException("Test-Junction konnte nicht entfernt werden.");
        }
        Directory.Delete(root, recursive: true);
    }
}

sealed class PackageHandler(byte[] archive) : HttpMessageHandler
{
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests++;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive),
            RequestMessage = request
        });
    }
}

sealed class CatalogHandler(string manifest) : HttpMessageHandler
{
    private int requests;
    public bool Conditional { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Conditional = request.Headers.IfNoneMatch.Any(v => v.Tag == "\"fixture\"");
        var result = new HttpResponseMessage(++requests == 1 ? System.Net.HttpStatusCode.OK :
            requests == 2 ? System.Net.HttpStatusCode.NotModified : System.Net.HttpStatusCode.ServiceUnavailable)
        {
            RequestMessage = request,
            Content = new StringContent(manifest)
        };
        result.Headers.ETag = new("\"fixture\"");
        return Task.FromResult(result);
    }
}

sealed class ReleaseHandler : HttpMessageHandler
{
    public bool ValidRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var limited = ValidRequest;
        ValidRequest = request.Headers.UserAgent.Any() && request.RequestUri!.Query.Contains("page=1");
        return Task.FromResult(new HttpResponseMessage(limited ? System.Net.HttpStatusCode.TooManyRequests : System.Net.HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent("""[{"tag_name":"v1.2.0","draft":false,"prerelease":false}]""")
        });
    }
}

sealed class ModReleaseHandler(byte[] archive) : HttpMessageHandler
{
    private const string Url = "https://github.com/sdli1995/dlssg_for_sm86/releases/download/0.4.1/dlssg-release-0.4.1-x64.zip";
    public byte[] Archive { get; set; } = archive;
    public int Downloads { get; private set; }
    public string? DigestOverride { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HttpContent content;
        if (request.RequestUri!.AbsoluteUri == Url)
        {
            Downloads++;
            content = new ByteArrayContent(Archive);
        }
        else
        {
            var digest = DigestOverride ?? Convert.ToHexString(SHA256.HashData(Archive));
            content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            {
                tag_name = "0.4.1", draft = false, prerelease = false,
                assets = new[] { new { name = "dlssg-release-0.4.1-x64.zip", size = Archive.Length,
                    digest = "sha256:" + digest, browser_download_url = Url } }
            }));
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            RequestMessage = request, Content = content
        });
    }
}

sealed class RtxHandler(string json) : HttpMessageHandler
{
    private int calls;
    public bool Conditional { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Conditional = request.Headers.IfNoneMatch.Any(t => t.Tag == "\"rtx-fixture\"");
        var status = ++calls == 2 ? System.Net.HttpStatusCode.NotModified : System.Net.HttpStatusCode.OK;
        var response = new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new StringContent(calls == 1 ? json : """{"data":[]}""")
        };
        response.Headers.ETag = new("\"rtx-fixture\"");
        return Task.FromResult(response);
    }
}

sealed class SyncProgress<T>(Action<T> onProgress) : IProgress<T>
{
    public void Report(T value) => onProgress(value);
}

sealed class NvidiaDriverHandler : HttpMessageHandler
{
    public byte[] InstallerPayload { get; set; } = [];
    public int DownloadRequests { get; private set; }
    public bool SawInstallerReferer { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;

        if (uri.Host.Equals("gfwsl.geforce.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.PathAndQuery.Contains("getMenuArrays", StringComparison.Ordinal))
            {
                if (uri.PathAndQuery.Contains("pst%22%3Anull", StringComparison.Ordinal))
                    return Json("""[[],[{"id":131,"menutext":"GeForce RTX 40 Series"},{"id":132,"menutext":"GeForce RTX 40 Series (Notebooks)"}]]""");
                if (uri.PathAndQuery.Contains("131", StringComparison.Ordinal))
                    return Json("""[[],[],[{"id":990,"menutext":"GeForce RTX 4080"}]]""");
                return Json("""[[],[],[{"id":991,"menutext":"GeForce RTX 4080 Laptop GPU"}]]""");
            }
            if (uri.PathAndQuery.Contains("AjaxDriverService.php", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "IDS": [
                        {
                          "downloadInfo": {
                            "IsWHQL": "1",
                            "Version": "621.40",
                            "DetailsURL": "https://www.nvidia.com/en-us/drivers/details/288687/",
                            "DownloadURL": "https://us.download.nvidia.com/Windows/621.40/621.40-desktop-win10-win11-64bit-international-dch-whql.exe",
                            "ReleaseNotes": "<p><strong>Game Ready</strong> driver highlights:</p><ul><li>DLSS 4 support</li></ul>"
                          }
                        },
                        {
                          "downloadInfo": {
                            "IsWHQL": "1",
                            "Version": "620.80",
                            "ReleaseDateTime": "Wed Nov 04, 2026",
                            "DetailsURL": "https://www.nvidia.com/en-us/drivers/details/287100/",
                            "DownloadURL": "https://us.download.nvidia.com/Windows/620.80/620.80-desktop-win10-win11-64bit-international-dch-whql.exe",
                            "ReleaseNotes": "<p>Previous WHQL driver.</p>"
                          }
                        },
                        {
                          "downloadInfo": {
                            "IsWHQL": "1",
                            "Version": "619.90",
                            "DetailsURL": "https://www.nvidia.com/en-us/drivers/details/286000/",
                            "DownloadURL": "https://us.download.nvidia.com/Windows/619.90/619.90-desktop-win10-win11-64bit-international-dch-whql.exe",
                            "ReleaseNotes": "<p>Older WHQL driver.</p>"
                          }
                        }
                      ]
                    }
                    """);
            }
        }

        if (uri.Host.Equals("nvidia.custhelp.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath == "/app/answers/list/kw/Hotfix")
            {
                var redirect = new HttpResponseMessage(System.Net.HttpStatusCode.Found) { RequestMessage = request };
                redirect.Headers.Location = new Uri("/app/answers/list/kw/Hotfix/resolved", UriKind.Relative);
                return Task.FromResult(redirect);
            }
            if (uri.AbsolutePath == "/app/answers/list/kw/Hotfix/resolved")
            {
                return Html("""
                    <h3><a href="/app/answers/detail/a_id/5940">GeForce Hotfix Display Driver version 621.45</a></h3>
                    <h3><a href="/app/answers/detail/a_id/5910">GeForce Hotfix Display Driver version 620.95</a></h3>
                    """);
            }
            if (uri.AbsolutePath == "/app/answers/detail/a_id/5940")
            {
                return Html("""
                    <p>GeForce Hotfix Display Driver version 621.45 is based on our latest Game Ready Driver 621.40.</p>
                    <p>This hotfix addresses the following:</p>
                    <ul>
                      <li>Fixed micro-stuttering in DX12 games when Frame Generation is enabled</li>
                      <li>Fixed display flicker on external monitors</li>
                    </ul>
                    <p><a href="https://international.download.nvidia.com/Windows/621.45/621.45-geforce-desktop-win10-win11-64bit-international-dch-hf.exe">Click here for Desktop</a></p>
                    <p><a href="https://international.download.nvidia.com/Windows/621.45/621.45-geforce-notebook-win10-win11-64bit-international-dch-hf.exe">Click here for Notebook</a></p>
                    """);
            }
            if (uri.AbsolutePath == "/app/answers/detail/a_id/5910")
            {
                return Html("""
                    <p>GeForce Hotfix Display Driver version 620.95 is based on Game Ready Driver 620.80.</p>
                    <p>This hotfix addresses the following:</p>
                    <ul>
                      <li>Fixed shader compilation crash on RTX 40 series</li>
                    </ul>
                    <p><a href="https://international.download.nvidia.com/Windows/620.95/620.95-geforce-desktop-win10-win11-64bit-international-dch-hf.exe">Click here for Desktop</a></p>
                    <p><a href="https://international.download.nvidia.com/Windows/620.95/620.95-geforce-notebook-win10-win11-64bit-international-dch-hf.exe">Click here for Notebook</a></p>
                    """);
            }
        }

        if (uri.Host.EndsWith("download.nvidia.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.EndsWith("html-error.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent("<html><body>403 Forbidden</body></html>", Encoding.UTF8, "text/html")
                });
            }
            if (uri.AbsolutePath.EndsWith("missing.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request });
            }
            if (uri.AbsolutePath.EndsWith("cancel.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StreamContent(new SlowBlockingStream())
                });
            }
            if (uri.Host.Equals("us.download.nvidia.com", StringComparison.OrdinalIgnoreCase))
            {
                SawInstallerReferer = request.Headers.Referrer is not null;
                var redirect = new HttpResponseMessage(System.Net.HttpStatusCode.Found) { RequestMessage = request };
                redirect.Headers.Location = new Uri("https://international.download.nvidia.com" + uri.AbsolutePath);
                return Task.FromResult(redirect);
            }

            DownloadRequests++;
            SawInstallerReferer |= request.Headers.Referrer is not null;
            var content = new ByteArrayContent(InstallerPayload);
            content.Headers.ContentLength = InstallerPayload.Length;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content
            });
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request });

        Task<HttpResponseMessage> Json(string text) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(text, Encoding.UTF8, "application/json")
        });

        Task<HttpResponseMessage> Html(string text) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(text, Encoding.UTF8, "text/html")
        });
    }

    private sealed class SlowBlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return 0;
        }
    }
}

sealed class FallbackHotfixHandler : HttpMessageHandler
{
    public int TotalRequests { get; private set; }
    public int TranslateRequests { get; private set; }
    public bool ReturnNotModified { get; set; }
    public bool BlockAll { get; set; }
    public bool RateLimitTranslate { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TotalRequests++;
        var uri = request.RequestUri!;

        if (uri.Host.Equals("translate.googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            TranslateRequests++;
            if (RateLimitTranslate)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests) { RequestMessage = request });

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("[[[\"• Behobener DX12-Absturz\",\"• Fixed DX12 crash\",null,null,10]]]", Encoding.UTF8, "application/json")
            });
        }

        if (BlockAll)
        {
            var blocked = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests) { RequestMessage = request };
            blocked.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(45));
            return Task.FromResult(blocked);
        }

        if (uri.Host.Equals("www.nvidia.com", StringComparison.OrdinalIgnoreCase))
        {
            var rateLimited = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests) { RequestMessage = request };
            rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(rateLimited);
        }

        if (uri.Host.Equals("www.techpowerup.com", StringComparison.OrdinalIgnoreCase))
        {
            if (ReturnNotModified && request.Headers.IfNoneMatch.Any())
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotModified) { RequestMessage = request });

            var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""
                    <div class="driver-entry">
                      <a href="/download/nvidia-geforce-graphics-drivers/">NVIDIA GeForce 622.15 Hotfix</a>
                      <span>Nov 20, 2026</span>
                      <ul><li>Fixed%20DX12%20stutter%20%26amp%3B%20crash</li></ul>
                    </div>
                    """, Encoding.UTF8, "text/html")
            };
            ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"tpu-v1\"");
            return Task.FromResult(ok);
        }

        if (uri.Host.Equals("nvidia.custhelp.com", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) { RequestMessage = request });

        if (uri.Host.Equals("international.download.nvidia.com", StringComparison.OrdinalIgnoreCase) &&
            request.Method == HttpMethod.Head &&
            uri.AbsolutePath.EndsWith("622.15-desktop-notebook-win10-win11-64bit-international-dch.hf.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([])
            });
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request });
    }
}

sealed class MultiSourceHotfixHandler : HttpMessageHandler
{
    public bool ReturnNotModifiedForTpu { get; set; }
    public bool FailGitHubFeed { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;

        if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            if (FailGitHubFeed)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request });

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""
                    {
                      "hotfixes": [
                        {
                          "version": "623.20",
                          "releaseDate": "2026-11-26",
                          "releasePageUrl": "https://nvidia.custhelp.com/app/answers/detail/a_id/6001",
                          "releaseSummary": "Fixes black screen on wakeup."
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Host.Equals("www.techpowerup.com", StringComparison.OrdinalIgnoreCase))
        {
            if (ReturnNotModifiedForTpu && request.Headers.IfNoneMatch.Any())
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotModified) { RequestMessage = request });

            var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""
                    <div class="entry">
                      <div><a href="/download/nvidia-geforce-graphics-drivers/">NVIDIA GeForce 623.10</a> <span>Hotfix</span></div>
                      <span>Nov 25, 2026</span>
                    </div>
                    """, Encoding.UTF8, "text/html")
            };
            ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"tpu-623\"");
            return Task.FromResult(ok);
        }

        if (uri.Host.Equals("news.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""
                    <?xml version="1.0" encoding="utf-8"?>
                    <rss version="2.0">
                      <channel>
                        <item>
                          <title>NVIDIA releases GeForce Hotfix Display Driver 623.05</title>
                          <link>https://news.google.com/articles/geforce-hotfix-623-05</link>
                          <pubDate>Tue, 24 Nov 2026 10:00:00 GMT</pubDate>
                        </item>
                      </channel>
                    </rss>
                    """, Encoding.UTF8, "application/rss+xml")
            });
        }

        if (uri.Host.Equals("www.googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""
                    {
                      "items": [
                        {
                          "title": "GeForce Hotfix Display Driver version 623.15",
                          "link": "https://nvidia.custhelp.com/app/answers/detail/a_id/5999",
                          "snippet": "Fixes shader compilation stutter."
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Host.Equals("nvidia.custhelp.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.Contains("/a_id/5800", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent("""
                        <p>GeForce Hotfix Display Driver version 576.26</p>
                        <a href="https://international.download.nvidia.com/Windows/576.26hf/576.26-desktop-notebook-win10-win11-64bit-international-dch.hf.exe">Download</a>
                        """, Encoding.UTF8, "text/html")
                });
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""
                    <h3><a href="/app/answers/detail/a_id/5800">GeForce Hotfix Display Driver version 576.26</a></h3>
                    """, Encoding.UTF8, "text/html")
            });
        }

        if (uri.Host.Equals("international.download.nvidia.com", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Head)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([])
            });
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request });
    }
}
