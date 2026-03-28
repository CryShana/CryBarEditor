using CryBar;
using CryBar.Bar;
using CryBar.Dependencies;
using CryBar.Indexing;
using CryBar.Sound;

namespace CryBar.Tests;

/// <summary>
/// Integration tests for DependencyFinder using real AoM:Retold game files.
/// Validates against examples from example_dependencies.md.
/// Skipped when game is not installed.
/// </summary>
[Collection("Integration")]
public class DependencyFinderIntegrationTests
{
    static readonly string GamePath =
        Environment.GetEnvironmentVariable("AOMR_GAME_PATH")
        ?? @"C:\Program Files (x86)\Steam\steamapps\common\Age of Mythology Retold\game";

    static bool GameInstalled => Directory.Exists(GamePath);

    /// <summary>
    /// Builds a FileIndex from all BAR files in the game directory.
    /// </summary>
    static FileIndex BuildFullIndex()
    {
        var index = new FileIndex();

        // Add loose root files
        foreach (var file in Directory.GetFiles(GamePath, "*.*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".bar", StringComparison.OrdinalIgnoreCase)) continue;
            var relPath = Path.GetRelativePath(Path.GetDirectoryName(GamePath)!, file);
            index.Add(new FileIndexEntry
            {
                FullRelativePath = relPath,
                FileName = Path.GetFileName(file),
                Source = FileIndexSource.RootFile,
            });
        }

        var barFiles = Directory.GetFiles(GamePath, "*.bar", SearchOption.AllDirectories);
        FileIndexBuilder.IndexBarFiles(index, barFiles);

        return index;
    }

    /// <summary>
    /// Reads and converts an XMB entry to XML text from a specific BAR file.
    /// </summary>
    static string ReadXmlFromBar(string barRelPath, string entryPathContains)
    {
        var barPath = Path.Combine(GamePath, barRelPath);
        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        bar.Load(out _);

        // Match by relative path (not just name) to avoid picking DLC/variant files
        var entry = bar.Entries!.First(e =>
            e.RelativePath.Contains(entryPathContains, StringComparison.OrdinalIgnoreCase));

        var raw = entry.ReadDataDecompressed(stream);
        var xml = ConversionHelper.ConvertXmbToXmlText(raw.Span);
        return xml!;
    }

    /// <summary>
    /// Searches multiple BAR files matching a pattern for an entry, returns (xml, entryPath).
    /// </summary>
    static (string xml, string entryPath)? FindXmlInBars(string barDirRelPath, string barPattern, string entryPathContains)
    {
        var barDir = Path.Combine(GamePath, barDirRelPath);
        if (!Directory.Exists(barDir)) return null;

        foreach (var barPath in Directory.GetFiles(barDir, barPattern))
        {
            try
            {
                using var stream = File.OpenRead(barPath);
                var bar = new BarFile(stream);
                if (!bar.Load(out _)) continue;

                var entry = bar.Entries!.FirstOrDefault(e =>
                    e.RelativePath.Contains(entryPathContains, StringComparison.OrdinalIgnoreCase));
                if (entry == null) continue;

                var raw = entry.ReadDataDecompressed(stream);
                var xml = ConversionHelper.ConvertXmbToXmlText(raw.Span);
                if (xml == null) continue;

                var entryPath = string.IsNullOrEmpty(bar.RootPath)
                    ? entry.RelativePath
                    : Path.Combine(bar.RootPath, entry.RelativePath);
                return (xml, entryPath);
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Builds a SoundsetIndex by parsing all soundset files in Sound.bar and finding bank files.
    /// </summary>
    static SoundsetIndex BuildSoundsetIndex(FileIndex index)
    {
        var soundsetIndex = new SoundsetIndex();

        var barPath = Path.Combine(GamePath, @"sound\Sound.bar");
        if (!File.Exists(barPath)) return soundsetIndex;

        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        if (!bar.Load(out _) || bar.Entries == null) return soundsetIndex;

        // Find bank files
        var bankDir = Path.Combine(GamePath, @"sound\banks\Desktop");
        var banksByName = new Dictionary<string, FileIndexEntry>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(bankDir))
        {
            foreach (var bp in Directory.GetFiles(bankDir, "*.bank"))
            {
                var fileName = Path.GetFileName(bp);
                var entries = index.Find(fileName);
                var entry = entries.Count > 0 ? entries[0] : new FileIndexEntry
                {
                    FullRelativePath = Path.GetRelativePath(Path.GetDirectoryName(GamePath)!, bp),
                    FileName = fileName,
                    Source = FileIndexSource.RootFile,
                };
                var culture = fileName[..^5]; // strip ".bank"
                banksByName[culture] = entry;
            }
        }

        // Parse each soundset file and populate index
        foreach (var barEntry in bar.Entries)
        {
            if (!barEntry.Name.StartsWith("soundsets_", StringComparison.OrdinalIgnoreCase) ||
                !barEntry.Name.Contains(".soundset", StringComparison.OrdinalIgnoreCase))
                continue;

            var culture = SoundsetIndex.ExtractCulture(barEntry.Name);
            if (culture == null) continue;

            try
            {
                var raw = barEntry.ReadDataDecompressed(stream);
                var xml = ConversionHelper.ConvertXmbToXmlText(raw.Span);
                if (xml == null) continue;

                var definitions = SoundsetParser.ParseSoundsetXml(xml);
                var fileEntries = index.Find(barEntry.Name);
                if (fileEntries.Count == 0) continue;

                banksByName.TryGetValue(culture, out var bankFile);
                soundsetIndex.AddFromParsedFile(definitions, culture, fileEntries[0], bankFile);
            }
            catch { }
        }

        return soundsetIndex;
    }

    // Shared index - built once for all tests in this class
    static FileIndex? _sharedIndex;
    static readonly Lock _indexLock = new();

    static FileIndex GetOrBuildIndex()
    {
        if (_sharedIndex != null) return _sharedIndex;
        lock (_indexLock)
        {
            _sharedIndex ??= BuildFullIndex();
            return _sharedIndex;
        }
    }

    // ========= proto.xml =========

    [SkippableFact]
    public void Proto_Hoplite_ResolvesIconAnimfileSoundsetfile()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = GetOrBuildIndex();

        var xml = ReadXmlFromBar(@"data\Data.bar", @"gameplay\proto.xml.XMB");
        var result = DependencyFinder.FindDependencies(xml, @"game\data\gameplay\proto.xml.XMB", index);

        // proto has hundreds of <unit> entities - should be grouped
        Assert.True(result.Groups.Count > 100, $"Expected many entity groups, got {result.Groups.Count}");

        // Find the Hoplite entity
        var hoplite = result.Groups.FirstOrDefault(g =>
            g.EntityName?.Equals("Hoplite", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(hoplite);
        Assert.Equal("unit", hoplite.EntityType);

        // Hoplite should have icon, animfile, soundsetfile paths
        var paths = hoplite.References.Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.True(paths.Count >= 3, $"Hoplite should have at least 3 path refs, got {paths.Count}");

        // Icon should resolve to ui_myth_4k AND ui_myth variants
        var iconRef = paths.FirstOrDefault(r => r.RawValue.Contains("hoplite_icon", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(iconRef);
        Assert.True(iconRef.Resolved.Count >= 2, $"Icon should resolve to at least 2 variants (4K+standard), got {iconRef.Resolved.Count}");

        // Animfile should resolve
        var animRef = paths.FirstOrDefault(r => r.SourceTag == "animfile");
        Assert.NotNull(animRef);
        Assert.True(animRef.Resolved.Count >= 1, "Animfile should resolve");

        // Soundsetfile should resolve
        var soundRef = paths.FirstOrDefault(r => r.SourceTag == "soundsetfile");
        Assert.NotNull(soundRef);
        Assert.True(soundRef.Resolved.Count >= 1, "Soundsetfile should resolve");

        // STR_ keys present
        var strKeys = hoplite.References.Where(r => r.Type == DependencyRefType.StringKey).ToList();
        Assert.Contains(strKeys, r => r.RawValue == "STR_UNIT_HOPLITE_NAME");
    }

    // ========= techtree.xml =========

    [SkippableFact]
    public void Techtree_SecretsOfTheTitans_ResolvesIcon()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = GetOrBuildIndex();

        var xml = ReadXmlFromBar(@"data\Data.bar", "techtree.xml.XMB");
        var result = DependencyFinder.FindDependencies(xml, @"game\data\gameplay\techtree.xml.XMB", index);

        // techtree has many <tech> entities
        Assert.True(result.Groups.Count > 50);

        var secrets = result.Groups.FirstOrDefault(g =>
            g.EntityName?.Equals("SecretsOfTheTitans", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(secrets);
        Assert.Equal("tech", secrets.EntityType);

        var iconRef = secrets.References.FirstOrDefault(r =>
            r.Type == DependencyRefType.FilePath && r.RawValue.Contains("secrets_of_the_titans_icon", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(iconRef);
        Assert.True(iconRef.Resolved.Count >= 2, "Tech icon should resolve to 4K + standard variants");
    }

    // ========= powers.xml =========

    [SkippableFact]
    public void Powers_IncludesResolveToAbilitiesAndGodpowers()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = GetOrBuildIndex();

        var xml = ReadXmlFromBar(@"data\Data.bar", "powers.xml.XMB");
        var result = DependencyFinder.FindDependencies(xml, @"game\data\gameplay\powers.xml.XMB", index);

        // powers.xml has <include> children - no name attr, so should be ungrouped
        var allPaths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.True(allPaths.Count >= 10, $"Expected many include paths, got {allPaths.Count}");

        // abilities\greek.abilities should resolve to game\data\gameplay\abilities\greek.abilities.XMB
        var greekAbilities = allPaths.FirstOrDefault(r =>
            r.RawValue.Contains("greek.abilities", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(greekAbilities);
        Assert.True(greekAbilities.Resolved.Count >= 1, "greek.abilities should resolve");

        // god_powers\greek.godpowers should resolve
        var greekGodpowers = allPaths.FirstOrDefault(r =>
            r.RawValue.Contains("greek.godpowers", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(greekGodpowers);
        Assert.True(greekGodpowers.Resolved.Count >= 1, "greek.godpowers should resolve");
    }

    // ========= godpowers (AOTGBolt) =========

    [SkippableFact]
    public void GodPowers_AOTGBolt_HasSoundsetNames()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = GetOrBuildIndex();

        var xml = ReadXmlFromBar(@"data\Data.bar", "aotg.godpowers.XMB");
        var result = DependencyFinder.FindDependencies(xml, @"game\data\gameplay\god_powers\aotg.godpowers.XMB", index);

        var bolt = result.Groups.FirstOrDefault(g =>
            g.EntityName?.Equals("AOTGBolt", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(bolt);

        var soundsets = bolt.References.Where(r => r.Type == DependencyRefType.SoundsetName).ToList();
        Assert.Contains(soundsets, r => r.RawValue == "LightningStrike");
        Assert.Contains(soundsets, r => r.RawValue == "GodPowerStart");
    }

    // ========= Animfile (hoplite) =========

    [SkippableFact]
    public void AnimFile_Hoplite_ResolvesModelsAndAnimations()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = GetOrBuildIndex();

        // hoplite animfile is in ArtGreek.bar
        var found = FindXmlInBars("art", "ArtGreek.bar", @"infantry\hoplite\hoplite.xml");
        Skip.If(found == null, "hoplite.xml.XMB animfile not found in ArtGreek.bar");

        var (xml, entryPath) = found.Value;
        var result = DependencyFinder.FindDependencies(xml, entryPath, index);

        var allPaths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.True(allPaths.Count >= 5, $"Hoplite animfile should have many path refs, got {allPaths.Count}");

        // TMModel references (e.g. hoplite_iron) should resolve to .tmm, .tmm.data, .fbximport, .material.XMB
        var modelRef = allPaths.FirstOrDefault(r =>
            r.RawValue.Contains("hoplite_iron", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(modelRef);
        Assert.True(modelRef.Resolved.Count >= 2, $"hoplite_iron should resolve to multiple files (.tmm, .fbximport, etc), got {modelRef.Resolved.Count}");

        // Include references (attachment animfiles) should resolve
        var helmRef = allPaths.FirstOrDefault(r =>
            r.RawValue.Contains("hoplite_helm", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(helmRef);
        Assert.True(helmRef.Resolved.Count >= 1, "hoplite_helm include should resolve");
    }

    // ========= Soundset file (hoplite VO) =========

    [SkippableFact]
    public void SoundsetFile_Hoplite_ParsesSoundsetNamesAndResolves()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = GetOrBuildIndex();

        var barPath = Path.Combine(GamePath, @"sound\Sound.bar");
        Skip.IfNot(File.Exists(barPath), "Sound.bar not found");

        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        bar.Load(out _);

        var entry = bar.Entries!.FirstOrDefault(e =>
            e.RelativePath.Contains(@"vo\hoplite\hoplite.xml", StringComparison.OrdinalIgnoreCase));
        Skip.If(entry == null, "hoplite.xml.XMB soundset not found in Sound.bar");

        var raw = entry!.ReadDataDecompressed(stream);
        var xml = ConversionHelper.ConvertXmbToXmlText(raw.Span);
        Assert.NotNull(xml);

        var entryPath = Path.Combine(bar.RootPath!, entry.RelativePath);

        // Build SoundsetIndex by parsing all soundset files
        var soundsetIndex = BuildSoundsetIndex(index);
        var result = DependencyFinder.FindDependencies(xml, entryPath, index, soundsetIndex);

        var soundsets = result.GetAllReferences().Where(r => r.Type == DependencyRefType.SoundsetName).ToList();
        Assert.True(soundsets.Count >= 3, $"Hoplite soundset should have multiple soundset names, got {soundsets.Count}");
        Assert.Contains(soundsets, r => r.RawValue == "GreekMilitarySelect");

        // GreekMilitarySelect should resolve to soundsets_greek.soundset.XMB AND greek.bank
        var greekSelect = soundsets.First(r => r.RawValue == "GreekMilitarySelect");
        Assert.True(greekSelect.Resolved.Count >= 1, "Should resolve to at least soundset file");
        Assert.Contains(greekSelect.Resolved, e =>
            e.FileName.Contains("soundsets_greek", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(greekSelect.Resolved, e =>
            e.FileName.Equals("greek.bank", StringComparison.OrdinalIgnoreCase));
    }

    // ========= Tactics =========

    [SkippableFact]
    public void Tactics_ResolvesImpactEffects()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = GetOrBuildIndex();

        // Find any tactics file in Data.bar
        var barPath = Path.Combine(GamePath, @"data\Data.bar");
        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        bar.Load(out _);

        var entry = bar.Entries!.FirstOrDefault(e =>
            e.Name.EndsWith(".tactics.XMB", StringComparison.OrdinalIgnoreCase));
        Skip.If(entry == null, "No tactics file found");

        var raw = entry!.ReadDataDecompressed(stream);
        var xml = ConversionHelper.ConvertXmbToXmlText(raw.Span);
        Assert.NotNull(xml);

        var entryPath = Path.Combine(bar.RootPath!, entry.RelativePath);
        var result = DependencyFinder.FindDependencies(xml, entryPath, index);

        // Tactics files typically have impacteffect paths and STR_ keys
        var allRefs = result.GetAllReferences().ToList();
        Assert.True(allRefs.Count >= 1, $"Tactics file should have at least 1 reference, got {allRefs.Count}");
    }

    // ========= Particle sets =========

    [SkippableFact]
    public void ParticleSets_ResolvesFilenames()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = GetOrBuildIndex();

        var found = FindXmlInBars("art", "ArtEffects.bar", "particlesets.xml");
        Skip.If(found == null, "particlesets.xml.XMB not found in ArtEffects.bar");

        var (xml, entryPath) = found.Value;
        var result = DependencyFinder.FindDependencies(xml, entryPath, index);

        // particlesets has many <particleset name="..."> entities with .pkfx file refs
        Assert.True(result.Groups.Count >= 10, $"Expected many particleset groups, got {result.Groups.Count}");

        var firstWithPkfx = result.Groups.FirstOrDefault(g =>
            g.References.Any(r => r.RawValue.Contains(".pkfx", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(firstWithPkfx);

        var pkfxRef = firstWithPkfx.References.First(r =>
            r.RawValue.Contains(".pkfx", StringComparison.OrdinalIgnoreCase));
        Assert.True(pkfxRef.Resolved.Count >= 1, $"pkfx reference should resolve, got {pkfxRef.Resolved.Count}");
    }

    // ========= Performance =========

    [SkippableFact]
    public void Proto_FullParse_CompletesUnder800ms()
    {
        const string BAR_FILE = @"game\data\gameplay\proto.xml.XMB";

        Skip.IfNot(GameInstalled, "Game not found");
        var index = GetOrBuildIndex();

        var xml = ReadXmlFromBar(@"data\Data.bar", @"gameplay\proto.xml.XMB");

        // Warmup
        DependencyFinder.FindDependencies(xml, BAR_FILE, index);
        DependencyFinder.FindDependencies(xml, BAR_FILE, index);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = DependencyFinder.FindDependencies(xml, BAR_FILE, index);
        sw.Stop();

        Assert.True(sw.Elapsed.TotalMilliseconds < 800,
            $"proto.xml dependency scan took {sw.ElapsedMilliseconds}ms, expected <800ms");

        // Sanity: should have found many groups and references
        Assert.True(result.Groups.Count > 1000, $"Expected many entity groups, got {result.Groups.Count}");
        Assert.True(result.GetAllReferences().Count() > 1000, $"Expected many references, got {result.GetAllReferences().Count()}");

        var hopliteGroup = result.Groups.Find(x => x.EntityName == "Hoplite");
        Assert.NotNull(hopliteGroup);
        Assert.True(hopliteGroup.References.Count == 7);
    }

    // ========= Speed: animfile dependency scan =========

    [SkippableFact]
    public void AnimFile_Hoplite_DependencyScan_CompletesUnder4ms()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = GetOrBuildIndex();

        var found = FindXmlInBars("art", "ArtGreek.bar", @"infantry\hoplite\hoplite.xml");
        Skip.If(found == null, "hoplite.xml.XMB animfile not found in ArtGreek.bar");

        var (xml, entryPath) = found.Value;

        // Warmup both JIT and caches
        const int WARMUP = 50;
        const int RUNS = 500;
        for (int i = 0; i < WARMUP; i++)
            DependencyFinder.FindDependencies(xml, entryPath, index);

        double totalMs = 0;
        for (int i = 0; i < RUNS; i++)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = DependencyFinder.FindDependencies(xml, entryPath, index);
            totalMs += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        var avgMs = totalMs / RUNS;

        Assert.True(avgMs < 4.0,
            $"Hoplite animfile dependency scan averaged {avgMs:F3}ms, expected <4ms");
    }

    // ========= TMM dependency: hoplite_iron.tmm =========

    [SkippableFact]
    public void Tmm_HopliteIron_ResolvesDataAndMaterial()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = GetOrBuildIndex();
        var animfileIndex = GetOrBuildAnimfileIndex();

        var result = DependencyFinder.FindDependenciesForTmm(
            @"intermediate\modelcache\greek\units\infantry\hoplite\hoplite_iron.tmm", index, animfileIndex);

        var refs = result.Groups[0].References;

        // .tmm.data should resolve
        var dataRef = refs.First(r => r.SourceTag == "geometry");
        Assert.True(dataRef.Resolved.Count >= 1, $"tmm.data should resolve, got {dataRef.Resolved.Count}");
        Assert.Contains(dataRef.Resolved, e => e.FileName.Equals("hoplite_iron.tmm.data", StringComparison.OrdinalIgnoreCase));

        // .material should resolve
        var matRef = refs.First(r => r.SourceTag == "material");
        Assert.True(matRef.Resolved.Count >= 1, $"material should resolve, got {matRef.Resolved.Count}");

        // animfile should resolve via AnimfileIndex (hoplite_iron -> strips _iron -> hoplite)
        var animRef = refs.FirstOrDefault(r => r.SourceTag == "animfile");
        Assert.NotNull(animRef);
        Assert.True(animRef.Resolved.Count >= 1, "animfile should resolve via AnimfileIndex");
    }

    // ========= AnimfileIndex integration: build from real BARs =========

    static AnimfileIndex? _sharedAnimfileIndex;
    static readonly Lock _animfileIndexLock = new();

    static AnimfileIndex GetOrBuildAnimfileIndex()
    {
        if (_sharedAnimfileIndex != null) return _sharedAnimfileIndex;
        lock (_animfileIndexLock)
        {
            _sharedAnimfileIndex ??= BuildAnimfileIndex();
            return _sharedAnimfileIndex;
        }
    }

    /// <summary>
    /// Mirrors the app's RebuildAnimfileIndexAsync: scans all BAR files for animfile XMLs
    /// and builds a reverse index from TMM model stems to animfile entries.
    /// </summary>
    static AnimfileIndex BuildAnimfileIndex()
    {
        var index = new AnimfileIndex();
        var barFiles = Directory.GetFiles(GamePath, "*.bar", SearchOption.AllDirectories);

        foreach (var barPath in barFiles)
        {
            try
            {
                using var stream = File.OpenRead(barPath);
                var bar = new BarFile(stream);
                if (!bar.Load(out _) || bar.Entries == null) continue;

                foreach (var entry in bar.Entries)
                {
                    if (!entry.Name.EndsWith(".xml.XMB", StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        var raw = entry.ReadDataDecompressed(stream);
                        var xmlText = ConversionHelper.ConvertXmbToXmlText(raw.Span);
                        if (xmlText == null || !xmlText.Contains("<animfile", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var modelPaths = CryBar.Export.AnimationDiscovery.FindAllModelsFromAnimXml(xmlText);
                        if (modelPaths.Count == 0) continue;

                        var fullRelPath = string.IsNullOrEmpty(bar.RootPath)
                            ? entry.RelativePath
                            : Path.Combine(bar.RootPath, entry.RelativePath);

                        var animfileEntry = new FileIndexEntry
                        {
                            FullRelativePath = fullRelPath,
                            FileName = entry.Name,
                            Source = FileIndexSource.BarEntry,
                            BarFilePath = barPath,
                            EntryRelativePath = entry.RelativePath,
                        };

                        foreach (var modelPath in modelPaths)
                            index.Add(modelPath, animfileEntry);
                    }
                    catch { }
                }
            }
            catch { }
        }

        return index;
    }

    [SkippableFact]
    public void AnimfileIndex_BuildFromRealBars_FindsHoplite()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var animfileIndex = GetOrBuildAnimfileIndex();

        Assert.True(animfileIndex.Count > 0, $"AnimfileIndex should not be empty after scanning game BARs");

        // Direct lookup: each variant is registered individually
        var ironEntry = animfileIndex.Find("hoplite_iron");
        Assert.NotNull(ironEntry);

        var bronzeEntry = animfileIndex.Find("hoplite_bronze");
        Assert.NotNull(bronzeEntry);

        // All variants should point to the same animfile entry
        Assert.Equal(ironEntry!.FileName, bronzeEntry!.FileName);
    }

    /// <summary>
    /// Diagnostic test: trace every step of animfile XML parsing for the hoplite animfile.
    /// </summary>
    [SkippableFact]
    public void AnimfileIndex_DiagnoseHopliteParsing()
    {
        Skip.IfNot(GameInstalled, "Game not found");

        // Step 1: find ArtGreek.bar
        var artGreekBar = Directory.GetFiles(GamePath, "ArtGreek.bar", SearchOption.AllDirectories);
        Assert.True(artGreekBar.Length > 0, "ArtGreek.bar not found in game directory");

        // Step 2: find hoplite.xml.XMB entry
        using var stream = File.OpenRead(artGreekBar[0]);
        var bar = new BarFile(stream);
        Assert.True(bar.Load(out _), "Failed to load ArtGreek.bar");

        var hopliteEntries = bar.Entries!.Where(e =>
            e.Name.Equals("hoplite.xml.XMB", StringComparison.OrdinalIgnoreCase) &&
            e.RelativePath.Contains("hoplite", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(hopliteEntries.Count > 0,
            $"hoplite.xml.XMB not found. All .xml.XMB entries: {string.Join(", ", bar.Entries!.Where(e => e.Name.EndsWith(".xml.XMB", StringComparison.OrdinalIgnoreCase) && e.RelativePath.Contains("hoplite", StringComparison.OrdinalIgnoreCase)).Select(e => e.RelativePath).Take(10))}");

        var entry = hopliteEntries[0];

        // Step 3: decompress and convert XMB->XML
        var raw = entry.ReadDataDecompressed(stream);
        Assert.True(raw.Length > 0, "Decompressed data is empty");

        var xmlText = ConversionHelper.ConvertXmbToXmlText(raw.Span);
        Assert.NotNull(xmlText);

        // Step 4: check for <animfile> tag
        Assert.True(xmlText.Contains("<animfile", StringComparison.OrdinalIgnoreCase),
            $"XML does not contain <animfile>. First 500 chars: {xmlText[..Math.Min(500, xmlText.Length)]}");

        // Step 5: extract ALL TMModel paths (real animfiles have many nested variants)
        var modelPaths = CryBar.Export.AnimationDiscovery.FindAllModelsFromAnimXml(xmlText);
        Assert.True(modelPaths.Count > 0, "No TMModel paths found in animfile XML");

        // Should contain hoplite_iron (the first/default variant)
        Assert.Contains(modelPaths, p => p.Contains("hoplite_iron", StringComparison.OrdinalIgnoreCase));

        // Step 6: verify stems work for index building
        var stems = modelPaths.Select(p => Path.GetFileNameWithoutExtension(p.Replace('/', '\\'))).ToList();
        Assert.Contains("hoplite_iron", stems, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("hoplite_bronze", stems, StringComparer.OrdinalIgnoreCase);
    }

    // ========= Composite JSON =========

    [SkippableFact]
    public void Composite_ResolvesModelRefs()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = GetOrBuildIndex();

        var barPath = Path.Combine(GamePath, @"art\ArtGreek.bar");
        Skip.IfNot(File.Exists(barPath), "ArtGreek.bar not found");

        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        bar.Load(out _);

        var entry = bar.Entries!.FirstOrDefault(e =>
            e.Name.EndsWith(".composite", StringComparison.OrdinalIgnoreCase));
        Skip.If(entry == null, "No .composite file found in ArtGreek.bar");

        var raw = entry!.ReadDataRaw(stream);
        var decompressed = BarCompression.EnsureDecompressed(raw, out _);
        var content = System.Text.Encoding.UTF8.GetString(decompressed.Span);

        var entryPath = string.IsNullOrEmpty(bar.RootPath)
            ? entry.RelativePath
            : Path.Combine(bar.RootPath, entry.RelativePath);
        var result = DependencyFinder.FindDependencies(content, entryPath, index);

        var paths = result.GetAllReferences().Where(r => r.Type == DependencyRefType.FilePath).ToList();
        Assert.True(paths.Count >= 1, $"Composite should have model_ref paths, got {paths.Count}");

        // At least one should resolve to .tmm or .fbximport
        var resolved = paths.Where(r => r.Resolved.Count > 0).ToList();
        Assert.True(resolved.Count >= 1, "At least one model_ref should resolve to indexed files");
    }
}
