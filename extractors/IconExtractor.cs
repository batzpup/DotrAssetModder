using SkiaSharp;
using System.Buffers.Binary;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;

namespace DotrAssetModder;

public static class IconExtractor
{
    // ICON.MRG contains 3 complete ICN blocks (normal / copy / delete icon).
    // format info from babyno.top/en/posts/2023/10/parsing-ps2-3d-icon/

    const uint IcnMagic = 0x010000;
    const int TexBytes = 128 * 64 * 2 * 2;

    static readonly string[] Suffixes = { "_normal", "_copy", "_delete" };

    record struct IcnBlock(int IcnOffset, uint AnimShapes, uint TexType, int VertCount, int TexOffset);

    static List<IcnBlock> ScanIcns(byte[] data)
    {
        var results = new List<IcnBlock>();
        int pos = 0;
        while (pos <= data.Length - 20)
        {

            int idx = -1;
            for (int i = pos; i <= data.Length - 4; i += 4)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4)) == IcnMagic)
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
            {
                break;   
            }
             

            uint animShapes = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(idx + 4, 4));
            uint texType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(idx + 8, 4));
            int vertCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(idx + 16, 4));

            if (animShapes < 1 || animShapes > 16 || vertCount < 3 || vertCount % 3 != 0 || vertCount > 20000)
            {
                pos = idx + 4;
                continue;
            }
            int vtxEnd = idx + 20 + (int)animShapes * vertCount * 24;
            if (vtxEnd + 24 > data.Length)
            {
                pos = idx + 4;
                continue;
            }
            uint numFrames = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(vtxEnd + 16, 4));
            int texOffset = vtxEnd + 24 + (int)numFrames * 12;

            if (texOffset + TexBytes > data.Length)
            {
                pos = idx + 4;
                continue;
            }

            results.Add(new IcnBlock(idx, animShapes, texType, vertCount, texOffset));
            pos = texOffset + TexBytes;
        }
        return results;
    }

    public static ExtractResult Extract(byte[] mrgBytes, string outDir, string stem)
    {
        Directory.CreateDirectory(outDir);
        int written = 0, failed = 0;
        var failures = new List<FailureInfo>();

        var icns = ScanIcns(mrgBytes);
        if (icns.Count == 0)
        {
            return new ExtractResult(AssetKind.Ps2Icon, 0, 1, 1)
            {
                Failures = new List<FailureInfo> { new FailureInfo(-1, "scan", "No valid ICN blocks found in file") }
            };
        }

        for (int i = 0; i < icns.Count; i++)
        {
            var block = icns[i];
            string suffix = i < Suffixes.Length ? Suffixes[i] : $"_{i}";

            byte[]? texPng = null;
            try
            {
                PS2Icon.CurrentTextureIndex = i < PS2Icon.TextureOffsets.Length ? i : 0;
                int texLen = (i + 1 < icns.Count ? icns[i + 1].IcnOffset : mrgBytes.Length) - block.TexOffset;
                var slotBytes = mrgBytes.AsSpan(block.TexOffset, texLen).ToArray();
                using var bmp = PS2Icon.ExtractIconTexture(slotBytes);
                using var rgba = bmp.Copy(SKColorType.Rgba8888);
                var ms = new MemoryStream();
                rgba.Encode(ms, SKEncodedImageFormat.Png, 100);
                texPng = ms.ToArray();
                File.WriteAllBytes(Path.Combine(outDir, $"{stem}{suffix}.png"), texPng);
                written++;
            }
            catch (Exception ex)
            {
                failures.Add(new FailureInfo(i, $"texture{suffix}", ex.Message));
                failed++;
            }

            try
            {
                string glbPath = Path.Combine(outDir, $"{stem}{suffix}.glb");
                ExportGlb(mrgBytes, block, texPng, glbPath, stem + suffix);
                written++;
            }
            catch (Exception ex)
            {
                failures.Add(new FailureInfo(i, $"model{suffix}", ex.Message));
                failed++;
            }
        }

        return new ExtractResult(AssetKind.Ps2Icon, written, failed, written + failed) 
        {
            Failures = failures
        };
    }

    static void ExportGlb(byte[] data, IcnBlock block, byte[]? texPng, string outPath, string name)
    {
        var span = data.AsSpan(block.IcnOffset);
        uint texType = block.TexType;
        int vertCount = block.VertCount;
        var positions = new Vector3[vertCount];
        var normals = new Vector3[vertCount];
        var uvs = new Vector2[vertCount];
        int shapeBase = 20;
        
        for (int i = 0; i < vertCount; i++)
        {
            int o = shapeBase + i * 24;
            positions[i] = new Vector3(
                BinaryPrimitives.ReadInt16LittleEndian(span.Slice(o + 0, 2)) / 4096f,
                BinaryPrimitives.ReadInt16LittleEndian(span.Slice(o + 2, 2)) / 4096f,
                BinaryPrimitives.ReadInt16LittleEndian(span.Slice(o + 4, 2)) / 4096f);
            normals[i] = new Vector3(
                BinaryPrimitives.ReadInt16LittleEndian(span.Slice(o + 8, 2)) / 4096f,
                BinaryPrimitives.ReadInt16LittleEndian(span.Slice(o + 10, 2)) / 4096f,
                BinaryPrimitives.ReadInt16LittleEndian(span.Slice(o + 12, 2)) / 4096f);
            uvs[i] = new Vector2(
                BinaryPrimitives.ReadInt16LittleEndian(span.Slice(o + 16, 2)) / 4096f,
                BinaryPrimitives.ReadInt16LittleEndian(span.Slice(o + 18, 2)) / 4096f);
        }

        bool hasTexture = (texType & 0x4) != 0 && texPng != null;
        var matBuilder = new MaterialBuilder(name)
            .WithDoubleSide(true)
            .WithMetallicRoughnessShader()
            .WithChannelParam(KnownChannel.MetallicRoughness, KnownProperty.MetallicFactor, 0f)
            .WithChannelParam(KnownChannel.MetallicRoughness, KnownProperty.RoughnessFactor, 1f);
        if (hasTexture)
        {
            matBuilder.WithChannelImage(KnownChannel.BaseColor, new MemoryImage(texPng));
        }

        var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(name);
        var prim = meshBuilder.UsePrimitive(matBuilder);
        for (int i = 0; i + 2 < vertCount; i += 3)
        {
            prim.AddTriangle(
                new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(new VertexPositionNormal(positions[i], normals[i]), new VertexTexture1(uvs[i])),
                new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(new VertexPositionNormal(positions[i + 1], normals[i + 1]), new VertexTexture1(uvs[i + 1])),
                new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(new VertexPositionNormal(positions[i + 2], normals[i + 2]), new VertexTexture1(uvs[i + 2]))
                );
        }

        var scene = new SceneBuilder(name);
        scene.AddRigidMesh(meshBuilder, Matrix4x4.Identity);
        scene.ToGltf2().SaveGLB(outPath);
    }
}