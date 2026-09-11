using System.Collections.Concurrent;
using CryBar;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CryBar.Export;

public static class GlbConverter
{
    public sealed record DdtMaterialParams(
        DDTVersion Version,
        DDTUsage Usage,
        DDTAlpha Alpha,
        DDTFormat Format,
        byte MipLevels,
        ReadOnlyMemory<byte>? ColorTable);

    public sealed record OutputFile(string Name, byte[] Bytes);

    public sealed record PlannedFile(string Name, bool NeedsDdtParams, string? DdtMaterialName);

    public sealed record InspectionResult(
        IReadOnlyList<PlannedFile> PlannedFiles,
        IReadOnlyList<string> MaterialsNeedingDdtParams);

    public sealed record ConversionResult(
        IReadOnlyList<OutputFile> Files,
        IReadOnlyList<string> Warnings);

    public static InspectionResult Inspect(GlbModel model, string glbBaseName)
    {
        var planned = new List<PlannedFile>
        {
            new($"{glbBaseName}.tmm", false, null),
            new($"{glbBaseName}.tmm.data", false, null),
        };
        if (model.Animations is { Length: > 0 })
        {
            foreach (var anim in model.Animations)
                planned.Add(new PlannedFile($"{anim.Name}.tma", false, null));
        }

        var needs = new List<string>();
        foreach (var mat in model.Materials)
        {
            foreach (var (ddtName, _) in EnumerateMaterialPngs(mat))
            {
                bool fromExtras = ExtrasContainDdt(model.Extras, ddtName);
                planned.Add(new PlannedFile($"{ddtName}.ddt", !fromExtras, ddtName));
                if (!fromExtras) needs.Add(ddtName);
            }
        }
        return new InspectionResult(planned, needs);
    }

    public static async Task<ConversionResult> ConvertAsync(
        GlbModel model,
        string glbBaseName,
        IReadOnlyDictionary<string, DdtMaterialParams> ddtParams,
        IProgress<string>? progress = null,
        CancellationToken token = default,
        IReadOnlyDictionary<string, byte[]>? fbximportByAnimName = null,
        bool includeTextures = true)
    {
        var files = new List<OutputFile>();
        var warnings = new List<string>();

        progress?.Report($"Encoding {glbBaseName}.tmm");
        var (tmm, tmmData, tmmWarn) = TmmWriter.Write(model);
        files.Add(new OutputFile($"{glbBaseName}.tmm", tmm));
        files.Add(new OutputFile($"{glbBaseName}.tmm.data", tmmData));
        warnings.AddRange(tmmWarn);

        if (model.Animations is { Length: > 0 })
        {
            if (model.Bones is null || model.Bones.Length == 0)
            {
                warnings.Add("Animations present but model has no bones; .tma files skipped.");
            }
            else
            {
                var anims = model.Animations;
                var tmaResults = new (OutputFile File, IReadOnlyList<string> Warn)[anims.Length];
                var prepWarnings = new ConcurrentBag<string>();

                // CPU-bound and independent per animation; resampling and Quat64 packing dominate
                // for many-bone skeletons.
                Parallel.For(0, anims.Length, new ParallelOptions { CancellationToken = token }, i =>
                {
                    var anim = anims[i];
                    progress?.Report($"Encoding {anim.Name}.tma");
                    GlbExtras.TmaSection? tmaExtras = null;
                    model.Extras?.Tma.TryGetValue(anim.Name, out tmaExtras);

                    // fbximport override: replaces extras controllers when supplied for this animation.
                    // Only the controllers field is overridden - frame count remains from extras/binary.
                    if (fbximportByAnimName != null
                        && fbximportByAnimName.TryGetValue(anim.Name, out var fbxBytes)
                        && fbxBytes is { Length: > 0 })
                    {
                        var ctrls = FbximportReader.ParseAnimationControllers(fbxBytes);
                        if (ctrls != null)
                        {
                            tmaExtras = new GlbExtras.TmaSection
                            {
                                OriginalFrameCount = tmaExtras?.OriginalFrameCount ?? 0,
                                Controllers = ctrls,
                            };
                        }
                        else
                        {
                            prepWarnings.Add($"fbximport for '{anim.Name}' could not be parsed; ignored.");
                        }
                    }

                    var (tmaBytes, tmaWarn) = TmaWriter.Write(anim, model.Bones, tmaExtras);
                    tmaResults[i] = (new OutputFile($"{anim.Name}.tma", tmaBytes), tmaWarn);
                });

                warnings.AddRange(prepWarnings);
                foreach (var (file, warn) in tmaResults)
                {
                    files.Add(file);
                    warnings.AddRange(warn);
                }
            }
        }

        // Build the effective params lookup: caller-supplied takes priority,
        // then extras.crybar.ddt[] fills the rest. This is what makes
        // round-tripped GLBs "just work" without re-prompting for params.
        var effectiveParams = new Dictionary<string, DdtMaterialParams>(ddtParams, StringComparer.Ordinal);
        if (model.Extras is not null)
        {
            foreach (var d in model.Extras.Ddt)
            {
                if (effectiveParams.ContainsKey(d.Material)) continue;
                effectiveParams[d.Material] = new DdtMaterialParams(
                    d.Version, d.Usage, d.Alpha, d.Format, d.MipLevels,
                    d.ColorTable is { Length: > 0 } ? d.ColorTable : null);
            }
        }

        var workItems = new List<(string DdtName, byte[] Png, DdtMaterialParams P)>();
        foreach (var mat in includeTextures ? model.Materials : [])
        {
            foreach (var (ddtName, png) in EnumerateMaterialPngs(mat))
            {
                if (!effectiveParams.TryGetValue(ddtName, out var p))
                {
                    warnings.Add($"Material '{mat.Name}' has texture but no DDT params provided; '{ddtName}.ddt' skipped.");
                    continue;
                }
                workItems.Add((ddtName, png, p));
            }
        }

        if (workItems.Count > 0)
        {
            // DXT compression and mipmap generation are CPU-bound and independent
            // per texture; parallelism scales linearly with core count.
            var ddtResults = new ConcurrentBag<OutputFile>();
            await Parallel.ForEachAsync(workItems, token, async (item, ct) =>
            {
                progress?.Report($"Encoding {item.DdtName}.ddt");
                using var image = Image.Load<Rgba32>(item.Png);
                var bytes = await DDTImage.EncodeImageToDDT(
                    image, item.P.Version, item.P.Usage, item.P.Alpha, item.P.Format,
                    item.P.MipLevels, item.P.ColorTable, ct);
                ddtResults.Add(new OutputFile($"{item.DdtName}.ddt", bytes.ToArray()));
            });
            foreach (var f in ddtResults) files.Add(f);
        }

        return new ConversionResult(files, warnings);
    }

    /// Writes a conversion result to disk, deleting anything it already wrote if a
    /// later file fails, so a half-converted model is not left behind.
    public static async Task<int> WriteFilesAsync(
        IReadOnlyList<OutputFile> files, string outputDir,
        IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(outputDir);

        var written = new List<string>();
        try
        {
            foreach (var f in files)
            {
                progress?.Report($"Writing {f.Name}");
                var path = Path.Combine(outputDir, f.Name);
                await File.WriteAllBytesAsync(path, f.Bytes);
                written.Add(path);
            }

            return files.Count;
        }
        catch
        {
            foreach (var path in written)
            {
                try { File.Delete(path); } catch { }
            }
            throw;
        }
    }

    static IEnumerable<(string DdtName, byte[] Png)> EnumerateMaterialPngs(GlbMaterial mat)
    {
        if (mat.BaseColorPng is { Length: > 0 })
            yield return (MaterialExporter.GetDdtKey(mat.Name, TextureRole.BaseColor), mat.BaseColorPng);
        if (mat.NormalMapPng is { Length: > 0 })
            yield return (MaterialExporter.GetDdtKey(mat.Name, TextureRole.Normal), mat.NormalMapPng);
        if (mat.Mask1Png is { Length: > 0 })
            yield return (MaterialExporter.GetDdtKey(mat.Name, TextureRole.Masks1), mat.Mask1Png);
        if (mat.Mask2Png is { Length: > 0 })
            yield return (MaterialExporter.GetDdtKey(mat.Name, TextureRole.Masks2), mat.Mask2Png);
    }

    static bool ExtrasContainDdt(GlbExtras? extras, string ddtName)
    {
        if (extras is null) return false;
        foreach (var d in extras.Ddt)
            if (d.Material == ddtName) return true;
        return false;
    }
}
