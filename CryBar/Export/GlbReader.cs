using System.Buffers.Binary;
using System.Text.Json;

namespace CryBar.Export;

public sealed class GlbModel
{
    public required GlbMesh Mesh { get; init; }
    public GlbBone[]? Bones { get; init; }
    public GlbAttachment[]? Attachments { get; init; }
    public GlbAnimation[]? Animations { get; init; }
    public GlbMaterial[] Materials { get; init; } = [];
    public GlbExtras? Extras { get; init; }
}

public sealed class GlbMesh
{
    public GlbMeshPrimitive[] Primitives { get; init; } = [];
}

public sealed class GlbMeshPrimitive
{
    public required string MaterialName { get; init; }
    public required float[] Positions { get; init; }   // length = vertexCount * 3
    public required float[] Normals { get; init; }     // length = vertexCount * 3
    public required float[] Tangents { get; init; }    // length = vertexCount * 4 (xyz + w sign)
    public required float[] TexCoords { get; init; }   // length = vertexCount * 2
    public byte[]? JointIndices { get; init; }         // length = vertexCount * 4 (u8) or null
    public float[]? JointWeights { get; init; }        // length = vertexCount * 4 or null
    public required uint[] Indices { get; init; }
}

public sealed class GlbBone
{
    public required string Name { get; init; }
    public int ParentIndex { get; init; } = -1;
    public required float[] LocalMatrix { get; init; }       // 16 floats column-major
    public required float[] InverseBindMatrix { get; init; } // 16 floats column-major
}

public sealed class GlbAttachment
{
    public required string Name { get; init; }
    public int ParentBoneIndex { get; init; } = -1;
    public required float[] LocalMatrix { get; init; }  // 16 floats column-major (glTF node transform)
    public required int Index { get; init; }             // from extras.crybar.node_type marker
}

public sealed class GlbAnimation
{
    public required string Name { get; init; }
    public required GlbBoneTrack[] Tracks { get; init; } // one per bone, may be all-null if static
    public float Duration { get; init; }
    public uint FrameCount { get; init; }                // resampled count (from extras or inferred)
}

public sealed class GlbBoneTrack
{
    public required int BoneIndex { get; init; }
    public required System.Numerics.Vector3[] Translations { get; init; } // length = frameCount
    public required System.Numerics.Quaternion[] Rotations { get; init; } // length = frameCount
    public System.Numerics.Vector3[] Scales { get; init; } = [];          // length = frameCount, or empty = rest scale
}

public sealed class GlbMaterial
{
    public required string Name { get; init; }
    public byte[]? BaseColorPng { get; init; }
    public byte[]? NormalMapPng { get; init; }
    public byte[]? Mask1Png { get; init; }
    public byte[]? Mask2Png { get; init; }
}

public static class GlbReader
{
    const uint GlbMagic = 0x46546C67;      // "glTF"
    const uint ChunkTypeJson = 0x4E4F534A; // "JSON"
    const uint ChunkTypeBin = 0x004E4942;  // "BIN\0"

    static readonly System.Text.RegularExpressions.Regex SuffixRegex =
        new(@"\.\d{3}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string StripSuffixForTests(string s) => SuffixRegex.Replace(s, "");

    // Extensions that genuinely change the binary layout or decoding pipeline: parsing produces
    // garbage if they're listed in extensionsRequired and we ignore them. Anything outside this
    // list is allowed through (we may not honor it perfectly, but the output is usable).
    static readonly Dictionary<string, string> BlockingExtensions = new(StringComparer.Ordinal)
    {
        ["KHR_draco_mesh_compression"] = "Draco-compressed GLBs are not supported; re-export without Draco compression (Blender: glTF export -> Compression unchecked).",
        ["KHR_mesh_quantization"]      = "Mesh quantization is not supported; re-export with float positions/normals.",
        ["KHR_texture_basisu"]         = "Basis Universal textures are not supported; re-export with PNG textures.",
        ["EXT_meshopt_compression"]    = "meshopt-compressed GLBs are not supported; re-export without meshopt compression.",
    };

    /// <summary>Parses a GLB byte stream into an in-memory <see cref="GlbModel"/>.</summary>
    public static GlbModel Parse(ReadOnlyMemory<byte> glb)
    {
        var (json, bin) = ParseContainer(glb);
        using (json)
        {
            var root = json.RootElement;

            CheckRequiredExtensions(root);
            CheckEmbeddedBuffer(root);

            GlbExtras? extras = null;
            if (root.TryGetProperty("extras", out var extrasEl))
                extras = GlbExtras.Read(extrasEl);
            extras = MergeNodeTaggedExtras(extras, root);

            var mesh = ReadMesh(root, bin);
            var bones = ReadBones(root, bin);
            var sceneRoot = ComputeSceneRootTransform(root);
            mesh = BakeRootTransformIntoMesh(mesh, sceneRoot);
            bones = BakeRootTransformIntoBones(bones, sceneRoot);
            // Blender's glTF exporter normalizes bone scales in the InverseBindMatrices, so they
            // stop being the inverse of the bone world matrices it writes. The engine multiplies
            // both for skinning, so the asymmetry shows up in-game as stretched limbs even though
            // Blender renders the rest pose correctly. Recomputing IBM = inverse(world) makes them
            // consistent again.
            bones = RecomputeIbmsFromBoneHierarchy(bones);
            var animations = ReadAnimations(root, bin, bones, extras);
            var materials = ReadMaterials(root, bin);
            var attachments = ReadAttachments(root, extras, bones);

            return new GlbModel { Mesh = mesh, Bones = bones, Animations = animations, Attachments = attachments, Materials = materials, Extras = extras };
        }
    }

    /// <summary>
    /// Recovers per-node CryBar metadata: the full <c>tmm</c> block from any node's
    /// <c>extras.crybar.tmm</c> (Blender strips asset-root extras but preserves Object custom
    /// properties), legacy <c>main_matrix</c>-only tags, and impact-point translations from tagged
    /// empties. Asset-root extras wins when both are present.
    /// </summary>
    static GlbExtras? MergeNodeTaggedExtras(GlbExtras? assetExtras, JsonElement root)
    {
        if (!root.TryGetProperty("nodes", out var nodes)) return assetExtras;

        GlbExtras? nodeRecovered = null;
        float[]? mainMatrix = null;
        var impactPoints = new List<(int Index, float[] Pos)>();

        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("extras", out var nx)) continue;
            if (!nx.TryGetProperty("crybar", out var cb)) continue;

            // Skip the Read when asset-root extras is going to win anyway.
            if (assetExtras is null && nodeRecovered is null && cb.TryGetProperty("tmm", out _))
                nodeRecovered = GlbExtras.Read(nx);

            if (mainMatrix is null && cb.TryGetProperty("main_matrix", out var mm))
            {
                mainMatrix = new float[mm.GetArrayLength()];
                for (int i = 0; i < mainMatrix.Length; i++) mainMatrix[i] = mm[i].GetSingle();
            }

            if (cb.TryGetProperty("node_type", out var nt) && nt.GetString() == GlbExtras.NodeTypeImpactPoint)
            {
                int idx = cb.TryGetProperty("index", out var iEl) ? iEl.GetInt32() : impactPoints.Count;
                // Reverse the LH->RH X-negation applied at export.
                float x = 0, y = 0, z = 0;
                if (node.TryGetProperty("translation", out var t) && t.GetArrayLength() == 3)
                {
                    x = -t[0].GetSingle();
                    y = t[1].GetSingle();
                    z = t[2].GetSingle();
                }
                impactPoints.Add((idx, [x, y, z]));
            }
        }

        var result = assetExtras ?? nodeRecovered;
        if (result == null && (mainMatrix != null || impactPoints.Count > 0))
            result = new GlbExtras();
        if (result == null) return null;

        if (mainMatrix is not null) result.Tmm.MainMatrix = mainMatrix;
        if (impactPoints.Count > 0)
        {
            impactPoints.Sort((a, b) => a.Index.CompareTo(b.Index));
            var ips = new float[impactPoints.Count][];
            for (int i = 0; i < ips.Length; i++) ips[i] = impactPoints[i].Pos;
            result.Tmm.ImpactPoints = ips;
        }
        return result;
    }

    static GlbAttachment[]? ReadAttachments(JsonElement root, GlbExtras? extras, GlbBone[]? bones)
    {
        if (!root.TryGetProperty("nodes", out var nodes)) return null;

        // Build node-to-bone lookup so we can find an attachment's parent bone.
        var nodeToBone = new Dictionary<int, int>();
        if (bones != null && root.TryGetProperty("skins", out var skins) && skins.GetArrayLength() > 0)
        {
            if (!skins[0].TryGetProperty("joints", out var joints))
                throw new GlbParseException("Skin is missing required 'joints' array.");
            for (int i = 0; i < joints.GetArrayLength(); i++) nodeToBone[joints[i].GetInt32()] = i;
        }

        // Build child-to-parent map for non-joint nodes.
        var childToParent = new Dictionary<int, int>();
        for (int n = 0; n < nodes.GetArrayLength(); n++)
        {
            if (!nodes[n].TryGetProperty("children", out var children)) continue;
            foreach (var c in children.EnumerateArray())
                childToParent[c.GetInt32()] = n;
        }

        var attachments = new List<GlbAttachment>();
        for (int n = 0; n < nodes.GetArrayLength(); n++)
        {
            var node = nodes[n];
            if (!node.TryGetProperty("extras", out var nx)) continue;
            if (!nx.TryGetProperty("crybar", out var cb)) continue;
            if (!cb.TryGetProperty("node_type", out var nt) || nt.GetString() != GlbExtras.NodeTypeAttachment) continue;

            // Older exports / hand-edited GLBs may omit `index`; fall back to encounter order
            // so the attachment is still recognized. Order matches node discovery, which is
            // typically the order the user authored.
            int attIdx = cb.TryGetProperty("index", out var attIdxProp) ? attIdxProp.GetInt32() : attachments.Count;
            string name = SuffixRegex.Replace(
                node.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "", "");
            float[] local = ReadNodeTransform(node);

            int parentBone = -1;
            if (childToParent.TryGetValue(n, out int parentNode))
                nodeToBone.TryGetValue(parentNode, out parentBone);

            attachments.Add(new GlbAttachment
            {
                Name = name,
                Index = attIdx,
                ParentBoneIndex = parentBone,
                LocalMatrix = local,
            });
        }
        if (attachments.Count == 0) return null;

        attachments.Sort((a, b) => a.Index.CompareTo(b.Index));
        return attachments.ToArray();
    }

    internal static (JsonDocument Json, byte[] Bin) ParseContainerForTests(ReadOnlyMemory<byte> glb)
        => ParseContainer(glb);

    static GlbMesh ReadMesh(JsonElement root, byte[] bin)
    {
        if (!root.TryGetProperty("meshes", out var meshes) || meshes.GetArrayLength() == 0)
            throw new GlbParseException("GLB has no meshes.");
        if (meshes.GetArrayLength() > 1)
            throw new GlbParseException(
                $"Multi-mesh GLBs are not supported (found {meshes.GetArrayLength()}). Merge meshes in Blender first.");

        var mesh = meshes[0];
        if (!mesh.TryGetProperty("primitives", out var primsEl) || primsEl.GetArrayLength() == 0)
            throw new GlbParseException("Mesh has no primitives.");

        var prims = new List<GlbMeshPrimitive>();
        string[] materialNames = ReadMaterialNames(root);

        int primIdx = -1;
        foreach (var p in primsEl.EnumerateArray())
        {
            primIdx++;
            if (!p.TryGetProperty("attributes", out var attrs))
                throw new GlbParseException("Mesh primitive has no 'attributes' object.");

            int posAcc  = RequireAttribute(attrs, "POSITION");
            int normAcc = RequireAttribute(attrs, "NORMAL");
            // TANGENT is optional in the glTF spec; TMM requires it for TBN packing.
            // If the source GLB lacks tangents (Blender export with Mesh -> Tangents disabled),
            // we recompute via MikkTSpace below - matches Blender's own algorithm bit-for-bit.
            int tanAcc  = attrs.TryGetProperty("TANGENT", out var tanEl) ? tanEl.GetInt32() : -1;
            int uvAcc   = RequireAttribute(attrs, "TEXCOORD_0");
            if (!p.TryGetProperty("indices", out var indicesProp))
                throw new GlbParseException("Mesh primitive has no 'indices'; non-indexed (point/strip) geometry is not supported. Triangulate before export.");
            int indicesAcc = indicesProp.GetInt32();

            byte[]? joints = null;
            float[]? weights = null;
            if (attrs.TryGetProperty("JOINTS_0", out var j))
                joints = ReadAccessorBytes(root, bin, j.GetInt32());
            if (attrs.TryGetProperty("WEIGHTS_0", out var w))
                weights = ReadAccessorFloats(root, bin, w.GetInt32());

            string materialName = "default";
            if (p.TryGetProperty("material", out var matIdx))
            {
                int idx = matIdx.GetInt32();
                if ((uint)idx >= (uint)materialNames.Length)
                    throw new GlbParseException(
                        $"Mesh primitive references material index {idx}, but only {materialNames.Length} materials are defined.");
                materialName = materialNames[idx];
            }

            var positions = ReadAccessorFloats(root, bin, posAcc);
            var normalsArr = ReadAccessorFloats(root, bin, normAcc);
            var texCoords = ReadAccessorFloats(root, bin, uvAcc);
            var triIndices = ReadAccessorIndices(root, bin, indicesAcc);
            float[]? tangents = tanAcc >= 0 ? ReadAccessorFloats(root, bin, tanAcc) : null;

            string? error = MeshPrimitiveValidator.CheckLengths(positions, normalsArr, tangents, texCoords, joints, weights, triIndices.Length)
                ?? MeshPrimitiveValidator.CheckIndexRange(triIndices, positions.Length / 3);
            if (error != null)
                throw new GlbParseException($"Mesh primitive {primIdx}: {error}.");

            tangents ??= MikkTSpace.ComputeTangents(positions, normalsArr, texCoords, triIndices);

            prims.Add(new GlbMeshPrimitive
            {
                MaterialName = materialName,
                Positions = positions,
                Normals = normalsArr,
                Tangents = tangents,
                TexCoords = texCoords,
                JointIndices = joints,
                JointWeights = weights,
                Indices = triIndices,
            });
        }

        return new GlbMesh { Primitives = prims.ToArray() };
    }

    static void CheckRequiredExtensions(JsonElement root)
    {
        if (!root.TryGetProperty("extensionsRequired", out var req)) return;

        foreach (var ext in req.EnumerateArray())
        {
            var name = ext.GetString();
            if (name != null && BlockingExtensions.TryGetValue(name, out var hint))
                throw new GlbParseException($"GLB requires unsupported extension '{name}'. {hint}");
        }
    }

    static void CheckEmbeddedBuffer(JsonElement root)
    {
        if (!root.TryGetProperty("buffers", out var buffers) || buffers.GetArrayLength() == 0)
            return;
        if (buffers.GetArrayLength() > 1)
            throw new GlbParseException(
                $"GLB has {buffers.GetArrayLength()} buffers; only single-buffer GLBs are supported.");
        if (buffers[0].TryGetProperty("uri", out _))
            throw new GlbParseException(
                "GLB buffer is referenced by external URI; only embedded BIN chunks are supported. Re-export as a single binary .glb.");
    }

    static int RequireAttribute(JsonElement attrs, string name, string? extraHelp = null)
    {
        if (!attrs.TryGetProperty(name, out var el))
        {
            var msg = $"Mesh primitive is missing required attribute '{name}'.";
            if (extraHelp != null) msg += " " + extraHelp;
            throw new GlbParseException(msg);
        }
        return el.GetInt32();
    }

    static string[] ReadMaterialNames(JsonElement root)
    {
        if (!root.TryGetProperty("materials", out var mats)) return [];
        var names = new List<string>();
        foreach (var m in mats.EnumerateArray())
            names.Add(SuffixRegex.Replace(m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", ""));
        return names.ToArray();
    }

    static GlbBone[]? ReadBones(JsonElement root, byte[] bin)
    {
        if (!root.TryGetProperty("skins", out var skins) || skins.GetArrayLength() == 0)
            return null;

        var skin = skins[0];
        if (!skin.TryGetProperty("joints", out var joints))
            throw new GlbParseException("Skin is missing required 'joints' array.");
        int boneCount = joints.GetArrayLength();
        int[] jointNodeIndices = new int[boneCount];
        for (int i = 0; i < boneCount; i++) jointNodeIndices[i] = joints[i].GetInt32();

        var nodeToBone = new Dictionary<int, int>();
        for (int i = 0; i < boneCount; i++) nodeToBone[jointNodeIndices[i]] = i;

        if (!root.TryGetProperty("nodes", out var nodes))
            throw new GlbParseException("GLB JSON has no 'nodes' array.");
        int[] parents = new int[boneCount];
        for (int i = 0; i < boneCount; i++) parents[i] = -1;
        for (int n = 0; n < nodes.GetArrayLength(); n++)
        {
            if (!nodes[n].TryGetProperty("children", out var children)) continue;
            if (!nodeToBone.TryGetValue(n, out int parentBoneIdx)) continue;
            foreach (var c in children.EnumerateArray())
            {
                int childNode = c.GetInt32();
                if (nodeToBone.TryGetValue(childNode, out int childBoneIdx))
                    parents[childBoneIdx] = parentBoneIdx;
            }
        }

        float[]? ibmFlat = null;
        if (skin.TryGetProperty("inverseBindMatrices", out var ibmIdx))
            ibmFlat = ReadAccessorFloats(root, bin, ibmIdx.GetInt32());

        var bones = new GlbBone[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            var node = nodes[jointNodeIndices[i]];
            string name = SuffixRegex.Replace(
                node.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "", "");
            float[] local = ReadNodeTransform(node);
            float[] ibm = ibmFlat != null
                ? ibmFlat.AsSpan(i * 16, 16).ToArray()
                : Identity4x4();

            bones[i] = new GlbBone
            {
                Name = name,
                ParentIndex = parents[i],
                LocalMatrix = local,
                InverseBindMatrix = ibm,
            };
        }
        return bones;
    }

    static float[] ReadNodeTransform(JsonElement node)
    {
        // glTF: either "matrix" (16 floats column-major) or "translation"+"rotation"+"scale".
        if (node.TryGetProperty("matrix", out var mEl))
        {
            var m = new float[16];
            int i = 0;
            foreach (var v in mEl.EnumerateArray()) m[i++] = v.GetSingle();
            return m;
        }
        var t = ReadVec3(node, "translation", System.Numerics.Vector3.Zero);
        var r = ReadQuat(node, "rotation", System.Numerics.Quaternion.Identity);
        var s = ReadVec3(node, "scale", System.Numerics.Vector3.One);

        var matT = System.Numerics.Matrix4x4.CreateTranslation(t);
        var matR = System.Numerics.Matrix4x4.CreateFromQuaternion(r);
        var matS = System.Numerics.Matrix4x4.CreateScale(s);
        var mat = matS * matR * matT; // glTF: M = T * R * S applied to a vector v as v' = T(R(S v))

        return [
            mat.M11, mat.M12, mat.M13, mat.M14,
            mat.M21, mat.M22, mat.M23, mat.M24,
            mat.M31, mat.M32, mat.M33, mat.M34,
            mat.M41, mat.M42, mat.M43, mat.M44,
        ];
    }

    static System.Numerics.Vector3 ReadVec3(JsonElement node, string key, System.Numerics.Vector3 def)
    {
        if (!node.TryGetProperty(key, out var el)) return def;
        return new System.Numerics.Vector3(el[0].GetSingle(), el[1].GetSingle(), el[2].GetSingle());
    }

    static System.Numerics.Quaternion ReadQuat(JsonElement node, string key, System.Numerics.Quaternion def)
    {
        if (!node.TryGetProperty(key, out var el)) return def;
        return new System.Numerics.Quaternion(
            el[0].GetSingle(), el[1].GetSingle(), el[2].GetSingle(), el[3].GetSingle());
    }

    static float[] Identity4x4() =>
        [1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1];

    static float[] ReadAccessorFloats(JsonElement root, byte[] bin, int accessorIdx)
    {
        var (offset, count, _, components, stride) =
            ResolveAccessor(root, accessorIdx, expectFloat: true, bin.Length);
        var result = new float[count * components];
        CopyElements(bin, offset, count, components * 4, stride,
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
    }

    static byte[] ReadAccessorBytes(JsonElement root, byte[] bin, int accessorIdx)
    {
        var (offset, count, componentSize, components, stride) =
            ResolveAccessor(root, accessorIdx, expectFloat: false, bin.Length);
        if (componentSize != 1)
            throw new GlbParseException("Expected u8 joint indices.");
        var result = new byte[count * components];
        CopyElements(bin, offset, count, components, stride, result);
        return result;
    }

    static uint[] ReadAccessorIndices(JsonElement root, byte[] bin, int accessorIdx)
    {
        var (offset, count, componentSize, _, stride) =
            ResolveAccessor(root, accessorIdx, expectFloat: false, bin.Length);
        if (componentSize != 1 && componentSize != 2 && componentSize != 4)
            throw new GlbParseException($"Unsupported index component size {componentSize}.");
        if (stride != componentSize)
            throw new GlbParseException(
                $"Accessor {accessorIdx}: index data must be tightly packed scalars (byteStride {stride}, component size {componentSize}).");

        var raw = bin.AsSpan(offset, count * componentSize);
        var result = new uint[count];
        if (componentSize == 1)
        {
            for (int i = 0; i < count; i++) result[i] = raw[i];
        }
        else if (componentSize == 2)
        {
            var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(raw);
            for (int i = 0; i < count; i++) result[i] = src[i];
        }
        else
        {
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(raw).CopyTo(result);
        }
        return result;
    }

    static void CopyElements(byte[] bin, int offset, int count, int elementSize, int stride, Span<byte> dest)
    {
        if (stride == elementSize)
        {
            bin.AsSpan(offset, count * elementSize).CopyTo(dest);
            return;
        }

        for (int i = 0; i < count; i++)
            bin.AsSpan(offset + i * stride, elementSize).CopyTo(dest.Slice(i * elementSize, elementSize));
    }

    static (int Offset, int Count, int ComponentSize, int Components, int Stride) ResolveAccessor(
        JsonElement root, int accessorIdx, bool expectFloat, int binLength)
    {
        if (!root.TryGetProperty("accessors", out var accessors))
            throw new GlbParseException("GLB JSON has no 'accessors' array.");
        var acc = accessors[accessorIdx];

        if (acc.TryGetProperty("sparse", out _))
            throw new GlbParseException("Sparse accessors are not supported.");

        if (!acc.TryGetProperty("count", out var countProp))
            throw new GlbParseException($"Accessor {accessorIdx} is missing required 'count'.");
        int count = countProp.GetInt32();
        if (!acc.TryGetProperty("componentType", out var ctProp))
            throw new GlbParseException($"Accessor {accessorIdx} is missing required 'componentType'.");
        int componentType = ctProp.GetInt32();
        if (!acc.TryGetProperty("type", out var typeProp))
            throw new GlbParseException($"Accessor {accessorIdx} is missing required 'type'.");
        string type = typeProp.GetString() ?? "";
        int components = type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2, "VEC3" => 3, "VEC4" => 4,
            "MAT4" => 16,
            _ => throw new GlbParseException($"Unsupported accessor type {type}.")
        };
        int componentSize = componentType switch
        {
            5121 => 1, // u8
            5123 => 2, // u16
            5125 => 4, // u32
            5126 => 4, // f32
            _ => throw new GlbParseException($"Unsupported componentType {componentType}.")
        };
        if (expectFloat && componentType != 5126)
            throw new GlbParseException($"Expected float accessor, got componentType {componentType}.");

        if (!acc.TryGetProperty("bufferView", out var bvIdxProp))
            throw new GlbParseException($"Accessor {accessorIdx} is missing required 'bufferView'.");
        int bufferViewIdx = bvIdxProp.GetInt32();
        if (!root.TryGetProperty("bufferViews", out var bufferViews))
            throw new GlbParseException("GLB JSON has no 'bufferViews' array.");
        var bv = bufferViews[bufferViewIdx];
        int bvOffset = bv.TryGetProperty("byteOffset", out var bvo) ? bvo.GetInt32() : 0;
        int accOffset = acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
        int offset = bvOffset + accOffset;

        int elementSize = components * componentSize;
        int stride = bv.TryGetProperty("byteStride", out var bsProp) ? bsProp.GetInt32() : 0;
        if (stride == 0) stride = elementSize;
        if (stride < elementSize)
            throw new GlbParseException(
                $"Accessor {accessorIdx}: bufferView byteStride {stride} is smaller than the element size {elementSize}.");

        long byteLen = count > 0 ? (long)(count - 1) * stride + elementSize : 0;
        if (count < 0 || offset < 0 || offset + byteLen > binLength)
            throw new GlbParseException("Accessor extends beyond BIN chunk.");

        return (offset, count, componentSize, components, stride);
    }

    static GlbAnimation[]? ReadAnimations(JsonElement root, byte[] bin, GlbBone[]? bones, GlbExtras? extras)
    {
        if (!root.TryGetProperty("animations", out var animsEl) || animsEl.GetArrayLength() == 0)
            return null;
        if (bones == null) return null;

        int boneCount = bones.Length;
        var nodeToBone = new Dictionary<int, int>();
        if (!root.TryGetProperty("skins", out var animSkins) || animSkins.GetArrayLength() == 0)
            throw new GlbParseException("GLB has animations but no skin.");
        var animSkin = animSkins[0];
        if (!animSkin.TryGetProperty("joints", out var animJoints))
            throw new GlbParseException("Skin is missing required 'joints' array.");
        for (int i = 0; i < animJoints.GetArrayLength(); i++) nodeToBone[animJoints[i].GetInt32()] = i;

        var result = new List<GlbAnimation>();
        foreach (var animEl in animsEl.EnumerateArray())
        {
            string name = animEl.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            if (!animEl.TryGetProperty("samplers", out var samplers))
                throw new GlbParseException($"Animation '{name}' is missing 'samplers'.");
            if (!animEl.TryGetProperty("channels", out var channels))
                throw new GlbParseException($"Animation '{name}' is missing 'channels'.");

            var samplerData = new (float[] Times, float[] Values, string Path)[samplers.GetArrayLength()];
            for (int s = 0; s < samplers.GetArrayLength(); s++)
            {
                var sm = samplers[s];
                string interp = sm.TryGetProperty("interpolation", out var ip) ? ip.GetString() ?? "LINEAR" : "LINEAR";

                if (!sm.TryGetProperty("input", out var inputProp))
                    throw new GlbParseException($"Animation '{name}' sampler {s} is missing 'input'.");
                if (!sm.TryGetProperty("output", out var outputProp))
                    throw new GlbParseException($"Animation '{name}' sampler {s} is missing 'output'.");
                var times = ReadAccessorFloats(root, bin, inputProp.GetInt32());
                var values = ReadAccessorFloats(root, bin, outputProp.GetInt32());

                if (interp == "STEP")
                {
                    // Materialize STEP into dense LINEAR keyframes - the downstream sampler
                    // assumes linear interpolation between adjacent keys. Blender's exporter
                    // emits STEP when Sampling Animations is enabled and it detects flat segments.
                    (times, values) = ExpandStepToLinear(times, values);
                }
                else if (interp == "CUBICSPLINE")
                {
                    throw new GlbParseException(
                        $"Animation '{name}' uses CUBICSPLINE interpolation; not supported. Re-export with LINEAR or STEP.");
                }
                else if (interp != "LINEAR")
                {
                    throw new GlbParseException(
                        $"Animation '{name}' uses unknown interpolation '{interp}'.");
                }

                samplerData[s] = (times, values, "");
            }

            // Bind sampler index to channel.target.path so we know which TRS each one is for.
            var channelInfo = new (int SamplerIdx, int BoneIdx, string Path)[channels.GetArrayLength()];
            float duration = 0;
            for (int c = 0; c < channels.GetArrayLength(); c++)
            {
                var ch = channels[c];
                if (!ch.TryGetProperty("sampler", out var samplerIdxProp))
                    throw new GlbParseException($"Animation '{name}' channel {c} is missing 'sampler'.");
                int samplerIdx = samplerIdxProp.GetInt32();
                if (!ch.TryGetProperty("target", out var chTarget))
                    throw new GlbParseException($"Animation '{name}' channel {c} is missing 'target'.");
                if (!chTarget.TryGetProperty("node", out var targetNodeProp))
                    throw new GlbParseException($"Animation '{name}' channel {c} target is missing 'node'.");
                int targetNode = targetNodeProp.GetInt32();
                if (!chTarget.TryGetProperty("path", out var pathProp))
                    throw new GlbParseException($"Animation '{name}' channel {c} target is missing 'path'.");
                string path = pathProp.GetString() ?? "";
                int boneIdx = nodeToBone.TryGetValue(targetNode, out var bi) ? bi : -1;
                channelInfo[c] = (samplerIdx, boneIdx, path);

                var tt = samplerData[samplerIdx].Times;
                if (tt.Length > 0) duration = MathF.Max(duration, tt[^1]);
            }

            uint frameCount = SelectFrameCount(samplerData, duration, name, extras);

            // Build per-bone tracks by sampling at uniform t = i * duration / (frameCount - 1).
            // Translation/rotation channels a bone lacks keep its rest pose; scale stays empty (rest) unless targeted.
            var translations = new System.Numerics.Vector3[boneCount][];
            var rotations = new System.Numerics.Quaternion[boneCount][];
            var scales = new System.Numerics.Vector3[]?[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                MatrixDecomp.Decompose(bones[b].LocalMatrix, out var restT, out var restR, out _);

                translations[b] = new System.Numerics.Vector3[frameCount];
                rotations[b] = new System.Numerics.Quaternion[frameCount];
                Array.Fill(translations[b], restT);
                Array.Fill(rotations[b], restR);
            }

            for (int c = 0; c < channelInfo.Length; c++)
            {
                var (samplerIdx, boneIdx, path) = channelInfo[c];
                if (boneIdx < 0) continue;
                if (path != "translation" && path != "rotation" && path != "scale") continue;
                var (times, values, _) = samplerData[samplerIdx];
                if (times.Length == 0) continue;

                bool isRotation = path == "rotation";
                int comps = isRotation ? 4 : 3;
                if (values.Length != times.Length * comps)
                    throw new GlbParseException(
                        $"Animation '{name}' channel {c} ({path}): sampler output has {values.Length} floats, expected {times.Length * comps} for {times.Length} keyframes.");

                // Output frames are sampled at strictly increasing times, so the keyframe cursor
                // only advances forward across the inner loop.
                int hint = 1;
                if (isRotation)
                {
                    var dst = rotations[boneIdx];
                    for (int f = 0; f < frameCount; f++)
                    {
                        float t = frameCount > 1 ? duration * f / (frameCount - 1) : 0;
                        dst[f] = SampleQuat(times, values, t, ref hint);
                    }
                }
                else
                {
                    var dst = path == "translation"
                        ? translations[boneIdx]
                        : (scales[boneIdx] ??= new System.Numerics.Vector3[frameCount]);
                    for (int f = 0; f < frameCount; f++)
                    {
                        float t = frameCount > 1 ? duration * f / (frameCount - 1) : 0;
                        dst[f] = SampleVec3(times, values, t, ref hint);
                    }
                }
            }

            var tracks = new GlbBoneTrack[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                tracks[b] = new GlbBoneTrack
                {
                    BoneIndex = b,
                    Translations = translations[b],
                    Rotations = rotations[b],
                    Scales = scales[b] ?? [],
                };
            }

            result.Add(new GlbAnimation
            {
                Name = name,
                Tracks = tracks,
                Duration = duration,
                FrameCount = frameCount,
            });
        }
        return result.ToArray();
    }

    static uint SelectFrameCount(
        (float[] Times, float[] Values, string Path)[] samplers, float duration,
        string animName, GlbExtras? extras)
    {
        // 1. extras override
        if (extras != null && extras.Tma.TryGetValue(animName, out var tmaSection) && tmaSection.OriginalFrameCount > 0)
            return tmaSection.OriginalFrameCount;

        // 2. infer from longest uniform sampler
        int bestN = 0;
        foreach (var s in samplers)
        {
            if (s.Times.Length < 2) continue;
            float dt = s.Times[1] - s.Times[0];
            bool uniform = true;
            for (int k = 2; k < s.Times.Length; k++)
            {
                float gap = s.Times[k] - s.Times[k - 1];
                if (MathF.Abs(gap - dt) > 1e-4f) { uniform = false; break; }
            }
            if (uniform && s.Times.Length > bestN) bestN = s.Times.Length;
        }
        if (bestN > 0) return (uint)bestN;

        // 3. 30 fps fallback
        return (uint)Math.Max(1, MathF.Ceiling(duration * 30));
    }

    // hint is the keyframe cursor; callers that sample at monotonically increasing t pass the same
    // ref hint across calls so we never scan keys we've already passed.
    static System.Numerics.Vector3 SampleVec3(float[] times, float[] values, float t, ref int hint)
    {
        if (times.Length == 0) return System.Numerics.Vector3.Zero;
        if (times.Length == 1 || t <= times[0])
            return new System.Numerics.Vector3(values[0], values[1], values[2]);
        if (t >= times[^1])
        {
            int b = (times.Length - 1) * 3;
            return new System.Numerics.Vector3(values[b], values[b + 1], values[b + 2]);
        }
        if (hint < 1) hint = 1;
        while (hint < times.Length && times[hint] < t) hint++;
        int lo = hint - 1;
        float a = (t - times[lo]) / (times[hint] - times[lo]);
        int li = lo * 3, ri = hint * 3;
        return System.Numerics.Vector3.Lerp(
            new System.Numerics.Vector3(values[li], values[li + 1], values[li + 2]),
            new System.Numerics.Vector3(values[ri], values[ri + 1], values[ri + 2]), a);
    }

    /// <summary>
    /// Converts STEP-interpolated keyframes into dense LINEAR keyframes that reproduce the
    /// step-hold behavior. For each transition i-1 -> i, an extra "hold" key is inserted at
    /// (t[i] - epsilon) carrying values[i-1]. Linear interpolation between (t[i] - epsilon, v[i-1])
    /// and (t[i], v[i]) gives a near-instant transition, matching STEP semantics within sub-frame
    /// precision. Output keyframe count is 2N - 1 for an N-key STEP sampler.
    /// </summary>
    internal static (float[] Times, float[] Values) ExpandStepToLinear(float[] times, float[] values)
    {
        int n = times.Length;
        if (n < 2) return (times, values);
        if (values.Length % n != 0) return (times, values); // malformed; pass through
        int comp = values.Length / n;
        if (comp <= 0) return (times, values);

        int outN = 2 * n - 1;
        var newTimes = new float[outN];
        var newValues = new float[outN * comp];

        const int floatSize = sizeof(float);
        newTimes[0] = times[0];
        Buffer.BlockCopy(values, 0, newValues, 0, comp * floatSize);

        int oi = 1;
        for (int i = 1; i < n; i++)
        {
            float gap = times[i] - times[i - 1];
            float epsilon = Math.Clamp(gap * 1e-4f, 1e-7f, gap * 0.5f);

            newTimes[oi] = times[i] - epsilon;
            Buffer.BlockCopy(values, (i - 1) * comp * floatSize, newValues, oi * comp * floatSize, comp * floatSize);
            oi++;

            newTimes[oi] = times[i];
            Buffer.BlockCopy(values, i * comp * floatSize, newValues, oi * comp * floatSize, comp * floatSize);
            oi++;
        }
        return (newTimes, newValues);
    }

    static System.Numerics.Quaternion SampleQuat(float[] times, float[] values, float t, ref int hint)
    {
        if (times.Length == 0) return System.Numerics.Quaternion.Identity;
        if (times.Length == 1 || t <= times[0])
            return new System.Numerics.Quaternion(values[0], values[1], values[2], values[3]);
        if (t >= times[^1])
        {
            int b = (times.Length - 1) * 4;
            return new System.Numerics.Quaternion(values[b], values[b + 1], values[b + 2], values[b + 3]);
        }
        if (hint < 1) hint = 1;
        while (hint < times.Length && times[hint] < t) hint++;
        int lo = hint - 1;
        float a = (t - times[lo]) / (times[hint] - times[lo]);
        int li = lo * 4, ri = hint * 4;
        var q1 = new System.Numerics.Quaternion(values[li], values[li + 1], values[li + 2], values[li + 3]);
        var q2 = new System.Numerics.Quaternion(values[ri], values[ri + 1], values[ri + 2], values[ri + 3]);
        return System.Numerics.Quaternion.Slerp(q1, q2, a);
    }

    static GlbMaterial[] ReadMaterials(JsonElement root, byte[] bin)
    {
        if (!root.TryGetProperty("materials", out var matsEl)) return [];

        var materials = new List<GlbMaterial>();
        foreach (var m in matsEl.EnumerateArray())
        {
            string name = SuffixRegex.Replace(m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", "");
            byte[]? baseColor = null, normalMap = null, mask1 = null, mask2 = null;

            if (m.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                if (pbr.TryGetProperty("baseColorTexture", out var bct) &&
                    bct.TryGetProperty("index", out var bctIdx))
                    baseColor = ReadTextureBytes(root, bin, bctIdx.GetInt32());

                if (pbr.TryGetProperty("metallicRoughnessTexture", out var mrt) &&
                    mrt.TryGetProperty("index", out var mrtIdx))
                    mask1 = ReadTextureBytes(root, bin, mrtIdx.GetInt32());
            }

            if (m.TryGetProperty("normalTexture", out var nt) &&
                nt.TryGetProperty("index", out var ntIdx))
                normalMap = ReadTextureBytes(root, bin, ntIdx.GetInt32());

            // If MR slot was absent, fall back to occlusion. Both slots conventionally
            // share the same ORM image so either one is the right source for Mask1.
            if (mask1 is null &&
                m.TryGetProperty("occlusionTexture", out var ot) &&
                ot.TryGetProperty("index", out var otIdx))
                mask1 = ReadTextureBytes(root, bin, otIdx.GetInt32());

            if (m.TryGetProperty("emissiveTexture", out var et) &&
                et.TryGetProperty("index", out var etIdx))
                mask2 = ReadTextureBytes(root, bin, etIdx.GetInt32());

            materials.Add(new GlbMaterial
            {
                Name = name,
                BaseColorPng = baseColor,
                NormalMapPng = normalMap,
                Mask1Png = mask1,
                Mask2Png = mask2,
            });
        }
        return materials.ToArray();
    }

    static byte[]? ReadTextureBytes(JsonElement root, byte[] bin, int textureIdx)
    {
        if (!root.TryGetProperty("textures", out var textures))
            throw new GlbParseException("GLB JSON has no 'textures' array.");
        var tex = textures[textureIdx];
        if (!tex.TryGetProperty("source", out var src)) return null;

        if (!root.TryGetProperty("images", out var images))
            throw new GlbParseException("GLB JSON has no 'images' array.");
        var image = images[src.GetInt32()];
        if (!image.TryGetProperty("bufferView", out var bvIdx)) return null;

        if (!root.TryGetProperty("bufferViews", out var bufferViewsTex))
            throw new GlbParseException("GLB JSON has no 'bufferViews' array.");
        var bv = bufferViewsTex[bvIdx.GetInt32()];
        int offset = bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
        if (!bv.TryGetProperty("byteLength", out var byteLen))
            throw new GlbParseException($"bufferView for texture {textureIdx} is missing required 'byteLength'.");
        int length = byteLen.GetInt32();
        return bin.AsSpan(offset, length).ToArray();
    }

    static System.Numerics.Matrix4x4 ComputeSceneRootTransform(JsonElement root)
    {
        if (!root.TryGetProperty("nodes", out var nodes))
            throw new GlbParseException("GLB JSON has no 'nodes' array.");

        int meshNodeIdx = -1;
        for (int i = 0; i < nodes.GetArrayLength(); i++)
        {
            if (nodes[i].TryGetProperty("mesh", out _)) { meshNodeIdx = i; break; }
        }
        if (meshNodeIdx < 0) return System.Numerics.Matrix4x4.Identity;

        var childToParent = new Dictionary<int, int>();
        for (int n = 0; n < nodes.GetArrayLength(); n++)
        {
            if (!nodes[n].TryGetProperty("children", out var children)) continue;
            foreach (var c in children.EnumerateArray())
                childToParent[c.GetInt32()] = n;
        }

        // System.Numerics is row-vector convention (world = local * parent_world), so we
        // accumulate bottom-up by right-multiplying each ancestor's local matrix.
        var accum = MatrixDecomp.ColMajorToMatrix(ReadNodeTransform(nodes[meshNodeIdx]));
        int current = meshNodeIdx;
        var visited = new HashSet<int> { current };
        while (childToParent.TryGetValue(current, out int parent))
        {
            if (!visited.Add(parent)) break;
            accum = accum * MatrixDecomp.ColMajorToMatrix(ReadNodeTransform(nodes[parent]));
            current = parent;
        }
        return accum;
    }

    static GlbMesh BakeRootTransformIntoMesh(GlbMesh mesh, System.Numerics.Matrix4x4 m)
    {
        if (m.IsIdentity) return mesh;

        System.Numerics.Matrix4x4.Invert(m, out var mInv);
        var nrmMat = System.Numerics.Matrix4x4.Transpose(mInv);

        // A mirroring root transform also flips winding and tangent handedness.
        bool mirrored = m.GetDeterminant() < 0;

        var prims = new GlbMeshPrimitive[mesh.Primitives.Length];
        for (int p = 0; p < mesh.Primitives.Length; p++)
        {
            var src = mesh.Primitives[p];
            int vc = src.Positions.Length / 3;

            var pos = new float[src.Positions.Length];
            var nrm = new float[src.Normals.Length];
            var tan = new float[src.Tangents.Length];

            for (int i = 0; i < vc; i++)
            {
                var v = new System.Numerics.Vector3(src.Positions[i * 3], src.Positions[i * 3 + 1], src.Positions[i * 3 + 2]);
                var vp = System.Numerics.Vector3.Transform(v, m);
                pos[i * 3] = vp.X; pos[i * 3 + 1] = vp.Y; pos[i * 3 + 2] = vp.Z;

                var n = new System.Numerics.Vector3(src.Normals[i * 3], src.Normals[i * 3 + 1], src.Normals[i * 3 + 2]);
                var np = NormalizeOr(System.Numerics.Vector3.TransformNormal(n, nrmMat), n, System.Numerics.Vector3.UnitY);
                nrm[i * 3] = np.X; nrm[i * 3 + 1] = np.Y; nrm[i * 3 + 2] = np.Z;

                var t = new System.Numerics.Vector3(src.Tangents[i * 4], src.Tangents[i * 4 + 1], src.Tangents[i * 4 + 2]);
                var tp = NormalizeOr(System.Numerics.Vector3.TransformNormal(t, m), t, System.Numerics.Vector3.UnitX);
                tan[i * 4] = tp.X; tan[i * 4 + 1] = tp.Y; tan[i * 4 + 2] = tp.Z;
                tan[i * 4 + 3] = mirrored ? -src.Tangents[i * 4 + 3] : src.Tangents[i * 4 + 3];
            }

            var indices = src.Indices;
            if (mirrored)
            {
                indices = new uint[src.Indices.Length];
                for (int k = 0; k + 2 < indices.Length; k += 3)
                {
                    indices[k]     = src.Indices[k];
                    indices[k + 1] = src.Indices[k + 2];
                    indices[k + 2] = src.Indices[k + 1];
                }
            }

            prims[p] = new GlbMeshPrimitive
            {
                MaterialName = src.MaterialName,
                Positions = pos, Normals = nrm, Tangents = tan,
                TexCoords = src.TexCoords, Indices = indices,
                JointIndices = src.JointIndices, JointWeights = src.JointWeights,
            };
        }
        return new GlbMesh { Primitives = prims };
    }

    static System.Numerics.Vector3 NormalizeOr(System.Numerics.Vector3 v, System.Numerics.Vector3 fallback, System.Numerics.Vector3 lastResort)
    {
        float len = v.Length();
        if (len > 1e-20f && float.IsFinite(len)) return v / len;

        len = fallback.Length();
        return len > 1e-20f && float.IsFinite(len) ? fallback / len : lastResort;
    }

    static GlbBone[]? BakeRootTransformIntoBones(GlbBone[]? bones, System.Numerics.Matrix4x4 m)
    {
        if (bones == null || m.IsIdentity) return bones;
        // Premultiply root bones' local matrix by m (root bones have ParentIndex == -1).
        var result = new GlbBone[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i].ParentIndex >= 0) { result[i] = bones[i]; continue; }
            var local = MatrixDecomp.ColMajorToMatrix(bones[i].LocalMatrix);
            var baked = local * m;
            result[i] = new GlbBone
            {
                Name = bones[i].Name,
                ParentIndex = bones[i].ParentIndex,
                LocalMatrix = MatrixDecomp.MatrixToColMajor(baked),
                InverseBindMatrix = bones[i].InverseBindMatrix,
            };
        }
        return result;
    }



    // Skips the recompute when supplied IBMs already invert the joint world matrices to
    // within float precision. Avoids per-bone allocation + matrix invert on CryBar's own
    // round-trips, where Blender isn't in the loop and the IBMs are already correct.
    static bool IbmsAreConsistent(GlbBone[] bones, System.Numerics.Matrix4x4[] worldMatrices)
    {
        const float Tol = 1e-3f;
        for (int i = 0; i < bones.Length; i++)
        {
            var ibm = MatrixDecomp.ColMajorToMatrix(bones[i].InverseBindMatrix);
            var prod = worldMatrices[i] * ibm;
            if (MathF.Abs(prod.M11 - 1f) > Tol || MathF.Abs(prod.M22 - 1f) > Tol || MathF.Abs(prod.M33 - 1f) > Tol) return false;
            if (MathF.Abs(prod.M41) > Tol || MathF.Abs(prod.M42) > Tol || MathF.Abs(prod.M43) > Tol) return false;
            if (MathF.Abs(prod.M12) > Tol || MathF.Abs(prod.M21) > Tol || MathF.Abs(prod.M13) > Tol) return false;
        }
        return true;
    }

    static GlbBone[]? RecomputeIbmsFromBoneHierarchy(GlbBone[]? bones)
    {
        if (bones == null || bones.Length == 0) return bones;
        var worldMatrices = MatrixDecomp.ComputeBoneWorldMatrices(bones);
        if (IbmsAreConsistent(bones, worldMatrices)) return bones;

        var result = new GlbBone[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            float[] ibm = System.Numerics.Matrix4x4.Invert(worldMatrices[i], out var invWorld)
                ? MatrixDecomp.MatrixToColMajor(invWorld)
                : bones[i].InverseBindMatrix;
            result[i] = new GlbBone
            {
                Name = bones[i].Name,
                ParentIndex = bones[i].ParentIndex,
                LocalMatrix = bones[i].LocalMatrix,
                InverseBindMatrix = ibm,
            };
        }
        return result;
    }

    static (JsonDocument Json, byte[] Bin) ParseContainer(ReadOnlyMemory<byte> glb)
    {
        var span = glb.Span;
        if (span.Length < 12)
            throw new GlbParseException("GLB shorter than 12-byte header.");

        if (BinaryPrimitives.ReadUInt32LittleEndian(span[..4]) != GlbMagic)
            throw new GlbParseException("Not a GLB file (magic number mismatch).");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
        if (version != 2)
            throw new GlbParseException($"Unsupported glTF version {version} (expected 2).");

        int offset = 12;
        // JSON chunk
        uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
        uint jsonType = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4));
        if (jsonType != ChunkTypeJson)
            throw new GlbParseException("First chunk is not JSON.");
        offset += 8;
        if (offset + (int)jsonLen > span.Length)
            throw new GlbParseException("JSON chunk length exceeds file size.");
        var jsonBytes = glb.Slice(offset, (int)jsonLen);
        offset += (int)jsonLen;

        // Optional BIN chunk
        byte[] bin = [];
        if (offset + 8 <= span.Length)
        {
            uint binLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
            uint binType = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4));
            if (binType != ChunkTypeBin)
                throw new GlbParseException("Second chunk is not BIN.");
            offset += 8;
            if (offset + (int)binLen > span.Length)
                throw new GlbParseException("BIN chunk length exceeds file size.");
            bin = glb.Slice(offset, (int)binLen).ToArray();
        }

        var json = JsonDocument.Parse(jsonBytes);
        return (json, bin);
    }
}
