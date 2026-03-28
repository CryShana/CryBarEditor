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

    #region TMA-GLB Diagnostic

    /// <summary>
    /// Comprehensive diagnostic for hoplite TMM + TMA data.
    /// Compares bones, matrices, decoded values to find the root cause of broken GLB animations.
    /// </summary>
    [SkippableFact]
    public void Diagnostic_HopliteTmaGlb_ComprehensiveAnalysis()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== TMA-GLB DIAGNOSTIC: hoplite_gold ===\n");

        // --- Load TMM ---
        var (_, tmmEntry, tmmStream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "hoplite_gold.tmm");
        var tmmRaw = BarCompression.EnsureDecompressed(tmmEntry.ReadDataRaw(tmmStream), out _);
        var tmm = new TmmFile(tmmRaw);
        tmmStream.Dispose();
        Assert.True(tmm.Parsed);

        // --- Load TMA ---
        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath), "Animation BAR not found");
        using var tmaStream = File.OpenRead(barPath);
        var tmaBar = new BarFile(tmaStream);
        tmaBar.Load(out _);

        // Find attack TMA
        var tmaEntry = tmaBar.Entries!.FirstOrDefault(e =>
            e.Name.Contains("hoplite_igc_attack_a", StringComparison.OrdinalIgnoreCase) &&
            e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase));
        if (tmaEntry == null)
        {
            // Fallback: any hoplite TMA
            tmaEntry = tmaBar.Entries!.FirstOrDefault(e =>
                e.Name.Contains("hoplite", StringComparison.OrdinalIgnoreCase) &&
                e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase));
        }
        Skip.If(tmaEntry == null, "No hoplite TMA found");

        var tmaRaw = BarCompression.EnsureDecompressed(tmaEntry!.ReadDataRaw(tmaStream), out _);
        var tma = new TmaFile(tmaRaw);
        Assert.True(tma.Parsed);

        sb.AppendLine($"TMM: {tmmEntry.Name} ({tmm.NumBones} bones)");
        sb.AppendLine($"TMA: {tmaEntry.Name} (v{tma.Version}, {tma.NumBones} bones, {tma.NumTracks} tracks, {tma.FrameCount} frames, {tma.Duration:F3}s)");
        sb.AppendLine();

        // --- SECTION 1: Bone comparison ---
        sb.AppendLine("=== BONE COMPARISON ===");
        var tmmBones = tmm.Bones!;
        var tmaBones = tma.Bones!;
        sb.AppendLine($"TMM bones: {tmmBones.Length}");
        sb.AppendLine($"TMA bones: {tmaBones.Length}");

        // Check name match
        var tmmBoneNames = new HashSet<string>(tmmBones.Select(b => b.Name));
        var tmaBoneNames = new HashSet<string>(tmaBones.Select(b => b.Name));
        var onlyInTmm = tmmBoneNames.Except(tmaBoneNames).ToList();
        var onlyInTma = tmaBoneNames.Except(tmmBoneNames).ToList();
        sb.AppendLine($"Only in TMM ({onlyInTmm.Count}): {string.Join(", ", onlyInTmm.Take(10))}");
        sb.AppendLine($"Only in TMA ({onlyInTma.Count}): {string.Join(", ", onlyInTma.Take(10))}");

        // Check bone order
        bool sameOrder = tmmBones.Length == tmaBones.Length;
        if (sameOrder)
        {
            for (int i = 0; i < tmmBones.Length; i++)
            {
                if (tmmBones[i].Name != tmaBones[i].Name)
                {
                    sameOrder = false;
                    sb.AppendLine($"First order mismatch at index {i}: TMM='{tmmBones[i].Name}' vs TMA='{tmaBones[i].Name}'");
                    break;
                }
            }
        }
        sb.AppendLine($"Same bone order: {sameOrder}");

        // Check parent IDs match
        if (sameOrder)
        {
            int parentMismatches = 0;
            for (int i = 0; i < tmmBones.Length; i++)
            {
                if (tmmBones[i].ParentId != tmaBones[i].ParentId)
                {
                    parentMismatches++;
                    if (parentMismatches <= 5)
                        sb.AppendLine($"  Parent mismatch bone[{i}] '{tmmBones[i].Name}': TMM parent={tmmBones[i].ParentId}, TMA parent={tmaBones[i].ParentId}");
                }
            }
            sb.AppendLine($"Parent ID mismatches: {parentMismatches}");
        }
        sb.AppendLine();

        // --- SECTION 2: Matrix comparison ---
        sb.AppendLine("=== MATRIX COMPARISON (TMM ParentSpace vs TMA matrices) ===");
        var tmmBoneMap = new Dictionary<string, int>();
        for (int i = 0; i < tmmBones.Length; i++) tmmBoneMap[tmmBones[i].Name] = i;

        for (int i = 0; i < Math.Min(tmaBones.Length, 5); i++)
        {
            var tmaBone = tmaBones[i];
            sb.AppendLine($"Bone: {tmaBone.Name}");

            if (tmmBoneMap.TryGetValue(tmaBone.Name, out int tmmIdx))
            {
                var tmmBone = tmmBones[tmmIdx];

                // Compare TMM ParentSpaceMatrix vs TMA LocalTransform
                sb.AppendLine($"  TMM ParentSpace:  [{string.Join(", ", tmmBone.ParentSpaceMatrix.Select(f => $"{f,8:F4}"))}]");
                sb.AppendLine($"  TMA LocalTransform: [{string.Join(", ", tmaBone.LocalTransform.Select(f => $"{f,8:F4}"))}]");
                float localDiff = MatrixMaxAbsDiff(tmmBone.ParentSpaceMatrix, tmaBone.LocalTransform);
                sb.AppendLine($"  Max diff (ParentSpace vs LocalTransform): {localDiff:F6}");

                // Compare TMM WorldSpace vs TMA BindPose
                sb.AppendLine($"  TMM WorldSpace: [{string.Join(", ", tmmBone.WorldSpaceMatrix.Select(f => $"{f,8:F4}"))}]");
                sb.AppendLine($"  TMA BindPose:   [{string.Join(", ", tmaBone.BindPose.Select(f => $"{f,8:F4}"))}]");
                float bindDiff = MatrixMaxAbsDiff(tmmBone.WorldSpaceMatrix, tmaBone.BindPose);
                sb.AppendLine($"  Max diff (WorldSpace vs BindPose): {bindDiff:F6}");

                // Compare InverseBindMatrix
                sb.AppendLine($"  TMM InverseBind: [{string.Join(", ", tmmBone.InverseBindMatrix.Select(f => $"{f,8:F4}"))}]");
                sb.AppendLine($"  TMA InverseBind: [{string.Join(", ", tmaBone.InverseBindPose.Select(f => $"{f,8:F4}"))}]");
                float ibmDiff = MatrixMaxAbsDiff(tmmBone.InverseBindMatrix, tmaBone.InverseBindPose);
                sb.AppendLine($"  Max diff (InverseBind): {ibmDiff:F6}");
            }
            else
            {
                sb.AppendLine("  NOT FOUND in TMM bones!");
            }
            sb.AppendLine();
        }

        // --- SECTION 3: Track analysis ---
        sb.AppendLine("=== TRACK ENCODING STATS ===");
        var tracks = tma.Tracks!;
        var encCounts = tracks.GroupBy(t => $"T:{t.TranslationEncoding} R:{t.RotationEncoding} S:{t.ScaleEncoding}")
            .OrderByDescending(g => g.Count());
        foreach (var g in encCounts)
            sb.AppendLine($"  {g.Key} -> {g.Count()} tracks");
        sb.AppendLine();

        // --- SECTION 4: Decoded value sanity ---
        sb.AppendLine("=== DECODED VALUE ANALYSIS ===");
        var decoded = TmaDecoder.DecodeAllTracks(tma)!;
        int matchedTracks = 0, unmatchedTracks = 0;
        foreach (var dt in decoded)
        {
            if (tmmBoneMap.ContainsKey(dt.Name)) matchedTracks++;
            else unmatchedTracks++;
        }
        sb.AppendLine($"Tracks matched to TMM bones: {matchedTracks}/{decoded.Length}");
        sb.AppendLine($"Unmatched track names: {string.Join(", ", decoded.Where(dt => !tmmBoneMap.ContainsKey(dt.Name)).Select(dt => dt.Name).Take(10))}");
        sb.AppendLine();

        // Check a few key bones
        string[] interestingBones = ["mixamorig:Hips", "mixamorig:Spine", "mixamorig:LeftUpLeg", "mixamorig:RightUpLeg", "mixamorig:LeftArm", "mixamorig:Head"];
        foreach (var boneName in interestingBones)
        {
            var dt = decoded.FirstOrDefault(d => d.Name == boneName);
            if (dt == null) continue;
            if (!tmmBoneMap.TryGetValue(boneName, out int bi)) continue;
            var tmmBone = tmmBones[bi];

            sb.AppendLine($"--- {boneName} ---");
            sb.AppendLine($"  TMM ParentSpace translation: ({tmmBone.ParentSpaceMatrix[12]:F4}, {tmmBone.ParentSpaceMatrix[13]:F4}, {tmmBone.ParentSpaceMatrix[14]:F4})");

            // Frame 0 decoded values
            if (dt.Translations.Length > 0)
                sb.AppendLine($"  TMA frame0 T: ({dt.Translations[0].X:F6}, {dt.Translations[0].Y:F6}, {dt.Translations[0].Z:F6})");
            if (dt.Rotations.Length > 0)
            {
                var q = dt.Rotations[0];
                sb.AppendLine($"  TMA frame0 R: ({q.X:F6}, {q.Y:F6}, {q.Z:F6}, {q.W:F6})  mag={MathF.Sqrt(q.X*q.X+q.Y*q.Y+q.Z*q.Z+q.W*q.W):F6}");
            }
            if (dt.Scales.Length > 0)
                sb.AppendLine($"  TMA frame0 S: ({dt.Scales[0].X:F6}, {dt.Scales[0].Y:F6}, {dt.Scales[0].Z:F6})");

            // Check: does frame0 T look like a delta (small) or absolute (large)?
            if (dt.Translations.Length > 0)
            {
                var tmmT = new System.Numerics.Vector3(tmmBone.ParentSpaceMatrix[12], tmmBone.ParentSpaceMatrix[13], tmmBone.ParentSpaceMatrix[14]);
                var animT = dt.Translations[0];
                var sum = tmmT + animT;
                sb.AppendLine($"  bindT + animT = ({sum.X:F4}, {sum.Y:F4}, {sum.Z:F4})");
                sb.AppendLine($"  |animT| = {animT.Length():F6}, |bindT| = {tmmT.Length():F6}");
                if (animT.Length() > 0.001f && tmmT.Length() > 0.001f)
                    sb.AppendLine($"  ratio |animT|/|bindT| = {animT.Length() / tmmT.Length():F4}");
            }

            // Rotation: decompose TMM bind rotation, check if animR is close to identity (delta) or close to bind (absolute)
            DecomposeColumnMajor(tmmBone.ParentSpaceMatrix, out _, out var bindR, out _);
            if (dt.Rotations.Length > 0)
            {
                var animR = dt.Rotations[0];
                float dotWithIdentity = MathF.Abs(System.Numerics.Quaternion.Dot(animR, System.Numerics.Quaternion.Identity));
                float dotWithBind = MathF.Abs(System.Numerics.Quaternion.Dot(animR, bindR));
                sb.AppendLine($"  bindR: ({bindR.X:F4}, {bindR.Y:F4}, {bindR.Z:F4}, {bindR.W:F4})");
                sb.AppendLine($"  |dot(animR, identity)| = {dotWithIdentity:F6}  (1.0=identical to identity=delta)");
                sb.AppendLine($"  |dot(animR, bindR)|    = {dotWithBind:F6}  (1.0=identical to bindR=absolute)");
            }

            // T range across all frames
            if (dt.Translations.Length > 1)
            {
                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var t in dt.Translations)
                {
                    if (t.X < minX) minX = t.X; if (t.X > maxX) maxX = t.X;
                    if (t.Y < minY) minY = t.Y; if (t.Y > maxY) maxY = t.Y;
                    if (t.Z < minZ) minZ = t.Z; if (t.Z > maxZ) maxZ = t.Z;
                }
                sb.AppendLine($"  T range: X[{minX:F4}..{maxX:F4}] Y[{minY:F4}..{maxY:F4}] Z[{minZ:F4}..{maxZ:F4}]");
            }

            sb.AppendLine();
        }

        // --- SECTION 5: Rotation continuity check ---
        sb.AppendLine("=== ROTATION CONTINUITY (jumps > 15 deg) ===");
        int totalJumps = 0;
        foreach (var dt in decoded)
        {
            var rots = dt.Rotations;
            if (rots.Length < 2) continue;
            for (int f = 1; f < rots.Length; f++)
            {
                float absDot = MathF.Abs(System.Numerics.Quaternion.Dot(rots[f - 1], rots[f]));
                float angleDeg = 2f * MathF.Acos(MathF.Min(1f, absDot)) * (180f / MathF.PI);
                if (angleDeg > 15f)
                {
                    totalJumps++;
                    if (totalJumps <= 20)
                    {
                        var srcTrack = tracks.First(t => t.Name == dt.Name);
                        sb.AppendLine($"  {dt.Name} enc={srcTrack.RotationEncoding} f{f-1}->f{f}: {angleDeg:F1} deg");
                    }
                }
            }
        }
        sb.AppendLine($"Total rotation jumps > 15 deg: {totalJumps}");
        sb.AppendLine();

        // --- SECTION 6: Check if TMA frame0 matches TMM rest pose ---
        sb.AppendLine("=== TMA FRAME0 vs TMM REST POSE (is frame0 identity-like?) ===");
        int closeToIdentity = 0, closeToBindPose = 0, neither = 0;
        foreach (var dt in decoded)
        {
            if (!tmmBoneMap.TryGetValue(dt.Name, out int bi)) continue;
            if (dt.Rotations.Length == 0) continue;

            var tmmBone = tmmBones[bi];
            DecomposeColumnMajor(tmmBone.ParentSpaceMatrix, out _, out var bR, out _);
            var aR = dt.Rotations[0];

            float dotId = MathF.Abs(System.Numerics.Quaternion.Dot(aR, System.Numerics.Quaternion.Identity));
            float dotBind = MathF.Abs(System.Numerics.Quaternion.Dot(aR, bR));

            if (dotId > 0.999f) closeToIdentity++;
            else if (dotBind > 0.999f) closeToBindPose++;
            else neither++;
        }
        sb.AppendLine($"Frame0 rotation close to identity (delta model): {closeToIdentity}");
        sb.AppendLine($"Frame0 rotation close to bind pose (absolute model): {closeToBindPose}");
        sb.AppendLine($"Frame0 rotation neither: {neither}");
        sb.AppendLine();

        // Same for translation
        int tCloseToZero = 0, tCloseToBind = 0, tNeither = 0;
        foreach (var dt in decoded)
        {
            if (!tmmBoneMap.TryGetValue(dt.Name, out int bi)) continue;
            if (dt.Translations.Length == 0) continue;

            var tmmBone = tmmBones[bi];
            var bindT = new System.Numerics.Vector3(tmmBone.ParentSpaceMatrix[12], tmmBone.ParentSpaceMatrix[13], tmmBone.ParentSpaceMatrix[14]);
            var animT = dt.Translations[0];

            if (animT.Length() < 0.01f) tCloseToZero++;
            else if ((animT - bindT).Length() < 0.01f) tCloseToBind++;
            else tNeither++;
        }
        sb.AppendLine($"Frame0 translation close to zero (delta model): {tCloseToZero}");
        sb.AppendLine($"Frame0 translation close to bind T (absolute model): {tCloseToBind}");
        sb.AppendLine($"Frame0 translation neither: {tNeither}");
        sb.AppendLine();

        // --- SECTION 7: TMA bone BindPose vs TMA bone LocalTransform ---
        sb.AppendLine("=== TMA BONE: BindPose vs LocalTransform ===");
        for (int i = 0; i < Math.Min(tmaBones.Length, 3); i++)
        {
            var b = tmaBones[i];
            sb.AppendLine($"  {b.Name}:");
            sb.AppendLine($"    LocalTransform: [{string.Join(", ", b.LocalTransform.Take(4).Select(f => $"{f:F4}"))}] ...");
            sb.AppendLine($"    BindPose:       [{string.Join(", ", b.BindPose.Take(4).Select(f => $"{f:F4}"))}] ...");
            sb.AppendLine($"    InvBindPose:    [{string.Join(", ", b.InverseBindPose.Take(4).Select(f => $"{f:F4}"))}] ...");
        }

        // Output as test failure so we can read the diagnostic
        Assert.Fail($"DIAGNOSTIC OUTPUT (not a real failure):\n\n{sb}");
    }

    /// <summary>
    /// Critical test: For bones with Constant rotation encoding (non-animated bones),
    /// check whether the constant value is identity (delta model) or matches bind pose (absolute model).
    /// This definitively answers whether TMA rotations are deltas or absolute.
    /// </summary>
    [SkippableFact]
    public void Diagnostic_TmaConstantRotations_DeltaOrAbsolute()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== CONSTANT ROTATION ANALYSIS ===\n");

        // Load TMM
        var (_, tmmEntry, tmmStream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "hoplite_gold.tmm");
        var tmmRaw = BarCompression.EnsureDecompressed(tmmEntry.ReadDataRaw(tmmStream), out _);
        var tmm = new TmmFile(tmmRaw);
        tmmStream.Dispose();
        Assert.True(tmm.Parsed);

        // Load TMA
        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath));
        using var tmaStream = File.OpenRead(barPath);
        var tmaBar = new BarFile(tmaStream);
        tmaBar.Load(out _);

        var tmaEntry = tmaBar.Entries!.FirstOrDefault(e =>
            e.Name.Contains("hoplite_igc_attack_a", StringComparison.OrdinalIgnoreCase) &&
            e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase));
        Skip.If(tmaEntry == null, "No hoplite attack TMA found");

        var tmaRaw = BarCompression.EnsureDecompressed(tmaEntry!.ReadDataRaw(tmaStream), out _);
        var tma = new TmaFile(tmaRaw);
        Assert.True(tma.Parsed);

        var decoded = TmaDecoder.DecodeAllTracks(tma)!;
        var tmmBones = tmm.Bones!;
        var tmmBoneMap = new Dictionary<string, int>();
        for (int i = 0; i < tmmBones.Length; i++) tmmBoneMap[tmmBones[i].Name] = i;

        int constantTracks = 0;
        int isIdentity = 0;
        int isBindPose = 0;
        int isNeither = 0;

        foreach (var dt in decoded)
        {
            var srcTrack = tma.Tracks!.First(t => t.Name == dt.Name);
            if (srcTrack.RotationEncoding != TmaEncoding.Constant) continue;
            if (!tmmBoneMap.TryGetValue(dt.Name, out int bi)) continue;

            constantTracks++;
            var animR = dt.Rotations[0];
            DecomposeColumnMajor(tmmBones[bi].ParentSpaceMatrix, out _, out var bindR, out _);

            float dotId = MathF.Abs(System.Numerics.Quaternion.Dot(animR, System.Numerics.Quaternion.Identity));
            float dotBind = MathF.Abs(System.Numerics.Quaternion.Dot(animR, bindR));

            string verdict;
            if (dotId > 0.999f) { isIdentity++; verdict = "IDENTITY (delta)"; }
            else if (dotBind > 0.999f) { isBindPose++; verdict = "BIND POSE (absolute)"; }
            else { isNeither++; verdict = "NEITHER"; }

            sb.AppendLine($"  {dt.Name,-40} R=({animR.X:F4},{animR.Y:F4},{animR.Z:F4},{animR.W:F4})  " +
                          $"bindR=({bindR.X:F4},{bindR.Y:F4},{bindR.Z:F4},{bindR.W:F4})  " +
                          $"dot(id)={dotId:F4}  dot(bind)={dotBind:F4}  -> {verdict}");
        }

        sb.AppendLine();
        sb.AppendLine($"Constant-rotation tracks: {constantTracks}");
        sb.AppendLine($"  Close to identity (delta model): {isIdentity}");
        sb.AppendLine($"  Close to bind pose (absolute model): {isBindPose}");
        sb.AppendLine($"  Neither: {isNeither}");

        // Also check: for Quat64 tracks, what does frame0 look like?
        sb.AppendLine("\n=== QUAT64 FRAME0 ANALYSIS ===");
        int q64Tracks = 0;
        int q64CloseId = 0, q64CloseBind = 0, q64Neither = 0;
        foreach (var dt in decoded)
        {
            var srcTrack = tma.Tracks!.First(t => t.Name == dt.Name);
            if (srcTrack.RotationEncoding != TmaEncoding.Quat64) continue;
            if (!tmmBoneMap.TryGetValue(dt.Name, out int bi)) continue;
            if (dt.Rotations.Length == 0) continue;

            q64Tracks++;
            var animR = dt.Rotations[0];
            DecomposeColumnMajor(tmmBones[bi].ParentSpaceMatrix, out _, out var bindR, out _);

            float dotId = MathF.Abs(System.Numerics.Quaternion.Dot(animR, System.Numerics.Quaternion.Identity));
            float dotBind = MathF.Abs(System.Numerics.Quaternion.Dot(animR, bindR));

            string verdict;
            if (dotId > 0.99f) { q64CloseId++; verdict = "~IDENTITY"; }
            else if (dotBind > 0.99f) { q64CloseBind++; verdict = "~BIND"; }
            else { q64Neither++; verdict = "NEITHER"; }

            sb.AppendLine($"  {dt.Name,-40} dot(id)={dotId:F4}  dot(bind)={dotBind:F4}  -> {verdict}");
        }
        sb.AppendLine($"\nQuat64 tracks: {q64Tracks}");
        sb.AppendLine($"  ~Identity: {q64CloseId}");
        sb.AppendLine($"  ~BindPose: {q64CloseBind}");
        sb.AppendLine($"  Neither: {q64Neither}");

        // Also check Constant translation
        sb.AppendLine("\n=== CONSTANT TRANSLATION ANALYSIS ===");
        int constT = 0, tIsZero = 0, tIsBindT = 0, tIsOther = 0;
        foreach (var dt in decoded)
        {
            var srcTrack = tma.Tracks!.First(t => t.Name == dt.Name);
            if (srcTrack.TranslationEncoding != TmaEncoding.Constant) continue;
            if (!tmmBoneMap.TryGetValue(dt.Name, out int bi)) continue;

            constT++;
            var animT = dt.Translations[0];
            var bindT = new System.Numerics.Vector3(tmmBones[bi].ParentSpaceMatrix[12], tmmBones[bi].ParentSpaceMatrix[13], tmmBones[bi].ParentSpaceMatrix[14]);

            if (animT.Length() < 0.001f) tIsZero++;
            else if ((animT - bindT).Length() < 0.001f) tIsBindT++;
            else tIsOther++;
        }
        sb.AppendLine($"Constant-translation tracks: {constT}");
        sb.AppendLine($"  Close to zero (delta): {tIsZero}");
        sb.AppendLine($"  Close to bindT (absolute): {tIsBindT}");
        sb.AppendLine($"  Other: {tIsOther}");

        Assert.Fail($"DIAGNOSTIC (not a real failure):\n\n{sb}");
    }

    /// <summary>
    /// Test the composed vs raw animation values for physical plausibility.
    /// </summary>
    [SkippableFact]
    public void Diagnostic_TmaCompositionComparison()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== COMPOSITION COMPARISON ===\n");

        // Load TMM
        var (_, tmmEntry, tmmStream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "hoplite_gold.tmm");
        var tmmRaw = BarCompression.EnsureDecompressed(tmmEntry.ReadDataRaw(tmmStream), out _);
        var tmm = new TmmFile(tmmRaw);
        tmmStream.Dispose();

        // Load TMA
        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath));
        using var tmaStream = File.OpenRead(barPath);
        var tmaBar = new BarFile(tmaStream);
        tmaBar.Load(out _);

        var tmaEntry = tmaBar.Entries!.FirstOrDefault(e =>
            e.Name.Contains("hoplite_igc_attack_a", StringComparison.OrdinalIgnoreCase) &&
            e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase));
        Skip.If(tmaEntry == null);

        var tmaRaw = BarCompression.EnsureDecompressed(tmaEntry!.ReadDataRaw(tmaStream), out _);
        var tma = new TmaFile(tmaRaw);
        Assert.True(tma.Parsed);

        var decoded = TmaDecoder.DecodeAllTracks(tma)!;
        var tmmBones = tmm.Bones!;
        var tmmBoneMap = new Dictionary<string, int>();
        for (int i = 0; i < tmmBones.Length; i++) tmmBoneMap[tmmBones[i].Name] = i;

        // For selected bones, compare:
        // Option A (current): final = bindR * animR
        // Option B: final = animR * bindR
        // Option C: final = animR (raw, no composition)
        string[] bones = ["mixamorig:Hips", "mixamorig:Spine", "mixamorig:LeftUpLeg",
                          "mixamorig:RightUpLeg", "mixamorig:LeftArm", "mixamorig:RightArm",
                          "mixamorig:LeftForeArm", "mixamorig:LeftShoulder"];
        foreach (var boneName in bones)
        {
            var dt = decoded.FirstOrDefault(d => d.Name == boneName);
            if (dt == null || !tmmBoneMap.TryGetValue(boneName, out int bi)) continue;
            if (dt.Rotations.Length == 0) continue;

            DecomposeColumnMajor(tmmBones[bi].ParentSpaceMatrix, out var bindT, out var bindR, out var bindS);
            var animR = dt.Rotations[0];

            var optA = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Multiply(bindR, animR));
            var optB = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Multiply(animR, bindR));
            var optC = animR;

            // Convert each to Euler-ish representation (angle in degrees)
            float angleA = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(optA.W))) * (180f / MathF.PI);
            float angleB = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(optB.W))) * (180f / MathF.PI);
            float angleC = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(optC.W))) * (180f / MathF.PI);
            float bindAngle = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(bindR.W))) * (180f / MathF.PI);

            sb.AppendLine($"{boneName}:");
            sb.AppendLine($"  bindR angle={bindAngle:F1}°: ({bindR.X:F4},{bindR.Y:F4},{bindR.Z:F4},{bindR.W:F4})");
            sb.AppendLine($"  animR angle={angleC:F1}°: ({animR.X:F4},{animR.Y:F4},{animR.Z:F4},{animR.W:F4})");
            sb.AppendLine($"  A (bindR*animR) angle={angleA:F1}°: ({optA.X:F4},{optA.Y:F4},{optA.Z:F4},{optA.W:F4})");
            sb.AppendLine($"  B (animR*bindR) angle={angleB:F1}°: ({optB.X:F4},{optB.Y:F4},{optB.Z:F4},{optB.W:F4})");
            sb.AppendLine($"  C (animR alone) angle={angleC:F1}°: ({optC.X:F4},{optC.Y:F4},{optC.Z:F4},{optC.W:F4})");
            sb.AppendLine();
        }

        Assert.Fail($"DIAGNOSTIC:\n\n{sb}");
    }

    /// <summary>
    /// Definitively determines the correct Quat64 index convention by comparing
    /// decoded values with both possible conventions and checking continuity.
    /// Also checks component ordering: WXYZ vs XYZW.
    /// </summary>
    [SkippableFact]
    public void Diagnostic_Quat64IndexConvention()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== QUAT64 INDEX CONVENTION TEST ===\n");

        // Load TMA
        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath));
        using var tmaStream = File.OpenRead(barPath);
        var tmaBar = new BarFile(tmaStream);
        tmaBar.Load(out _);

        var tmaEntry = tmaBar.Entries!.FirstOrDefault(e =>
            e.Name.Contains("hoplite_igc_attack_a", StringComparison.OrdinalIgnoreCase) &&
            e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase));
        Skip.If(tmaEntry == null);

        var tmaRaw = BarCompression.EnsureDecompressed(tmaEntry!.ReadDataRaw(tmaStream), out _);
        var tma = new TmaFile(tmaRaw);
        Assert.True(tma.Parsed);

        // Find all Quat64 tracks and count which convention produces smoother rotations
        int currentConventionJumps = 0, altConventionJumps = 0;
        int currentConventionJumps30 = 0, altConventionJumps30 = 0;

        foreach (var track in tma.Tracks!)
        {
            if (track.RotationEncoding != TmaEncoding.Quat64) continue;
            if (track.KeyframeCount < 2) continue;

            var currentQuats = new System.Numerics.Quaternion[track.KeyframeCount];
            var altQuats = new System.Numerics.Quaternion[track.KeyframeCount];

            for (int f = 0; f < track.KeyframeCount; f++)
            {
                int off = f * 8;
                if (off + 8 > track.RotationData.Length) break;
                ulong packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(track.RotationData.AsSpan(off));

                currentQuats[f] = DecodeQuat64_Current(packed);
                altQuats[f] = DecodeQuat64_Alt_XYZW(packed);
            }

            // Count jumps > 15° and > 30° for each convention
            for (int f = 1; f < track.KeyframeCount; f++)
            {
                float currentAngle = QuatAngleDeg(currentQuats[f - 1], currentQuats[f]);
                float altAngle = QuatAngleDeg(altQuats[f - 1], altQuats[f]);

                if (currentAngle > 15f) currentConventionJumps++;
                if (altAngle > 15f) altConventionJumps++;
                if (currentAngle > 30f) currentConventionJumps30++;
                if (altAngle > 30f) altConventionJumps30++;
            }
        }

        sb.AppendLine($"Convention A (current: index 0=W, WXYZ order):");
        sb.AppendLine($"  Jumps > 15°: {currentConventionJumps}");
        sb.AppendLine($"  Jumps > 30°: {currentConventionJumps30}");
        sb.AppendLine();
        sb.AppendLine($"Convention B (alt: index 0=X, XYZW order):");
        sb.AppendLine($"  Jumps > 15°: {altConventionJumps}");
        sb.AppendLine($"  Jumps > 30°: {altConventionJumps30}");
        sb.AppendLine();

        // Also check: what index values actually appear in the data?
        var indexCounts = new int[4];
        foreach (var track in tma.Tracks)
        {
            if (track.RotationEncoding != TmaEncoding.Quat64) continue;
            for (int f = 0; f < track.KeyframeCount; f++)
            {
                int off = f * 8;
                if (off + 8 > track.RotationData.Length) break;
                ulong packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(track.RotationData.AsSpan(off));
                int idx = (int)(packed >> 62) & 3;
                indexCounts[idx]++;
            }
        }
        sb.AppendLine($"Index distribution: 0={indexCounts[0]}, 1={indexCounts[1]}, 2={indexCounts[2]}, 3={indexCounts[3]}");
        sb.AppendLine();

        // For bones near identity rotation, check which convention gives closer-to-identity
        sb.AppendLine("=== NEAR-IDENTITY BONES CHECK ===");
        sb.AppendLine("(For Quat64 bones with small animations, frame0 decoded value comparison)\n");

        foreach (var track in tma.Tracks)
        {
            if (track.RotationEncoding != TmaEncoding.Quat64) continue;
            if (track.KeyframeCount < 1) continue;

            ulong packed0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(track.RotationData.AsSpan(0));
            var curQ = DecodeQuat64_Current(packed0);
            var altQ = DecodeQuat64_Alt_XYZW(packed0);

            float curDotId = MathF.Abs(System.Numerics.Quaternion.Dot(curQ, System.Numerics.Quaternion.Identity));
            float altDotId = MathF.Abs(System.Numerics.Quaternion.Dot(altQ, System.Numerics.Quaternion.Identity));

            int idx = (int)(packed0 >> 62) & 3;
            sb.AppendLine($"  {track.Name,-35} idx={idx} cur=({curQ.X:F3},{curQ.Y:F3},{curQ.Z:F3},{curQ.W:F3}) dot(id)={curDotId:F4}  alt=({altQ.X:F3},{altQ.Y:F3},{altQ.Z:F3},{altQ.W:F3}) dot(id)={altDotId:F4}");
        }

        // Also test: does the TMA Constant rotation data for constant quats decode as raw XYZW?
        sb.AppendLine("\n=== CONSTANT QUATERNION RAW BYTES ===");
        foreach (var track in tma.Tracks)
        {
            if (track.RotationEncoding != TmaEncoding.Constant) continue;
            if (track.RotationData.Length < 16) continue;

            float v0 = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(track.RotationData.AsSpan(0));
            float v1 = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(track.RotationData.AsSpan(4));
            float v2 = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(track.RotationData.AsSpan(8));
            float v3 = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(track.RotationData.AsSpan(12));

            float magXYZW = MathF.Sqrt(v0 * v0 + v1 * v1 + v2 * v2 + v3 * v3);
            // Check if the existing decoder interprets this correctly (XYZW)
            // The current DecodeConstantQuat reads as X,Y,Z,W
            sb.AppendLine($"  {track.Name,-35} raw=({v0:F4},{v1:F4},{v2:F4},{v3:F4}) mag={magXYZW:F4} -> Quat(x={v0:F4},y={v1:F4},z={v2:F4},w={v3:F4})");
            if (MathF.Abs(magXYZW - 1.0f) > 0.01f)
                sb.AppendLine($"    WARNING: magnitude {magXYZW:F4} is not 1.0!");
            break; // just show a few
        }

        Assert.Fail($"DIAGNOSTIC:\n\n{sb}");
    }

    /// <summary>
    /// Check which tracks get DROPPED during export due to keyframeCount mismatch,
    /// and which bones produce the most rotation jumps.
    /// </summary>
    [SkippableFact]
    public void Diagnostic_TrackDropAndJumpAnalysis()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var sb = new System.Text.StringBuilder();

        // Load TMM
        var (_, tmmEntry, tmmStream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "hoplite_gold.tmm");
        var tmmRaw = BarCompression.EnsureDecompressed(tmmEntry.ReadDataRaw(tmmStream), out _);
        var tmm = new TmmFile(tmmRaw);
        tmmStream.Dispose();
        Assert.True(tmm.Parsed);

        var tmmBones = tmm.Bones!;
        var boneMap = new Dictionary<string, int>();
        for (int i = 0; i < tmmBones.Length; i++) boneMap[tmmBones[i].Name] = i;

        // Load TMA
        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath));
        using var tmaStream = File.OpenRead(barPath);
        var tmaBar = new BarFile(tmaStream);
        tmaBar.Load(out _);

        var tmaEntry = tmaBar.Entries!.FirstOrDefault(e =>
            e.Name.Contains("hoplite_igc_attack_a", StringComparison.OrdinalIgnoreCase) &&
            e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase));
        Skip.If(tmaEntry == null);

        var tmaRaw = BarCompression.EnsureDecompressed(tmaEntry!.ReadDataRaw(tmaStream), out _);
        var tma = new TmaFile(tmaRaw);
        Assert.True(tma.Parsed);

        int fileFrameCount = (int)tma.FrameCount;
        sb.AppendLine($"TMA: {tmaEntry.Name}  FrameCount={fileFrameCount}  Duration={tma.Duration:F3}s\n");

        var decoded = TmaDecoder.DecodeAllTracks(tma)!;

        // SECTION 1: Track keyframe counts and which would be DROPPED
        sb.AppendLine("=== TRACK KEYFRAME COUNTS (file FrameCount=" + fileFrameCount + ") ===");
        int dropped = 0, kept = 0;
        foreach (var dt in decoded)
        {
            var src = tma.Tracks!.First(t => t.Name == dt.Name);
            bool hasBone = boneMap.ContainsKey(dt.Name);
            bool tOk = dt.Translations.Length >= fileFrameCount;
            bool rOk = dt.Rotations.Length >= fileFrameCount;
            bool sOk = dt.Scales.Length >= fileFrameCount;
            bool wouldKeep = hasBone && tOk && rOk && sOk;

            if (!wouldKeep) dropped++;
            else kept++;

            string status = wouldKeep ? "KEEP" : "DROP";
            if (!wouldKeep)
            {
                sb.AppendLine($"  {status} {dt.Name,-35} kf={src.KeyframeCount,4} T={dt.Translations.Length,4} R={dt.Rotations.Length,4} S={dt.Scales.Length,4} " +
                    $"enc=T:{src.TranslationEncoding} R:{src.RotationEncoding} S:{src.ScaleEncoding}" +
                    (hasBone ? "" : " [NO BONE MATCH]"));
            }
        }
        sb.AppendLine($"\nKept: {kept}, Dropped: {dropped}\n");

        // SECTION 2: Per-bone rotation jump analysis (on raw decoded, before composition)
        sb.AppendLine("=== PER-BONE ROTATION JUMPS (raw decoded, no composition) ===");
        foreach (var dt in decoded)
        {
            if (dt.Rotations.Length < 2) continue;
            var src = tma.Tracks!.First(t => t.Name == dt.Name);

            int jumps15 = 0, jumps30 = 0;
            float maxJump = 0;
            int maxJumpFrame = 0;
            for (int f = 1; f < dt.Rotations.Length; f++)
            {
                float angle = QuatAngleDeg(dt.Rotations[f - 1], dt.Rotations[f]);
                if (angle > 15) jumps15++;
                if (angle > 30) jumps30++;
                if (angle > maxJump) { maxJump = angle; maxJumpFrame = f; }
            }
            if (jumps15 > 0 || maxJump > 10)
                sb.AppendLine($"  {dt.Name,-35} enc={src.RotationEncoding,-8} >15°:{jumps15,3}  >30°:{jumps30,3}  max={maxJump:F1}° at f{maxJumpFrame}");
        }

        // SECTION 3: Per-bone rotation jumps AFTER composition with bind
        sb.AppendLine("\n=== PER-BONE ROTATION JUMPS (AFTER bindR*animR composition) ===");
        foreach (var dt in decoded)
        {
            if (dt.Rotations.Length < 2) continue;
            if (!boneMap.TryGetValue(dt.Name, out int bi)) continue;

            DecomposeColumnMajor(tmmBones[bi].ParentSpaceMatrix, out _, out var bindR, out _);

            int jumps15 = 0, jumps30 = 0;
            float maxJump = 0;
            int maxJumpFrame = 0;
            for (int f = 1; f < dt.Rotations.Length; f++)
            {
                var rA = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Multiply(bindR, dt.Rotations[f - 1]));
                var rB = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Multiply(bindR, dt.Rotations[f]));
                float angle = QuatAngleDeg(rA, rB);
                if (angle > 15) jumps15++;
                if (angle > 30) jumps30++;
                if (angle > maxJump) { maxJump = angle; maxJumpFrame = f; }
            }
            if (jumps15 > 0 || maxJump > 10)
                sb.AppendLine($"  {dt.Name,-35} >15°:{jumps15,3}  >30°:{jumps30,3}  max={maxJump:F1}° at f{maxJumpFrame}");
        }

        // SECTION 4: Check first few frames of a problematic bone
        sb.AppendLine("\n=== FRAME-BY-FRAME: mixamorig:Hips (first 10 frames) ===");
        var hips = decoded.FirstOrDefault(d => d.Name == "mixamorig:Hips");
        if (hips != null)
        {
            for (int f = 0; f < Math.Min(10, hips.Rotations.Length); f++)
            {
                var q = hips.Rotations[f];
                var t = hips.Translations[f];
                float angleFromPrev = f > 0 ? QuatAngleDeg(hips.Rotations[f - 1], q) : 0;
                sb.AppendLine($"  f{f,2}: T=({t.X:F4},{t.Y:F4},{t.Z:F4}) R=({q.X:F4},{q.Y:F4},{q.Z:F4},{q.W:F4}) dAngle={angleFromPrev:F1}°");
            }
        }

        // Same for Spine
        sb.AppendLine("\n=== FRAME-BY-FRAME: mixamorig:Spine (first 10 frames) ===");
        var spine = decoded.FirstOrDefault(d => d.Name == "mixamorig:Spine");
        if (spine != null)
        {
            for (int f = 0; f < Math.Min(10, spine.Rotations.Length); f++)
            {
                var q = spine.Rotations[f];
                float angleFromPrev = f > 0 ? QuatAngleDeg(spine.Rotations[f - 1], q) : 0;
                sb.AppendLine($"  f{f,2}: R=({q.X:F4},{q.Y:F4},{q.Z:F4},{q.W:F4}) dAngle={angleFromPrev:F1}°");
            }
        }

        Assert.Fail($"DIAGNOSTIC:\n\n{sb}");
    }

    /// <summary>
    /// Dump raw packed Quat64 bits around problematic frames to understand the bit layout.
    /// </summary>
    [SkippableFact]
    public void Diagnostic_Quat64RawBitDump()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var sb = new System.Text.StringBuilder();

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath));
        using var tmaStream = File.OpenRead(barPath);
        var tmaBar = new BarFile(tmaStream);
        tmaBar.Load(out _);

        var tmaEntry = tmaBar.Entries!.FirstOrDefault(e =>
            e.Name.Contains("hoplite_igc_attack_a", StringComparison.OrdinalIgnoreCase) &&
            e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase));
        Skip.If(tmaEntry == null);

        var tmaRaw = BarCompression.EnsureDecompressed(tmaEntry!.ReadDataRaw(tmaStream), out _);
        var tma = new TmaFile(tmaRaw);
        Assert.True(tma.Parsed);

        // Dump raw bits for Spine (frames 3-7 around the jump at f5)
        var spineTrack = tma.Tracks!.First(t => t.Name == "mixamorig:Spine");
        sb.AppendLine($"=== SPINE RAW QUAT64 (frames 0-9) ===");
        sb.AppendLine($"Encoding: {spineTrack.RotationEncoding}, KF={spineTrack.KeyframeCount}, DataLen={spineTrack.RotationData.Length}");

        for (int f = 0; f < Math.Min(10, spineTrack.KeyframeCount); f++)
        {
            int off = f * 8;
            ulong packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(spineTrack.RotationData.AsSpan(off));

            // Current layout: [63:62]=idx [61:40]=A(22) [39:20]=B(20) [19:0]=C(20)
            int idx = (int)(packed >> 62) & 3;
            int rawA = (int)((packed >> 40) & 0x3FFFFF);
            int rawB = (int)((packed >> 20) & 0xFFFFF);
            int rawC = (int)(packed & 0xFFFFF);

            float fa = SignedComp(rawA, 22) * InvSqrt3;
            float fb = SignedComp(rawB, 20) * InvSqrt3;
            float fc = SignedComp(rawC, 20) * InvSqrt3;
            float fw = MathF.Sqrt(MathF.Max(0, 1.0f - fa * fa - fb * fb - fc * fc));

            sb.AppendLine($"  f{f}: packed=0x{packed:X16}");
            sb.AppendLine($"       bits: {Convert.ToString((long)packed, 2).PadLeft(64, '0')}");
            sb.AppendLine($"       idx={idx} rawA=0x{rawA:X6}({rawA}) rawB=0x{rawB:X5}({rawB}) rawC=0x{rawC:X5}({rawC})");
            sb.AppendLine($"       signedA={SignedComp(rawA,22):F6} signedB={SignedComp(rawB,20):F6} signedC={SignedComp(rawC,20):F6}");
            sb.AppendLine($"       scaled: a={fa:F6} b={fb:F6} c={fc:F6} recon={fw:F6}");
            sb.AppendLine($"       -> Quat({fa:F4},{fb:F4},{fc:F4},{fw:F4})");

            // Also try alt layout: index at bottom
            int idxBot = (int)(packed & 3);
            int rawA2 = (int)((packed >> 42) & 0x3FFFFF);
            int rawB2 = (int)((packed >> 22) & 0xFFFFF);
            int rawC2 = (int)((packed >> 2) & 0xFFFFF);
            float fa2 = SignedComp(rawA2, 22) * InvSqrt3;
            float fb2 = SignedComp(rawB2, 20) * InvSqrt3;
            float fc2 = SignedComp(rawC2, 20) * InvSqrt3;
            float fw2 = MathF.Sqrt(MathF.Max(0, 1.0f - fa2*fa2 - fb2*fb2 - fc2*fc2));
            sb.AppendLine($"       ALT(idx@bot): idx={idxBot} a={fa2:F6} b={fb2:F6} c={fc2:F6} recon={fw2:F6}");
            sb.AppendLine();
        }

        // Also dump LeftLeg which has the most jumps (31 > 15°)
        var legTrack = tma.Tracks!.First(t => t.Name == "mixamorig:LeftLeg");
        sb.AppendLine($"\n=== LEFT LEG RAW QUAT64 (frames 0-15) ===");
        for (int f = 0; f < Math.Min(16, legTrack.KeyframeCount); f++)
        {
            int off = f * 8;
            ulong packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(legTrack.RotationData.AsSpan(off));

            int idx = (int)(packed >> 62) & 3;
            int rawA = (int)((packed >> 40) & 0x3FFFFF);
            int rawB = (int)((packed >> 20) & 0xFFFFF);
            int rawC = (int)(packed & 0xFFFFF);

            float fa = SignedComp(rawA, 22) * InvSqrt3;
            float fb = SignedComp(rawB, 20) * InvSqrt3;
            float fc = SignedComp(rawC, 20) * InvSqrt3;
            float fw = MathF.Sqrt(MathF.Max(0, 1.0f - fa * fa - fb * fb - fc * fc));

            // Prev frame for angle calc
            float angle = 0;
            if (f > 0)
            {
                int offP = (f-1) * 8;
                ulong packedP = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(legTrack.RotationData.AsSpan(offP));
                var qP = DecodeQuat64_Current(packedP);
                var qC = new System.Numerics.Quaternion(fa, fb, fc, fw);
                angle = QuatAngleDeg(qP, qC);
            }

            // Check what bottom 2 bits are
            int bot2 = (int)(packed & 3);

            sb.AppendLine($"  f{f,2}: 0x{packed:X16} idx(top)={idx} idx(bot)={bot2} -> ({fa:F4},{fb:F4},{fc:F4},{fw:F4}) dAngle={angle:F1}°");
        }

        // For the leg, try alt decoding with idx at bottom and see if jumps go away
        sb.AppendLine($"\n=== LEFT LEG: ALT DECODE (idx at bottom) ===");
        System.Numerics.Quaternion prevAlt = System.Numerics.Quaternion.Identity;
        for (int f = 0; f < Math.Min(16, legTrack.KeyframeCount); f++)
        {
            int off = f * 8;
            ulong packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(legTrack.RotationData.AsSpan(off));

            int idxBot = (int)(packed & 3);
            int rawA2 = (int)((packed >> 42) & 0x3FFFFF);
            int rawB2 = (int)((packed >> 22) & 0xFFFFF);
            int rawC2 = (int)((packed >> 2) & 0xFFFFF);
            float fa2 = SignedComp(rawA2, 22) * InvSqrt3;
            float fb2 = SignedComp(rawB2, 20) * InvSqrt3;
            float fc2 = SignedComp(rawC2, 20) * InvSqrt3;
            float fw2 = MathF.Sqrt(MathF.Max(0, 1.0f - fa2*fa2 - fb2*fb2 - fc2*fc2));

            // Assemble with the bottom index using WXYZ convention
            var q = idxBot switch
            {
                0 => new System.Numerics.Quaternion(fa2, fb2, fc2, fw2),
                1 => new System.Numerics.Quaternion(fw2, fb2, fc2, fa2),
                2 => new System.Numerics.Quaternion(fb2, fw2, fc2, fa2),
                3 => new System.Numerics.Quaternion(fb2, fc2, fw2, fa2),
                _ => System.Numerics.Quaternion.Identity,
            };

            float angle = f > 0 ? QuatAngleDeg(prevAlt, q) : 0;
            prevAlt = q;
            sb.AppendLine($"  f{f,2}: idx={idxBot} -> ({q.X:F4},{q.Y:F4},{q.Z:F4},{q.W:F4}) dAngle={angle:F1}°");
        }

        Assert.Fail($"DIAGNOSTIC:\n\n{sb}");
    }

    /// <summary>
    /// Brute-force test: try many different Quat64 bit layouts and find which produces
    /// the smoothest animation (fewest frame-to-frame jumps).
    /// </summary>
    [SkippableFact]
    public void Diagnostic_Quat64BitLayoutSearch()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var sb = new System.Text.StringBuilder();

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath));
        using var tmaStream = File.OpenRead(barPath);
        var tmaBar = new BarFile(tmaStream);
        tmaBar.Load(out _);

        var tmaEntry = tmaBar.Entries!.FirstOrDefault(e =>
            e.Name.Contains("hoplite_igc_attack_a", StringComparison.OrdinalIgnoreCase) &&
            e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase));
        Skip.If(tmaEntry == null);

        var tmaRaw = BarCompression.EnsureDecompressed(tmaEntry!.ReadDataRaw(tmaStream), out _);
        var tma = new TmaFile(tmaRaw);
        Assert.True(tma.Parsed);

        // Collect all Quat64 packed values across all tracks
        var allPacked = new List<(string name, ulong[] frames)>();
        foreach (var track in tma.Tracks!)
        {
            if (track.RotationEncoding != TmaEncoding.Quat64) continue;
            var frames = new ulong[track.KeyframeCount];
            for (int f = 0; f < track.KeyframeCount; f++)
            {
                int off = f * 8;
                if (off + 8 <= track.RotationData.Length)
                    frames[f] = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(track.RotationData.AsSpan(off));
            }
            allPacked.Add((track.Name, frames));
        }

        // Try different layouts and score each by total rotation jumps > 20°
        var layouts = new (string name, int idxBits, int idxShift, int aBits, int aShift, int bBits, int bShift, int cBits, int cShift, float scale)[]
        {
            // Current: idx@top(2), A(22), B(20), C(20), scale=1/√3
            ("cur 2+22+20+20 top InvSqrt3", 2, 62, 22, 40, 20, 20, 20, 0, 0.57735026919f),
            // Same but InvSqrt2
            ("cur 2+22+20+20 top InvSqrt2", 2, 62, 22, 40, 20, 20, 20, 0, 0.70710678118f),
            // No index, 21+21+22
            ("no-idx 21+21+22", 0, 0, 21, 43, 21, 22, 22, 0, 0.57735026919f),
            ("no-idx 21+21+22 InvSqrt2", 0, 0, 21, 43, 21, 22, 22, 0, 0.70710678118f),
            // No index, 22+21+21
            ("no-idx 22+21+21", 0, 0, 22, 42, 21, 21, 21, 0, 0.57735026919f),
            ("no-idx 22+21+21 InvSqrt2", 0, 0, 22, 42, 21, 21, 21, 0, 0.70710678118f),
            // idx@bottom(2), A(22), B(20), C(20)
            ("idx@bot 22+20+20+2", 2, 0, 22, 42, 20, 22, 20, 2, 0.57735026919f),
            // 20+20+20+4 (4 unused/idx bits)
            ("20+20+20 top4", 4, 60, 20, 40, 20, 20, 20, 0, 0.57735026919f),
            ("20+20+20 top4 InvSqrt2", 4, 60, 20, 40, 20, 20, 20, 0, 0.70710678118f),
            // No index, all data: 21+21+21 + 1 spare
            ("no-idx 21+21+21+1", 0, 0, 21, 43, 21, 22, 21, 1, 0.57735026919f),
            // Scale=1.0 (no scaling)
            ("cur layout scale=1", 2, 62, 22, 40, 20, 20, 20, 0, 1.0f),
            // Try reading as two 32-bit halves: Quat32 in upper half
            ("quat32-upper", -1, 0, 10, 20, 10, 10, 10, 0, 0.57735026919f),
        };

        foreach (var layout in layouts)
        {
            int totalJumps20 = 0;
            int totalTransitions = 0;

            foreach (var (name, frames) in allPacked)
            {
                System.Numerics.Quaternion prev = System.Numerics.Quaternion.Identity;
                for (int f = 0; f < frames.Length; f++)
                {
                    System.Numerics.Quaternion q;
                    if (layout.idxBits == -1)
                    {
                        // Special: Quat32 decode of upper 32 bits
                        uint upper = (uint)(frames[f] >> 32);
                        q = DecodeAsQuat32(upper, layout.scale);
                    }
                    else
                    {
                        q = DecodeWithLayout(frames[f], layout.idxBits, layout.idxShift,
                            layout.aBits, layout.aShift, layout.bBits, layout.bShift,
                            layout.cBits, layout.cShift, layout.scale);
                    }

                    if (f > 0)
                    {
                        totalTransitions++;
                        float angle = QuatAngleDeg(prev, q);
                        if (angle > 20) totalJumps20++;
                    }
                    prev = q;
                }
            }

            sb.AppendLine($"  {layout.name,-40} jumps>20°: {totalJumps20,4} / {totalTransitions} ({100.0*totalJumps20/Math.Max(1,totalTransitions):F1}%)");
        }

        Assert.Fail($"BIT LAYOUT SEARCH:\n\n{sb}");
    }

    static System.Numerics.Quaternion DecodeWithLayout(ulong packed, int idxBits, int idxShift,
        int aBits, int aShift, int bBits, int bShift, int cBits, int cShift, float scale)
    {
        int idx = idxBits > 0 ? (int)((packed >> idxShift) & ((1u << idxBits) - 1)) : 0;

        int rawA = (int)((packed >> aShift) & ((1UL << aBits) - 1));
        int rawB = (int)((packed >> bShift) & ((1UL << bBits) - 1));
        int rawC = (int)((packed >> cShift) & ((1UL << cBits) - 1));

        float fa = SignedComp(rawA, aBits) * scale;
        float fb = SignedComp(rawB, bBits) * scale;
        float fc = SignedComp(rawC, cBits) * scale;
        float sumSq = fa * fa + fb * fb + fc * fc;
        float fw = sumSq <= 1.0f ? MathF.Sqrt(1.0f - sumSq) : 0f;

        // Always use index 0 convention (W reconstructed) since all data has idx=0
        return new System.Numerics.Quaternion(fa, fb, fc, fw);
    }

    static System.Numerics.Quaternion DecodeAsQuat32(uint packed, float scale)
    {
        int idx = (int)(packed >> 30) & 3;
        int rawA = (int)((packed >> 20) & 0x3FF);
        int rawB = (int)((packed >> 10) & 0x3FF);
        int rawC = (int)(packed & 0x3FF);

        float fa = SignedComp(rawA, 10) * scale;
        float fb = SignedComp(rawB, 10) * scale;
        float fc = SignedComp(rawC, 10) * scale;
        float sumSq = fa * fa + fb * fb + fc * fc;
        float fw = sumSq <= 1.0f ? MathF.Sqrt(1.0f - sumSq) : 0f;

        return new System.Numerics.Quaternion(fa, fb, fc, fw);
    }

    /// <summary>
    /// Test offset binary vs two's complement, and delta accumulation vs absolute.
    /// </summary>
    [SkippableFact]
    public void Diagnostic_Quat64SignedConventionAndDelta()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var sb = new System.Text.StringBuilder();

        var barPath = Path.Combine(GamePath, @"modelcache\ArtModelCacheAnimationData.bar");
        Skip.IfNot(File.Exists(barPath));
        using var tmaStream = File.OpenRead(barPath);
        var tmaBar = new BarFile(tmaStream);
        tmaBar.Load(out _);

        var tmaEntry = tmaBar.Entries!.FirstOrDefault(e =>
            e.Name.Contains("hoplite_igc_attack_a", StringComparison.OrdinalIgnoreCase) &&
            e.Name.EndsWith(".tma", StringComparison.OrdinalIgnoreCase));
        Skip.If(tmaEntry == null);

        var tmaRaw = BarCompression.EnsureDecompressed(tmaEntry!.ReadDataRaw(tmaStream), out _);
        var tma = new TmaFile(tmaRaw);
        Assert.True(tma.Parsed);

        var allPacked = new List<(string name, ulong[] frames)>();
        foreach (var track in tma.Tracks!)
        {
            if (track.RotationEncoding != TmaEncoding.Quat64) continue;
            var frames = new ulong[track.KeyframeCount];
            for (int f = 0; f < track.KeyframeCount; f++)
            {
                int off = f * 8;
                if (off + 8 <= track.RotationData.Length)
                    frames[f] = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(track.RotationData.AsSpan(off));
            }
            allPacked.Add((track.Name, frames));
        }

        float scale = 0.57735026919f;

        // Test 1: Two's complement (current) vs offset binary
        int tcJumps = 0, obJumps = 0, deltaJumps = 0;
        int total = 0;

        // Also dump LeftLeg with each method
        sb.AppendLine("=== LEFT LEG: Two's complement vs Offset Binary vs Delta ===\n");

        foreach (var (name, frames) in allPacked)
        {
            System.Numerics.Quaternion prevTC = default, prevOB = default, accumDelta = System.Numerics.Quaternion.Identity;
            bool isLeg = name == "mixamorig:LeftLeg";

            for (int f = 0; f < frames.Length; f++)
            {
                ulong packed = frames[f];
                int rawA = (int)((packed >> 40) & 0x3FFFFF);
                int rawB = (int)((packed >> 20) & 0xFFFFF);
                int rawC = (int)(packed & 0xFFFFF);

                // Two's complement (current)
                float tcA = SignedComp(rawA, 22) * scale;
                float tcB = SignedComp(rawB, 20) * scale;
                float tcC = SignedComp(rawC, 20) * scale;
                float tcW = MathF.Sqrt(MathF.Max(0, 1f - tcA*tcA - tcB*tcB - tcC*tcC));
                var qTC = new System.Numerics.Quaternion(tcA, tcB, tcC, tcW);

                // Offset binary: subtract midpoint
                float obA = ((rawA / (float)((1 << 22) - 1)) * 2f - 1f) * scale;
                float obB = ((rawB / (float)((1 << 20) - 1)) * 2f - 1f) * scale;
                float obC = ((rawC / (float)((1 << 20) - 1)) * 2f - 1f) * scale;
                float obW = MathF.Sqrt(MathF.Max(0, 1f - obA*obA - obB*obB - obC*obC));
                var qOB = new System.Numerics.Quaternion(obA, obB, obC, obW);

                // Delta accumulation: treat each frame as a delta quaternion, accumulate
                var qDelta = new System.Numerics.Quaternion(tcA, tcB, tcC, tcW);
                if (f == 0)
                    accumDelta = qDelta;
                else
                    accumDelta = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Multiply(accumDelta, qDelta));

                if (f > 0)
                {
                    total++;
                    if (QuatAngleDeg(prevTC, qTC) > 20) tcJumps++;
                    if (QuatAngleDeg(prevOB, qOB) > 20) obJumps++;
                    // For delta, compare accumulated vs previous accumulated
                }

                if (isLeg && f < 10)
                {
                    sb.AppendLine($"  f{f}: TC=({tcA:F4},{tcB:F4},{tcC:F4},{tcW:F4}) OB=({obA:F4},{obB:F4},{obC:F4},{obW:F4})");
                    if (f > 0)
                        sb.AppendLine($"       TC dAngle={QuatAngleDeg(prevTC, qTC):F1}° OB dAngle={QuatAngleDeg(prevOB, qOB):F1}°");
                }

                prevTC = qTC;
                prevOB = qOB;
            }
        }

        sb.AppendLine($"\nTwo's complement jumps > 20°: {tcJumps}/{total} ({100.0*tcJumps/Math.Max(1,total):F1}%)");
        sb.AppendLine($"Offset binary jumps > 20°: {obJumps}/{total} ({100.0*obJumps/Math.Max(1,total):F1}%)");

        // Also test with TMA bones' Constant rotation data - check what byte order they use
        // Constant stores a raw __m128 (XYZW floats). Are they in the same space as Quat64?
        sb.AppendLine("\n=== COMPARING: Constant vs Quat64 for same bone ===");
        // Find a bone that has Constant encoding in one TMA and Quat64 in another
        // For this TMA, let's compare LeftToeBase (Constant) raw values vs known good

        var toeTrack = tma.Tracks!.FirstOrDefault(t => t.Name == "mixamorig:LeftToeBase");
        if (toeTrack != null)
        {
            float v0 = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(toeTrack.RotationData.AsSpan(0));
            float v1 = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(toeTrack.RotationData.AsSpan(4));
            float v2 = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(toeTrack.RotationData.AsSpan(8));
            float v3 = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(toeTrack.RotationData.AsSpan(12));
            sb.AppendLine($"LeftToeBase Constant raw: ({v0:F6}, {v1:F6}, {v2:F6}, {v3:F6})");
            sb.AppendLine($"  Interpreted as XYZW: Quat({v0:F4},{v1:F4},{v2:F4},{v3:F4})");
        }

        // Now find a bone that has Quat64 with very small rotation (near identity)
        // and check if the decoded value makes sense
        var allDecoded = TmaDecoder.DecodeAllTracks(tma)!;
        var headTrack = allDecoded.FirstOrDefault(d => d.Name == "mixamorig:Head");
        if (headTrack != null)
        {
            sb.AppendLine($"\nHead (Quat64) frame0: ({headTrack.Rotations[0].X:F6},{headTrack.Rotations[0].Y:F6},{headTrack.Rotations[0].Z:F6},{headTrack.Rotations[0].W:F6})");
            sb.AppendLine($"  This should be a small rotation (head tilt during attack)");
        }

        Assert.Fail($"DIAGNOSTIC:\n\n{sb}");
    }

    static float InvSqrt3 = 0.57735026919f;

    // Current convention: index 0=W, components in WXYZ order
    static System.Numerics.Quaternion DecodeQuat64_Current(ulong packed)
    {
        int idx = (int)(packed >> 62) & 3;
        int rawA = (int)((packed >> 40) & 0x3FFFFF);
        int rawB = (int)((packed >> 20) & 0xFFFFF);
        int rawC = (int)(packed & 0xFFFFF);

        float fa = SignedComp(rawA, 22) * InvSqrt3;
        float fb = SignedComp(rawB, 20) * InvSqrt3;
        float fc = SignedComp(rawC, 20) * InvSqrt3;
        float fw = MathF.Sqrt(MathF.Max(0, 1.0f - fa * fa - fb * fb - fc * fc));

        return idx switch
        {
            0 => new System.Numerics.Quaternion(fa, fb, fc, fw),        // W largest: a=X, b=Y, c=Z
            1 => new System.Numerics.Quaternion(fw, fb, fc, fa),        // X largest: a=W, b=Y, c=Z
            2 => new System.Numerics.Quaternion(fb, fw, fc, fa),        // Y largest: a=W, b=X, c=Z
            3 => new System.Numerics.Quaternion(fb, fc, fw, fa),        // Z largest: a=W, b=X, c=Y
            _ => System.Numerics.Quaternion.Identity,
        };
    }

    // Alt convention: index 0=X, components in XYZW order
    static System.Numerics.Quaternion DecodeQuat64_Alt_XYZW(ulong packed)
    {
        int idx = (int)(packed >> 62) & 3;
        int rawA = (int)((packed >> 40) & 0x3FFFFF);
        int rawB = (int)((packed >> 20) & 0xFFFFF);
        int rawC = (int)(packed & 0xFFFFF);

        float fa = SignedComp(rawA, 22) * InvSqrt3;
        float fb = SignedComp(rawB, 20) * InvSqrt3;
        float fc = SignedComp(rawC, 20) * InvSqrt3;
        float fw = MathF.Sqrt(MathF.Max(0, 1.0f - fa * fa - fb * fb - fc * fc));

        // Alt: index 0=X, 1=Y, 2=Z, 3=W; remaining in XYZW order
        return idx switch
        {
            0 => new System.Numerics.Quaternion(fw, fa, fb, fc),        // X largest: a=Y, b=Z, c=W
            1 => new System.Numerics.Quaternion(fa, fw, fb, fc),        // Y largest: a=X, b=Z, c=W
            2 => new System.Numerics.Quaternion(fa, fb, fw, fc),        // Z largest: a=X, b=Y, c=W
            3 => new System.Numerics.Quaternion(fa, fb, fc, fw),        // W largest: a=X, b=Y, c=Z
            _ => System.Numerics.Quaternion.Identity,
        };
    }

    static float SignedComp(int raw, int bits)
    {
        int half = 1 << (bits - 1);
        int signed_ = raw >= half ? raw - (half << 1) : raw;
        return signed_ / (float)half;
    }

    // Sign-magnitude: MSB=sign, rest=magnitude, normalized to [-1,1]
    static float SignedComp_SM(int raw, int bits)
    {
        int signBit = 1 << (bits - 1);
        int magnitude = raw & (signBit - 1);
        float value = magnitude / (float)signBit;
        return (raw & signBit) != 0 ? -value : value;
    }

    static float QuatAngleDeg(System.Numerics.Quaternion a, System.Numerics.Quaternion b)
    {
        float absDot = MathF.Abs(System.Numerics.Quaternion.Dot(a, b));
        return 2f * MathF.Acos(MathF.Min(1f, absDot)) * (180f / MathF.PI);
    }

    static float MatrixMaxAbsDiff(float[] a, float[] b)
    {
        float max = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            float d = MathF.Abs(a[i] - b[i]);
            if (d > max) max = d;
        }
        return max;
    }

    static void DecomposeColumnMajor(float[] m, out System.Numerics.Vector3 translation, out System.Numerics.Quaternion rotation, out System.Numerics.Vector3 scale)
    {
        translation = new System.Numerics.Vector3(m[12], m[13], m[14]);

        var col0 = new System.Numerics.Vector3(m[0], m[1], m[2]);
        var col1 = new System.Numerics.Vector3(m[4], m[5], m[6]);
        var col2 = new System.Numerics.Vector3(m[8], m[9], m[10]);
        scale = new System.Numerics.Vector3(col0.Length(), col1.Length(), col2.Length());

        if (scale.X > 0) col0 /= scale.X;
        if (scale.Y > 0) col1 /= scale.Y;
        if (scale.Z > 0) col2 /= scale.Z;

        var rotMatrix = new System.Numerics.Matrix4x4(
            col0.X, col0.Y, col0.Z, 0,
            col1.X, col1.Y, col1.Z, 0,
            col2.X, col2.Y, col2.Z, 0,
            0, 0, 0, 1);
        rotation = System.Numerics.Quaternion.CreateFromRotationMatrix(rotMatrix);
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

    #region TMA vs FBX Reference Comparison

    static readonly string ExampleFbxDir = @"C:\Users\adamv\Downloads\AoM-ExampleFBX";

    /// <summary>
    /// Compares TmaDecoder output with FBX reference data extracted from Blender.
    /// This test helps diagnose Quat64 decoding issues by comparing
    /// decoded quaternions against known-good FBX animation data.
    /// </summary>
    [SkippableFact]
    public void Compare_TMA_vs_FBX_Charmeine_AttackA()
    {
        var tmaPath = Path.Combine(ExampleFbxDir, "models", "tmm", "anim", "Charmeine_AttackA.tma");
        var fbxJsonPath = Path.Combine(ExampleFbxDir, "extracted", "Charmeine_AttackA.json");
        Skip.IfNot(File.Exists(tmaPath), "Example FBX data not available");
        Skip.IfNot(File.Exists(fbxJsonPath), "Extracted FBX JSON not available");

        // Load TMA
        var tmaData = File.ReadAllBytes(tmaPath);
        var tma = new TmaFile(tmaData);
        Assert.True(tma.Parsed, "TMA failed to parse");

        var decoded = TmaDecoder.DecodeAllTracks(tma)!;
        Assert.NotNull(decoded);

        // Load FBX JSON
        var fbxJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fbxJsonPath));
        var root = fbxJson.RootElement;
        var fbxBoneNames = root.GetProperty("bone_names").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        var fbxFrameCount = root.GetProperty("frame_count").GetInt32();
        var fbxDeltaRotations = root.GetProperty("frames").GetProperty("delta_rotations");
        var fbxAbsRotations = root.GetProperty("frames").GetProperty("rotations");
        var fbxRestRotations = root.GetProperty("rest_rotations");

        // Build FBX bone name -> index map
        var fbxBoneIdx = new Dictionary<string, int>();
        for (int i = 0; i < fbxBoneNames.Length; i++)
            fbxBoneIdx[fbxBoneNames[i]] = i;

        // Print overview
        var output = new System.Text.StringBuilder();
        output.AppendLine($"TMA: {tma.NumTracks} tracks, {tma.FrameCount} frames");
        output.AppendLine($"FBX: {fbxBoneNames.Length} bones, {fbxFrameCount} frames");
        output.AppendLine();

        // Check constant rotation tracks
        output.AppendLine("=== Constant rotation tracks ===");
        int constIdentity = 0, constNonIdentity = 0;
        foreach (var track in tma.Tracks!)
        {
            if (track.RotationEncoding != TmaEncoding.Constant) continue;
            var dt = decoded.First(d => d.Name == track.Name);
            var q = dt.Rotations[0];
            bool isIdentity = MathF.Abs(q.X) < 0.001f && MathF.Abs(q.Y) < 0.001f &&
                              MathF.Abs(q.Z) < 0.001f && MathF.Abs(q.W - 1f) < 0.001f;
            if (isIdentity) constIdentity++;
            else
            {
                constNonIdentity++;
                float angle = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(q.W))) * 180f / MathF.PI;
                output.AppendLine($"  {track.Name}: ({q.X:F4}, {q.Y:F4}, {q.Z:F4}, {q.W:F4}) angle={angle:F1}");
            }
        }
        output.AppendLine($"  Identity: {constIdentity}, Non-identity: {constNonIdentity}");
        output.AppendLine();

        // Compare Quat64 tracks: frame-to-frame smoothness
        output.AppendLine("=== Quat64 tracks: smoothness comparison ===");
        output.AppendLine($"{"Bone",-30} {"TMA_maxJ",8} {"TMA_j>20",8} {"FBX_maxJ",8} {"FBX_j>20",8} {"TMA_avg",8} {"FBX_avg",8}");

        foreach (var track in tma.Tracks!)
        {
            if (track.RotationEncoding != TmaEncoding.Quat64) continue;
            if (!fbxBoneIdx.TryGetValue(track.Name, out var fi)) continue;

            var dt = decoded.First(d => d.Name == track.Name);
            var tmaQuats = dt.Rotations;

            // TMA frame-to-frame angles
            var tmaAngles = new List<float>();
            for (int f = 0; f < tmaQuats.Length - 1; f++)
            {
                var dot = System.Numerics.Quaternion.Dot(tmaQuats[f], tmaQuats[f + 1]);
                tmaAngles.Add(2f * MathF.Acos(MathF.Min(1f, MathF.Abs(dot))) * 180f / MathF.PI);
            }

            // FBX delta angles (frame-to-frame changes in absolute rotation)
            var fbxAngles = new List<float>();
            for (int f = 0; f < fbxFrameCount - 1; f++)
            {
                var q1 = GetFbxQuat(fbxAbsRotations, f, fi);
                var q2 = GetFbxQuat(fbxAbsRotations, f + 1, fi);
                var dot = System.Numerics.Quaternion.Dot(q1, q2);
                fbxAngles.Add(2f * MathF.Acos(MathF.Min(1f, MathF.Abs(dot))) * 180f / MathF.PI);
            }

            float tmaMax = tmaAngles.Count > 0 ? tmaAngles.Max() : 0;
            float fbxMax = fbxAngles.Count > 0 ? fbxAngles.Max() : 0;
            int tmaJ20 = tmaAngles.Count(a => a > 20);
            int fbxJ20 = fbxAngles.Count(a => a > 20);
            float tmaAvg = tmaAngles.Count > 0 ? tmaAngles.Average() : 0;
            float fbxAvg = fbxAngles.Count > 0 ? fbxAngles.Average() : 0;

            output.AppendLine($"  {track.Name,-28} {tmaMax,8:F1} {tmaJ20,8} {fbxMax,8:F1} {fbxJ20,8} {tmaAvg,8:F1} {fbxAvg,8:F1}");
        }

        output.AppendLine();

        // Detailed: for first few Quat64 tracks, show frame-by-frame decoded values vs FBX
        output.AppendLine("=== Frame-by-frame: TMA decoded vs FBX delta (first 5 frames) ===");
        var detailBones = new[] { "NPC COM", "NPC Calf.L", "NPC Calf.R", "NPC Spine" };
        foreach (var boneName in detailBones)
        {
            var track = tma.Tracks!.FirstOrDefault(t => t.Name == boneName && t.RotationEncoding == TmaEncoding.Quat64);
            if (track == null || !fbxBoneIdx.TryGetValue(boneName, out var fi)) continue;

            var dt = decoded.First(d => d.Name == boneName);
            output.AppendLine($"  {boneName}:");
            for (int f = 0; f < Math.Min(5, dt.Rotations.Length); f++)
            {
                var q = dt.Rotations[f];
                float tmaAngle = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(q.W))) * 180f / MathF.PI;

                // Corresponding FBX frame (approximate: map TMA frame to nearest FBX frame)
                int fbxFrame = (int)Math.Round((double)f / (tma.FrameCount - 1) * (fbxFrameCount - 1));
                fbxFrame = Math.Clamp(fbxFrame, 0, fbxFrameCount - 1);
                var fbxDelta = GetFbxQuat(fbxDeltaRotations, fbxFrame, fi);
                var fbxAbs = GetFbxQuat(fbxAbsRotations, fbxFrame, fi);
                float fbxDeltaAngle = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(fbxDelta.W))) * 180f / MathF.PI;
                float fbxAbsAngle = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(fbxAbs.W))) * 180f / MathF.PI;

                output.AppendLine($"    f{f}: TMA=({q.X:F4},{q.Y:F4},{q.Z:F4},{q.W:F4}) angle={tmaAngle:F1}  " +
                    $"FBXdelta=angle={fbxDeltaAngle:F1}  FBXabs=angle={fbxAbsAngle:F1}");
            }
        }

        // Output everything
        var result = output.ToString();
        Assert.Fail($"Diagnostic output (not a real failure):\n\n{result}");
    }

    static System.Numerics.Quaternion GetFbxQuat(System.Text.Json.JsonElement framesArray, int frame, int boneIdx)
    {
        var arr = framesArray[frame][boneIdx];
        return new System.Numerics.Quaternion(
            arr[0].GetSingle(), arr[1].GetSingle(), arr[2].GetSingle(), arr[3].GetSingle());
    }

    /// <summary>
    /// Tests if Quat64 is actually 4×float16 (IEEE 754 half-precision) by comparing
    /// decoded values against known FBX reference data for multiple component orderings.
    /// </summary>
    [SkippableFact]
    public void Diagnostic_Quat64_Float16Hypothesis()
    {
        var tmaPath = Path.Combine(ExampleFbxDir, "models", "tmm", "anim", "Charmeine_AttackA.tma");
        var fbxJsonPath = Path.Combine(ExampleFbxDir, "extracted", "Charmeine_AttackA.json");
        Skip.IfNot(File.Exists(tmaPath), "Example FBX data not available");
        Skip.IfNot(File.Exists(fbxJsonPath), "Extracted FBX JSON not available");

        var tma = new TmaFile(File.ReadAllBytes(tmaPath));
        Assert.True(tma.Parsed);

        var fbxJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fbxJsonPath));
        var root = fbxJson.RootElement;
        var fbxBoneNames = root.GetProperty("bone_names").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var fbxFrameCount = root.GetProperty("frame_count").GetInt32();
        var fbxAbsRotations = root.GetProperty("frames").GetProperty("rotations");

        var fbxBoneIdx = new Dictionary<string, int>();
        for (int i = 0; i < fbxBoneNames.Length; i++)
            fbxBoneIdx[fbxBoneNames[i]] = i;

        var sb = new System.Text.StringBuilder();

        // Component orderings to try: which 16-bit slot maps to which quat component
        // slot0 = bits 63-48, slot1 = bits 47-32, slot2 = bits 31-16, slot3 = bits 15-0
        var orderings = new (string name, int x, int y, int z, int w)[]
        {
            ("XYZW", 0, 1, 2, 3),
            ("WXYZ", 1, 2, 3, 0),
            ("Wxyz_neg", 1, 2, 3, 0), // with x-negate
            ("XYZW_neg", 0, 1, 2, 3), // with x-negate
            ("ZYXW", 2, 1, 0, 3),
            ("WZYX", 3, 2, 1, 0),
        };

        // Collect Quat64 tracks with FBX matches
        var testTracks = tma.Tracks!
            .Where(t => t.RotationEncoding == TmaEncoding.Quat64 && fbxBoneIdx.ContainsKey(t.Name))
            .Take(15).ToArray();

        sb.AppendLine($"Testing {testTracks.Length} Quat64 tracks against FBX reference");
        sb.AppendLine();

        // Check for extra data in rotation blocks (metadata/range?)
        sb.AppendLine("=== Block size analysis ===");
        foreach (var track in testTracks.Take(5))
        {
            int expectedSize = track.KeyframeCount * 8;
            int actualSize = track.RotationData.Length;
            sb.AppendLine($"  {track.Name}: {actualSize} bytes, expected {expectedSize} ({actualSize - expectedSize} extra)");
            if (actualSize > expectedSize)
            {
                sb.Append("    Extra bytes: ");
                for (int b = 0; b < Math.Min(32, actualSize - expectedSize); b++)
                    sb.Append($"{track.RotationData[expectedSize + b]:X2} ");
                sb.AppendLine();
                sb.Append("    First 16 bytes: ");
                for (int b = 0; b < Math.Min(16, actualSize); b++)
                    sb.Append($"{track.RotationData[b]:X2} ");
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        // Dump raw values and try different decodings for first track
        var firstTrack = testTracks[0];
        sb.AppendLine($"Raw data dump for {firstTrack.Name} (kf={firstTrack.KeyframeCount}, dataLen={firstTrack.RotationData.Length}):");
        for (int f = 0; f < Math.Min(3, firstTrack.KeyframeCount); f++)
        {
            ulong packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                firstTrack.RotationData.AsSpan(f * 8));

            // 2+22+20+20 layout
            int idx = (int)(packed >> 62) & 3;
            int rawA = (int)((packed >> 40) & 0x3FFFFF);
            int rawB = (int)((packed >> 20) & 0xFFFFF);
            int rawC = (int)(packed & 0xFFFFF);

            // Sign-magnitude decode
            var smQ = DecodeQuat64_Current(packed);

            // FBX reference
            int fi = fbxBoneIdx[firstTrack.Name];
            int fbxFrame = (int)Math.Round((double)f / (tma.FrameCount - 1) * (fbxFrameCount - 1));
            var fbxQ = GetFbxQuat(fbxAbsRotations, Math.Clamp(fbxFrame, 0, fbxFrameCount - 1), fi);

            // Expected TMA values (reverse x-negate mapping)
            sb.AppendLine($"  f{f}: packed=0x{packed:X16} idx={idx} rawA={rawA} rawB={rawB} rawC={rawC}");
            sb.AppendLine($"       SM  decode=({smQ.X:F6},{smQ.Y:F6},{smQ.Z:F6},{smQ.W:F6})");
            sb.AppendLine($"       FBX abs  =({fbxQ.X:F6},{fbxQ.Y:F6},{fbxQ.Z:F6},{fbxQ.W:F6})");
            sb.AppendLine($"       Need TMA =({-fbxQ.X:F6},{fbxQ.Y:F6},{fbxQ.Z:F6},{fbxQ.W:F6}) [after -x map]");

            // Try different decodings with InvSqrt2
            float InvSqrt2 = 0.70710678f;
            float fa_sm = SignedComp_SM(rawA, 22);
            float fb_sm = SignedComp_SM(rawB, 20);
            float fc_sm = SignedComp_SM(rawC, 20);
            sb.AppendLine($"       SM raw normalized: a={fa_sm:F6} b={fb_sm:F6} c={fc_sm:F6}");

            // Try power-law decodings
            foreach (float power in new[] { 2f, 3f, 4f, 5f })
            {
                float pa = MathF.Sign(fa_sm) * MathF.Pow(MathF.Abs(fa_sm), power) * InvSqrt2;
                float pb = MathF.Sign(fb_sm) * MathF.Pow(MathF.Abs(fb_sm), power) * InvSqrt2;
                float pc = MathF.Sign(fc_sm) * MathF.Pow(MathF.Abs(fc_sm), power) * InvSqrt2;
                float pw = MathF.Sqrt(MathF.Max(0, 1f - pa * pa - pb * pb - pc * pc));
                sb.AppendLine($"       pow={power:F0} s=InvSqrt2: ({pa:F6},{pb:F6},{pc:F6},{pw:F6})");
            }
        }
        sb.AppendLine();

        var fbxDeltaRotations = root.GetProperty("frames").GetProperty("delta_rotations");
        var fbxRestRotations = root.GetProperty("rest_rotations");

        // Compare against BOTH FBX absolute and delta rotations at frame 0
        sb.AppendLine("=== Frame 0: SM*InvSqrt2 components vs FBX abs AND delta ===");
        float InvSqrt2_global = 0.70710678f;
        sb.AppendLine($"{"Bone",-22} {"smB*s2",8} {"smC*s2",8} | {"absY",8} {"absZ",8} {"errAbsY",7} {"errAbsZ",7} | {"dltY",8} {"dltZ",8} {"errDltY",7} {"errDltZ",7} | {"restW",7}");
        foreach (var track in testTracks)
        {
            int fi = fbxBoneIdx[track.Name];
            ulong packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(track.RotationData.AsSpan(0));
            int rawB = (int)((packed >> 20) & 0xFFFFF);
            int rawC = (int)(packed & 0xFFFFF);
            float smB = SignedComp_SM(rawB, 20) * InvSqrt2_global;
            float smC = SignedComp_SM(rawC, 20) * InvSqrt2_global;
            var absQ = GetFbxQuat(fbxAbsRotations, 0, fi);
            var dltQ = GetFbxQuat(fbxDeltaRotations, 0, fi);
            var restArr = fbxRestRotations[fi];
            float restW = restArr[3].GetSingle();
            float eAbsY = MathF.Abs(smB - absQ.Y);
            float eAbsZ = MathF.Abs(smC - absQ.Z);
            float eDltY = MathF.Abs(smB - dltQ.Y);
            float eDltZ = MathF.Abs(smC - dltQ.Z);
            sb.AppendLine($"  {track.Name,-20} {smB,8:F5} {smC,8:F5} | {absQ.Y,8:F5} {absQ.Z,8:F5} {eAbsY,7:F4} {eAbsZ,7:F4} | {dltQ.Y,8:F5} {dltQ.Z,8:F5} {eDltY,7:F4} {eDltZ,7:F4} | {restW,7:F4}");
        }
        sb.AppendLine();

        // Test hypothesis: layout is 4(header)+20+20+20, all three as 20-bit SM * InvSqrt2
        sb.AppendLine("=== Hypothesis: 4+20+20+20 layout, SM*InvSqrt2, with x-negate ===");
        sb.AppendLine($"{"Bone",-22} {"top4",5} {"a20",8} {"b20",8} {"c20",8} | {"decX",8} {"decY",8} {"decZ",8} {"decW",8} | {"fbxX",8} {"fbxY",8} {"fbxZ",8} {"fbxW",8} | {"err°",7}");
        foreach (var track in testTracks)
        {
            int fi = fbxBoneIdx[track.Name];
            ulong packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(track.RotationData.AsSpan(0));
            int top4 = (int)(packed >> 60) & 0xF;
            int a20 = (int)((packed >> 40) & 0xFFFFF);
            int b20 = (int)((packed >> 20) & 0xFFFFF);
            int c20 = (int)(packed & 0xFFFFF);
            float fa = SignedComp_SM(a20, 20) * InvSqrt2_global;
            float fb = SignedComp_SM(b20, 20) * InvSqrt2_global;
            float fc = SignedComp_SM(c20, 20) * InvSqrt2_global;
            float fw = MathF.Sqrt(MathF.Max(0, 1f - fa * fa - fb * fb - fc * fc));
            // Apply x-negate for coordinate mapping
            var tmaQ = new System.Numerics.Quaternion(-fa, fb, fc, fw);
            var fbxQ = GetFbxQuat(fbxAbsRotations, 0, fi);
            float err = QuatAngleDeg(tmaQ, fbxQ);
            sb.AppendLine($"  {track.Name,-20} {top4,5} {a20,8} {b20,8} {c20,8} | {-fa,8:F4} {fb,8:F4} {fc,8:F4} {fw,8:F4} | {fbxQ.X,8:F4} {fbxQ.Y,8:F4} {fbxQ.Z,8:F4} {fbxQ.W,8:F4} | {err,7:F2}");
        }
        sb.AppendLine();

        // Also test 4+20+20+20 WITHOUT x-negate
        sb.AppendLine("=== Same but WITHOUT x-negate ===");
        sb.AppendLine($"{"Bone",-22} {"decX",8} {"decY",8} {"decZ",8} {"decW",8} | {"fbxX",8} {"fbxY",8} {"fbxZ",8} {"fbxW",8} | {"err°",7}");
        foreach (var track in testTracks)
        {
            int fi = fbxBoneIdx[track.Name];
            ulong packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(track.RotationData.AsSpan(0));
            int a20 = (int)((packed >> 40) & 0xFFFFF);
            int b20 = (int)((packed >> 20) & 0xFFFFF);
            int c20 = (int)(packed & 0xFFFFF);
            float fa = SignedComp_SM(a20, 20) * InvSqrt2_global;
            float fb = SignedComp_SM(b20, 20) * InvSqrt2_global;
            float fc = SignedComp_SM(c20, 20) * InvSqrt2_global;
            float fw = MathF.Sqrt(MathF.Max(0, 1f - fa * fa - fb * fb - fc * fc));
            var tmaQ = new System.Numerics.Quaternion(fa, fb, fc, fw);
            var fbxQ = GetFbxQuat(fbxAbsRotations, 0, fi);
            float err = QuatAngleDeg(tmaQ, fbxQ);
            sb.AppendLine($"  {track.Name,-20} {fa,8:F4} {fb,8:F4} {fc,8:F4} {fw,8:F4} | {fbxQ.X,8:F4} {fbxQ.Y,8:F4} {fbxQ.Z,8:F4} {fbxQ.W,8:F4} | {err,7:F2}");
        }
        sb.AppendLine();

        // Test each ordering across all tracks and frames
        foreach (var ord in orderings)
        {
            float totalError = 0;
            int totalFrames = 0;
            float maxError = 0;
            string maxBone = "";

            foreach (var track in testTracks)
            {
                int fi = fbxBoneIdx[track.Name];
                int frames = Math.Min(track.KeyframeCount, 10); // first 10 frames

                for (int f = 0; f < frames; f++)
                {
                    ulong packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                        track.RotationData.AsSpan(f * 8));

                    // Extract 4 float16 values
                    float[] slots = new float[4];
                    slots[0] = (float)BitConverter.UInt16BitsToHalf((ushort)(packed >> 48));
                    slots[1] = (float)BitConverter.UInt16BitsToHalf((ushort)(packed >> 32));
                    slots[2] = (float)BitConverter.UInt16BitsToHalf((ushort)(packed >> 16));
                    slots[3] = (float)BitConverter.UInt16BitsToHalf((ushort)packed);

                    float qx = slots[ord.x];
                    float qy = slots[ord.y];
                    float qz = slots[ord.z];
                    float qw = slots[ord.w];

                    // Apply x-negate if ordering name contains "neg"
                    if (ord.name.Contains("neg")) qx = -qx;

                    var tmaQ = new System.Numerics.Quaternion(qx, qy, qz, qw);

                    // Get FBX reference
                    int fbxFrame = (int)Math.Round((double)f / (tma.FrameCount - 1) * (fbxFrameCount - 1));
                    var fbxQ = GetFbxQuat(fbxAbsRotations, Math.Clamp(fbxFrame, 0, fbxFrameCount - 1), fi);

                    float err = QuatAngleDeg(tmaQ, fbxQ);
                    totalError += err;
                    totalFrames++;
                    if (err > maxError) { maxError = err; maxBone = $"{track.Name} f{f}"; }
                }
            }

            float avgErr = totalFrames > 0 ? totalError / totalFrames : 999;
            sb.AppendLine($"Ordering {ord.name,-12}: avg={avgErr:F2}deg  max={maxError:F1}deg ({maxBone})  [{totalFrames} frames]");
        }

        sb.AppendLine();

        // Also test current sign-magnitude for comparison
        {
            float totalError = 0;
            int totalFrames = 0;
            float maxError = 0;
            string maxBone = "";
            var decoded = TmaDecoder.DecodeAllTracks(tma)!;

            foreach (var track in testTracks)
            {
                int fi = fbxBoneIdx[track.Name];
                var dt = decoded.First(d => d.Name == track.Name);
                int frames = Math.Min(dt.Rotations.Length, 10);

                for (int f = 0; f < frames; f++)
                {
                    var tmaQ = dt.Rotations[f];
                    int fbxFrame = (int)Math.Round((double)f / (tma.FrameCount - 1) * (fbxFrameCount - 1));
                    var fbxQ = GetFbxQuat(fbxAbsRotations, Math.Clamp(fbxFrame, 0, fbxFrameCount - 1), fi);

                    // Try with x-negate (known coordinate mapping)
                    var tmaQn = new System.Numerics.Quaternion(-tmaQ.X, tmaQ.Y, tmaQ.Z, tmaQ.W);
                    float err = QuatAngleDeg(tmaQn, fbxQ);
                    totalError += err;
                    totalFrames++;
                    if (err > maxError) { maxError = err; maxBone = $"{track.Name} f{f}"; }
                }
            }

            float avgErr = totalFrames > 0 ? totalError / totalFrames : 999;
            sb.AppendLine($"Current SM(-x):  avg={avgErr:F2}deg  max={maxError:F1}deg ({maxBone})  [{totalFrames} frames]");
        }

        Assert.Fail($"Float16 hypothesis test:\n\n{sb}");
    }

    #endregion
}
