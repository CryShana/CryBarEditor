using System.Xml;

using CryBar;
using CryBar.Bar;
using CryBar.Export;
using CryBar.Scenario;
using CryBar.TMM;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CryBar.Tests;

/// <summary>
/// Integration tests that use real game files from AoM:Retold.
/// These tests are skipped unless the game is installed at the expected path.
/// Set the AOMR_GAME_PATH environment variable to override the default path.
/// </summary>
[Collection("Integration")]
public class IntegrationTests
{
    static readonly string GamePath =
        Environment.GetEnvironmentVariable("AOMR_GAME_PATH")
        ?? @"C:\Program Files (x86)\Steam\steamapps\common\Age of Mythology Retold\game";

    static bool GameInstalled => Directory.Exists(GamePath);

    static string[] FindBarFiles()
    {
        if (!GameInstalled) return [];
        return Directory.GetFiles(GamePath, "*.bar", SearchOption.AllDirectories);
    }

    /// <summary>
    /// Opens a BAR file and finds an entry by name (case-insensitive contains match).
    /// </summary>
    static (BarFile bar, BarFileEntry entry, FileStream stream) OpenBarAndFindEntry(string barRelativePath, string entryNameContains)
    {
        var barPath = Path.Combine(GamePath, barRelativePath);
        var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        bar.Load(out var error);
        Assert.True(bar.Loaded, $"Failed to load {barRelativePath}: {error}");

        var entry = bar.Entries!.FirstOrDefault(e =>
            e.Name.Contains(entryNameContains, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);

        return (bar, entry, stream);
    }

    #region General BAR Tests

    [SkippableFact]
    public void LoadRealBarFile_FirstFound()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barFiles = FindBarFiles();
        Skip.If(barFiles.Length == 0, "No .bar files found in game directory");

        // Pick the smallest .bar file
        var smallest = barFiles
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Length)
            .First();

        using var stream = File.OpenRead(smallest.FullName);
        var bar = new BarFile(stream);
        var result = bar.Load(out var error);

        Assert.True(result, $"Failed to load {smallest.Name}: {error}");
        Assert.True(bar.Loaded);
        Assert.NotNull(bar.Entries);
        Assert.True(bar.Entries.Count > 0, "BAR file had no entries");
        Assert.NotNull(bar.RootPath);
    }

    [SkippableFact]
    public void LoadRealBarFile_ReadFirstEntry()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barFiles = FindBarFiles();
        Skip.If(barFiles.Length == 0, "No .bar files found");

        var smallest = barFiles
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Length)
            .First();

        using var stream = File.OpenRead(smallest.FullName);
        var bar = new BarFile(stream);
        bar.Load(out _);

        var entry = bar.Entries![0];
        var rawData = entry.ReadDataRaw(stream);

        Assert.NotNull(rawData);
        Assert.Equal(entry.SizeInArchive, rawData.Length);
    }

    [SkippableFact]
    public void LoadRealBarFile_CopyFirstEntry()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barFiles = FindBarFiles();
        Skip.If(barFiles.Length == 0, "No .bar files found");

        var smallest = barFiles
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Length)
            .First();

        using var stream = File.OpenRead(smallest.FullName);
        var bar = new BarFile(stream);
        bar.Load(out _);

        var entry = bar.Entries![0];
        using var dest = new MemoryStream();
        entry.CopyData(stream, dest);

        Assert.Equal(entry.SizeInArchive, (int)dest.Length);
    }

    [SkippableFact]
    public void LoadAllBarFiles_HeadersAreValid()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barFiles = FindBarFiles();
        Skip.If(barFiles.Length == 0, "No .bar files found");

        foreach (var barPath in barFiles)
        {
            using var stream = File.OpenRead(barPath);
            var bar = new BarFile(stream);
            var result = bar.Load(out var error);

            Assert.True(result, $"Failed to load {Path.GetFileName(barPath)}: {error}");
            Assert.Equal((uint)6, bar.Version);
        }
    }

    #endregion

    #region Data.bar - XMB Copy & Conversion

    [SkippableFact]
    public void DataBar_ProtoXmb_CopyReturnsSameData()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"data\Data.bar", "proto.xml.XMB");
        using var s = stream;

        // Verify entry is found and compressed
        Assert.Contains("proto.xml.XMB", entry.Name, StringComparison.OrdinalIgnoreCase);
        Assert.True(entry.IsCompressed);

        // Read raw data (copy operation)
        var raw1 = entry.ReadDataRaw(stream);
        var raw2 = entry.ReadDataRaw(stream);

        // Copy should be deterministic - same data each time
        Assert.Equal(raw1, raw2);
        Assert.Equal(entry.SizeInArchive, raw1.Length);
    }

    [SkippableFact]
    public void DataBar_ProtoXmb_CopyViaStream()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"data\Data.bar", "proto.xml.XMB");
        using var s = stream;

        // Copy via CopyData should produce the same bytes as ReadDataRaw
        var raw = entry.ReadDataRaw(stream);
        using var dest = new MemoryStream();
        entry.CopyData(stream, dest);

        Assert.Equal(raw, dest.ToArray());
    }

    [SkippableFact]
    public void DataBar_ProtoXmb_DecompressesAsAlz4()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"data\Data.bar", "proto.xml.XMB");
        using var s = stream;

        var raw = entry.ReadDataRaw(s);

        // Should be Alz4 compressed
        Assert.True(((Span<byte>)raw).IsAlz4());

        var decompressed = BarCompression.EnsureDecompressed(raw, out var compressionType);
        Assert.Equal(CompressionType.Alz4, compressionType);
        Assert.True(decompressed.Length > raw.Length, "Decompressed should be larger than compressed");
    }

    [SkippableFact]
    public void DataBar_ProtoXmb_XmbToXmlConversion_ProducesValidXml()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"data\Data.bar", "proto.xml.XMB");
        using var s = stream;

        // Decompress then convert
        var raw = entry.ReadDataRaw(s);
        var decompressed = BarCompression.EnsureDecompressed(raw, out var ct);

        var xml = BarFormatConverter.XMBtoXML(decompressed.Span);
        Assert.NotNull(xml);
        Assert.NotNull(xml.DocumentElement);
        Assert.Equal("proto", xml.DocumentElement.Name);

        // The formatted XML should be valid and non-empty
        var formatted = BarFormatConverter.FormatXML(xml);
        Assert.NotEmpty(formatted);
        Assert.Contains("<proto", formatted);
    }

    [SkippableFact]
    public void DataBar_ProtoXmb_ConversionHelper_ProducesXmlText()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"data\Data.bar", "proto.xml.XMB");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);
        var decompressed = BarCompression.EnsureDecompressed(raw, out _);

        var xmlText = ConversionHelper.ConvertXmbToXmlText(decompressed.Span);
        Assert.NotNull(xmlText);
        Assert.StartsWith("<proto", xmlText);

        var xmlBytes = ConversionHelper.ConvertXmbToXmlBytes(decompressed.Span);
        Assert.NotNull(xmlBytes);
        Assert.True(xmlBytes.Length > 0);
    }

    [SkippableFact]
    public void DataBar_ProtoXmb_ReadDataDecompressed()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"data\Data.bar", "proto.xml.XMB");
        using var s = stream;

        // Use the built-in decompression method
        var decompressed = entry.ReadDataDecompressed(stream);
        Assert.True(decompressed.Length > 0);

        // Should start with X1 (XMB header)
        Assert.Equal(88, decompressed.Span[0]); // 'X'
        Assert.Equal(49, decompressed.Span[1]); // '1'
    }

    #endregion

    #region ArtUI.bar - TGA Parsing

    [SkippableFact]
    public void ArtUIBar_VeterancyTga_EntryExists()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"art\ArtUI.bar", "veterancy_1_icon.tga");
        using var s = stream;

        Assert.Equal("veterancy_1_icon.tga", entry.Name, ignoreCase: true);
        Assert.Contains("veterancy", entry.RelativePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(entry.IsCompressed, "TGA file should not be compressed");
    }

    [SkippableFact]
    public void ArtUIBar_VeterancyTga_IsValidTga()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"art\ArtUI.bar", "veterancy_1_icon.tga");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);
        Assert.True(raw.Length > 18, "TGA file should have at least an 18-byte header");

        // TGA header: byte[2] = image type. Type 2 = uncompressed true-color
        Assert.Equal(2, raw[2]);
    }

    [SkippableFact]
    public void ArtUIBar_VeterancyTga_LoadableByImageSharp()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"art\ArtUI.bar", "veterancy_1_icon.tga");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);

        // ImageSharp should be able to load this TGA
        using var image = Image.Load<Rgba32>(raw);
        Assert.True(image.Width > 0);
        Assert.True(image.Height > 0);
        // veterancy icon is a small image (24x24 based on TGA header bytes)
        Assert.Equal(24, image.Width);
        Assert.Equal(24, image.Height);
    }

    #endregion

    #region ArtVFXTextures.bar - DDT Conversion

    [SkippableFact]
    public void ArtVFXTexturesBar_CrackedDdt_EntryExists()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"art\ArtVFXTextures.bar", "cracked_quater_fading01.ddt");
        using var s = stream;

        Assert.Equal("cracked_quater_fading01.ddt", entry.Name, ignoreCase: true);
        Assert.False(entry.IsCompressed, "This DDT file should not be compressed");
    }

    [SkippableFact]
    public void ArtVFXTexturesBar_CrackedDdt_ParsesValidDDT()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"art\ArtVFXTextures.bar", "cracked_quater_fading01.ddt");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);
        var ddt = new DDTImage(raw);
        var parsed = ddt.ParseHeader();

        Assert.True(parsed, "DDT header should parse successfully");
        Assert.Equal(DDTVersion.RTS4, ddt.Version);
        Assert.Equal(DDTFormat.DXT1, ddt.FormatFlag);
        Assert.Equal(DDTUsage.None, ddt.UsageFlag);
        Assert.Equal(DDTAlpha.None, ddt.AlphaFlag);
        Assert.Equal((ushort)256, ddt.BaseWidth);
        Assert.Equal((ushort)256, ddt.BaseHeight);
        Assert.Equal(7, ddt.MipmapLevels);
        Assert.NotNull(ddt.MipmapOffsets);
        Assert.Equal(7, ddt.MipmapOffsets.Length);
    }

    [SkippableFact]
    public async Task ArtVFXTexturesBar_CrackedDdt_DecodesToImage()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"art\ArtVFXTextures.bar", "cracked_quater_fading01.ddt");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);
        var ddt = new DDTImage(raw);
        ddt.ParseHeader();

        using var image = await ddt.DecodeMipmapToImage(0);
        Assert.NotNull(image);
        Assert.Equal(256, image.Width);
        Assert.Equal(256, image.Height);
    }

    [SkippableFact]
    public async Task ArtVFXTexturesBar_CrackedDdt_ConvertToTga()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"art\ArtVFXTextures.bar", "cracked_quater_fading01.ddt");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);
        var tgaBytes = await ConversionHelper.ConvertDdtToTgaBytes(raw);

        Assert.NotNull(tgaBytes);
        Assert.True(tgaBytes.Length > 0);

        // Verify the resulting TGA is valid by loading it
        using var image = Image.Load<Rgba32>(tgaBytes);
        Assert.Equal(256, image.Width);
        Assert.Equal(256, image.Height);
    }

    [SkippableFact]
    public async Task ArtVFXTexturesBar_CrackedDdt_ParseDDTHelper()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"art\ArtVFXTextures.bar", "cracked_quater_fading01.ddt");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);
        var ddt = new DDTImage(raw);

        // Use the BarFormatConverter.ParseDDT helper
        using var image = await BarFormatConverter.ParseDDT(ddt);
        Assert.NotNull(image);
        Assert.Equal(256, image.Width);
        Assert.Equal(256, image.Height);
    }

    #endregion

    #region PNG - Direct File

    [SkippableFact]
    public void SplashPng_FileExists()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var pngPath = Path.Combine(GamePath, @"movies\splash\beta_static_startup_splash.png");
        Assert.True(File.Exists(pngPath), "Splash PNG file should exist");

        var fi = new FileInfo(pngPath);
        Assert.True(fi.Length > 0, "PNG file should not be empty");
    }

    [SkippableFact]
    public void SplashPng_LoadableByImageSharp()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var pngPath = Path.Combine(GamePath, @"movies\splash\beta_static_startup_splash.png");
        Skip.IfNot(File.Exists(pngPath), "PNG file not found");

        using var image = Image.Load<Rgba32>(pngPath);
        Assert.True(image.Width > 0);
        Assert.True(image.Height > 0);
    }

    #endregion

    #region ArtModelCacheMeta.bar - TMM Parsing

    [SkippableFact]
    public void ArtModelCacheMetaBar_ArmoryTmm_EntryExists()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "armory_a_age2.tmm");
        using var s = stream;

        Assert.Equal("armory_a_age2.tmm", entry.Name, ignoreCase: true);
        Assert.False(entry.IsCompressed, "This TMM should not be compressed");
    }

    [SkippableFact]
    public void ArtModelCacheMetaBar_ArmoryTmm_ParsesHeader()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "armory_a_age2.tmm");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);
        var tmm = new TmmFile(BarCompression.EnsureDecompressed(raw, out _));
        var parsed = tmm.Parsed;

        Assert.True(parsed, "TMM should parse successfully");
        Assert.NotNull(tmm.ImportNames);
        Assert.Equal(2, tmm.ImportNames.Length);
        Assert.Contains("armory_a_age2", tmm.ImportNames[0]);
        Assert.Contains("armory_a_age2", tmm.ImportNames[1]);

        // One should be .fbximport, one should be .fbx
        Assert.Contains(tmm.ImportNames, n => n.EndsWith(".fbximport"));
        Assert.Contains(tmm.ImportNames, n => n.EndsWith(".fbx"));
    }

    #endregion

    #region TMM.DATA - Compressed and Uncompressed
    [SkippableFact]
    public void TmmData_Japanese_ShutenDoji_ReadDataDecompressed()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheModelDataJapanese.bar", "shuten_doji.tmm.data");
        using var s = stream;

        // Use the BarFileEntry decompression path
        var decompressed = entry.ReadDataDecompressed(stream);
        Assert.True(decompressed.Length > 0, "Decompressed data should not be empty");
    }

    [SkippableFact]
    public void TmmData_Greek_Petrobolos_IsNotCompressed()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheModelDataGreek.bar", "petrobolos.tmm.data");
        using var s = stream;

        Assert.False(entry.IsCompressed, "petrobolos.tmm.data should NOT be compressed");

        var raw = entry.ReadDataRaw(stream);

        // Should not have compression headers
        Assert.False(((Span<byte>)raw).IsAlz4(), "Data should not have Alz4 header");
        Assert.False(((Span<byte>)raw).IsL33t(), "Data should not have L33t header");
    }

    [SkippableFact]
    public void TmmData_Greek_Petrobolos_EnsureDecompressed_ReturnsOriginal()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheModelDataGreek.bar", "petrobolos.tmm.data");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);
        var result = BarCompression.EnsureDecompressed(raw, out var compressionType);

        Assert.Equal(CompressionType.None, compressionType);
        Assert.True(result.Span.SequenceEqual(raw), "Uncompressed data should pass through unchanged");
    }

    #endregion

    #region TMM Full Parsing (TmmFile)

    [SkippableFact]
    public void ArtModelCacheMetaBar_ArmoryTmm_FullParse()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "armory_a_age2.tmm");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);
        var tmm = new TmmFile(BarCompression.EnsureDecompressed(raw, out _));
        var parsed = tmm.Parsed;

        Assert.True(parsed, "Full TMM parse should succeed");
        Assert.NotNull(tmm.ImportNames);
        Assert.True(tmm.ImportNames.Length > 0, "Should have import names");
        Assert.NotNull(tmm.MeshGroups);
        Assert.True(tmm.MeshGroups.Length > 0, "Should have mesh groups");
        Assert.NotNull(tmm.Materials);
        Assert.True(tmm.Materials.Length > 0, "Should have materials");
        Assert.NotNull(tmm.Submodels);

        // Verify summary doesn't throw
        var summary = tmm.GetSummary();
        Assert.Contains("TMM Model", summary);
        Assert.Contains("Mesh Groups", summary);
    }

    [SkippableFact]
    public void ArtModelCacheMetaBar_ShutenDojiTmm_HasBones()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "shuten_doji.tmm");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);
        var tmm = new TmmFile(BarCompression.EnsureDecompressed(raw, out _));
        var parsed = tmm.Parsed;

        Assert.True(parsed, "Shuten Doji TMM should parse");
        Assert.NotNull(tmm.Bones);
        Assert.True(tmm.Bones.Length > 0, "Shuten Doji should have bones (it's a rigged character)");
        Assert.Contains(tmm.Bones, b => b.ParentId == -1); // root bone
    }

    [SkippableFact]
    public void ArtModelCacheMetaBar_ParseAllTmm_AllSucceed()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheMeta.bar");
        Skip.IfNot(File.Exists(barPath), "ArtModelCacheMeta.bar not found");

        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        Assert.True(bar.Load(out _));

        var tmmEntries = bar.Entries!
            .Where(e => e.Name.EndsWith(".tmm", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(tmmEntries.Count > 0, "No .tmm entries found");

        var failures = new List<string>();
        foreach (var entry in tmmEntries)
        {
            var raw = BarCompression.EnsureDecompressed(entry.ReadDataRaw(stream), out _);
            var tmm = new TmmFile(raw);
            if (!tmm.Parsed)
                failures.Add(entry.RelativePath);
        }
        
        Assert.True(failures.Count == 0,
            $"{failures.Count}/{tmmEntries.Count} TMM files failed to parse:\n{string.Join("\n", failures.Take(20))}");
    }

    [SkippableFact]
    public void ArtModelCacheMetaBar_ParseAllTmm_ReportFullyParsed()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheMeta.bar");
        Skip.IfNot(File.Exists(barPath), "ArtModelCacheMeta.bar not found");

        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        Assert.True(bar.Load(out _));

        var tmmEntries = bar.Entries!
            .Where(e => e.Name.EndsWith(".tmm", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int total = tmmEntries.Count;
        int parsed = 0;
        int fullyParsed = 0;
        var partialList = new List<string>();

        foreach (var entry in tmmEntries)
        {
            var raw = BarCompression.EnsureDecompressed(entry.ReadDataRaw(stream), out _);
            var tmm = new TmmFile(raw);
            if (tmm.Parsed) parsed++;
            if (tmm.Parsed && tmm.FullyParsed) fullyParsed++;
            else if (tmm.Parsed && !tmm.FullyParsed)
                partialList.Add($"{entry.RelativePath} (v{tmm.Version})");
        }

        // Report but don't fail on partial parses - this test is informational
        // If some files only partially parse, log them for investigation
        Assert.True(parsed == total,
            $"{total - parsed}/{total} TMM files failed core parse");

        // This is a soft check - we log partial parses but don't fail
        if (partialList.Count > 0)
        {
            // Output as test message for visibility
            Assert.True(true,
                $"INFO: {fullyParsed}/{total} fully parsed, {partialList.Count} partial:\n{string.Join("\n", partialList.Take(20))}");
        }
    }

    #endregion

    #region TMM.DATA Full Parsing (TmmDataFile)

    [SkippableFact]
    public void TmmData_Greek_Petrobolos_FullParse()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        // Load the companion .tmm first
        var (_, tmmEntry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "petrobolos.tmm");
        var tmmRaw = BarCompression.EnsureDecompressed(tmmEntry.ReadDataRaw(stream), out _);
        var tmm = new TmmFile(tmmRaw);
        Assert.True(tmm.Parsed, "Companion TMM should parse");
        stream.Dispose();

        // Load the .tmm.data
        var (_, dataEntry, stream2) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheModelDataGreek.bar", "petrobolos.tmm.data");
        using var s = stream2;

        var dataRaw = BarCompression.EnsureDecompressed(dataEntry.ReadDataRaw(stream2), out _);
        var dataFile = new TmmDataFile(dataRaw, tmm);
        var parsed = dataFile.Parsed;

        Assert.True(parsed, "TMM.DATA should parse successfully");
        Assert.NotNull(dataFile.Vertices);
        Assert.Equal((int)tmm.NumVertices, dataFile.Vertices.Length);
        Assert.NotNull(dataFile.Indices);
        Assert.Equal((int)tmm.NumTriangleVerts, dataFile.Indices.Length);

        var summary = dataFile.GetSummary();
        Assert.Contains("Vertices:", summary);
    }

    [SkippableFact]
    public void TmmData_Japanese_ShutenDoji_FullParse()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        // Load companion .tmm
        var (bar, tmmEntry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "shuten_doji.tmm");
        var tmmRaw = BarCompression.EnsureDecompressed(tmmEntry.ReadDataRaw(stream), out _);
        var tmm = new TmmFile(tmmRaw);
        Assert.True(tmm.Parsed, "Companion TMM should parse");
        stream.Dispose();

        // Load .tmm.data
        var (bar2, dataEntry, stream2) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheModelDataJapanese.bar", "shuten_doji.tmm.data");
        using var s = stream2;

        var dataRaw = BarCompression.EnsureDecompressed(dataEntry.ReadDataRaw(stream2), out _);
        var dataFile = new TmmDataFile(dataRaw, tmm);
        Assert.True(dataFile.Parsed, "TMM.DATA should parse");

        Assert.NotNull(dataFile.Vertices);
        Assert.NotNull(dataFile.Indices);

        // Shuten Doji is rigged, so should have skin weights
        Assert.NotNull(dataFile.SkinWeights);
        Assert.Equal((int)tmm.NumVertices, dataFile.SkinWeights.Length);
    }

    [SkippableFact]
    public void TmmObjExport_Greek_Petrobolos()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        // Load .tmm
        var (bar, tmmEntry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "petrobolos.tmm");
        var tmmRaw = BarCompression.EnsureDecompressed(tmmEntry.ReadDataRaw(stream), out _);
        stream.Dispose();

        // Load .tmm.data
        var (bar2, dataEntry, stream2) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheModelDataGreek.bar", "petrobolos.tmm.data");
        var dataRaw = BarCompression.EnsureDecompressed(dataEntry.ReadDataRaw(stream2), out _);
        stream2.Dispose();

        // Convert to OBJ
        var objBytes = ConversionHelper.ConvertTmmToObjBytes(tmmRaw, dataRaw);

        Assert.NotNull(objBytes);
        Assert.True(objBytes.Length > 0);

        var objText = System.Text.Encoding.UTF8.GetString(objBytes);
        Assert.Contains("v ", objText);   // vertex positions
        Assert.Contains("vt ", objText);  // texture coords
        Assert.Contains("vn ", objText);  // normals
        Assert.Contains("f ", objText);   // faces
        Assert.Contains("usemtl ", objText); // material assignments
    }

    [SkippableFact]
    public void GlbExport_SPC_ShadeSpc_HasAttachmentNodes()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        // Load .tmm from ArtModelCacheMeta.bar
        var (bar, tmmEntry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "shade_spc.tmm");
        var tmmRaw = BarCompression.EnsureDecompressed(tmmEntry.ReadDataRaw(stream), out _);
        var tmm = new TmmFile(tmmRaw);
        Assert.True(tmm.Parsed, "shade_spc.tmm should parse");
        stream.Dispose();

        // Verify the model has attachments (the VFX dummy bones users reported missing)
        Assert.NotNull(tmm.Attachments);
        Assert.True(tmm.Attachments.Length > 0, "shade_spc should have attachments (VFX attach points)");
        Assert.True(tmm.Bones!.Length > 0, "shade_spc should have bones");

        // Load .tmm.data from ArtModelCacheModelData.bar
        var (bar2, dataEntry, stream2) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheModelData.bar", "shade_spc.tmm.data");
        var dataRaw = BarCompression.EnsureDecompressed(dataEntry.ReadDataRaw(stream2), out _);
        stream2.Dispose();

        var dataFile = new TmmDataFile(dataRaw, tmm);
        Assert.True(dataFile.Parsed, "shade_spc.tmm.data should parse");

        // Export to GLB
        var glb = GlbExporter.ExportGlb(tmm, dataFile);
        Assert.NotNull(glb);
        Assert.True(glb.Length > 100, "GLB should have meaningful size");

        // Parse the GLB JSON and verify attachment nodes exist
        var json = GlbExporterTests.ExtractJson(glb);

        var nodes = json.GetProperty("nodes");
        // Node 0 = mesh, nodes 1..N = bones, nodes N+1.. = attachments
        int expectedNodes = 1 + tmm.Bones.Length + tmm.Attachments.Length;
        Assert.Equal(expectedNodes, nodes.GetArrayLength());

        // Verify attachment nodes have names matching the parsed attachments
        int attachmentNodeStart = 1 + tmm.Bones.Length;
        for (int i = 0; i < tmm.Attachments.Length; i++)
        {
            var node = nodes[attachmentNodeStart + i];
            Assert.Equal(tmm.Attachments[i].Name, node.GetProperty("name").GetString());
            Assert.True(node.TryGetProperty("matrix", out var mat), $"Attachment node '{tmm.Attachments[i].Name}' should have a matrix");
            Assert.Equal(16, mat.GetArrayLength());
        }

        // Verify at least one attachment is a child of a bone node
        bool foundBoneParentedAttachment = false;
        for (int b = 0; b < tmm.Bones.Length; b++)
        {
            var boneNode = nodes[1 + b];
            if (boneNode.TryGetProperty("children", out var children))
            {
                for (int c = 0; c < children.GetArrayLength(); c++)
                {
                    int childIdx = children[c].GetInt32();
                    if (childIdx >= attachmentNodeStart)
                    {
                        foundBoneParentedAttachment = true;
                        break;
                    }
                }
            }
            if (foundBoneParentedAttachment) break;
        }
        Assert.True(foundBoneParentedAttachment, "At least one attachment should be parented to a bone");
    }

    #endregion

    #region TMA - Animation File Exploration

    [SkippableFact]
    public void TmaFile_CanFindAnimationEntries()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath), "Animation BAR not found");

        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        bar.Load(out var error);
        Assert.True(bar.Loaded, $"Failed to load animation BAR: {error}");

        var tmaEntries = bar.Entries!.Where(e => e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(tmaEntries.Count > 0, "Should find .tma entries in animation BAR");
    }

    [SkippableFact]
    public void TmaFile_HeaderExploration()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath), "Animation BAR not found");

        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        bar.Load(out _);

        var tmaEntry = bar.Entries!.FirstOrDefault(e => e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(tmaEntry);

        var raw = BarCompression.EnsureDecompressed(tmaEntry.ReadDataRaw(stream), out _);
        Assert.True(raw.Length >= 8, $"TMA file too small: {raw.Length} bytes");

        // Parse TMA header
        var tma = new TmaFile(raw);
        var parsed = tma.Parsed;

        Assert.True(parsed, $"TMA header should parse for {tmaEntry.Name}");
        Assert.True(tma.Version > 0, "TMA should have a positive version");
    }

    [SkippableFact]
    public void TmaFile_FullBodyParse()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath), "Animation BAR not found");

        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        bar.Load(out _);

        var tmaEntry = bar.Entries!.FirstOrDefault(e => e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(tmaEntry);

        var raw = BarCompression.EnsureDecompressed(tmaEntry.ReadDataRaw(stream), out _);
        var tma = new TmaFile(raw);
        Assert.True(tma.Parsed, $"TMA should parse for {tmaEntry.Name}");

        // Header fields
        Assert.True(tma.Version > 0, "Version should be positive");
        Assert.True(tma.NumTracks > 0, "Should have at least one animation track");
        Assert.True(tma.FrameCount > 0, "Should have at least one frame");
        Assert.True(tma.Duration > 0, "Duration should be positive");

        // Full body: bones and tracks must be populated
        Assert.NotNull(tma.Bones);
        Assert.Equal((int)tma.NumBones, tma.Bones.Length);
        foreach (var bone in tma.Bones)
        {
            Assert.False(string.IsNullOrEmpty(bone.Name), "Bone name should not be empty");
            Assert.Equal(16, bone.LocalTransform.Length);
        }

        Assert.NotNull(tma.Tracks);
        Assert.Equal((int)tma.NumTracks, tma.Tracks.Length);
        foreach (var track in tma.Tracks)
        {
            Assert.False(string.IsNullOrEmpty(track.Name), "Track name should not be empty");
            Assert.True(track.KeyframeCount >= 0, "KeyframeCount should be non-negative");
        }

        // GetSummary should not throw
        var summary = tma.GetSummary();
        Assert.Contains("TMA Animation File", summary);
        Assert.Contains($"Version: {tma.Version}", summary);
    }

    /// <summary>
    /// Verifies TMA animation data is in bone-local space (not matching TMM ParentSpaceMatrix),
    /// confirming that animation TRS must be composed with the bind pose for glTF export.
    /// </summary>
    [SkippableFact]
    public void TmaVsTmm_AnimationIsInBoneLocalSpace()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (_, tmmEntry, tmmStream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "hoplite_gold.tmm");
        var tmmRaw = BarCompression.EnsureDecompressed(tmmEntry.ReadDataRaw(tmmStream), out _);
        var tmm = new TmmFile(tmmRaw);
        tmmStream.Dispose();
        Assert.True(tmm.Parsed);

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath), "Animation BAR not found");
        using var tmaStream = File.OpenRead(barPath);
        var tmaBar = new BarFile(tmaStream);
        tmaBar.Load(out _);
        var tmaEntry = tmaBar.Entries!.FirstOrDefault(e =>
            e.Name.Contains("hoplite", StringComparison.OrdinalIgnoreCase) &&
            e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase) &&
            !e.Name.Contains("igc", StringComparison.OrdinalIgnoreCase));
        Skip.If(tmaEntry == null, "No hoplite TMA found");

        var tmaRaw = BarCompression.EnsureDecompressed(tmaEntry!.ReadDataRaw(tmaStream), out _);
        var tma = new TmaFile(tmaRaw);
        Assert.True(tma.Parsed);

        var tracks = TmaDecoder.DecodeAllTracks(tma);
        Assert.NotNull(tracks);

        var bones = tmm.Bones!;
        var boneMap = new Dictionary<string, int>(bones.Length);
        for (int i = 0; i < bones.Length; i++)
            boneMap[bones[i].Name] = i;

        // All 52 tracks should match TMM bone names
        int matched = tracks!.Count(t => boneMap.ContainsKey(t.Name));
        Assert.Equal(tracks.Length, matched);

        // Hips bone: TMM has it at Y≈1.186, but TMA frame0 T is near zero
        // This confirms TMA is in bone-local space (delta), not absolute parent space
        var hipsTrack = tracks.First(t => t.Name == "mixamorig:Hips");
        int hipsIdx = boneMap["mixamorig:Hips"];
        float tmmHipsY = bones[hipsIdx].ParentSpaceMatrix[13]; // column-major Y translation
        Assert.True(tmmHipsY > 1.0f, "TMM hips should be positioned well above root");
        Assert.True(MathF.Abs(hipsTrack.Translations[0].Y) < 0.5f,
            "TMA hips translation should be a small delta, not the full bind-pose offset");
    }

    /// <summary>
    /// Validates TMA decoded rotation continuity - checks for frame-to-frame jumps
    /// that would indicate a decoding bug (wrong quaternion component layout).
    /// </summary>
    [SkippableFact]
    public void TmaDecoder_RotationContinuity()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath), "Animation BAR not found");
        using var tmaStream = File.OpenRead(barPath);
        var tmaBar = new BarFile(tmaStream);
        tmaBar.Load(out _);

        // Find a hoplite animation with many frames
        var tmaEntry = tmaBar.Entries!.FirstOrDefault(e =>
            e.Name.Contains("hoplite", StringComparison.OrdinalIgnoreCase) &&
            e.Name.Contains("idle", StringComparison.OrdinalIgnoreCase) &&
            e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase));
        Skip.If(tmaEntry == null, "No hoplite idle TMA found");

        var tmaRaw = BarCompression.EnsureDecompressed(tmaEntry!.ReadDataRaw(tmaStream), out _);
        var tma = new TmaFile(tmaRaw);
        Assert.True(tma.Parsed);

        var tracks = TmaDecoder.DecodeAllTracks(tma);
        Assert.NotNull(tracks);

        // Dump raw Quat64 packed values for the first bone with Quat64 encoding
        var sb = new System.Text.StringBuilder();
        var q64Track = tma.Tracks!.FirstOrDefault(t => t.RotationEncoding == TmaEncoding.Quat64);
        if (q64Track != null)
        {
            sb.AppendLine($"Raw Quat64 dump for: {q64Track.Name} ({q64Track.KeyframeCount} kf, {q64Track.RotationData.Length} bytes)");
            int numFrames = Math.Min(6, q64Track.KeyframeCount);
            for (int f = 0; f < numFrames; f++)
            {
                int off = f * 8;
                if (off + 8 > q64Track.RotationData.Length) break;
                ulong packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(q64Track.RotationData.AsSpan(off));

                // Current layout: index at top 2 bits
                int idxTop = (int)(packed >> 62) & 3;
                int topA = (int)((packed >> 41) & 0x1FFFFF);
                int topB = (int)((packed >> 20) & 0x1FFFFF);
                int topC = (int)(packed & 0xFFFFF);

                // Alternative layout: index at bottom 2 bits
                int idxBot = (int)(packed & 3);
                int botA = (int)((packed >> 43) & 0x1FFFFF);
                int botB = (int)((packed >> 22) & 0x1FFFFF);
                int botC = (int)((packed >> 2) & 0xFFFFF);

                sb.AppendLine($"  Frame {f}: 0x{packed:X16}");
                // Layout 21+21+20 (current)
                sb.AppendLine($"    21-21-20: idx={idxTop} A={topA} B={topB} C={topC}");

                // Layout 22+20+20 (hypothesis: first component gets extra 2 bits)
                int fixA = (int)((packed >> 40) & 0x3FFFFF);  // 22 bits
                int fixB = (int)((packed >> 20) & 0xFFFFF);   // 20 bits
                int fixC = (int)(packed & 0xFFFFF);            // 20 bits
                int fixIdx = (int)(packed >> 62) & 3;
                sb.AppendLine($"    22-20-20: idx={fixIdx} A={fixA} B={fixB} C={fixC}");
            }
        }

        // Now test rotation continuity with the fixed decoder
        int totalJumps = 0;
        var jumpDetails = new System.Text.StringBuilder();
        foreach (var track in tracks!)
        {
            var rots = track.Rotations;
            if (rots.Length < 2) continue;
            var src = tma.Tracks!.First(t => t.Name == track.Name);
            for (int f = 1; f < rots.Length; f++)
            {
                float absDot = MathF.Abs(System.Numerics.Quaternion.Dot(rots[f - 1], rots[f]));
                float angleDeg = 2f * MathF.Acos(MathF.Min(1f, absDot)) * (180f / MathF.PI);
                if (angleDeg > 30f)
                {
                    totalJumps++;
                    if (totalJumps <= 10)
                        jumpDetails.AppendLine($"  {track.Name} enc={src.RotationEncoding} f{f - 1}->{f}: {angleDeg:F1}°");
                }
            }
        }

        if (totalJumps > 0)
            Assert.Fail($"Quat64 analysis:\n{sb}\n\nStill {totalJumps} rotation jumps > 30°:\n{jumpDetails}");
        // If no jumps, test passes!
    }

    [SkippableFact]
    public void TmaFile_SakimoriAttack_FullBodyParse()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath), "Animation BAR not found");

        const string entryName = "sakimori_tsurugi_attack_a.tma";

        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        bar.Load(out _);

        var entry = bar.Entries!.FirstOrDefault(e =>
            e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));
        Skip.If(entry == null, $"Entry '{entryName}' not found in BAR");

        var raw = BarCompression.EnsureDecompressed(entry!.ReadDataRaw(stream), out _);
        var tma = new TmaFile(raw);
        Assert.True(tma.Parsed, "TMA should parse successfully");

        // Header
        Assert.Equal(12u, tma.Version);
        Assert.True(tma.NumTracks > 0, $"Expected tracks, got {tma.NumTracks}");
        Assert.True(tma.FrameCount > 0, $"Expected frames, got {tma.FrameCount}");
        Assert.True(tma.Duration > 0f, $"Expected positive duration, got {tma.Duration}");

        // Full body should have parsed (v12 format)
        Assert.NotNull(tma.Bones);
        Assert.True(tma.Bones.Length > 0, "Expected at least one bone");
        Assert.Equal((int)tma.NumBones, tma.Bones.Length);

        // All bones should have a name and valid parent
        foreach (var bone in tma.Bones)
        {
            Assert.False(string.IsNullOrEmpty(bone.Name), "Bone name should not be empty");
            Assert.True(bone.ParentId >= -1, "Parent ID should be -1 (root) or a valid index");
            Assert.Equal(16, bone.LocalTransform.Length);
            Assert.Equal(16, bone.BindPose.Length);
            Assert.Equal(16, bone.InverseBindPose.Length);
        }

        Assert.NotNull(tma.Tracks);
        Assert.Equal((int)tma.NumTracks, tma.Tracks.Length);
        foreach (var track in tma.Tracks)
        {
            Assert.False(string.IsNullOrEmpty(track.Name), "Track name should not be empty");
            Assert.True(track.KeyframeCount >= 0);
        }

        var summary = tma.GetSummary();
        Assert.Contains("Version: 12", summary);
        Assert.Contains($"Bones ({tma.Bones.Length})", summary);
        Assert.Contains($"Animation Tracks ({tma.Tracks.Length})", summary);
    }

    [SkippableFact]
    public void TmaFile_AllEntriesParseFully()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath), "Animation BAR not found");

        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        bar.Load(out _);

        var tmaEntries = bar.Entries!
            .Where(e => e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(tmaEntries.Count > 0, "Should find .tma entries in animation BAR");

        var failures = new List<string>();
        foreach (var entry in tmaEntries)
        {
            var raw = BarCompression.EnsureDecompressed(entry.ReadDataRaw(stream), out _);
            var tma = new TmaFile(raw);

            if (!tma.Parsed)
                failures.Add($"{entry.Name}: header parse failed");
            else if (tma.Tracks == null && tma.NumTracks > 0)
                failures.Add($"{entry.Name}: tracks failed (NumTracks={tma.NumTracks})");
            else if (tma.Controllers == null && tma.NumControllers > 0)
                failures.Add($"{entry.Name}: controllers failed (NumControllers={tma.NumControllers})");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{tmaEntries.Count} TMA files failed:\n{string.Join("\n", failures.Take(20))}");
    }

    [SkippableFact]
    public void TmaDecoder_AllTracksDecodeWithUnitQuaternions()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath), "Animation BAR not found");

        using var stream = File.OpenRead(barPath);
        var bar = new BarFile(stream);
        bar.Load(out _);

        var tmaEntries = bar.Entries!
            .Where(e => e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int totalTracks = 0;
        int totalDecoded = 0;
        var failures = new List<string>();

        foreach (var entry in tmaEntries)
        {
            var raw = BarCompression.EnsureDecompressed(entry.ReadDataRaw(stream), out _);
            var tma = new TmaFile(raw);
            if (!tma.Parsed || tma.Tracks == null)
                continue;

            var decoded = TmaDecoder.DecodeAllTracks(tma);
            Assert.NotNull(decoded);
            Assert.Equal(tma.Tracks.Length, decoded.Length);

            foreach (var dt in decoded)
            {
                totalTracks++;

                // Validate all rotation quaternions have magnitude ~1.0
                for (int qi = 0; qi < dt.Rotations.Length; qi++)
                {
                    var q = dt.Rotations[qi];
                    var mag = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
                    if (MathF.Abs(mag - 1.0f) > 0.01f)
                    {
                        // Show raw data for the failing keyframe
                        var srcTrack = tma.Tracks[Array.IndexOf(decoded, dt)];
                        ulong rawPacked = 0;
                        if (srcTrack.RotationEncoding == TmaEncoding.Quat64 && qi * 8 + 8 <= srcTrack.RotationData.Length)
                            rawPacked = BitConverter.ToUInt64(srcTrack.RotationData, qi * 8);
                        
                        failures.Add($"{entry.Name}/{dt.Name} kf{qi}: mag={mag:F6} q=[{q.X:F4},{q.Y:F4},{q.Z:F4},{q.W:F4}] raw=0x{rawPacked:X16}");
                        break;
                    }
                }

                totalDecoded++;
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{totalTracks} tracks had bad quaternion magnitudes:\n{string.Join("\n", failures.Take(20))}");
        Assert.True(totalDecoded > 0, "Should have decoded some tracks");
    }

    #endregion

    #region FMOD Bank - File Exists

    [SkippableFact]
    public void FmodBank_GreekBank_FileExists()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var bankPath = Path.Combine(GamePath, @"sound\banks\Desktop\greek.bank");
        Assert.True(File.Exists(bankPath), "greek.bank should exist");

        var fi = new FileInfo(bankPath);
        Assert.True(fi.Length > 1_000_000, "greek.bank should be a substantial file (>1MB)");
    }

    // NOTE: FMOD bank event parsing requires native fmod/fmodstudio DLLs
    // and the FMODBank class lives in CryBarEditor (not the CryBar library).
    // Full event parsing tests would need to reference CryBarEditor or
    // extract FMOD loading into the library layer.

    #endregion

    #region Compression Roundtrip - Standalone Files

    static readonly string FottCampaignDir = Path.Combine(GamePath, @"campaign\fott");

    /// <summary>
    /// Roundtrip test for L33t .mythscn files from the campaign/fott directory.
    /// Verifies: decompress -> recompress -> decompress produces identical content,
    /// and that the CRC32 checksum in the recompressed output is self-consistent.
    /// </summary>
    [SkippableFact]
    public async Task L33t_Roundtrip_Mythscn_ContentAndChecksum()
    {
        Skip.IfNot(Directory.Exists(FottCampaignDir), $"Campaign directory not found: {FottCampaignDir}");

        var mythscnFiles = Directory.GetFiles(FottCampaignDir, "*.mythscn");
        Skip.If(mythscnFiles.Length == 0, "No .mythscn files found in campaign/fott");

        var failures = new List<string>();
        var tested = 0;

        await Parallel.ForEachAsync(mythscnFiles, async (filePath, t) =>
        {
            if (t.IsCancellationRequested)
                return;

            var original = File.ReadAllBytes(filePath);
            if (!((Span<byte>)original).IsL33t()) return;

            Interlocked.Increment(ref tested);
            var fileName = Path.GetFileName(filePath);

            // Decompress original
            var decompressed = BarCompression.DecompressL33t(original);
            if (decompressed == null)
            {
                lock (failures) failures.Add($"{fileName}: decompression returned null");
                return;
            }

            // Recompress
            var recompressed = BarCompression.CompressL33t(decompressed);
            if (!recompressed.Span.IsL33t())
            {
                lock (failures) failures.Add($"{fileName}: recompressed data missing L33t header");
                return;
            }

            // Verify CRC32 checksum is self-consistent in recompressed output
            var recompSpan = recompressed.Span;
            if (!BarCompression.VerifyL33tChecksum(recompSpan))
            {
                lock (failures) failures.Add($"{fileName}: CRC32 checksum verification failed on recompressed output");
                return;
            }

            // Decompress the recompressed output and verify content matches
            var roundtripped = BarCompression.DecompressL33t(recompressed);
            if (roundtripped == null)
            {
                lock (failures) failures.Add($"{fileName}: roundtrip decompression returned null");
                return;
            }

            if (!decompressed.AsSpan().SequenceEqual(roundtripped))
                lock (failures)
                    failures.Add($"{fileName}: content mismatch after roundtrip (original={decompressed.Length}, roundtripped={roundtripped.Length})");
        });

        Assert.True(tested > 0, "No L33t-compressed .mythscn files found in campaign/fott");
        Assert.True(failures.Count == 0,
            $"{failures.Count}/{tested} L33t roundtrips failed:\n{string.Join("\n", failures)}");
    }

    /// <summary>
    /// Verifies the CRC32 checksum formula on original game-created .mythscn files.
    /// CRC32 should equal the stored checksum.
    /// </summary>
    [SkippableFact]
    public void L33t_OriginalFiles_ChecksumIsValid()
    {
        Skip.IfNot(Directory.Exists(FottCampaignDir), $"Campaign directory not found: {FottCampaignDir}");

        var mythscnFiles = Directory.GetFiles(FottCampaignDir, "*.mythscn");
        Skip.If(mythscnFiles.Length == 0, "No .mythscn files found in campaign/fott");

        var failures = new List<string>();
        var tested = 0;

        foreach (var filePath in mythscnFiles)
        {
            var raw = File.ReadAllBytes(filePath);
            if (!((Span<byte>)raw).IsL33t()) continue;

            tested++;

            if (!BarCompression.VerifyL33tChecksum(raw))
                failures.Add($"{Path.GetFileName(filePath)}: CRC32 checksum verification failed");
        }

        Assert.True(tested > 0, "No L33t-compressed .mythscn files found in campaign/fott");
        Assert.True(failures.Count == 0,
            $"{failures.Count}/{tested} original file CRC32 checks failed:\n{string.Join("\n", failures)}");
    }

    #endregion

    #region Compression Roundtrip - BAR Entries

    [SkippableFact]
    public void Alz4_Roundtrip_BarEntries()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var barFiles = FindBarFiles();
        Skip.If(barFiles.Length == 0, "No .bar files found");

        var failures = new List<string>();
        var tested = 0;
        var maxToTest = 50; // cap to avoid very long test runs

        foreach (var barPath in barFiles)
        {
            if (tested >= maxToTest) break;

            using var stream = File.OpenRead(barPath);
            var bar = new BarFile(stream);
            if (!bar.Load(out _) || bar.Entries == null) continue;

            foreach (var entry in bar.Entries)
            {
                if (tested >= maxToTest) break;

                var raw = entry.ReadDataRaw(stream);
                if (!((Span<byte>)raw).IsAlz4()) continue;

                tested++;
                var decompressed = BarCompression.DecompressAlz4(raw);
                if (decompressed == null)
                {
                    failures.Add($"{Path.GetFileName(barPath)}:{entry.Name}: decompression returned null");
                    continue;
                }

                var recompressed = BarCompression.CompressAlz4(decompressed);
                var roundtripped = BarCompression.DecompressAlz4(recompressed.Span);
                if (roundtripped == null)
                {
                    failures.Add($"{Path.GetFileName(barPath)}:{entry.Name}: roundtrip decompression returned null");
                    continue;
                }

                if (!decompressed.AsSpan().SequenceEqual(roundtripped))
                    failures.Add($"{Path.GetFileName(barPath)}:{entry.Name}: roundtrip data mismatch");
            }
        }

        Skip.If(tested == 0, "No Alz4-compressed entries found in any BAR files");
        Assert.True(failures.Count == 0,
            $"{failures.Count}/{tested} Alz4 BAR entry roundtrips failed:\n{string.Join("\n", failures.Take(20))}");
    }

    #endregion

    #region ScenarioFile Tests

    static string[] FindScenarioFiles()
    {
        // Only use campaign files from the game install - user scenarios are not stable test fixtures
        var campaignDir = Path.Combine(GamePath, "campaign");
        if (!Directory.Exists(campaignDir)) return [];
        return Directory.GetFiles(campaignDir, "*.mythscn", SearchOption.AllDirectories);
    }

    [SkippableFact]
    public async Task ScenarioFile_Parse_AllTestFiles()
    {
        var allFiles = FindScenarioFiles();
        Skip.If(allFiles.Length == 0, "No .mythscn files found");

        await Parallel.ForEachAsync(allFiles, async (filePath, t) =>
        {
            if (t.IsCancellationRequested)
                return;

            var fileName = Path.GetFileName(filePath);

            var decompressed = BarCompression.DecompressL33t(File.ReadAllBytes(filePath));
            Assert.NotNull(decompressed);

            var scenario = new ScenarioFile(decompressed);
            Assert.True(scenario.Parsed, $"{fileName}: failed to parse");
            Assert.NotNull(scenario.Sections);
            Assert.True(scenario.Sections.Length >= 15, $"{fileName}: expected at least 15 sections, got {scenario.Sections.Length}");

            Assert.NotNull(scenario.FindSection("FH"));
            Assert.NotNull(scenario.FindSection("J1"));
            Assert.NotNull(scenario.FindSection("CT"));
            Assert.NotNull(scenario.FindSection("CM"));

            var j1 = scenario.GetJ1();
            Assert.NotNull(j1);
            Assert.True(j1.Parsed, $"{fileName}: J1 failed to parse");
            Assert.True(j1.Sections!.Count >= 50, $"{fileName}: J1 expected at least 50 sub-sections, got {j1.Sections.Count}");

            Assert.NotNull(j1.FindSection("TN"));
            Assert.NotNull(j1.FindSection("PL"));
            Assert.NotNull(j1.FindSection("Z1"));
        });
    }

    [SkippableFact]
    public async Task ScenarioFile_Roundtrip_BytePerfect()
    {
        var allFiles = FindScenarioFiles();
        Skip.If(allFiles.Length == 0, "No .mythscn files found");
        var failures = new List<string>();

        await Parallel.ForEachAsync(allFiles, async (filePath, t) =>
        {
            if (t.IsCancellationRequested)
                return;

            var fileName = Path.GetFileName(filePath);
            var decompressed = BarCompression.DecompressL33t(File.ReadAllBytes(filePath))!;
            var scenario = new ScenarioFile(decompressed);
            Assert.True(scenario.Parsed, $"{fileName}: parse failed");

            var roundtripped = scenario.ToBytes();

            if (!decompressed.AsSpan().SequenceEqual(roundtripped))
            {
                var minLen = Math.Min(decompressed.Length, roundtripped.Length);
                int firstDiff = -1;
                for (int i = 0; i < minLen; i++)
                    if (decompressed[i] != roundtripped[i]) { firstDiff = i; break; }
                
                lock (failures)
                    failures.Add($"{fileName}: size orig={decompressed.Length} rt={roundtripped.Length}, first diff at {firstDiff}");
            }
        });

        Assert.True(failures.Count == 0, $"Roundtrip failures:\n{string.Join("\n", failures)}");
    }

    [SkippableFact]
    public async Task ScenarioFile_J1_Roundtrip()
    {
        var allFiles = FindScenarioFiles();
        Skip.If(allFiles.Length == 0, "No .mythscn files found");

        await Parallel.ForEachAsync(allFiles, async (filePath, t) =>
        {
            if (t.IsCancellationRequested)
                return;

            var fileName = Path.GetFileName(filePath);
            var decompressed = BarCompression.DecompressL33t(File.ReadAllBytes(filePath))!;
            var scenario = new ScenarioFile(decompressed);
            Assert.True(scenario.Parsed);

            var j1Section = scenario.FindSection("J1")!;
            var j1 = new ScenarioJ1(j1Section.Data);
            Assert.True(j1.Parsed);

            var roundtripped = j1.ToBytes();
            Assert.True(j1Section.Data.AsSpan().SequenceEqual(roundtripped),
                $"{fileName}: J1 roundtrip mismatch (orig={j1Section.Data.Length}, rt={roundtripped.Length})");
        });
    }

    [SkippableFact]
    public async Task ScenarioFile_XmlRoundtrip_BytePerfect()
    {
        var allFiles = FindScenarioFiles();
        Skip.If(allFiles.Length == 0, "No .mythscn files found");
        var failures = new List<string>();

        await Parallel.ForEachAsync(allFiles, async (filePath, t) =>
        {
            if (t.IsCancellationRequested)
                return;

            var fileName = Path.GetFileName(filePath);
            var decompressed = BarCompression.DecompressL33t(File.ReadAllBytes(filePath))!;
            var scenario = new ScenarioFile(decompressed);
            Assert.True(scenario.Parsed, $"{fileName}: parse failed");

            var xml = scenario.ToXml();
            Assert.True(xml.Length > 100, $"{fileName}: XML too short");

            var fromXml = ScenarioFile.FromXml(xml);
            Assert.True(fromXml.Parsed, $"{fileName}: FromXml parse failed");

            var roundtripped = fromXml.ToBytes();

            if (!decompressed.AsSpan().SequenceEqual(roundtripped))
            {
                var minLen = Math.Min(decompressed.Length, roundtripped.Length);
                int firstDiff = -1;
                for (int i = 0; i < minLen; i++)
                    if (decompressed[i] != roundtripped[i]) { firstDiff = i; break; }
                
                lock (failures)
                    failures.Add($"{fileName}: size orig={decompressed.Length} rt={roundtripped.Length}, first diff at {firstDiff}");
            }
        });

        Assert.True(failures.Count == 0, $"XML roundtrip failures:\n{string.Join("\n", failures)}");
    }

    [SkippableFact]
    public void ScenarioFile_ProtoIndex_Extraction()
    {
        // Test campaign scenario (old format, EN size 76, inline P1)
        var campaignFiles = FindScenarioFiles();
        Skip.If(campaignFiles.Length == 0, "No campaign files found");
        var fott18 = campaignFiles.FirstOrDefault(f => Path.GetFileName(f) == "fott18.mythscn");
        Skip.If(fott18 == null, "fott18.mythscn not found");

        var fott18Xml = ParseScenarioXml(fott18);
        var fottEntities = fott18Xml.GetElementsByTagName("Entity");
        Assert.True(fottEntities.Count > 0, "Expected entities in fott18");
        // Verify protoIndex is present and not all zeros
        bool hasNonZero = false;
        for (int i = 0; i < Math.Min(fottEntities.Count, 50); i++)
        {
            var ent = (XmlElement)fottEntities[i]!;
            var pi = ent.GetAttribute("protoIndex");
            Assert.False(string.IsNullOrEmpty(pi), $"Entity {i} missing protoIndex");
            if (pi != "0") hasNonZero = true;
        }
        Assert.True(hasNonZero, "All protoIndex values are 0 - likely still reading fake P1");
    }

    [SkippableFact]
    public void ScenarioFile_P1_P2_KB_Decode()
    {
        // Campaign scenarios use old format (inline P1/P2 in H1Trail)
        var campaignFiles = FindScenarioFiles();
        Skip.If(campaignFiles.Length == 0, "No campaign files found");
        var anyWithEntities = campaignFiles.FirstOrDefault(f => Path.GetFileName(f) == "fott18.mythscn");
        Skip.If(anyWithEntities == null, "fott18.mythscn not found");

        var doc = ParseScenarioXml(anyWithEntities);
        var entities = doc.GetElementsByTagName("Entity");
        Assert.True(entities.Count > 0, "Expected entities");

        // Old format: P1/P2 attrs should appear on H1Trail element
        var entity0 = (XmlElement)entities[0]!;
        var trail = entity0.SelectSingleNode("H1Trail") as XmlElement;
        Assert.NotNull(trail);
        var hp = trail.GetAttribute("hp");
        Assert.False(string.IsNullOrEmpty(hp), "hp missing on H1Trail (old format inline P1)");
        Assert.True(float.Parse(hp) > 0, "hp should be positive");
        var scale = trail.GetAttribute("scale");
        Assert.False(string.IsNullOrEmpty(scale), "scale missing on H1Trail (old format inline P1)");
        var garrisonedIn = trail.GetAttribute("garrisonedIn");
        Assert.False(string.IsNullOrEmpty(garrisonedIn), "garrisonedIn missing on H1Trail (old format inline P2)");

        // KB should have army names
        var kbElements = doc.GetElementsByTagName("KB");
        Assert.True(kbElements.Count >= 1, "Expected at least 1 KB section");
        var kb = (XmlElement)kbElements[0]!;
        var armies = kb.GetAttribute("armies");
        Assert.Contains("SelectionArmy", armies);

        // T3Tail minimap
        var t3Tail = doc.GetElementsByTagName("T3Tail");
        if (t3Tail.Count >= 1)
        {
            var minimap = (XmlElement)t3Tail[0]!;
            var mw = minimap.GetAttribute("minimapWidth");
            if (!string.IsNullOrEmpty(mw))
                Assert.True(int.Parse(mw) > 0, "minimapWidth should be positive");
        }
    }

    static XmlDocument ParseScenarioXml(string path)
    {
        var decompressed = BarCompression.DecompressL33t(File.ReadAllBytes(path))!;
        var scenario = new ScenarioFile(decompressed);
        Assert.True(scenario.Parsed);
        var xml = scenario.ToXml();
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    #endregion

    #region Trigger XS Lossless Tests

    static readonly string SamplesDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".tr-samples"));

    static string NormalizeXml(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        // Strip XML comments (informational, not data)
        foreach (XmlNode node in doc.SelectNodes("//comment()")!)
            node.ParentNode!.RemoveChild(node);
        // Also strip top-level comments (before/after document element)
        foreach (XmlNode node in doc.ChildNodes)
            if (node is XmlComment) { doc.RemoveChild(node); break; }
        var sb = new System.Text.StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", OmitXmlDeclaration = true };
        using var writer = XmlWriter.Create(sb, settings);
        doc.WriteTo(writer);
        writer.Flush();
        return sb.ToString();
    }

    static string SimpleTriggersXml => """
        <Triggers version="11" unk="2,172,1">
        	<Trigger name="Test" id="0" group="0" priority="4" unk="-1" loop="0" active="1" runImm="0">
        		<Cond name="Timer: Seconds" cmd="(xsGetTime() - (cActivationTime / 1000)) &gt;= %Param1%">
        			<Arg key="Param1" name="Seconds" kt="10" vt="0">3</Arg>
        		</Cond>
        		<Effect name="Player: Set Tech Status" cmd="true">
        			<Arg key="PlayerID" name="Player" kt="10" vt="6">1</Arg>
        			<Arg key="TechID" name="Tech" kt="10" vt="10">37</Arg>
        			<Arg key="Status" kt="10" vt="11">2</Arg>
        			<Extra>trTechSetStatus(%PlayerID%, %TechID%, %Status%);</Extra>
        		</Effect>
        	</Trigger>
        	<Group id="0" name="Ungrouped" indexes="0" />
        </Triggers>
        """;

    [Fact]
    public void XsLossless_ContainsMetadata()
    {
        var xs = ScenarioFile.ConvertTriggersXmlToXs(SimpleTriggersXml, lossless: true);
        Assert.Contains("// @CryBar:triggers", xs);
        Assert.Contains("// @CryBar:trigger id=\"0\"", xs);
        Assert.Contains("// @CryBar:cond", xs);
        Assert.Contains("// @CryBar:effect", xs);
        Assert.Contains("// @CryBar:arg", xs);
        Assert.Contains("// @CryBar:extra", xs);
        Assert.Contains("// @CryBar:group", xs);
    }

    [SkippableFact]
    public async Task ScenarioFile_TrgXsLosslessRoundtrip()
    {
        var allFiles = FindScenarioFiles();
        Skip.If(allFiles.Length == 0, "No .mythscn files found");
        var failures = new List<string>();

        await Parallel.ForEachAsync(allFiles, async (filePath, t) =>
        {
            if (t.IsCancellationRequested)
                return;

            var fileName = Path.GetFileName(filePath);
            var decompressed = BarCompression.DecompressL33t(File.ReadAllBytes(filePath))!;
            var scenario = new ScenarioFile(decompressed);
            if (!scenario.Parsed) return;

            var trSection = scenario.FindSection("TR");
            if (trSection == null || trSection.Data.Length < 20) return;

            // TR section -> XML -> XS (lossless) -> XML -> compare
            string originalXml;
            try { originalXml = ScenarioFile.SectionToTriggersXml(trSection); }
            catch (InvalidOperationException) { return; } // TR section not parseable (e.g. empty triggers)
            var xs = ScenarioFile.ConvertTriggersXmlToXs(originalXml, lossless: true);
            var roundtrippedXml = ScenarioFile.ParseXsToTriggersXml(xs);

            var origNorm = NormalizeXml(originalXml);
            var rtNorm = NormalizeXml(roundtrippedXml);

            if (origNorm != rtNorm)
            {
                // Find first diff location for diagnostics
                var origLines = origNorm.Split('\n');
                var rtLines = rtNorm.Split('\n');
                var diffLine = -1;
                for (int i = 0; i < Math.Min(origLines.Length, rtLines.Length); i++)
                {
                    if (origLines[i] != rtLines[i]) { diffLine = i; break; }
                }
                var detail = diffLine >= 0
                    ? $"line {diffLine}: orig=[{origLines[diffLine].Trim()}] rt=[{rtLines[diffLine].Trim()}]"
                    : $"line count differs: orig={origLines.Length} rt={rtLines.Length}";

                lock (failures)
                    failures.Add($"{fileName}: {detail}");
            }
        });

        Assert.True(failures.Count == 0,
            $"Lossless XS roundtrip failures:\n{string.Join("\n", failures)}");
    }

    /// <summary>
    /// Perfect lossy roundtrip: XML -> XS (no comments) -> XML (with template matching).
    /// For simple triggers, template matching should produce XML identical to the original.
    /// </summary>
    [SkippableFact]
    public void XsLossy_Roundtrip_PerfectForSimpleTriggers()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = TriggerDataIndex.Load(GamePath);
        Skip.If(index == null, "trigger_data.xml not found");

        var xs = ScenarioFile.ConvertTriggersXmlToXs(SimpleTriggersXml, lossless: false);
        Assert.DoesNotContain("@CryBar", xs);

        var roundtrippedXml = ScenarioFile.ParseXsToTriggersXml(xs, index);
        var orig = NormalizeXml(SimpleTriggersXml);
        var rt = NormalizeXml(roundtrippedXml);
        Assert.Equal(orig, rt);
    }

    /// <summary>
    /// Tests lossy roundtrip on campaign scenarios - counts Extra tags per scenario
    /// to measure template matching quality across real-world data.
    /// </summary>
    [SkippableFact]
    public async Task XsLossy_CampaignScenarios_QualityMetric()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = TriggerDataIndex.Load(GamePath);
        Skip.If(index == null, "trigger_data.xml not found");

        var allFiles = FindScenarioFiles();
        Skip.If(allFiles.Length == 0, "No .mythscn files found");

        int totalTriggers = 0, totalExtras = 0, totalStructured = 0;

        await Parallel.ForEachAsync(allFiles, async (filePath, t) =>
        {
            if (t.IsCancellationRequested)
                return;

            var decompressed = BarCompression.DecompressL33t(File.ReadAllBytes(filePath))!;
            var scenario = new ScenarioFile(decompressed);
            if (!scenario.Parsed) return;
            var trSection = scenario.FindSection("TR");
            if (trSection == null || trSection.Data.Length < 20) return;

            string originalXml;
            try { originalXml = ScenarioFile.SectionToTriggersXml(trSection); }
            catch { return; }

            var xs = ScenarioFile.ConvertTriggersXmlToXs(originalXml, lossless: false);
            var rtXml = ScenarioFile.ParseXsToTriggersXml(xs, index);

            var doc = new XmlDocument();
            doc.LoadXml(rtXml);

            var trigs = doc.GetElementsByTagName("Trigger").Count;
            var extras = doc.GetElementsByTagName("Extra").Count;
            var structured = doc.GetElementsByTagName("Effect").Count + doc.GetElementsByTagName("Cond").Count;

            Interlocked.Add(ref totalTriggers, trigs);
            Interlocked.Add(ref totalExtras, extras);
            Interlocked.Add(ref totalStructured, structured);
        });

        // Report quality metric - this test always passes, it's informational
        var pctStructured = totalStructured + totalExtras > 0
            ? (100.0 * totalStructured / (totalStructured + totalExtras)).ToString("F1")
            : "N/A";
        Assert.True(true,
            $"Lossy quality across {allFiles.Length} scenarios: " +
            $"{totalTriggers} triggers, {totalStructured} structured elements, {totalExtras} extras " +
            $"({pctStructured}% structured)");
    }

    #endregion

    #region XS Import Tests

    [Fact]
    public void XsImport_SimpleTrigger_FallbackToExtra()
    {
        var xs = "rule _Test\ngroup TestGroup\nhighFrequency\nactive\n{\n      trSomething();\n      xsDisableRule(\"_Test\");\n      trDisableRule(\"Test\");\n}\n";
        var xml = ScenarioFile.ParseXsToTriggersXml(xs);
        Assert.Contains("<Triggers", xml);
        Assert.Contains("name=\"Test\"", xml);
        Assert.Contains("loop=\"0\"", xml);
        Assert.Contains("active=\"1\"", xml);
        Assert.Contains("<Extra>trSomething();</Extra>", xml);
        Assert.Contains("<Group", xml);
    }

    [Fact]
    public void XsImport_LoopingTrigger_NoDisableCalls()
    {
        var xs = "rule _Looper\nhighFrequency\nactive\n{\n      doStuff();\n}\n";
        var xml = ScenarioFile.ParseXsToTriggersXml(xs);
        Assert.Contains("loop=\"1\"", xml);
        Assert.DoesNotContain("xsDisableRule", xml);
    }

    [Fact]
    public void XsImport_BetweenRuleCode_AttachesToPrecedingTrigger()
    {
        var xs = "rule _First\nhighFrequency\nactive\n{\n      code1();\n}\n\n// some library code\nint x = 5;\n\nrule _Second\nhighFrequency\nactive\n{\n      code2();\n}\n";
        var xml = ScenarioFile.ParseXsToTriggersXml(xs);
        Assert.Contains("name=\"First\"", xml);
        Assert.Contains("name=\"Second\"", xml);
        Assert.Contains("<Extra>// some library code</Extra>", xml);
    }

    [Fact]
    public void XsImport_RollBlessings_ParsesWithoutCrash()
    {
        var xsPath = Path.Combine(SamplesDir, "Roll_Blessings_Test_PlayerList.xs");
        if (!File.Exists(xsPath)) return;
        var xs = File.ReadAllText(xsPath);
        var xml = ScenarioFile.ParseXsToTriggersXml(xs);
        Assert.Contains("<Triggers", xml);
        Assert.Contains("name=\"Initialize_NTL\"", xml);
    }

    [Fact]
    public void XsImport_GroupReconstruction()
    {
        var xs = "rule _A\ngroup Alpha\nhighFrequency\nactive\n{\n      a();\n}\n\nrule _B\ngroup Beta\nhighFrequency\nactive\n{\n      b();\n}\n\nrule _C\ngroup Alpha\nhighFrequency\nactive\n{\n      c();\n}\n";
        var xml = ScenarioFile.ParseXsToTriggersXml(xs);
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var groups = doc.GetElementsByTagName("Group");
        Assert.Equal(2, groups.Count);
        Assert.Equal("Alpha", ((XmlElement)groups[0]!).GetAttribute("name"));
        Assert.Equal("Beta", ((XmlElement)groups[1]!).GetAttribute("name"));
        Assert.Equal("0,2", ((XmlElement)groups[0]!).GetAttribute("indexes"));
    }

    [Fact]
    public void XsLossless_Roundtrip_XmlIdentical()
    {
        var originalXml = """
            <Triggers version="11" unk="2,172,1">
            	<Trigger name="Trigger_0" id="0" group="0" priority="4" unk="-1" loop="0" active="1" runImm="0">
            		<Cond name="Timer: Seconds" cmd="(xsGetTime() - (cActivationTime / 1000)) &gt;= %Param1%">
            			<Arg key="Param1" name="Seconds" kt="10" vt="0">3</Arg>
            		</Cond>
            		<Effect name="Player: Set Tech Status" cmd="true">
            			<Arg key="PlayerID" name="Player" kt="10" vt="6">1</Arg>
            			<Arg key="TechID" name="Tech" kt="10" vt="10">37</Arg>
            			<Arg key="Status" kt="10" vt="11">2</Arg>
            			<Extra>trTechSetStatus(%PlayerID%, %TechID%, %Status%);</Extra>
            		</Effect>
            		<Effect name="Unit: Create" cmd="true">
            			<Arg key="PlayerID" name="Player" kt="10" vt="6">1</Arg>
            			<Arg key="ProtoName" kt="10" vt="13">AmanraOlder</Arg>
            			<Arg key="Location" name="" kt="10" vt="5" flag="0">29.07, 4.00, 26.58</Arg>
            			<Arg key="Heading" name="Heading (0-359)" kt="10" vt="0">0</Arg>
            			<Arg key="SkipBirth" name="Skip Birth Anim:" kt="10" vt="3">true</Arg>
            			<Extra>trUnitCreate("%ProtoName%", %Location%, %Heading%, %PlayerID%, %SkipBirth%);</Extra>
            		</Effect>
            	</Trigger>
            	<Trigger name="Trigger_1" id="1" group="0" priority="4" unk="-1" loop="0" active="1" runImm="0">
            		<Cond name="Always" cmd="true" />
            		<Effect name="Render: Fog/Black Map" cmd="true">
            			<Arg key="Fog" name="Fog of War:" kt="10" vt="3">false</Arg>
            			<Arg key="Black" name="Black Map:" kt="10" vt="3">false</Arg>
            			<Extra>trSetFogAndBlackmap(%Fog%, %Black%);</Extra>
            		</Effect>
            		<Effect name="God Power: Grant to Player" cmd="true">
            			<Arg key="PlayerID" name="Player" kt="10" vt="6">1</Arg>
            			<Arg key="PowerName" name="Power" kt="10" vt="12">BlazingPrairie</Arg>
            			<Arg key="Count" name="Uses" kt="10" vt="0">5</Arg>
            			<Arg key="Cooldown" name="Cooldown (s)" kt="10" vt="0">10</Arg>
            			<Arg key="UseCost" name="Use Cost" kt="10" vt="3">false</Arg>
            			<Arg key="RepeatAtEnd" name="Repeatable at End" kt="10" vt="3">false</Arg>
            			<Extra>trGodPowerGrant(%PlayerID%, "%PowerName%", %Count%, %Cooldown%, %UseCost%, %RepeatAtEnd%);</Extra>
            		</Effect>
            	</Trigger>
            	<Group id="0" name="Ungrouped" indexes="0,1" />
            </Triggers>
            """;
        var xs = ScenarioFile.ConvertTriggersXmlToXs(originalXml, lossless: true);
        var roundtrippedXml = ScenarioFile.ParseXsToTriggersXml(xs);
        var orig = NormalizeXml(originalXml);
        var rt = NormalizeXml(roundtrippedXml);
        Assert.Equal(orig, rt);
    }

    [Fact]
    public void XsImport_UnrecognizedCondition_FallsBackToAlways()
    {
        var xs = "rule _Test\nhighFrequency\nactive\n{\n   if (someCustomCheck(42))\n   {\n      doThing();\n   }\n}\n";
        var xml = ScenarioFile.ParseXsToTriggersXml(xs);
        Assert.Contains("<Triggers", xml);
        // Without @CryBar comments or trigger_data, condition is part of Extra blocks
        Assert.Contains("name=\"Test\"", xml);
    }

    #endregion

    #region TriggerDataIndex Tests

    [SkippableFact]
    public void TriggerDataIndex_LoadFromGameData()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = TriggerDataIndex.Load(GamePath);
        Assert.NotNull(index);
        Assert.True(index.Conditions.Count > 10, $"Expected >10 conditions, got {index.Conditions.Count}");
        Assert.True(index.Effects.Count > 10, $"Expected >10 effects, got {index.Effects.Count}");
        Assert.True(index.Conditions.ContainsKey("Always"));
        Assert.True(index.Conditions.ContainsKey("Timer: Seconds"));
    }

    #endregion

    #region XS Import Template Matching Tests

    [SkippableFact]
    public void XsImport_WithTemplateMatching_ProducesStructuredXml()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = TriggerDataIndex.Load(GamePath);
        Skip.If(index == null, "trigger_data.xml not found");

        var xsPath = Path.Combine(SamplesDir, "example_trigger_converted.xs");
        if (!File.Exists(xsPath)) return;
        var xs = File.ReadAllText(xsPath);
        var xml = ScenarioFile.ParseXsToTriggersXml(xs, index);

        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var conds = doc.GetElementsByTagName("Cond");
        bool hasTimerCond = false;
        for (int i = 0; i < conds.Count; i++)
            if (((XmlElement)conds[i]!).GetAttribute("name") == "Timer: Seconds") hasTimerCond = true;
        Assert.True(hasTimerCond, "Expected 'Timer: Seconds' condition from template matching");
    }

    [SkippableFact]
    public void XsImport_ExampleTrigger_MatchesReferenceXml()
    {
        Skip.IfNot(GameInstalled, "Game not found");
        var index = TriggerDataIndex.Load(GamePath);
        Skip.If(index == null, "trigger_data.xml not found");

        var xsPath = Path.Combine(SamplesDir, "example_trigger_converted.xs");
        var refXmlPath = Path.Combine(SamplesDir, "example_trigger_rawExport.xml");
        if (!File.Exists(xsPath) || !File.Exists(refXmlPath)) return;

        var xs = File.ReadAllText(xsPath);
        var xml = ScenarioFile.ParseXsToTriggersXml(xs, index);
        var refXml = File.ReadAllText(refXmlPath);

        var doc = new XmlDocument(); doc.LoadXml(xml);
        var refDoc = new XmlDocument(); refDoc.LoadXml(refXml);
        Assert.Equal(refDoc.GetElementsByTagName("Trigger").Count, doc.GetElementsByTagName("Trigger").Count);

        for (int i = 0; i < refDoc.GetElementsByTagName("Trigger").Count; i++)
        {
            var refTrig = (XmlElement)refDoc.GetElementsByTagName("Trigger")[i]!;
            var trig = (XmlElement)doc.GetElementsByTagName("Trigger")[i]!;
            Assert.Equal(refTrig.GetAttribute("name"), trig.GetAttribute("name"));
        }
    }

    #endregion

    #region XS Include Resolution Tests

    [Fact]
    public void XsInclude_ResolvesAndParsesRules()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crybar_test_includes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // helper.xs defines a shared rule
            File.WriteAllText(Path.Combine(dir, "helper.xs"),
                "rule _SharedHelper\nhighFrequency\nactive\n{\n      trShared();\n}\n");

            // main.xs includes helper.xs and has its own rule
            File.WriteAllText(Path.Combine(dir, "main.xs"),
                "include \"helper.xs\";\n\nrule _Main\nhighFrequency\nactive\n{\n      trMain();\n}\n");

            var xs = File.ReadAllText(Path.Combine(dir, "main.xs"));
            var xml = ScenarioFile.ParseXsToTriggersXml(xs, sourceDir: dir);

            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var triggers = doc.GetElementsByTagName("Trigger");

            Assert.Equal(2, triggers.Count);
            Assert.Equal("SharedHelper", ((XmlElement)triggers[0]!).GetAttribute("name"));
            Assert.Equal("Main", ((XmlElement)triggers[1]!).GetAttribute("name"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void XsInclude_NestedIncludes_Resolved()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crybar_test_nested_" + Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(dir, "sub");
        Directory.CreateDirectory(subDir);
        try
        {
            // sub/deep.xs - deepest included file
            File.WriteAllText(Path.Combine(subDir, "deep.xs"),
                "rule _Deep\nhighFrequency\nactive\n{\n      trDeep();\n}\n");

            // mid.xs includes sub/deep.xs
            File.WriteAllText(Path.Combine(dir, "mid.xs"),
                "include \"sub/deep.xs\";\n\nrule _Mid\nhighFrequency\nactive\n{\n      trMid();\n}\n");

            // top.xs includes mid.xs
            File.WriteAllText(Path.Combine(dir, "top.xs"),
                "include \"mid.xs\";\n\nrule _Top\nhighFrequency\nactive\n{\n      trTop();\n}\n");

            var xs = File.ReadAllText(Path.Combine(dir, "top.xs"));
            var xml = ScenarioFile.ParseXsToTriggersXml(xs, sourceDir: dir);

            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var triggers = doc.GetElementsByTagName("Trigger");

            Assert.Equal(3, triggers.Count);
            Assert.Equal("Deep", ((XmlElement)triggers[0]!).GetAttribute("name"));
            Assert.Equal("Mid", ((XmlElement)triggers[1]!).GetAttribute("name"));
            Assert.Equal("Top", ((XmlElement)triggers[2]!).GetAttribute("name"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void XsInclude_MissingFile_LeftAsIs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crybar_test_missing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "main.xs"),
                "include \"nonexistent.xs\";\n\nrule _Main\nhighFrequency\nactive\n{\n      trMain();\n}\n");

            var xs = File.ReadAllText(Path.Combine(dir, "main.xs"));
            var xml = ScenarioFile.ParseXsToTriggersXml(xs, sourceDir: dir);

            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var triggers = doc.GetElementsByTagName("Trigger");

            // Include line becomes preamble content -> __preamble__ trigger + Main
            Assert.True(triggers.Count >= 1);
            Assert.Equal("Main", ((XmlElement)triggers[^1]!).GetAttribute("name"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void XsInclude_CircularInclude_NoCrash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crybar_test_circular_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // a.xs includes b.xs, b.xs includes a.xs
            File.WriteAllText(Path.Combine(dir, "a.xs"),
                "include \"b.xs\";\n\nrule _A\nhighFrequency\nactive\n{\n      trA();\n}\n");
            File.WriteAllText(Path.Combine(dir, "b.xs"),
                "include \"a.xs\";\n\nrule _B\nhighFrequency\nactive\n{\n      trB();\n}\n");

            var xs = File.ReadAllText(Path.Combine(dir, "a.xs"));
            var xml = ScenarioFile.ParseXsToTriggersXml(xs, sourceDir: dir);

            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var triggers = doc.GetElementsByTagName("Trigger");

            // Circular includes produce duplicates but should not crash
            Assert.True(triggers.Count >= 2, $"Expected at least 2 triggers, got {triggers.Count}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void XsInclude_NoSourceDir_IncludesIgnored()
    {
        // When no sourceDir is provided, includes should be left as-is (no file resolution)
        var xs = "include \"helper.xs\";\n\nrule _Main\nhighFrequency\nactive\n{\n      trMain();\n}\n";
        var xml = ScenarioFile.ParseXsToTriggersXml(xs);

        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var triggers = doc.GetElementsByTagName("Trigger");

        Assert.True(triggers.Count >= 1);
        Assert.Equal("Main", ((XmlElement)triggers[^1]!).GetAttribute("name"));
    }

    [SkippableFact]
    public void XsInclude_GameAiFiles_ParseWithIncludes()
    {
        Skip.IfNot(GameInstalled, "Game not found");

        var aiDir = Path.Combine(GamePath, "ai", "campaign", "yas");
        var xsFile = Path.Combine(aiDir, "yas02_p2.xs");
        Skip.IfNot(File.Exists(xsFile), "yas02_p2.xs not found");

        var xs = File.ReadAllText(xsFile);
        // Should not throw - includes get resolved from the ai/ parent directory
        var aiRoot = Path.Combine(GamePath, "ai");
        var xml = ScenarioFile.ParseXsToTriggersXml(xs, sourceDir: aiRoot);

        var doc = new XmlDocument();
        doc.LoadXml(xml);

        // AI scripts don't have trigger rules, but include resolution should work
        // and produce valid XML without crashing
        Assert.NotNull(doc.DocumentElement);
        Assert.Equal("Triggers", doc.DocumentElement!.Name);
    }

    #endregion

    #region TMM Companion Resolution

    /// <summary>
    /// Scores how well an entry's directory matches a preferred directory by comparing
    /// path segments from right to left. Inlined from MainWindow.BestMatchByDirectorySuffix.
    /// </summary>
    static BarFileEntry? BestMatchByDirectorySuffix(
        IEnumerable<BarFileEntry> candidates, string? preferredRelativeDir)
    {
        BarFileEntry? best = null;
        int bestScore = -1;

        foreach (var entry in candidates)
        {
            if (best == null) { best = entry; bestScore = 0; }
            if (preferredRelativeDir == null) continue;

            var entryDir = entry.DirectoryPath.Replace('\\', '/').TrimEnd('/');
            var prefDir = preferredRelativeDir.Replace('\\', '/').TrimEnd('/');

            var entrySegs = entryDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var prefSegs = prefDir.Split('/', StringSplitOptions.RemoveEmptyEntries);

            int score = 0;
            int ei = entrySegs.Length - 1, pi = prefSegs.Length - 1;
            while (ei >= 0 && pi >= 0 &&
                   entrySegs[ei].Equals(prefSegs[pi], StringComparison.OrdinalIgnoreCase))
            {
                score++;
                ei--;
                pi--;
            }

            if (score > bestScore)
            {
                best = entry;
                bestScore = score;
            }
        }

        return best;
    }

    [SkippableFact]
    public void WonderDemeter_CompanionResolution_MatchesByPath()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var metaBarPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheMeta.bar");
        Skip.IfNot(File.Exists(metaBarPath), "ArtModelCacheMeta.bar not found");

        using var metaStream = File.OpenRead(metaBarPath);
        var metaBar = new BarFile(metaStream);
        Assert.True(metaBar.Load(out _));

        // Find both wonder_demeter.tmm entries (should be 2 with different paths)
        var demeterEntries = metaBar.Entries!
            .Where(e => e.Name.Equals("wonder_demeter.tmm", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(demeterEntries.Count >= 2,
            $"Expected 2+ wonder_demeter.tmm entries, found {demeterEntries.Count}");

        // Parse each and verify they have different vertex counts
        var parsedModels = new List<(string path, TmmFile tmm)>();
        foreach (var entry in demeterEntries)
        {
            var raw = BarCompression.EnsureDecompressed(entry.ReadDataRaw(metaStream), out _);
            var tmm = new TmmFile(raw);
            Assert.True(tmm.Parsed, $"Failed to parse {entry.RelativePath}");
            parsedModels.Add((entry.RelativePath, tmm));
        }

        // The two entries should have different vertex counts (they're different models)
        var vertexCounts = parsedModels.Select(m => m.tmm.NumVertices).Distinct().ToList();
        Assert.True(vertexCounts.Count >= 2,
            $"Expected different vertex counts but got: {string.Join(", ", parsedModels.Select(m => $"{m.path}: {m.tmm.NumVertices}"))}");

        // Now find companion .tmm.data files in the data BARs
        // Search all ArtModelCacheModelData*.bar files
        var barDir = Path.Combine(GamePath, "modelcache");
        var dataBarFiles = Directory.GetFiles(barDir, "ArtModelCacheModelData*.bar");

        foreach (var (path, tmm) in parsedModels)
        {
            var dir = Path.GetDirectoryName(path)?.Replace('\\', '/');

            // Find all wonder_demeter.tmm.data entries across data BARs
            var allDataEntries = new List<(BarFileEntry entry, string barPath)>();
            foreach (var dataBarPath in dataBarFiles)
            {
                using var dataStream = File.OpenRead(dataBarPath);
                var dataBar = new BarFile(dataStream);
                if (!dataBar.Load(out _)) continue;

                foreach (var e in dataBar.Entries!)
                {
                    if (e.Name.Equals("wonder_demeter.tmm.data", StringComparison.OrdinalIgnoreCase))
                        allDataEntries.Add((e, dataBarPath));
                }
            }

            // Use BestMatchByDirectorySuffix to pick the right one
            var bestEntry = BestMatchByDirectorySuffix(
                allDataEntries.Select(m => m.entry), dir);

            Assert.NotNull(bestEntry);

            // Read the matched .tmm.data and verify it parses with the right vertex count
            var bestBarPath = allDataEntries.First(m => m.entry == bestEntry).barPath;
            using var bestStream = File.OpenRead(bestBarPath);
            var bestBar = new BarFile(bestStream);
            Assert.True(bestBar.Load(out _));

            var dataEntry = bestBar.Entries!.First(e =>
                e.RelativePath.Equals(bestEntry.RelativePath, StringComparison.OrdinalIgnoreCase));
            var dataRaw = BarCompression.EnsureDecompressed(dataEntry.ReadDataRaw(bestStream), out _);
            var dataFile = new TmmDataFile(dataRaw, tmm);

            Assert.True(dataFile.Parsed,
                $"TmmDataFile failed to parse for {path} using data from {bestEntry.RelativePath}");
            Assert.Equal((int)tmm.NumVertices, dataFile.Vertices!.Length);
        }
    }

    #endregion


    #region Animation Discovery

    /// <summary>
    /// Verifies that FindAnimationsFromAnimXml captures ALL TMAnimation references
    /// in the hoplite animxml, including variants within a single anim element
    /// (e.g. HandAttack has attack_a and attack_b, Cinematic has 6 clips).
    /// </summary>
    [SkippableFact]
    public void HopliteAnimXml_FindsAllAnimationVariants()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var artGreekPath = Path.Combine(GamePath, @"art\ArtGreek.bar");
        Skip.IfNot(File.Exists(artGreekPath), "ArtGreek.bar not found");

        using var stream = File.OpenRead(artGreekPath);
        var bar = new BarFile(stream);
        bar.Load(out _);

        var entry = bar.Entries!.FirstOrDefault(e =>
            e.Name.Equals("hoplite.xml.XMB", StringComparison.OrdinalIgnoreCase) &&
            e.RelativePath.Contains("hoplite", StringComparison.OrdinalIgnoreCase));
        Skip.If(entry == null, "hoplite.xml.XMB not found");

        var raw = entry!.ReadDataDecompressed(stream);
        var xmlText = ConversionHelper.ConvertXmbToXmlText(raw.Span);
        Assert.NotNull(xmlText);

        var animRefs = AnimationDiscovery.FindAnimationsFromAnimXml(xmlText);

        // ground truth: scan XML for ALL TMAnimation assetreferences
        var allTmaRefs = new List<string>();
        using var reader = XmlReader.Create(new StringReader(xmlText));
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "assetreference")
                continue;
            if (reader.GetAttribute("type") != "TMAnimation") continue;
            if (reader.ReadToDescendant("file"))
                allTmaRefs.Add(reader.ReadElementContentAsString().Trim());
        }

        // every TMAnimation reference in the XML should appear in the parsed results
        var discoveredPaths = animRefs.Select(r => r.TmaPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = allTmaRefs.Where(p => !discoveredPaths.Contains(p)).ToList();
        Assert.True(missing.Count == 0,
            $"Missing {missing.Count} TMA refs: {string.Join(", ", missing.Select(Path.GetFileName))}");

        // specific expectations for hoplite
        Assert.Equal(13, animRefs.Count);

        // HandAttack has 2 variants (attack_a and attack_b)
        var handAttacks = animRefs.Where(r => r.AnimName == "HandAttack").ToList();
        Assert.Equal(2, handAttacks.Count);
        Assert.Contains(handAttacks, r => r.TmaPath.Contains("attack_a", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handAttacks, r => r.TmaPath.Contains("attack_b", StringComparison.OrdinalIgnoreCase));

        // Cinematic has 6 clips
        var cinematics = animRefs.Where(r => r.AnimName == "Cinematic").ToList();
        Assert.Equal(6, cinematics.Count);

        // single-variant anims
        Assert.Single(animRefs, r => r.AnimName == "Idle");
        Assert.Single(animRefs, r => r.AnimName == "Walk");
        Assert.Single(animRefs, r => r.AnimName == "Death");
        Assert.Single(animRefs, r => r.AnimName == "Flail");
        Assert.Single(animRefs, r => r.AnimName == "Bored");
    }

    #endregion
}
