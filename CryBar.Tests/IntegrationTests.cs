using System.Xml;

using CryBar;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CryBar.Tests;

/// <summary>
/// Integration tests that use real game files from AoM:Retold.
/// These tests are skipped unless the game is installed at the expected path.
/// Set the AOMR_GAME_PATH environment variable to override the default path.
/// </summary>
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

        // Copy should be deterministic — same data each time
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
        var tmm = new TmmModel(raw);
        var parsed = tmm.ParseHeader();

        Assert.True(parsed, "TMM header should parse successfully");
        Assert.NotNull(tmm.ModelNames);
        Assert.Equal(2, tmm.ModelNames.Length);
        Assert.Contains("armory_a_age2", tmm.ModelNames[0]);
        Assert.Contains("armory_a_age2", tmm.ModelNames[1]);

        // One should be .fbximport, one should be .fbx
        Assert.Contains(tmm.ModelNames, n => n.EndsWith(".fbximport"));
        Assert.Contains(tmm.ModelNames, n => n.EndsWith(".fbx"));
    }

    #endregion

    #region TMM.DATA - Compressed and Uncompressed

    [SkippableFact]
    public void TmmData_Japanese_ShutenDoji_IsCompressed()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheModelDataJapanese.bar", "shuten_doji.tmm.data");
        using var s = stream;

        Assert.True(entry.IsCompressed, "shuten_doji.tmm.data should be compressed");

        var raw = entry.ReadDataRaw(stream);

        // Should be Alz4 compressed
        Assert.True(((Span<byte>)raw).IsAlz4(), "Data should have Alz4 header");
        Assert.False(((Span<byte>)raw).IsL33t(), "Data should not be L33t");
    }

    [SkippableFact]
    public void TmmData_Japanese_ShutenDoji_DecompressesSuccessfully()
    {
        Skip.IfNot(GameInstalled, "AoM:Retold game directory not found");

        var (bar, entry, stream) = OpenBarAndFindEntry(@"modelcache\ArtModelCacheModelDataJapanese.bar", "shuten_doji.tmm.data");
        using var s = stream;

        var raw = entry.ReadDataRaw(stream);
        var decompressed = BarCompression.EnsureDecompressed(raw, out var compressionType);

        Assert.Equal(CompressionType.Alz4, compressionType);
        Assert.True(decompressed.Length > raw.Length, "Decompressed TMM.DATA should be larger");
        Assert.Equal(146138, decompressed.Length);
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
}
