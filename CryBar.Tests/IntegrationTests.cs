using System.Diagnostics;
using System.Xml;

using CryBar;
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
    public async Task TmmData_Japanese_ShutenDoji_PooledAndNonPooled()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheModelDataJapanese.bar", "shuten_doji.tmm.data");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);
        using var rawPooled = await entry.ReadDataRawPooledAsync(stream);
        Assert.Equal(raw, rawPooled.Memory);

        // Warmup both paths equally
        const int WARMUP = 200;
        const int RUNS = 3000;
        for (int i = 0; i < WARMUP; i++)
        {
            _ = entry.ReadDataRaw(stream);
            (await entry.ReadDataRawPooledAsync(stream)).Dispose();
        }

        // Interleave runs to reduce bias from CPU/OS scheduling
        double rawTotalMs = 0, pooledTotalMs = 0;
        for (int i = 0; i < RUNS; i++)
        {
            var start = Stopwatch.GetTimestamp();
            _ = entry.ReadDataRaw(stream);
            rawTotalMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            start = Stopwatch.GetTimestamp();
            (await entry.ReadDataRawPooledAsync(stream)).Dispose();
            pooledTotalMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        var rawTimeMs = rawTotalMs / RUNS;
        var pooledTimeMs = pooledTotalMs / RUNS;

        // pooled avoids allocations but microbenchmarks are noisy
        Assert.True(pooledTimeMs < rawTimeMs,
            $"Pooled ({pooledTimeMs:F4}ms) should be faster than raw ({rawTimeMs:F4}ms)");
    }

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

    #endregion

    #region TMM.DATA Full Parsing (TmmDataFile)

    [SkippableFact]
    public void TmmData_Greek_Petrobolos_FullParse()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        // Load the companion .tmm first
        var (bar, tmmEntry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheMeta.bar", "petrobolos.tmm");
        var tmmRaw = BarCompression.EnsureDecompressed(tmmEntry.ReadDataRaw(stream), out _);
        var tmm = new TmmFile(tmmRaw);
        Assert.True(tmm.Parsed, "Companion TMM should parse");
        stream.Dispose();

        // Load the .tmm.data
        var (bar2, dataEntry, stream2) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheModelDataGreek.bar", "petrobolos.tmm.data");
        using var s = stream2;

        var dataRaw = BarCompression.EnsureDecompressed(dataEntry.ReadDataRaw(stream2), out _);
        var dataFile = new TmmDataFile(dataRaw, tmm.NumVertices, tmm.NumTriangleVerts, tmm.NumBones > 0);
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
        var dataFile = new TmmDataFile(dataRaw, tmm.NumVertices, tmm.NumTriangleVerts, tmm.NumBones > 0);
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

        var dataFile = new TmmDataFile(dataRaw, tmm.NumVertices, tmm.NumTriangleVerts, tmm.NumBones > 0);
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
    /// Verifies: decompress → recompress → decompress produces identical content,
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
            var roundtripped = BarCompression.DecompressL33t(recompSpan);
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
        // Only use campaign files from the game install — user scenarios are not stable test fixtures
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
        // Test hoplite scenario (new format, EN size 80, named P1)
        var hoplitePath = @"C:\Users\adamv\Games\Age of Mythology Retold\76561198066415280\scenario\test-empty-hoplite.mythscn";
        Skip.If(!File.Exists(hoplitePath), "test-empty-hoplite.mythscn not found");

        var hopliteXml = ParseScenarioXml(hoplitePath);
        var entities = hopliteXml.GetElementsByTagName("Entity");
        Assert.True(entities.Count >= 1, "Expected at least 1 entity");
        var hopliteEntity = (XmlElement)entities[0]!;
        var protoIdx = hopliteEntity.GetAttribute("protoIndex");
        Assert.False(string.IsNullOrEmpty(protoIdx), "protoIndex missing on hoplite entity");
        // Hoplite is index 58 in TM[0]
        Assert.Equal("58", protoIdx);

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
        Assert.True(hasNonZero, "All protoIndex values are 0 — likely still reading fake P1");
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
}
