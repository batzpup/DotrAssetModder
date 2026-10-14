using SkiaSharp;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using static DotrAssetModder.Bin;

namespace DotrAssetModder;

public static class IconExtractor
{
    // ICON.MRG contains 3 complete ICN blocks (normal / copy / delete icon).
    // format info from babyno.top/en/posts/2023/10/parsing-ps2-3d-icon/

    const uint IcnMagic = 0x010000;
    const int TexBytes = 128 * 64 * 2 * 2;

    static readonly string[] Suffixes = { "_normal", "_copy", "_delete" };

    record struct IcnBlock(int IcnOffset, uint AnimShapes, uint TexType, int VertCount, int TexOffset);

    static List<IcnBlock> ScanIcns(byte[] mrgBytes)
    {
        var results = new List<IcnBlock>();
        int searchStart = 0;
        while (searchStart <= mrgBytes.Length - 20)
        {

            int magicOffset = -1;
            for (int i = searchStart; i <= mrgBytes.Length - 4; i += 4)
            {
                if (ReadU32(mrgBytes, i) == IcnMagic)
                {
                    magicOffset = i;
                    break;
                }
            }
            if (magicOffset < 0)
            {
                break;   
            }
             

            uint animShapes = ReadU32(mrgBytes, magicOffset + 4);
            uint texType = ReadU32(mrgBytes, magicOffset + 8);
            int vertCount = (int)ReadU32(mrgBytes, magicOffset + 16);

            if (animShapes < 1 || animShapes > 16 || vertCount < 3 || vertCount % 3 != 0 || vertCount > 20000)
            {
                searchStart = magicOffset + 4;
                continue;
            }
            int vertexEnd = magicOffset + 20 + (int)animShapes * vertCount * 24;
            if (vertexEnd + 24 > mrgBytes.Length)
            {
                searchStart = magicOffset + 4;
                continue;
            }
            uint numFrames = ReadU32(mrgBytes, vertexEnd + 16);
            int texOffset = vertexEnd + 24 + (int)numFrames * 12;

            if (texOffset + TexBytes > mrgBytes.Length)
            {
                searchStart = magicOffset + 4;
                continue;
            }

            results.Add(new IcnBlock(magicOffset, animShapes, texType, vertCount, texOffset));
            searchStart = texOffset + TexBytes;
        }
        return results;
    }

    public static ExtractResult Extract(byte[] mrgBytes, string outputDirectory, string fileNameStem)
    {
        Directory.CreateDirectory(outputDirectory);
        int written = 0, failed = 0;
        var failures = new List<FailureInfo>();

        var iconBlocks = ScanIcns(mrgBytes);
        if (iconBlocks.Count == 0)
        {
            return new ExtractResult(AssetKind.Ps2Icon, 0, 1, 1)
            {
                Failures = new List<FailureInfo> { new FailureInfo(-1, "scan", "No valid ICN blocks found in file") }
            };
        }

        for (int i = 0; i < iconBlocks.Count; i++)
        {
            var block = iconBlocks[i];
            string suffix = i < Suffixes.Length ? Suffixes[i] : $"_{i}";

            byte[]? texPng = null;
            try
            {
                Ps2Icon.CurrentTextureIndex = i < Ps2Icon.TextureOffsets.Length ? i : 0;
                int texLen = (i + 1 < iconBlocks.Count ? iconBlocks[i + 1].IcnOffset : mrgBytes.Length) - block.TexOffset;
                var slotBytes = mrgBytes.AsSpan(block.TexOffset, texLen).ToArray();
                using var iconBitmap = Ps2Icon.ExtractIconTexture(slotBytes);
                using var rgbaBitmap = iconBitmap.Copy(SKColorType.Rgba8888);
                var pngStream = new MemoryStream();
                rgbaBitmap.Encode(pngStream, SKEncodedImageFormat.Png, 100);
                texPng = pngStream.ToArray();
                File.WriteAllBytes(Path.Combine(outputDirectory, $"{fileNameStem}{suffix}.png"), texPng);
                written++;
            }
            catch (Exception exception)
            {
                failures.Add(new FailureInfo(i, $"texture{suffix}", exception.Message));
                failed++;
            }

            try
            {
                string glbPath = Path.Combine(outputDirectory, $"{fileNameStem}{suffix}.glb");
                ExportGlb(mrgBytes, block, texPng, glbPath, fileNameStem + suffix);
                written++;
            }
            catch (Exception exception)
            {
                failures.Add(new FailureInfo(i, $"model{suffix}", exception.Message));
                failed++;
            }
        }

        return new ExtractResult(AssetKind.Ps2Icon, written, failed, written + failed) 
        {
            Failures = failures
        };
    }

    static void ExportGlb(byte[] mrgBytes, IcnBlock block, byte[]? texPng, string outputPath, string name)
    {
        var iconSpan = mrgBytes.AsSpan(block.IcnOffset);
        uint texType = block.TexType;
        int vertCount = block.VertCount;
        var positions = new Vector3[vertCount];
        var normals = new Vector3[vertCount];
        var uvs = new Vector2[vertCount];
        int shapeBase = 20;
        
        for (int i = 0; i < vertCount; i++)
        {
            int vertexOffset = shapeBase + i * 24;
            positions[i] = new Vector3(
                ReadI16(iconSpan, vertexOffset + 0) / 4096f,
                ReadI16(iconSpan, vertexOffset + 2) / 4096f,
                ReadI16(iconSpan, vertexOffset + 4) / 4096f);
            normals[i] = new Vector3(
                ReadI16(iconSpan, vertexOffset + 8) / 4096f,
                ReadI16(iconSpan, vertexOffset + 10) / 4096f,
                ReadI16(iconSpan, vertexOffset + 12) / 4096f);
            uvs[i] = new Vector2(
                ReadI16(iconSpan, vertexOffset + 16) / 4096f,
                ReadI16(iconSpan, vertexOffset + 18) / 4096f);
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
        scene.ToGltf2().SaveGLB(outputPath);
    }
}