using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using AlphaMode = SharpGLTF.Materials.AlphaMode;
using static DotrAssetModder.Bin;
namespace DotrAssetModder;

//Mostly AI
static class ModelExtractor
{
    public const float Ps2FullAlpha = 128f;

    // GIF PRIM bits
    const int PrimTme = 0x10;
    const int PrimAbe = 0x40;

    // Retail default GS state written by GeometryWriter and used by ordinary model pages.
    // ALPHA_1: (Cs - Cd) * As + Cd, FIX=0x80. TEST_1: alpha test disabled, Z test GEQUAL.
    const ulong DefaultGsAlpha = 0x0000008000000044UL;
    const ulong DefaultGsTest = 0x000000000007000CUL;
    // CLAMP_1: WMS = bits 0-1, WMT = bits 2-3 (0 = REPEAT, 1 = CLAMP).
    const ulong DefaultGsClamp = 0UL;

    public static ExtractResult Extract(byte[] assetBytes, string outputDirectory, string fileNameStem,
        IList<string>? assetNames = null,
        ExtractionProgress? progress = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var slots = SliceArchive(assetBytes, fileNameStem);
        if (slots.Count == 0)
            return new ExtractResult(AssetKind.Rh0Model, 0, 0, 0);

        progress?.SetTotal(slots.Count);

        int written = 0, failed = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<FailureInfo>();
        int maxParallelism = Math.Max(1, Environment.ProcessorCount - 1);
        var options = new ParallelOptions {
            MaxDegreeOfParallelism = maxParallelism,
        };

        Parallel.For(0, slots.Count, options, i =>
        {
            if (progress?.IsCancellationRequested == true) return;

            var (slotOffset, slotLength) = slots[i];
            if (slotLength < 0x80)
            {
                progress?.Increment();
                return;
            }
            var slice = new byte[slotLength];
            System.Buffer.BlockCopy(assetBytes, slotOffset, slice, 0, slotLength);

            string slotName = (assetNames != null && i < assetNames.Count && !string.IsNullOrEmpty(assetNames[i]))
                ? assetNames[i]
                : $"{fileNameStem}_{i:D3}";
            try
            {
                ExtractSingle(slice, outputDirectory, slotName);
                Interlocked.Increment(ref written);
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref failed);
                string reason = $"{exception.GetType().Name}: {exception.Message}";
                failures.Add(new FailureInfo(i, slotName, reason));
                Console.WriteLine($"[ModelExtractor] FAILED slot {i} ({slotName}): {reason}");
                Console.WriteLine(exception.StackTrace);
            }
            finally
            {
                progress?.Increment();
            }
        });

        var failureList = failures.OrderBy(failure => failure.Slot).ToArray();
        return new ExtractResult(AssetKind.Rh0Model, written, failed, slots.Count) {
            Failures = failureList,
        };
    }

    static List<(int Offset, int Length)> SliceArchive(byte[] data, string stem)
    {
        bool isMonster = string.Equals(Path.GetFileNameWithoutExtension(stem),
                             "MONSTER", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(stem, "MONSTER.MRG", StringComparison.OrdinalIgnoreCase);
        if (isMonster)
        {
            var slots = new List<(int, int)>();
            int slotCount = data.Length / GameTables.MonsterSlotSize;
            for (int i = 0; i < slotCount; i++)
            {
                int blockOffset = i * GameTables.MonsterSlotSize;
                if (blockOffset + 4 > data.Length) break;
                if (ReadU32(data, blockOffset) != Rh0Assembler.Rh0Magic)
                {
                    slots.Add((blockOffset, 0));
                    continue;
                }
                slots.Add((blockOffset, Math.Min(GameTables.MonsterSlotSize, data.Length - blockOffset)));
            }
            return slots;
        }
        return FindRh0Offsets(data);
    }

     static List<(int Offset, int Length)> FindRh0Offsets(byte[] data)
    {
        ReadOnlySpan<byte> needle = stackalloc byte[] { 0x52, 0x48, 0x30, 0x00 };
        var magicOffsets = new List<int>();
        int searchFrom = 0;
        while (true)
        {
            int found = data.AsSpan(searchFrom).IndexOf(needle);
            if (found < 0) break;
            magicOffsets.Add(searchFrom + found);
            searchFrom += found + 4;
        }
        var slots = new List<(int, int)>(magicOffsets.Count);
        for (int i = 0; i < magicOffsets.Count; i++)
        {
            int start = magicOffsets[i];
            int blockEnd = (i + 1 < magicOffsets.Count) ? magicOffsets[i + 1] : data.Length;
            slots.Add((start, blockEnd - start));
        }
        return slots;
    }

    // internal: the Pack Models tab extracts one MODEL.MRG block to show it in the viewer
    internal static void ExtractSingle(byte[] data, string outDir, string slotName)
    {
        File.WriteAllBytes(Path.Combine(outDir, slotName + ".glb"), ExtractGlb(data));
    }

    // One RH0 block as glb bytes, for the viewer and the importer preview (no file).
    internal static byte[] ExtractGlb(byte[] data)
    {
        var header = new Rh0Header(data);
        var (nodes, mslotMap) = WalkGNodes(data, (int)header.NodeTree);
        var (mainWalks, altWalks) = ParseWalks(data, header);
        if (altWalks.Count == 0 && header.Section1Alt != 0 && header.Section1AltSize >= 16)
        {
            altWalks = ParseAltPagesByScan(data, header);
        }


        // GS registers persist from the opaque chain into the alpha chain, which may have no
        // state block of its own (card 53 draws with the opaque chain's last one).
        var chainState = ChainState.Default;
        var mainPages = ParsePagesInSection(data, (int)header.Section1, (int)header.Section1Size, mainWalks, ref chainState);
        var altPages = (header.Section1Alt != 0 && altWalks.Count > 0)
            ? ParsePagesInSection(data, (int)header.Section1Alt, (int)header.Section1AltSize, altWalks, ref chainState)
            : new List<PageInfo>();
        var (texOffsets, postOffsets, mainDispatches, altDispatches) = ParseTexTableForModel(data, header);

        var textures = new List<DecodedTextureInfo>(texOffsets.Length);
        var texHasAlpha = new List<bool>(texOffsets.Length);
        for (int textureIndex = 0; textureIndex < texOffsets.Length; textureIndex++)
        {
            uint textureOffset = texOffsets[textureIndex];
            if (textureOffset == 0)
            {
                textures.Add(new DecodedTextureInfo(1, 1, MagentaPixel));
                texHasAlpha.Add(false);
                continue;
            }
            try
            {
                var decoded = Rh2Decoder.Decode(data, (int)textureOffset);
                textures.Add(new DecodedTextureInfo(decoded.Width, decoded.Height, decoded.Rgba));
                texHasAlpha.Add(decoded.HasAlpha);
            }
            catch
            {
                textures.Add(new DecodedTextureInfo(1, 1, MagentaPixel));
                texHasAlpha.Add(false);
            }
        }

        if (textures.Count == 0)
        {
            textures.Add(new DecodedTextureInfo(1, 1, MagentaPixel));
            texHasAlpha.Add(false);
        }

        int slotsByte = (int)(header.SectionFlags & 0xFF);
        int activeSlotMask = slotsByte != 0 ? (slotsByte & -slotsByte) : 0xFF;
        var texAnims = new Dictionary<int, TexAnimBake>();
        for (int textureIndex = 0; textureIndex < textures.Count && textureIndex < postOffsets.Length; textureIndex++)
        {
            uint postOffset = postOffsets[textureIndex];
            if (postOffset == 0) continue;
            var texture = textures[textureIndex];
            if (texture.Width < 2 || texture.Height < 2) continue;
            try
            {
                var entries = TexAnim.Parse(data, (int)postOffset);
                if (entries.Count == 0) continue;
                TexAnim.ApplyStaticStamps(texture.Rgba, texture.Width, texture.Height, entries, activeSlotMask);
                var bake = TexAnim.Bake(texture.Rgba, texture.Width, texture.Height, entries, activeSlotMask);
                if (bake != null) texAnims[textureIndex] = bake;
            }
            catch
            {
            }
        }

        var packets = new List<Packet>(mainPages.Count + altPages.Count);
        foreach (var page in mainPages)
        {
            int packetStart = (int)header.Section2 + (int)page.Sec2Off;
            int packetEnd = packetStart + (int)page.PktSize;
            packets.Add(new Packet {
                WalkOff = page.WalkOff,
                BoneMap = page.BoneMap,
                Strips = ParseStripsInRange(data, packetStart, packetEnd),
                ColorQ0 = page.ColorQ0,
                ColorQ1 = page.ColorQ1,
                ColorQ2 = page.ColorQ2,
                IsAlt = false,
                AlphaW = page.AlphaW,
                GsAlpha = page.GsAlpha,
                GsTest = page.GsTest,
                GsClamp = page.GsClamp,
            });
        }
        foreach (var page in altPages)
        {
            int packetStart = (int)header.Section2Alt + (int)page.Sec2Off;
            int packetEnd = packetStart + (int)page.PktSize;
            packets.Add(new Packet {
                WalkOff = page.WalkOff,
                BoneMap = page.BoneMap,
                Strips = ParseStripsInRange(data, packetStart, packetEnd),
                ColorQ0 = page.ColorQ0,
                ColorQ1 = page.ColorQ1,
                ColorQ2 = page.ColorQ2,
                IsAlt = true,
                AlphaW = page.AlphaW,
                GsAlpha = page.GsAlpha,
                GsTest = page.GsTest,
                GsClamp = page.GsClamp,
            });
        }

        var animations = ParseMotSlots(data, header, nodes.Count);
        var bind = BindMatrices(nodes, animations, header.AnimTransScale, packets, out var poseSamples,
            out var mslotNode);
        var built = BuildMesh(packets, mslotMap, bind, mainDispatches, altDispatches, poseSamples, mslotNode);
        /* The raw records too: the bake above plays the lowest motion slot's records only;
           records gated to other motions (byte +1, a slot mask) or on their own clock
           (bit 1) need the viewer to play them per slot. */
        var texAnimRecords = new Dictionary<int, System.Text.Json.Nodes.JsonArray>();
        for (int textureIndex = 0; textureIndex < textures.Count && textureIndex < postOffsets.Length; textureIndex++)
            if (postOffsets[textureIndex] != 0)
                try { texAnimRecords[textureIndex] = TexAnimRecords(data, (int)postOffsets[textureIndex]); }
                catch { }
        return BuildGlb(nodes, mslotMap, built, bind, textures, texHasAlpha, animations, header.AnimTransScale,
            (int)header.NodeTreeSize, texAnims, texAnimRecords);
    }

    /* One pose of the node tree, as lRhModel_SetupFloatMotionRecursive @ 0x210940 and
       lRhModel_SetupNoMotionRecursive @ 0x210010 build it. World matrices carry no scale:
       a node's accumulated scale reaches only the matrix it writes into its own slot
       (SlotMatrix), never its children's stack. */
    sealed class Pose
    {
        public Matrix4x4[] World;
        public Vector3[] AccumulatedScale;
        public Vector3[] LocalT;
        public Quaternion[] LocalR;

        public Pose(int nodeCount)
        {
            World = new Matrix4x4[nodeCount];
            AccumulatedScale = new Vector3[nodeCount];
            LocalT = new Vector3[nodeCount];
            LocalR = new Quaternion[nodeCount];
        }

        public int Motion;

        public Matrix4x4 SlotMatrix(int nodeIndex) => MatrixMath.Scale(AccumulatedScale[nodeIndex].X, AccumulatedScale[nodeIndex].Y, AccumulatedScale[nodeIndex].Z) * World[nodeIndex];
    }

    // anim == null is the node default (rest) pose. Motion entries are in traversal order,
    // which is GameNode.Index, and WalkGNodes adds parents before their children.
    static Pose EvalPose(List<GameNode> nodes, AnimSlot? anim, int frame, float posScale)
    {
        var pose = new Pose(nodes.Count);
        foreach (var node in nodes)
        {
            int skipFlags = node.Flags;
            var position = node.RawPos;
            var rotation = node.RawRot;
            var scale = node.RawScale;
            if (anim != null && anim.Tracks.TryGetValue(node.Index, out var track))
            {
                if (track.Trans != null)
                {
                    position = SampleVec(track.Trans, frame) * (node.Index == 0 ? 1f : posScale);
                    skipFlags &= ~1;
                }
                if (track.Rotation != null)
                {
                    rotation = SampleRot(track.Rotation, frame);
                    skipFlags &= ~2;
                }
                if (track.Scale != null)
                {
                    scale = SampleVec(track.Scale, frame);
                    skipFlags &= ~4;
                }
            }
            // a type-7 motion leaves the root's own scale alone
            if (anim != null && anim.Scaled && node.Index == 0) scale = node.RawScale;

            var parentScale = node.ParentIndex < 0 ? Vector3.One : pose.AccumulatedScale[node.ParentIndex];
            var parentWorld = node.ParentIndex < 0 ? Matrix4x4.Identity : pose.World[node.ParentIndex];
            var localTranslation = Vector3.Zero;
            var localRotation = Quaternion.Identity;
            var world = parentWorld;
            if ((skipFlags & 3) != 3)
            {
                var rotationMatrix = Matrix4x4.Identity;
                if ((skipFlags & 1) == 0) localTranslation = position * parentScale;
                if ((skipFlags & 2) == 0)
                {
                    rotationMatrix = MatrixMath.RotX(rotation.X) * MatrixMath.RotY(rotation.Y) * MatrixMath.RotZ(rotation.Z);
                    localRotation = MatrixMath.EulerToQuatXyz(rotation.X * MatrixMath.BamsRad, rotation.Y * MatrixMath.BamsRad, rotation.Z * MatrixMath.BamsRad);
                }
                world = rotationMatrix * MatrixMath.Translation(localTranslation.X, localTranslation.Y, localTranslation.Z) * parentWorld;
            }
            pose.World[node.Index] = world;
            pose.AccumulatedScale[node.Index] = (skipFlags & 4) != 0 ? parentScale : parentScale * scale;
            pose.LocalT[node.Index] = localTranslation;
            pose.LocalR[node.Index] = localRotation;
        }
        return pose;
    }

    // lRhModel_GetInterpolateMotionItem @ 0x210CF0: a list with fewer than 3 keys plays
    // key 0 only (`slti a2, 3` at 0x210D70).
    static int KeyBefore<T>(List<T> keys, Func<T, int> frame, int targetFrame)
    {
        int k = 0;
        while (k + 1 < keys.Count && frame(keys[k + 1]) <= targetFrame) k++;
        return k;
    }

    static Vector3 SampleVec(List<(int Frame, float X, float Y, float Z)> keys, int frame)
    {
        var firstKey = keys[0];
        if (keys.Count < 3) return new Vector3(firstKey.X, firstKey.Y, firstKey.Z);
        int k = KeyBefore(keys, key => key.Frame, frame);
        var previousKey = keys[k];
        if (k + 1 >= keys.Count) return new Vector3(previousKey.X, previousKey.Y, previousKey.Z);
        var nextKey = keys[k + 1];
        float blend = (frame - previousKey.Frame) / (float)(nextKey.Frame - previousKey.Frame);
        return new Vector3(previousKey.X, previousKey.Y, previousKey.Z) * (1f - blend) + new Vector3(nextKey.X, nextKey.Y, nextKey.Z) * blend;
    }

    static (int X, int Y, int Z) SampleRot(List<(int Frame, int RotationX, int RotationY, int RotationZ)> keys, int frame)
    {
        var firstKey = keys[0];
        if (keys.Count < 3) return (firstKey.RotationX, firstKey.RotationY, firstKey.RotationZ);
        int k = KeyBefore(keys, key => key.Frame, frame);
        var previousKey = keys[k];
        if (k + 1 >= keys.Count) return (previousKey.RotationX, previousKey.RotationY, previousKey.RotationZ);
        var nextKey = keys[k + 1];
        float previousWeight = (nextKey.Frame - frame) / (float)(nextKey.Frame - previousKey.Frame);
        float nextWeight = (frame - previousKey.Frame) / (float)(nextKey.Frame - previousKey.Frame);
        return (RotLerp(previousWeight, nextWeight, previousKey.RotationX, nextKey.RotationX), RotLerp(previousWeight, nextWeight, previousKey.RotationY, nextKey.RotationY), RotLerp(previousWeight, nextWeight, previousKey.RotationZ, nextKey.RotationZ));
    }

    // RhGeom_RotationInterpolate @ 0x20D1D0: both angles mod 0x10000, the second moved by
    // 0x10000 to the short side, then fptosi(b*r1 + a*r0 + 0.5) (truncates toward zero).
    static int RotLerp(float firstWeight, float secondWeight, int firstAngle, int secondAngle)
    {
        int firstWrapped = firstAngle & 0xFFFF, secondWrapped = secondAngle & 0xFFFF;
        if (secondWrapped - firstWrapped > 0x8000) secondWrapped -= 0x10000;
        else if (secondWrapped - firstWrapped < -0x8000) secondWrapped += 0x10000;
        return (int)(secondWrapped * secondWeight + firstWrapped * firstWeight + 0.5f);
    }

    /* Per matrix slot, the slot matrix the geometry is bound with. Normally the rest pose's.
       A part much smaller at rest than at its largest (Meteor Dragon's head and limbs at 0,
       card 178's arms at 1e-4, card 142's at 1e-2 of their full size) may be bound at the
       motion frame where it is largest instead: inverting a shrunken matrix multiplies every
       offset by the shrink, and those bone-local positions overflowed s16 on re-import
       (178's cloak stretched out to its arms). A part tiny in every frame is bound unscaled
       apart from the root's scale.

       A glb vertex has one position, so a vertex blending such a part with another can only
       match the game's skinning (a separate offset per influence) in some poses, depending
       on where the part is bound. No size ratio is right for every card (537's parts at
       1/13 want rest, 447's tail at 6e-5 wants its largest frame), so each such part takes
       whichever of the two bind frames skins its blended vertices closer to the game over
       the motions. */
    static Dictionary<int, Matrix4x4> BindMatrices(List<GameNode> nodes, List<AnimSlot> animations, float posScale,
        List<Packet> packets, out List<Pose> sampledPoses, out Dictionary<int, int> mslotNode)
    {
        const float ShrunkBy = 10f;       // rest 10x smaller than the largest frame: try both
        const float NeverVisible = 1e-3f; // relative to the root's scale
        const int SampleEvery = 6;        // motion frames the error is measured on
        var rest = EvalPose(nodes, null, 0, posScale);
        var rootAccumulatedScale = rest.AccumulatedScale[0];
        float rootScale = MathF.Max(MathF.Abs(rootAccumulatedScale.X), MathF.Max(MathF.Abs(rootAccumulatedScale.Y), MathF.Abs(rootAccumulatedScale.Z)));
        static float Size(Vector3 accumulatedScale) => MathF.Min(MathF.Abs(accumulatedScale.X), MathF.Min(MathF.Abs(accumulatedScale.Y), MathF.Abs(accumulatedScale.Z)));

        var slotted = nodes.Where(node => node.MeshSlot != 0).ToList();
        var bind = new Dictionary<int, Matrix4x4>();
        var best = new Dictionary<int, (float Size, Pose P)>();
        var spread = new Dictionary<int, Vector3>();   // the widest |accumulated scale| per axis
        var samples = new List<Pose>();
        foreach (var node in slotted)
        {
            best[node.MeshSlot] = (Size(rest.AccumulatedScale[node.Index]), rest);
            spread[node.MeshSlot] = Vector3.Abs(rest.AccumulatedScale[node.Index]);
        }
        int motion = 0;
        foreach (var anim in animations)
        {
            for (int frame = 0; frame < anim.FrameCount; frame++)
            {
                var pose = EvalPose(nodes, anim, frame, posScale);
                pose.Motion = motion;
                if (frame % SampleEvery == 0) samples.Add(pose);
                foreach (var node in slotted)
                {
                    float size = Size(pose.AccumulatedScale[node.Index]);
                    if (size > best[node.MeshSlot].Size) best[node.MeshSlot] = (size, pose);
                    spread[node.MeshSlot] = Vector3.Max(spread[node.MeshSlot], Vector3.Abs(pose.AccumulatedScale[node.Index]));
                }
            }
            motion++;
        }

        sampledPoses = samples;
        mslotNode = slotted.GroupBy(node => node.MeshSlot).ToDictionary(slotGroup => slotGroup.Key, slotGroup => slotGroup.First().Index);

        var shrunk = new List<int>();
        foreach (var node in slotted)
        {
            float restSize = Size(rest.AccumulatedScale[node.Index]);
            float maxSize = best[node.MeshSlot].Size;
            if (maxSize < NeverVisible * rootScale)
                bind[node.MeshSlot] = MatrixMath.Scale(rootAccumulatedScale.X, rootAccumulatedScale.Y, rootAccumulatedScale.Z) * rest.World[node.Index];
            else
            {
                bind[node.MeshSlot] = rest.SlotMatrix(node.Index);
                if (restSize * ShrunkBy < maxSize) shrunk.Add(node.MeshSlot);
            }
        }
        if (shrunk.Count == 0) return bind;

        // The blended vertices that touch a shrunk part: (slot, normalised weight, offset)
        // per influence, as BuildMesh reads them.
        var slotNode = slotted.ToDictionary(node => node.MeshSlot, node => node.Index);
        var shrunkSet = shrunk.ToHashSet();
        var blended = new List<(int Slot, float Weight, Vector3 Position)[]>();
        foreach (var packet in packets)
            foreach (var strip in packet.Strips)
            {
                if (strip.Type == "INDEP") continue;
                foreach (var vertex in strip.Verts)
                {
                    if (vertex.Weights == null) continue;
                    var influences = new List<(int Slot, float Weight, Vector3 Position)>();
                    foreach (var stripWeight in vertex.Weights)
                    {
                        int mslot = packet.BoneMap.TryGetValue(stripWeight.Bone, out var mappedSlot) ? mappedSlot : 0;
                        if (!bind.ContainsKey(mslot)) continue;
                        influences.Add((mslot, stripWeight.Weight, new Vector3(stripWeight.Position.X, stripWeight.Position.Y, stripWeight.Position.Z)));
                    }
                    float weightSum = influences.Sum(influence => influence.Weight);
                    if (weightSum == 0 || influences.Select(influence => influence.Slot).Distinct().Count() < 2) continue;
                    // Every blended vertex, not only those touching a shrunk part: moving a
                    // part's bind also moves the vertices it blends with elsewhere.
                    blended.Add(influences.Select(influence => (influence.Slot, influence.Weight / weightSum, influence.Position)).ToArray());
                }
            }

        /* How far the glb's skinning (one position x, the weighted average of B_i p_i, then
           sum w_i S_i B_i^-1 x) lands from the game's (sum w_i S_i p_i), summed over the
           sampled frames. A bind that cannot be inverted counts as a miss.
           Every part that shares a blended vertex with a shrunk part may move with it: a
           vertex is only skinned right where its parts' S_i*B_i^-1 agree, and two parts
           bound in different poses never agree, however the one position is chosen. So the
           candidates for such a part are its own default bind (rest), its own largest frame
           if it is shrunk, and the pose in which any shrunk part of its group is largest -
           binding the whole group in one pose makes the blend exact in that pose and close
           wherever the group moves rigidly. */
        var currentBind = new Dictionary<int, Matrix4x4>(bind);
        Matrix4x4 BindOf(int slot) => currentBind[slot];

        // Error() runs thousands of times per card over the same samples, so each sampled slot
        // matrix and each vertex's game position per sample is computed once.
        var sampledSlotMatrices = new Dictionary<int, Matrix4x4[]>();
        foreach (var node in slotted)
        {
            if (sampledSlotMatrices.ContainsKey(node.MeshSlot))
            {
                continue;
            }
            var matrices = new Matrix4x4[samples.Count];
            for (int k = 0; k < samples.Count; k++)
            {
                matrices[k] = samples[k].SlotMatrix(slotNode[node.MeshSlot]);
            }
            sampledSlotMatrices[node.MeshSlot] = matrices;
        }
        var gamePositions = new Dictionary<(int Slot, float Weight, Vector3 Position)[], Vector3[]>(ReferenceEqualityComparer.Instance);

        float Error((int Slot, float Weight, Vector3 Position)[] influences)
        {
            var blendedPosition = Vector3.Zero;
            foreach (var (slot, weight, offset) in influences) blendedPosition += weight * MatrixMath.Apply(BindOf(slot), offset.X, offset.Y, offset.Z);
            var local = new Vector3[influences.Length];
            for (int i = 0; i < influences.Length; i++)
            {
                if (!Matrix4x4.Invert(BindOf(influences[i].Slot), out var inverse)) return float.PositiveInfinity;
                local[i] = MatrixMath.Apply(inverse, blendedPosition.X, blendedPosition.Y, blendedPosition.Z);
                if (!float.IsFinite(local[i].X + local[i].Y + local[i].Z)) return float.PositiveInfinity;
            }
            var influenceMatrices = new Matrix4x4[influences.Length][];
            for (int i = 0; i < influences.Length; i++)
            {
                influenceMatrices[i] = sampledSlotMatrices[influences[i].Slot];
            }
            if (!gamePositions.TryGetValue(influences, out var game))
            {
                game = new Vector3[samples.Count];
                for (int k = 0; k < samples.Count; k++)
                {
                    Vector3 gamePosition = Vector3.Zero;
                    for (int i = 0; i < influences.Length; i++)
                    {
                        gamePosition += influences[i].Weight * MatrixMath.Apply(influenceMatrices[i][k], influences[i].Position.X, influences[i].Position.Y, influences[i].Position.Z);
                    }
                    game[k] = gamePosition;
                }
                gamePositions[influences] = game;
            }
            float totalError = 0;
            for (int k = 0; k < samples.Count; k++)
            {
                Vector3 glbPosition = Vector3.Zero;
                for (int i = 0; i < influences.Length; i++)
                {
                    glbPosition += influences[i].Weight * MatrixMath.Apply(influenceMatrices[i][k], local[i].X, local[i].Y, local[i].Z);
                }
                totalError += (glbPosition - game[k]).Length();
            }
            return totalError;
        }

        /* A part with no blended vertices skins exactly whatever its bind is, and is bound at
           its largest frame, which keeps its offsets in s16. Parts that share blended
           vertices (card 537's pair, 447's tail links, 178's cloak and the body under it) are
           first decided together, every member taking the same candidate: one at a time, a
           vertex between two parts bound in different poses is worse than with both in
           either, so the search would stick. Then each part is tried on its own. */
        var members = new HashSet<int>(shrunk);
        foreach (var influences in blended)
            foreach (var influence in influences)
                members.Add(influence.Slot);
        var touching = members.ToDictionary(slot => slot, slot => blended.Where(vertexInfluences => vertexInfluences.Any(influence => influence.Slot == slot)).ToList());
        var group = members.ToDictionary(slot => slot, slot => slot);
        int FindGroup(int slot) => group[slot] == slot ? slot : group[slot] = FindGroup(group[slot]);
        foreach (var influences in blended)
        {
            var parts = influences.Select(influence => influence.Slot).Distinct().ToList();
            for (int k = 1; k < parts.Count; k++) group[FindGroup(parts[k])] = FindGroup(parts[0]);
        }

        /* A part flattened at rest (a cloak sheet with one accumulated scale axis at or near
           zero) has a singular rest matrix: glTF cannot even store its inverse bind, and the
           game's offsets along that axis are free. Binding it in its rest pose with the
           flattened axes opened out to the width they reach in the motions keeps the part
           where the rest pose puts it - so it agrees with the parts it blends with, which are
           bound at rest - while staying invertible. */
        Matrix4x4 Unflattened(int slot)
        {
            int nodeIndex = slotNode[slot];
            var restScale = rest.AccumulatedScale[nodeIndex];
            var widestScale = spread[slot];
            float Axis(float restAxis, float widestAxis) => MathF.Abs(restAxis) * 50f >= widestAxis ? restAxis : (restAxis < 0 ? -widestAxis : widestAxis);
            return MatrixMath.Scale(Axis(restScale.X, widestScale.X), Axis(restScale.Y, widestScale.Y), Axis(restScale.Z, widestScale.Z)) * rest.World[nodeIndex];
        }

        var groups = members.GroupBy(FindGroup).Select(grouping => grouping.ToList()).ToList();
        // Candidate binds, per group: index 0 is each part's default, 1 its own largest
        // frame, then one per pose in which a shrunk member is largest.
        foreach (var slotGroup in groups)
        {
            var poses = new List<Pose>();
            foreach (int slot in slotGroup)
                if (shrunkSet.Contains(slot) && !poses.Contains(best[slot].P)) poses.Add(best[slot].P);
            var candidates = new Dictionary<int, List<Matrix4x4>>();
            foreach (int slot in slotGroup)
            {
                var list = new List<Matrix4x4> { bind[slot] };
                list.Add(shrunkSet.Contains(slot) ? best[slot].P.SlotMatrix(slotNode[slot]) : bind[slot]);
                list.Add(shrunkSet.Contains(slot) ? Unflattened(slot) : bind[slot]);
                foreach (var pose in poses) list.Add(pose.SlotMatrix(slotNode[slot]));
                candidates[slot] = list;
            }
            var groupVertices = slotGroup.SelectMany(slot => touching[slot]).Distinct().ToList();
            if (groupVertices.Count == 0)
            {
                // No blended vertex: a shrunk part is bound at its largest frame.
                foreach (int slot in slotGroup)
                    if (shrunkSet.Contains(slot)) bind[slot] = candidates[slot][1];
                continue;
            }

            /* Each start (every part on its own default, every shrunk part at its own
               largest frame, or the whole group in one shrunk member's pose) is then
               refined one part at a time; the configuration with the least total error
               over the group's vertices wins. */
            int candidateCount = 3 + poses.Count;
            Dictionary<int, int> bestPick = null;
            float bestError = float.PositiveInfinity;
            for (int start = 0; start < candidateCount; start++)
            {
                var pick = slotGroup.ToDictionary(slot => slot, _ => start);
                foreach (int slot in slotGroup) currentBind[slot] = candidates[slot][start];
                for (int pass = 0; pass < 3; pass++)
                {
                    bool changed = false;
                    foreach (int slot in slotGroup)
                    {
                        if (touching[slot].Count == 0) continue;
                        int keep = pick[slot];
                        float keepError = touching[slot].Sum(Error);
                        for (int k = 0; k < candidateCount; k++)
                        {
                            if (k == pick[slot]) continue;
                            currentBind[slot] = candidates[slot][k];
                            float candidateError = touching[slot].Sum(Error);
                            if (candidateError < keepError) { keepError = candidateError; keep = k; }
                        }
                        currentBind[slot] = candidates[slot][keep];
                        changed |= keep != pick[slot];
                        pick[slot] = keep;
                    }
                    if (!changed) break;
                }
                float total = groupVertices.Sum(Error);
                if (total < bestError) { bestError = total; bestPick = pick; }
            }
            foreach (int slot in slotGroup) currentBind[slot] = candidates[slot][bestPick[slot]];
            foreach (int slot in slotGroup) bind[slot] = currentBind[slot];
        }
        return bind;
    }

    /* The one position a glb vertex gets, for a vertex the game skins from several bones.

       The game evaluates Sum w_i*S_i(t)*p_i, a separate offset p_i per influence; a glb has
       one position x and the importer stores p_i' = B_i^-1*x, so the glb skins to
       Sum w_i*S_i(t)*B_i^-1*x. The plain weighted average Sum w_i*B_i*p_i is exact only in
       poses where every S_i*B_i^-1 of the vertex agrees - at the bind pose, and wherever the
       parts move together. When they don't (card 178's cloak hangs off two parts whose binds
       are frames apart), that vertex drifts in every other frame and pokes through its
       neighbours. Instead x is the least-squares fit over the sampled motion frames:
       minimise Sum_t |x*A(t) + a(t) - g(t)|^2, A(t) = Sum w_i*S_i(t)*B_i^-1 and
       g(t) = Sum w_i*S_i(t)*p_i, which is the same weighted average whenever that one is
       exact. A ridge toward the average keeps the fit defined when the frames leave a
       direction free (a part collapsed to zero in every frame). */
    sealed class BlendFitter
    {
        readonly Dictionary<int, Matrix4x4> bindMatrices;
        readonly List<Pose> sampledPoses;
        readonly Dictionary<int, int> nodeOfSlot;
        readonly Dictionary<int, Matrix4x4> inverseBindCache = new();
        readonly Dictionary<string, Vector3> fitCache = new();

        public BlendFitter(Dictionary<int, Matrix4x4> bind, List<Pose> samples, Dictionary<int, int> nodeBySlot)
        {
            bindMatrices = bind;
            sampledPoses = samples;
            nodeOfSlot = nodeBySlot;
        }

        Matrix4x4? InverseBind(int slot)
        {
            if (inverseBindCache.TryGetValue(slot, out var cached)) return cached.M11 == 0 && cached.M22 == 0 && cached.M33 == 0 && cached.M44 == 0
                ? (Matrix4x4?)null : cached;
            if (!bindMatrices.TryGetValue(slot, out var bindMatrix) || !Matrix4x4.Invert(bindMatrix, out var inverse))
            {
                inverseBindCache[slot] = default;
                return null;
            }
            inverseBindCache[slot] = inverse;
            return inverse;
        }

        public Vector3 Position(List<(int Slot, float Weight, Vector3 Position)> influences, Vector3 average)
            => Fitted(influences, null, average, "P");

        /* The same fit for the normal. A glb normal is skinned by the same A(t) without the
           translation, and the game's own normal is Sum w_i*S_i*n_i normalised, so x is the
           least-squares fit of that unnormalised sum over the same sampled frames, kept only
           where its worst frame is no worse than the plain average's. The caller normalises. */
        public Vector3 Normal(List<(int Slot, float Weight, Vector3 Position)> influences, List<Vector3> influenceNormals, Vector3 average)
            => Fitted(influences, influenceNormals, average, "N");

        Vector3 Fitted(List<(int Slot, float Weight, Vector3 Position)> influences, List<Vector3> influenceNormals, Vector3 average, string kind)
        {
            if (sampledPoses == null || sampledPoses.Count == 0 || nodeOfSlot == null) return average;
            if (influences.Count < 2) return average;
            bool mixed = false;
            for (int i = 1; i < influences.Count; i++)
                if (influences[i].Slot != influences[0].Slot) { mixed = true; break; }
            if (!mixed) return average;

            var keyBuilder = new System.Text.StringBuilder(kind);
            for (int i = 0; i < influences.Count; i++)
            {
                var (slot, weight, offset) = influences[i];
                keyBuilder.Append(slot).Append(':').Append(weight).Append(':')
                   .Append(offset.X).Append(',').Append(offset.Y).Append(',').Append(offset.Z).Append(';');
                if (influenceNormals != null)
                    keyBuilder.Append(influenceNormals[i].X).Append(',').Append(influenceNormals[i].Y).Append(',')
                       .Append(influenceNormals[i].Z).Append(';');
            }
            string cacheKey = keyBuilder.ToString();
            if (fitCache.TryGetValue(cacheKey, out var cached)) return cached;

            var inverseBinds = new Matrix4x4[influences.Count];
            for (int i = 0; i < influences.Count; i++)
            {
                var inverse = InverseBind(influences[i].Slot);
                if (inverse == null || !nodeOfSlot.ContainsKey(influences[i].Slot)) return average;
                inverseBinds[i] = inverse.Value;
            }

            // Normal equations N*x = r, with a ridge lambda toward the weighted average.
            Span<double> normalMatrix = stackalloc double[9];
            Span<double> rightSide = stackalloc double[3];
            double scale = 0;
            foreach (var pose in sampledPoses)
            {
                Matrix4x4 combinedMatrix = default;
                Vector3 gameResult = Vector3.Zero;
                for (int i = 0; i < influences.Count; i++)
                {
                    var slotMatrix = pose.SlotMatrix(nodeOfSlot[influences[i].Slot]);
                    float weight = influences[i].Weight;
                    var skinMatrix = inverseBinds[i] * slotMatrix;
                    combinedMatrix.M11 += weight * skinMatrix.M11; combinedMatrix.M12 += weight * skinMatrix.M12; combinedMatrix.M13 += weight * skinMatrix.M13;
                    combinedMatrix.M21 += weight * skinMatrix.M21; combinedMatrix.M22 += weight * skinMatrix.M22; combinedMatrix.M23 += weight * skinMatrix.M23;
                    combinedMatrix.M31 += weight * skinMatrix.M31; combinedMatrix.M32 += weight * skinMatrix.M32; combinedMatrix.M33 += weight * skinMatrix.M33;
                    combinedMatrix.M41 += weight * skinMatrix.M41; combinedMatrix.M42 += weight * skinMatrix.M42; combinedMatrix.M43 += weight * skinMatrix.M43;
                    gameResult += weight * (influenceNormals == null
                        ? MatrixMath.Apply(slotMatrix, influences[i].Position.X, influences[i].Position.Y, influences[i].Position.Z)
                        : Vector3.TransformNormal(influenceNormals[i], slotMatrix));
                }
                double[] linearPart = { combinedMatrix.M11, combinedMatrix.M12, combinedMatrix.M13, combinedMatrix.M21, combinedMatrix.M22, combinedMatrix.M23, combinedMatrix.M31, combinedMatrix.M32, combinedMatrix.M33 };
                float translationX = influenceNormals == null ? combinedMatrix.M41 : 0, translationY = influenceNormals == null ? combinedMatrix.M42 : 0,
                    translationZ = influenceNormals == null ? combinedMatrix.M43 : 0;
                double targetX = gameResult.X - translationX, targetY = gameResult.Y - translationY, targetZ = gameResult.Z - translationZ;
                /* Only a normal's direction is kept, so every frame counts the same: divide
                   the frame out by the game normal's length, or a frame where the bones are
                   large drags the direction of the small ones. */
                if (influenceNormals != null && gameResult.Length() > 0)
                {
                    double inverseLength = 1.0 / gameResult.Length();
                    for (int entryIndex = 0; entryIndex < 9; entryIndex++) linearPart[entryIndex] *= inverseLength;
                    targetX *= inverseLength; targetY *= inverseLength; targetZ *= inverseLength;
                }
                if (!double.IsFinite(linearPart[0] + linearPart[4] + linearPart[8] + targetX + targetY + targetZ)) return average;
                for (int row = 0; row < 3; row++)
                {
                    for (int column = 0; column < 3; column++)
                        normalMatrix[row * 3 + column] += linearPart[row * 3 + 0] * linearPart[column * 3 + 0] + linearPart[row * 3 + 1] * linearPart[column * 3 + 1]
                            + linearPart[row * 3 + 2] * linearPart[column * 3 + 2];
                    rightSide[row] += linearPart[row * 3 + 0] * targetX + linearPart[row * 3 + 1] * targetY + linearPart[row * 3 + 2] * targetZ;
                }
                scale += (normalMatrix[0] + normalMatrix[4] + normalMatrix[8]);
            }
            double lambda = 1e-6 * (normalMatrix[0] + normalMatrix[4] + normalMatrix[8]) / 3.0;
            if (!(lambda > 0)) return average;
            normalMatrix[0] += lambda; normalMatrix[4] += lambda; normalMatrix[8] += lambda;
            rightSide[0] += lambda * average.X; rightSide[1] += lambda * average.Y; rightSide[2] += lambda * average.Z;

            if (!Solve3(normalMatrix, rightSide, out var fitted)) return average;
            if (!float.IsFinite(fitted.X) || !float.IsFinite(fitted.Y) || !float.IsFinite(fitted.Z)) fitted = average;

            /* A least-squares fit can trade a little of one frame for the rest. A position's
               fit is kept on its worst frame over the whole sample (a vertex poking through
               its neighbours is a worst-frame problem). A normal is kept only where its worst
               frame is no worse in every single motion, so a motion whose normals were
               already close cannot pay for another one's. */
            if (influenceNormals == null ? Worst(influences, influenceNormals, inverseBinds, fitted) > Worst(influences, influenceNormals, inverseBinds, average)
                            : WorseInSomeMotion(influences, influenceNormals, inverseBinds, fitted, average)) fitted = average;
            fitCache[cacheKey] = fitted;
            return fitted;
        }

        /* The largest distance, over the sampled frames, between the glb's skinning of x and
           the game's own result for this vertex. For a normal both sides are normalised
           first, so the distance is the chord of the angle between them. */
        float Worst(List<(int Slot, float Weight, Vector3 Position)> influences, List<Vector3> influenceNormals, Matrix4x4[] inverseBinds, Vector3 candidatePoint)
        {
            float worst = 0;
            foreach (var pose in sampledPoses)
                worst = MathF.Max(worst, FrameError(pose, influences, influenceNormals, inverseBinds, candidatePoint));
            return worst;
        }

        // Whether x's worst frame is worse than the average's within any one motion (the
        // samples come in motion order).
        bool WorseInSomeMotion(List<(int Slot, float Weight, Vector3 Position)> influences, List<Vector3> influenceNormals,
            Matrix4x4[] inverseBinds, Vector3 candidatePoint, Vector3 average)
        {
            float worstCandidate = 0, worstAverage = 0;
            int motion = sampledPoses[0].Motion;
            foreach (var pose in sampledPoses)
            {
                if (pose.Motion != motion)
                {
                    if (worstCandidate > worstAverage + 1e-6f) return true;
                    worstCandidate = worstAverage = 0;
                    motion = pose.Motion;
                }
                worstCandidate = MathF.Max(worstCandidate, FrameError(pose, influences, influenceNormals, inverseBinds, candidatePoint));
                worstAverage = MathF.Max(worstAverage, FrameError(pose, influences, influenceNormals, inverseBinds, average));
            }
            return worstCandidate > worstAverage + 1e-6f;
        }

        // One frame's distance, measured as in Worst.
        float FrameError(Pose pose, List<(int Slot, float Weight, Vector3 Position)> influences, List<Vector3> influenceNormals,
            Matrix4x4[] inverseBinds, Vector3 candidatePoint)
        {
            Vector3 glbResult = Vector3.Zero, game = Vector3.Zero;
            for (int i = 0; i < influences.Count; i++)
            {
                var slotMatrix = pose.SlotMatrix(nodeOfSlot[influences[i].Slot]);
                if (influenceNormals == null)
                {
                    var local = MatrixMath.Apply(inverseBinds[i], candidatePoint.X, candidatePoint.Y, candidatePoint.Z);
                    glbResult += influences[i].Weight * MatrixMath.Apply(slotMatrix, local.X, local.Y, local.Z);
                    game += influences[i].Weight * MatrixMath.Apply(slotMatrix, influences[i].Position.X, influences[i].Position.Y, influences[i].Position.Z);
                }
                else
                {
                    var local = Vector3.TransformNormal(candidatePoint, inverseBinds[i]);
                    glbResult += influences[i].Weight * Vector3.TransformNormal(local, slotMatrix);
                    game += influences[i].Weight * Vector3.TransformNormal(influenceNormals[i], slotMatrix);
                }
            }
            if (influenceNormals != null) { glbResult = NormalizeOrZero(glbResult); game = NormalizeOrZero(game); }
            return (glbResult - game).Length();
        }

        // Symmetric 3x3 solve by Cramer's rule; a singular system falls back to the average.
        static bool Solve3(Span<double> matrix, Span<double> rightSide, out Vector3 solution)
        {
            double entry00 = matrix[0], entry01 = matrix[1], entry02 = matrix[2], entry10 = matrix[3], entry11 = matrix[4], entry12 = matrix[5], entry20 = matrix[6], entry21 = matrix[7], entry22 = matrix[8];
            double determinant = entry00 * (entry11 * entry22 - entry12 * entry21) - entry01 * (entry10 * entry22 - entry12 * entry20) + entry02 * (entry10 * entry21 - entry11 * entry20);
            double norm = Math.Abs(entry00) + Math.Abs(entry11) + Math.Abs(entry22);
            solution = Vector3.Zero;
            if (!(Math.Abs(determinant) > 1e-12 * norm * norm * norm) || !double.IsFinite(determinant)) return false;
            double right0 = rightSide[0], right1 = rightSide[1], right2 = rightSide[2];
            double determinantX = right0 * (entry11 * entry22 - entry12 * entry21) - entry01 * (right1 * entry22 - entry12 * right2) + entry02 * (right1 * entry21 - entry11 * right2);
            double determinantY = entry00 * (right1 * entry22 - entry12 * right2) - right0 * (entry10 * entry22 - entry12 * entry20) + entry02 * (entry10 * right2 - right1 * entry20);
            double determinantZ = entry00 * (entry11 * right2 - right1 * entry21) - entry01 * (entry10 * right2 - right1 * entry20) + right0 * (entry10 * entry21 - entry11 * entry20);
            solution = new Vector3((float)(determinantX / determinant), (float)(determinantY / determinant), (float)(determinantZ / determinant));
            return true;
        }
    }

    static readonly byte[] MagentaPixel = { 0xff, 0x00, 0xff, 0xff };

    static (List<GameNode> Nodes, Dictionary<int, GameNode> MslotMap) WalkGNodes(byte[] data, int treeBase)
    {
        var nodes = new List<GameNode>();
        var mslotMap = new Dictionary<int, GameNode>();

        void Visit(int nodeOffset, int parentIndex, Matrix4x4 parentWorld, Vector3 parentAccumulatedScale)
        {
            float scaleX = ReadF32(data, nodeOffset + 0x00);
            float scaleY = ReadF32(data, nodeOffset + 0x04);
            float scaleZ = ReadF32(data, nodeOffset + 0x08);
            ushort flags = ReadU16(data, nodeOffset + 0x0C);
            ushort mslot = ReadU16(data, nodeOffset + 0x0E);
            int rotationX = ReadI32(data, nodeOffset + 0x10);
            int rotationY = ReadI32(data, nodeOffset + 0x14);
            int rotationZ = ReadI32(data, nodeOffset + 0x18);

            int childOffset = ReadI32(data, nodeOffset + 0x1C) & 0x0FFFFFFF;
            float positionX = ReadF32(data, nodeOffset + 0x20);
            float positionY = ReadF32(data, nodeOffset + 0x24);
            float positionZ = ReadF32(data, nodeOffset + 0x28);
            int siblingOffset = ReadI32(data, nodeOffset + 0x2C) & 0x0FFFFFFF;

            bool skipTranslation = (flags & 1) != 0;
            bool skipRotation = (flags & 2) != 0;
            bool noScalePropagation = (flags & 4) != 0;
            float unscaledTranslationX = skipTranslation ? 0f : positionX;
            float unscaledTranslationY = skipTranslation ? 0f : positionY;
            float unscaledTranslationZ = skipTranslation ? 0f : positionZ;
            int appliedRotationX = skipRotation ? 0 : rotationX;
            int appliedRotationY = skipRotation ? 0 : rotationY;
            int appliedRotationZ = skipRotation ? 0 : rotationZ;
            float translationX = unscaledTranslationX * parentAccumulatedScale.X;
            float translationY = unscaledTranslationY * parentAccumulatedScale.Y;
            float translationZ = unscaledTranslationZ * parentAccumulatedScale.Z;
            Vector3 childAccumulatedScale = noScalePropagation
                ? parentAccumulatedScale
                : new Vector3(parentAccumulatedScale.X * scaleX, parentAccumulatedScale.Y * scaleY, parentAccumulatedScale.Z * scaleZ);

            var translation = MatrixMath.Translation(translationX, translationY, translationZ);
            var rotation = MatrixMath.RotX(appliedRotationX) * MatrixMath.RotY(appliedRotationY) * MatrixMath.RotZ(appliedRotationZ);
            var world = rotation * translation * parentWorld;

            int nodeIndex = nodes.Count;
            var node = new GameNode {
                Index = nodeIndex,
                ParentIndex = parentIndex,
                Flags = flags,
                MeshSlot = mslot,
                RawScale = new Vector3(scaleX, scaleY, scaleZ),
                RawPos = new Vector3(positionX, positionY, positionZ),
                RawRot = (rotationX, rotationY, rotationZ),
                RestWorld = world,
            };
            nodes.Add(node);
            if (parentIndex >= 0) nodes[parentIndex].Children.Add(nodeIndex);
            if (mslot != 0) mslotMap[mslot] = node;

            if (childOffset != 0) Visit(treeBase + childOffset, nodeIndex, world, childAccumulatedScale);
            if (siblingOffset != 0) Visit(treeBase + siblingOffset, parentIndex, parentWorld, parentAccumulatedScale);
        }

        Visit(treeBase, -1, Matrix4x4.Identity, new Vector3(1, 1, 1));
        return (nodes, mslotMap);
    }

    static (List<int> Main, List<int> Alt) ParseWalks(byte[] data, in Rh0Header header)
    {
        var mainWalks = new List<int>();
        var alternateWalks = new List<int>();
        int wordCount = (int)(header.RelocWalkSize / 4);
        for (int i = 0; i < wordCount; i++)
        {
            uint word = ReadU32(data, (int)header.RelocWalk + i * 4);
            if (word == 0xFFFFFFFF) break;
            if (word == 0) continue;
            uint flag = word & 0xF0000000;
            int walkOffset = (int)(word & 0x0FFFFFFF);
            if (flag == 0x20000000) mainWalks.Add(walkOffset);
            else if (flag == 0x40000000) alternateWalks.Add(walkOffset);
        }
        return (mainWalks, alternateWalks);
    }

    // Draw state that persists from one draw chain into the next.
    struct ChainState
    {
        public (float R, float G, float B) MaterialColor0, MaterialColor1, MaterialColor2;
        public float AlphaW;
        public ulong GsAlpha, GsTest, GsClamp;

        public static ChainState Default => new() {
            MaterialColor0 = (0, 0, 0), MaterialColor1 = (255, 255, 255), MaterialColor2 = (255, 255, 255), AlphaW = 128f,
            GsAlpha = DefaultGsAlpha, GsTest = DefaultGsTest, GsClamp = DefaultGsClamp,
        };
    }

    private static List<PageInfo> ParsePagesInSection(
        byte[] data, int sectionBase, int sectionSize, List<int> walkOffsets, ref ChainState state)
    {
        var pages = new List<PageInfo>();
        var runningBoneMap = new Dictionary<int, int>();
        var currentQ0 = state.MaterialColor0;
        var currentQ1 = state.MaterialColor1;
        var currentQ2 = state.MaterialColor2;
        float currentAlphaW = state.AlphaW;
        ulong currentGsAlpha = state.GsAlpha;
        ulong currentGsTest = state.GsTest;
        ulong currentGsClamp = state.GsClamp;
        int scanStart = 0;
        /* The state and colour blocks are scanned between consecutive pages, so the pages must be
           visited in chain order. Retail's walk list is ascending; an imported block's follows its
           texture dispatch order (Kryuel's untextured page came after tex2's and took its colours). */
        walkOffsets = walkOffsets.OrderBy(offset => offset).ToList();
        for (int walkIndex = 0; walkIndex < walkOffsets.Count; walkIndex++)
        {
            int walkOffset = walkOffsets[walkIndex];
            if (walkOffset < 4 || walkOffset > sectionSize) continue;
            int terminatorOffset = walkOffset - 4;
            int scanEnd = Math.Min(terminatorOffset, sectionSize);
            int scanPosition = scanStart;
            while (scanPosition + 4 <= scanEnd)
            {
                uint word = ReadU32(data, sectionBase + scanPosition);
                if (word == 0x10000003 && scanPosition + 0x40 <= sectionSize)
                {
                    // three V4-32 colours unpacked to VU 0x3C: c0 +0x10, c1 +0x20, c2 +0x30
                    float q0Red = ReadF32(data, sectionBase + scanPosition + 0x10);
                    float q0Green = ReadF32(data, sectionBase + scanPosition + 0x14);
                    float q0Blue = ReadF32(data, sectionBase + scanPosition + 0x18);
                    if (q0Red >= 0 && q0Red <= 255 && q0Green >= 0 && q0Green <= 255 && q0Blue >= 0 && q0Blue <= 255)
                        currentQ0 = (q0Red, q0Green, q0Blue);
                    float q1Red = ReadF32(data, sectionBase + scanPosition + 0x20);
                    float q1Green = ReadF32(data, sectionBase + scanPosition + 0x24);
                    float q1Blue = ReadF32(data, sectionBase + scanPosition + 0x28);
                    float q1Alpha = ReadF32(data, sectionBase + scanPosition + 0x2C);
                    if (q1Red >= 0 && q1Red <= 255 && q1Green >= 0 && q1Green <= 255 && q1Blue >= 0 && q1Blue <= 255)
                        currentQ1 = (q1Red, q1Green, q1Blue);
                    if (q1Alpha >= 0 && q1Alpha <= 255) currentAlphaW = q1Alpha;
                    float q2Red = ReadF32(data, sectionBase + scanPosition + 0x30);
                    float q2Green = ReadF32(data, sectionBase + scanPosition + 0x34);
                    float q2Blue = ReadF32(data, sectionBase + scanPosition + 0x38);
                    if (q2Red >= 0 && q2Red <= 255 && q2Green >= 0 && q2Green <= 255 && q2Blue >= 0 && q2Blue <= 255)
                        currentQ2 = (q2Red, q2Green, q2Blue);
                    scanPosition += 0x40;
                    continue;
                }

                /* The material setup is a packed GIF A+D list. The first qword is the 64-bit
                   GS value and the second qword's low byte is the GS register (0x42/43 =
                   ALPHA, 0x47/48 = TEST, 0x08/09 = CLAMP). The state active when a page is
                   dispatched is kept as is, not inferred from the alternate section or texture
                   animation. */
                if ((scanPosition & 0xF) == 0 && scanPosition + 16 <= scanEnd)
                {
                    uint gsRegister = ReadU32(data, sectionBase + scanPosition + 8);
                    uint gsRegisterHigh = ReadU32(data, sectionBase + scanPosition + 12);
                    if (gsRegisterHigh == 0 && (gsRegister == 0x42 || gsRegister == 0x43))
                    {
                        currentGsAlpha = ReadU64(data, sectionBase + scanPosition);
                        scanPosition += 16;
                        continue;
                    }
                    if (gsRegisterHigh == 0 && (gsRegister == 0x47 || gsRegister == 0x48))
                    {
                        currentGsTest = ReadU64(data, sectionBase + scanPosition);
                        scanPosition += 16;
                        continue;
                    }
                    if (gsRegisterHigh == 0 && (gsRegister == 0x08 || gsRegister == 0x09))
                    {
                        // Only the plain wrap-mode nibble is used by retail models; anything
                        // larger is unrelated data that happens to look like an A+D pair.
                        ulong clampValue = ReadU64(data, sectionBase + scanPosition);
                        if (clampValue <= 0xFUL)
                        {
                            currentGsClamp = clampValue;
                            scanPosition += 16;
                            continue;
                        }
                    }
                }

                scanPosition += 4;
            }
            scanStart = terminatorOffset + 0x10;

            var entries = FindStripEntries(data, sectionBase, terminatorOffset);
            foreach (var (vuAddress, slotOffset) in entries) runningBoneMap[vuAddress] = slotOffset >> 4;

            uint terminatorWord0 = ReadU32(data, sectionBase + terminatorOffset + 0);
            uint terminatorWord1 = ReadU32(data, sectionBase + terminatorOffset + 4);

            pages.Add(new PageInfo {
                WalkOff = walkOffset,
                BoneMap = new Dictionary<int, int>(runningBoneMap),
                Sec2Off = terminatorWord1 & 0x0FFFFFFF,
                PktSize = (terminatorWord0 & 0xFFFF) << 4,
                ColorQ0 = currentQ0,
                ColorQ1 = currentQ1,
                ColorQ2 = currentQ2,
                AlphaW = currentAlphaW,
                GsAlpha = currentGsAlpha,
                GsTest = currentGsTest,
                GsClamp = currentGsClamp,
            });
        }
        state = new ChainState {
            MaterialColor0 = currentQ0, MaterialColor1 = currentQ1, MaterialColor2 = currentQ2, AlphaW = currentAlphaW,
            GsAlpha = currentGsAlpha, GsTest = currentGsTest, GsClamp = currentGsClamp,
        };
        return pages;
    }

    private static List<int> ParseAltPagesByScan(byte[] data, in Rh0Header header)
    {
        var walks = new List<int>();
        if (header.Section1Alt == 0 || header.Section1AltSize < 16) return walks;
        int sectionStart = (int)header.Section1Alt;
        int sectionSize = (int)header.Section1AltSize;
        for (int entryOffset = 0; entryOffset + 16 <= sectionSize; entryOffset += 16)
        {
            uint word0 = ReadU32(data, sectionStart + entryOffset);
            uint word1 = ReadU32(data, sectionStart + entryOffset + 4);
            uint word2 = ReadU32(data, sectionStart + entryOffset + 8);
            uint word3 = ReadU32(data, sectionStart + entryOffset + 12);
            if ((word0 & 0xFFFF0000) != 0x30000000) continue;
            if ((word0 & 0xFFFF) == 0x0007) continue;
            if (word2 != 0 || word3 != 0) continue;
            walks.Add(entryOffset + 4);
        }
        return walks;
    }

    private static List<AnimSlot> ParseMotSlots(byte[] data, in Rh0Header header, int nodeCount)
    {
        var animations = new List<AnimSlot>();
        if (header.MotTable == 0) return animations;
        var slotIds = new List<int>();
        for (int i = 0; i < 8; i++)
            if ((header.SectionFlags & (1u << i)) != 0)
                slotIds.Add(i);
        int motionOffset = (int)header.MotTable;
        const uint MotMagic = 0x1A544F4D;
        foreach (int slotId in slotIds)
        {
            if (motionOffset + 32 > data.Length) break;
            uint magic = ReadU32(data, motionOffset);
            if (magic != MotMagic) break;
            uint size = ReadU32(data, motionOffset + 4);
            ushort motionEntryCount = ReadU16(data, motionOffset + 0x10);
            uint motionHeaderOffset = ReadU32(data, motionOffset + 0x14);
            int motionStart = motionOffset + (int)motionHeaderOffset;
            uint motionDataOffset = ReadU32(data, motionStart);
            uint frameCount = ReadU32(data, motionStart + 4);
            ushort motionType = ReadU16(data, motionStart + 8);
            // type 3: pos ptr, angle ptr, n pos, n angle (16 B); type 7 adds a scale ptr
            // after the angle ptr and its count last (24 B)
            bool hasScale = (motionType & 4) != 0;
            int lists = hasScale ? 3 : 2;
            int motionDataStart = motionOffset + (int)motionDataOffset;
            var tracks = new Dictionary<int, AnimTrack>();
            // The game walks the tree and reads only the entries of nodes it visits, so a
            // motion with more entries than the tree still plays (2 retail motions).
            int entryCount = Math.Min((int)motionEntryCount, nodeCount);
            for (int i = 0; i < entryCount; i++)
            {
                int entryOffset = motionDataStart + i * lists * 8;
                uint translationKeysOffset = ReadU32(data, entryOffset);
                uint rotationKeysOffset = ReadU32(data, entryOffset + 4);
                uint scaleKeysOffset = hasScale ? ReadU32(data, entryOffset + 8) : 0;
                int translationKeyCount = (int)ReadU32(data, entryOffset + lists * 4);
                int rotationKeyCount = (int)ReadU32(data, entryOffset + lists * 4 + 4);
                int scaleKeyCount = hasScale ? (int)ReadU32(data, entryOffset + 20) : 0;
                var track = new AnimTrack();
                if (translationKeysOffset != 0 && translationKeyCount > 0) track.Trans = ReadVecKeys(data, motionOffset + (int)translationKeysOffset, translationKeyCount);
                if (scaleKeysOffset != 0 && scaleKeyCount > 0) track.Scale = ReadVecKeys(data, motionOffset + (int)scaleKeysOffset, scaleKeyCount);
                if (rotationKeysOffset != 0 && rotationKeyCount > 0)
                {
                    var rotationKeys = new List<(int, int, int, int)>(rotationKeyCount);
                    for (int k = 0; k < rotationKeyCount; k++)
                    {
                        int keyOffset = motionOffset + (int)rotationKeysOffset + k * 16;
                        int frame = ReadI32(data, keyOffset);
                        int rotationX = ReadI32(data, keyOffset + 4);
                        int rotationY = ReadI32(data, keyOffset + 8);
                        int rotationZ = ReadI32(data, keyOffset + 12);
                        rotationKeys.Add((frame, rotationX, rotationY, rotationZ));
                    }
                    track.Rotation = rotationKeys;
                }
                if (track.Trans != null || track.Rotation != null || track.Scale != null) tracks[i] = track;
            }
            animations.Add(new AnimSlot {
                SlotId = slotId,
                FrameCount = (int)frameCount,
                Scaled = hasScale,
                Tracks = tracks,
            });
            motionOffset += (int)size;
        }
        return animations;
    }

    // 16-byte keys: u32 frame, f32 x3
    private static List<(int Frame, float X, float Y, float Z)> ReadVecKeys(byte[] data, int firstKeyOffset, int count)
    {
        var keys = new List<(int, float, float, float)>(count);
        for (int k = 0; k < count; k++)
        {
            int keyOffset = firstKeyOffset + k * 16;
            keys.Add((ReadI32(data, keyOffset), ReadF32(data, keyOffset + 4), ReadF32(data, keyOffset + 8), ReadF32(data, keyOffset + 12)));
        }
        return keys;
    }

    private static List<(int Vu, int So)> FindStripEntries(byte[] data, int sectionBase, int terminatorOffset)
    {
        var entries = new List<(int, int)>();
        int entryOffset = terminatorOffset - 16;
        while (entryOffset >= 0)
        {
            uint word0 = ReadU32(data, sectionBase + entryOffset);
            if (word0 != 0x30000007) break;
            uint word1 = ReadU32(data, sectionBase + entryOffset + 4);
            uint word3 = ReadU32(data, sectionBase + entryOffset + 12);
            entries.Add(((int)(word3 & 0x3FF), (int)(word1 & 0x0FFFFFFF)));
            entryOffset -= 16;
        }
        entries.Reverse();
        return entries;
    }

    private static (uint[] TexOffsets, uint[] PostOffsets, List<(int TexIdx, int Off)> MainDispatches, List<(int TexIdx, int Off)> AltDispatches)
        ParseTexTableForModel(byte[] data, in Rh0Header header)
    {
        if (header.TexTable == 0 || header.TexCount == 0)
            return (Array.Empty<uint>(), Array.Empty<uint>(), new List<(int, int)>(), new List<(int, int)>());
        int textureTable = (int)header.TexTable;
        uint count = ReadU32(data, textureTable);
        uint perTextureOffset = ReadU32(data, textureTable + 4);
        if (count == 0 || count > 256)
            return (Array.Empty<uint>(), Array.Empty<uint>(), new List<(int, int)>(), new List<(int, int)>());

        var textureOffsets = new uint[count];
        var postOffsets = new uint[count];
        for (int i = 0; i < count; i++)
        {
            textureOffsets[i] = ReadU32(data, textureTable + 16 + i * 16);
            // word[+8] of each 16-byte block points at the texture's "post section"
            // (the packed .anm entry array), or 0 when the texture is not animated.
            postOffsets[i] = ReadU32(data, textureTable + 16 + i * 16 + 8);
        }

        var mainDispatches = new List<(int, int)>();
        var altDispatches = new List<(int, int)>();
        for (int i = 0; i < count; i++)
        {
            int entryOffset = textureTable + (int)perTextureOffset + i * 8;
            uint dispatchCount = ReadU32(data, entryOffset);
            uint dispatchOffset = ReadU32(data, entryOffset + 4);
            if (dispatchCount > 64) continue;
            for (int j = 0; j < dispatchCount; j++)
            {
                uint word = ReadU32(data, textureTable + (int)dispatchOffset + j * 4);
                uint flag = word & 0xF0000000;
                int walkOffset = (int)(word & 0x0FFFFFFF);
                if (flag == 0x20000000) mainDispatches.Add((i, walkOffset));
                else if (flag == 0x40000000) altDispatches.Add((i, walkOffset));
            }
        }
        return (textureOffsets, postOffsets, mainDispatches, altDispatches);
    }

    // The texture a page draws with: the last dispatch at or before it, or -1 if none (the
    // GS keeps whatever TEX0 was set last).
    private static int PageToTexture(int walkOffset, List<(int TexIdx, int Off)> dispatches)
    {
        int bestTexture = -1, bestOffset = -1;
        foreach (var (textureIndex, dispatchOffset) in dispatches)
            if (dispatchOffset <= walkOffset && dispatchOffset > bestOffset)
            {
                bestOffset = dispatchOffset;
                bestTexture = textureIndex;
            }
        return bestTexture;
    }

    // PCSX2 nVifT: bytes per vector for each VIF UNPACK type
    private static readonly int[] VifUnpackBytesPerVector = { 4, 2, 1, 0, 8, 4, 2, 0, 12, 6, 3, 0, 16, 8, 4, 2 };

    /* The packet walk of rh0.py's Model._packet: runs of strips ending in MSCALF, each strip
       a header UNPACK (V4-32 rigid, V3-32 skinned) then one V3-16 UNPACK holding its
       vertices. The REF's size has to be used up exactly; anything else is not retail data. */
    private static List<Strip> ParseStripsInRange(byte[] data, int start, int end)
    {
        var strips = new List<Strip>();
        int offset = start;
        int headerWords = 0;              // 4 = rigid (V4-32), 3 = skinned (V3-32), 0 = none yet
        int headerCount = 0, headerVuAddress = 0, headerPrim = 0;
        while (offset < end)
        {
            if (offset + 4 > end)
                throw new InvalidDataException($"packet at 0x{start:x} overruns its REF by {offset + 4 - end}");
            uint vifCode = ReadU32(data, offset);
            int command = (int)((vifCode >> 24) & 0x7F);
            int unpackCount = (int)((vifCode >> 16) & 0xFF);
            if (command < 0x60)            // a REF spans several MSCALF-ended runs
            {
                if (command != 0x00 && command != 0x15)
                    throw new InvalidDataException($"unexpected VIF 0x{vifCode:x8} at 0x{offset:x}");
                offset += 4;
                continue;
            }
            int vectorCount = unpackCount == 0 ? 256 : unpackCount;
            int unpackType = command & 0xF;
            int body = offset + 4;
            offset = body + ((VifUnpackBytesPerVector[unpackType] * vectorCount + 3) & ~3);
            if (offset > end)
                throw new InvalidDataException($"packet at 0x{start:x} overruns its REF by {offset - end}");

            if (unpackType == 8 || unpackType == 12)     // strip header: GIF tag and registers, rigid also the VU bone address
            {
                uint gifTagWord1 = ReadU32(data, body + 4);
                int prim = (int)((gifTagWord1 >> 15) & 0x7FF);   // GIF tag PRIM: 4 = tristrip, 0x40 = alpha
                if (vectorCount != 1 || (gifTagWord1 & 0xF0007FFF) != 0x30004000 || (prim & 7) != 4)
                    throw new InvalidDataException($"strip header 0x{gifTagWord1:x8} at 0x{body:x}");
                headerWords = VifUnpackBytesPerVector[unpackType] / 4;
                headerCount = (int)(ReadU32(data, body) & 0x7FFF);
                headerPrim = prim;
                headerVuAddress = headerWords == 4 ? (int)(ReadU32(data, body + 12) & 0x3FF) : 0;
                continue;
            }
            if (unpackType != 9 || headerWords == 0)
                throw new InvalidDataException($"unexpected UNPACK type {unpackType} at 0x{body:x}");

            var strip = new Strip {
                Type = headerWords == 4 ? "INDEP" : "SHARED",
                VuAddr = headerVuAddress,
                Prim = headerPrim,
            };
            int vectorIndex = 0;
            for (int vertexIndex = 0; vertexIndex < headerCount; vertexIndex++)
            {
                if (vectorIndex + 1 > vectorCount)
                    throw new InvalidDataException($"strip at 0x{body:x} runs past its UNPACK");
                int vertexOffset = body + vectorIndex * 6;
                short textureU = ReadI16(data, vertexOffset), textureV = ReadI16(data, vertexOffset + 2);
                // rigid: the one influence is the header's VU address, at full weight
                int influenceCount = headerWords == 4 ? 1 : ReadI16(data, vertexOffset + 4);
                vectorIndex++;
                if (influenceCount < 0 || vectorIndex + influenceCount * (headerWords == 4 ? 2 : 3) > vectorCount)
                    throw new InvalidDataException($"strip at 0x{body:x} runs past its UNPACK");
                var vertex = new StripVertex { Uv = (textureU, textureV) };
                if (headerWords == 4)
                {
                    int normalOffset = body + vectorIndex * 6, positionOffset = normalOffset + 6;
                    vertex.Normal = (ReadI16(data, normalOffset), ReadI16(data, normalOffset + 2), ReadI16(data, normalOffset + 4));
                    vertex.Position = (ReadI16(data, positionOffset), ReadI16(data, positionOffset + 2), ReadI16(data, positionOffset + 4));
                    vectorIndex += 2;
                }
                else
                {
                    var weights = new List<StripWeight>(influenceCount);
                    int weightSum = 0;
                    for (int j = 0; j < influenceCount; j++)
                    {
                        int influenceOffset = body + vectorIndex * 6, normalOffset = influenceOffset + 6, positionOffset = influenceOffset + 12;
                        int bone = ReadU16(data, influenceOffset);
                        short weight = ReadI16(data, influenceOffset + 2);
                        weights.Add(new StripWeight(bone, weight,
                            (ReadI16(data, positionOffset), ReadI16(data, positionOffset + 2), ReadI16(data, positionOffset + 4)),
                            (ReadI16(data, normalOffset), ReadI16(data, normalOffset + 2), ReadI16(data, normalOffset + 4))));
                        weightSum += weight;
                        vectorIndex += 3;
                    }
                    // each 4.12 weight was rounded on its own: off by at most one per influence
                    if (Math.Abs(weightSum - 4096) > influenceCount)
                        throw new InvalidDataException($"{influenceCount} weights at 0x{body:x} sum to {weightSum}");
                    vertex.Weights = weights;
                }
                strip.Verts.Add(vertex);
            }
            if (vectorIndex != vectorCount)
                throw new InvalidDataException($"strip at 0x{body:x} does not consume its UNPACK ({vectorIndex} of {vectorCount})");
            strips.Add(strip);
        }
        if (offset != end)
            throw new InvalidDataException($"packet at 0x{start:x} overruns its REF by {offset - end}");
        return strips;
    }

    private static BuiltMesh BuildMesh(
        List<Packet> packets,
        Dictionary<int, GameNode> mslotMap,
        Dictionary<int, Matrix4x4> bind,
        List<(int, int)> mainDispatches,
        List<(int, int)> altDispatches,
        List<Pose> samples = null,
        Dictionary<int, int> mslotNode = null)
    {
        var fitter = new BlendFitter(bind, samples, mslotNode);
        var mesh = new BuiltMesh {
            SortedMslots = mslotMap.Keys.OrderBy(slot => slot).ToList(),
        };
        for (int i = 0; i < mesh.SortedMslots.Count; i++)
            mesh.MslotToJoint[mesh.SortedMslots[i]] = i;

        var primFaces = new Dictionary<PrimitiveKey, (List<(int First, int Second, int Third)> Faces, float AlphaW)>();

        // TEX0 persists into the alpha chain: a page with no dispatch of its own draws with
        // the opaque chain's last texture
        int lastMainTex = 0;
        foreach (var packet in packets.Where(page => !page.IsAlt))
        {
            int pageTexture = PageToTexture(packet.WalkOff, mainDispatches);
            if (pageTexture >= 0) lastMainTex = pageTexture;
        }

        foreach (var packet in packets)
        {
            var dispatches = packet.IsAlt ? altDispatches : mainDispatches;
            int textureIndex = PageToTexture(packet.WalkOff, dispatches);
            if (textureIndex < 0) textureIndex = packet.IsAlt ? lastMainTex : 0;
            var roundedQ0 = ((int)Math.Round(packet.ColorQ0.R), (int)Math.Round(packet.ColorQ0.G), (int)Math.Round(packet.ColorQ0.B));
            var roundedQ1 = ((int)Math.Round(packet.ColorQ1.R), (int)Math.Round(packet.ColorQ1.G), (int)Math.Round(packet.ColorQ1.B));
            var roundedQ2 = ((int)Math.Round(packet.ColorQ2.R), (int)Math.Round(packet.ColorQ2.G), (int)Math.Round(packet.ColorQ2.B));
            int alphaKey = (int)Math.Round(packet.AlphaW);

            // The game draws every strip in the chain. Untextured ones (PRIM TME clear: 131
            // retail strips) keep their geometry with a plain-colour material.
            foreach (var strip in packet.Strips)
            {
                int firstVertex = mesh.Vertices.Count;

                if (strip.Type == "INDEP")
                {
                    int vuAddress = strip.VuAddr;
                    int mslot = packet.BoneMap.TryGetValue(vuAddress, out var mappedSlot) ? mappedSlot : 0;
                    var matrix = bind.TryGetValue(mslot, out var slotBind) ? slotBind : Matrix4x4.Identity;
                    int jointIndex = mesh.MslotToJoint.TryGetValue(mslot, out var mappedJoint) ? mappedJoint : -1;
                    foreach (var vertex in strip.Verts)
                    {
                        var position = MatrixMath.Apply(matrix, vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
                        mesh.Vertices.Add(position);
                        mesh.Normals.Add(NormalizeOrZero(Vector3.TransformNormal(
                            new Vector3(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z), matrix)));
                        mesh.Uvs.Add(new Vector2(vertex.Uv.U / 4096f, vertex.Uv.V / 4096f));
                        mesh.VertJoints.Add(jointIndex >= 0
                            ? new List<(int, float)> { (jointIndex, 1.0f) }
                            : new List<(int, float)>());
                        mesh.VertGame.Add(jointIndex >= 0
                            ? new List<StripWeight> { new StripWeight(jointIndex, 4096, vertex.Position, vertex.Normal) }
                            : new List<StripWeight>());
                    }
                }
                else
                {
                    foreach (var vertex in strip.Verts)
                    {
                        float weightedX = 0, weightedY = 0, weightedZ = 0, weightSum = 0;
                        float normalX = 0, normalY = 0, normalZ = 0;
                        var jointWeights = new List<(int Joint, float W)>();
                        var influences = new List<(int Slot, float Weight, Vector3 Position)>();
                        var influenceNormals = new List<Vector3>();
                        var game = new List<StripWeight>();
                        if (vertex.Weights != null)
                        {
                            foreach (var stripWeight in vertex.Weights)
                            {
                                int mslot = packet.BoneMap.TryGetValue(stripWeight.Bone, out var mappedSlot) ? mappedSlot : 0;
                                if (!bind.TryGetValue(mslot, out var bindMatrix))
                                {
                                    game.Add(new StripWeight(-1, stripWeight.Weight, stripWeight.Position, stripWeight.Normal));
                                    continue;
                                }
                                var position = MatrixMath.Apply(bindMatrix, stripWeight.Position.X, stripWeight.Position.Y, stripWeight.Position.Z);
                                float weight = stripWeight.Weight;
                                weightedX += position.X * weight;
                                weightedY += position.Y * weight;
                                weightedZ += position.Z * weight;
                                var boneNormal = Vector3.TransformNormal(
                                    new Vector3(stripWeight.Normal.X, stripWeight.Normal.Y, stripWeight.Normal.Z), bindMatrix);
                                normalX += boneNormal.X * weight;
                                normalY += boneNormal.Y * weight;
                                normalZ += boneNormal.Z * weight;
                                weightSum += weight;
                                int jointIndex = mesh.MslotToJoint.TryGetValue(mslot, out var mappedJoint) ? mappedJoint : -1;
                                if (jointIndex >= 0) jointWeights.Add((jointIndex, weight));
                                game.Add(new StripWeight(jointIndex, stripWeight.Weight, stripWeight.Position, stripWeight.Normal));
                                influences.Add((mslot, weight, new Vector3(stripWeight.Position.X, stripWeight.Position.Y, stripWeight.Position.Z)));
                                influenceNormals.Add(new Vector3(stripWeight.Normal.X, stripWeight.Normal.Y, stripWeight.Normal.Z));
                            }
                        }
                        if (weightSum != 0)
                        {
                            weightedX /= weightSum;
                            weightedY /= weightSum;
                            weightedZ /= weightSum;
                            for (int k = 0; k < jointWeights.Count; k++)
                                jointWeights[k] = (jointWeights[k].Joint, jointWeights[k].W / weightSum);
                            for (int k = 0; k < influences.Count; k++)
                                influences[k] = (influences[k].Slot, influences[k].Weight / weightSum, influences[k].Position);
                        }
                        mesh.Vertices.Add(fitter.Position(influences, new Vector3(weightedX, weightedY, weightedZ)));
                        mesh.Normals.Add(NormalizeOrZero(fitter.Normal(influences, influenceNormals, NormalizeOrZero(new Vector3(normalX, normalY, normalZ)))));
                        mesh.Uvs.Add(new Vector2(vertex.Uv.U / 4096f, vertex.Uv.V / 4096f));
                        mesh.VertJoints.Add(jointWeights);
                        // an influence on a slot with no joint cannot be written back as it was
                        if (game.Any(influence => influence.Bone < 0)) game.Clear();
                        mesh.VertGame.Add(game);
                    }
                }

                int vertexCount = strip.Verts.Count;
                var key = new PrimitiveKey(textureIndex, packet.IsAlt, alphaKey, roundedQ0, roundedQ1, roundedQ2, packet.GsAlpha, packet.GsTest, packet.GsClamp,
                    strip.Prim & (PrimAbe | PrimTme));
                if (!primFaces.TryGetValue(key, out var entry))
                {
                    entry = (new List<(int, int, int)>(), packet.AlphaW);
                    primFaces[key] = entry;
                }
                for (int i = 2; i < vertexCount; i++)
                {
                    int corner0 = firstVertex + i - 2, corner1 = firstVertex + i - 1, corner2 = firstVertex + i;
                    (int X, int Y, int Z) triangle = (i % 2 == 0) ? (corner0, corner1, corner2) : (corner1, corner0, corner2);
                    /* PS2 strips have arbitrary winding (the GS never back-face culls), so
                       flip each triangle whose geometric normal disagrees with the stored
                       vertex normals, for consistent outward facing under glTF back-face
                       culling. No normal (vn == 0) keeps the strip winding. */
                    Vector3 corner0Position = mesh.Vertices[triangle.X], corner1Position = mesh.Vertices[triangle.Y], corner2Position = mesh.Vertices[triangle.Z];
                    Vector3 normalSum = mesh.Normals[triangle.X] + mesh.Normals[triangle.Y] + mesh.Normals[triangle.Z];
                    if (Vector3.Dot(Vector3.Cross(corner1Position - corner0Position, corner2Position - corner0Position), normalSum) < 0f)
                        triangle = (triangle.Y, triangle.X, triangle.Z);
                    entry.Faces.Add((triangle.X, triangle.Y, triangle.Z));
                }
            }
        }

        foreach (var primitiveKey in primFaces.Keys
                     .OrderBy(sortKey => sortKey.TextureIndex).ThenBy(sortKey => sortKey.IsAlt).ThenBy(sortKey => sortKey.Alpha)
                     .ThenBy(sortKey => sortKey.MaterialColor1).ThenBy(sortKey => sortKey.MaterialColor0).ThenBy(sortKey => sortKey.MaterialColor2)
                     .ThenBy(sortKey => sortKey.GsAlpha).ThenBy(sortKey => sortKey.GsTest).ThenBy(sortKey => sortKey.GsClamp).ThenBy(sortKey => sortKey.Prim))
        {
            var entry = primFaces[primitiveKey];
            mesh.Primitives.Add(new Primitive {
                Key = primitiveKey,
                AlphaW = entry.AlphaW,
                Faces = entry.Faces,
            });
        }
        return mesh;
    }

    /* What a node does, by its DFS index and matrix slot. Node 1 is the model centre: every
       draw books coordinate 1 (SzModel_DrawWithFunc_Pipe -> BookingGetAnyCoordinate(1)) and
       stores that node's world position as the centre SzModel_GetCenter returns (Model +0x80),
       and it is the scale / SetRotC pivot of the instance matrix. A node with a matrix slot
       is a skin bone; the rest only carry transforms down to their children. */
    private static string ReadableNodeName(int nodeIndex, int mslot)
    {
        if (nodeIndex == 0)
        {
            return "Root";
        }
        if (nodeIndex == 1)
        {
            return "PivotCamera";
        }
        if (mslot != 0)
        {
            return "Bone";
        }
        return "Group";
    }

    private static byte[] BuildGlb(
        List<GameNode> nodes,
        Dictionary<int, GameNode> mslotMap,
        BuiltMesh built,
        Dictionary<int, Matrix4x4> bind,
        List<DecodedTextureInfo> textures,
        List<bool> texHasAlpha,
        List<AnimSlot> animations,
        float animTransScale,
        int nodeTreeSize,
        Dictionary<int, TexAnimBake> texAnims,
        Dictionary<int, System.Text.Json.Nodes.JsonArray> texAnimRecords)
    {
        /* Ref-bone (particle-effect anchor) index = a node's depth-first visit index, which
           is exactly GameNode.Index (WalkGNodes visits child-before-sibling, root = 0). The
           engine can only anchor an effect to a node whose DFS index is < refBoneNum, where
           refBoneNum = nodeTreeSize >> 6 (lSzModel_LoadCB@0x1e8bf0), nodeTreeSize being
           section 0's size from header +0x14 (64 B per node on every retail model). Each
           node name carries its ref index (_refOOR when out of the usable range), to show in
           Blender which nodes effects can lock onto. Names are descriptive only: the
           importer matches nodes by tree position (it special-cases just "MrgRoot" and the
           "_bone" scale children). The readable word goes first (ReadableNodeName); the _f
           and _m suffixes the importer reads stay at the end. */
        int refBoneCount = nodeTreeSize >> 6;

        /* Node layout (as DotR-PC's tools/gltf.py): one glTF node per RH0 node carrying
           translation and rotation, and for each node with a matrix slot a child `_bone` node
           carrying the accumulated scale. That is the RH0 rule (scale reaches the node's own
           output matrix, never its children) expressed in glTF, where scale would otherwise
           propagate. The `_bone` nodes are the skin's joints and the scale channels' targets.
           A slotless node gets one too when its accumulated scale differs from its parent's
           in any pose: retail animates scale on slotless parents (card 679), and without it
           the importer could only push that scale down into every slot below as extra keys. */
        var rest = EvalPose(nodes, null, 0, animTransScale);
        var baked = animations.OrderBy(animation => animation.SlotId)
            .Select(animation => (Anim: animation, Frames: Enumerable.Range(0, Math.Max(animation.FrameCount, 1))
                .Select(frame => EvalPose(nodes, animation, frame, animTransScale)).ToList()))
            .ToList();
        var ownScale = new bool[nodes.Count];
        foreach (var pose in baked.SelectMany(bakedMotion => bakedMotion.Frames).Prepend(rest))
            for (int i = 1; i < nodes.Count; i++)
                ownScale[i] |= pose.AccumulatedScale[i] != pose.AccumulatedScale[nodes[i].ParentIndex];
        var nodeBuilders = new NodeBuilder[nodes.Count];
        var boneBuilders = new Dictionary<int, NodeBuilder>(); // node index -> its _bone child
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            string refTag = node.Index < refBoneCount ? $"_ref{node.Index}" : "_refOOR";
            var nodeBuilder = new NodeBuilder($"{ReadableNodeName(node.Index, node.MeshSlot)}_node_{node.Index}{refTag}_f{node.Flags:X4}" + (node.MeshSlot != 0 ? $"_m{node.MeshSlot}" : ""));
            nodeBuilder.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
                Vector3.One, rest.LocalR[i], rest.LocalT[i]);
            nodeBuilders[i] = nodeBuilder;
        }
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].MeshSlot != 0 || ownScale[i])
            {
                var bone = nodeBuilders[i].CreateNode($"node_{i}_bone");
                bone.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
                    rest.AccumulatedScale[i], Quaternion.Identity, Vector3.Zero);
                boneBuilders[i] = bone;
            }
            foreach (var childIndex in nodes[i].Children)
                nodeBuilders[i].AddNode(nodeBuilders[childIndex]);
        }

        /* Recenter the whole model on GameNode(1) (the root's first child), the engine's own
           centre-of-model (SzModel_GetCenterOfs = GameNode(1).pos / animTransScale, placed via
           SzModel_SetPos), so the glb sits the way the duel frames the monster. The shift
           rides on the MrgRoot wrapper AND the baked vertices together (a skinned mesh won't
           move from the wrapper alone: the inverse-bind matrices cancel it). GlbImporter
           reads each node's world matrix with the wrapper folded in while node-0's local
           stays put, so the quantised locals are invariant and the round trip is
           byte-identical. A user-added 'origin' empty overrides this pivot on re-import
           (see PrepareRoot). */
        var pivot = nodes.Count > 1 ? nodes[1].RestWorld.Translation : Vector3.Zero;

        var mrgRoot = new NodeBuilder("MrgRoot");
        mrgRoot.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
            Vector3.One, System.Numerics.Quaternion.Identity, -pivot);
        mrgRoot.AddNode(nodeBuilders[0]);

        var materials = new Dictionary<PrimitiveKey, MaterialBuilder>();
        var blendStates = new Dictionary<string, DotrBlendState>(StringComparer.Ordinal);

        MaterialBuilder GetMaterial(PrimitiveKey key, float alphaW)
        {
            int textureIndex = key.TextureIndex;
            if (textureIndex < 0 || textureIndex >= textures.Count) textureIndex = 0;
            key = key with { TextureIndex = textureIndex };
            if (materials.TryGetValue(key, out var existing)) return existing;
            bool textured = (key.Prim & PrimTme) != 0;
            bool alphaBlendEnable = (key.Prim & PrimAbe) != 0;
            bool isAlt = key.IsAlt;
            var q1Key = key.MaterialColor1;
            var (gsAlpha, gsTest, gsClamp) = (key.GsAlpha, key.GsTest, key.GsClamp);
            // An untextured material must not be named mat_tex*: GlbImporter would take the
            // name as a texture slot and fill that slot with its flat colour.
            string materialName = (textured ? $"mat_tex{textureIndex}" : "mat_untextured")
                             + $"_a{key.Alpha}_gs{(byte)gsAlpha:X2}_t{(uint)(gsTest & 0xFFFFF):X5}_c{(byte)gsClamp:X}"
                             + (textured ? "" : $"_rgb{q1Key.R:X2}{q1Key.G:X2}{q1Key.B:X2}")
                             // the colour block, so pages on one texture with different c0/c1/c2
                             // stay separate materials (DOTR_blend extras are looked up by name)
                             + $"_k{key.MaterialColor0.R:X2}{key.MaterialColor0.G:X2}{key.MaterialColor0.B:X2}{q1Key.R:X2}{q1Key.G:X2}{q1Key.B:X2}{key.MaterialColor2.R:X2}{key.MaterialColor2.G:X2}{key.MaterialColor2.B:X2}"
                             + (isAlt ? "_alt" : "");
            var material = new MaterialBuilder(materialName)
                .WithDoubleSide(true)
                .WithMetallicRoughnessShader()
                .WithMetallicRoughness(0f, 1f);
            var texture = textures[textureIndex];
            if (textured)
            {
                var pngBytes = PngEncoder.Encode(texture.Width, texture.Height, texture.Rgba);
                material.WithChannelImage(KnownChannel.BaseColor, new MemoryImage(pngBytes));

                /* Reproduce the GS sampler. glTF defaults to REPEAT on both axes, which makes
                   bilinear filtering at U=0 blend in the opposite edge of the texture (the
                   atlas-seam line on the world-map arena bases); the game clamps those axes
                   via CLAMP_1. Retail TEX1_1 is always 0x60 (bilinear, no mipmaps), so also
                   keep viewers from generating mips that smear neighbouring atlas regions. */
                TextureWrapMode wrapS = TextureWrapMode.REPEAT;
                TextureWrapMode wrapT = TextureWrapMode.REPEAT;
                if ((gsClamp & 0x3UL) == 1UL)
                {
                    wrapS = TextureWrapMode.CLAMP_TO_EDGE;
                }
                if (((gsClamp >> 2) & 0x3UL) == 1UL)
                {
                    wrapT = TextureWrapMode.CLAMP_TO_EDGE;
                }
                TextureBuilder textureBuilder = material.GetChannel(KnownChannel.BaseColor).Texture;
                textureBuilder.WithSampler(wrapS, wrapT, TextureMipMapFilter.LINEAR, TextureInterpolationFilter.LINEAR);
            }

            /* Opaque chain: lit by VU1 microcode from c0/c1/c2, which is not decoded (and
               scaled by SzModel_SetIntensity's default 0.8 first). The +0x20 row (c1) is
               exported at full magnitude (q1Key/255), not max-normalised: retail neutral is
               178, and a normalised value would re-import as over-bright 255. This is an
               approximation; the raw rows are kept in the extras. */
            float red = q1Key.R / 255f;
            float green = q1Key.G / 255f;
            float blue = q1Key.B / 255f;
            bool clamped = false;
            if (isAlt)
            {
                /* Alpha chain: drawn unlit by the CPU (RhModelCpu_DrawAlpha @0x212FD0), one
                   RGBA per colour block from lRhModelCpu_TraceDynamicBlock @0x2131D0:
                   RGB = c0 + c1 / 2, A = c1.w, which the GS multiplies with the texture
                   (0x80 = 1.0). Typical 127 + 178/2 = 216 is ~1.7x the texture; glTF can't
                   brighten past 1, so it is clamped (noted in the extras). */
                var q0Colour = key.MaterialColor0;
                float LitComponent(int q0Component, int q1Component)
                {
                    float litValue = (q0Component + q1Component / 2f) / Ps2FullAlpha;
                    clamped |= litValue > 1f;
                    return Math.Min(litValue, 1f);
                }
                red = LitComponent(q0Colour.R, q1Key.R);
                green = LitComponent(q0Colour.G, q1Key.G);
                blue = LitComponent(q0Colour.B, q1Key.B);
            }

            bool textureHasAlpha = textured && textureIndex < texHasAlpha.Count && texHasAlpha[textureIndex];

            // PS2 colour alpha uses 0x80 as 1.0; the GS texture function multiplies it by
            // texture alpha before the ALPHA equation consumes As.
            float alpha = Math.Clamp(alphaW / Ps2FullAlpha, 0f, 1f);

            string blendMode = ClassifyBlendMode(gsAlpha, gsTest, alpha, alphaBlendEnable, textureHasAlpha);
            float alphaCutoff = GetAlphaCutoff(gsTest);

            switch (blendMode)
            {
                case "additive":
                case "subtract":
                case "alpha":
                    material.WithAlpha(AlphaMode.BLEND);
                    break;
                case "mask":
                    material.WithAlpha(AlphaMode.MASK, alphaCutoff);
                    break;
                default:
                    /* Always state OPAQUE explicitly: every embedded texture is RGBA, and
                       inference could place a fully opaque model in the transparent
                       no-depth-write pass. */
                    material.WithAlpha(AlphaMode.OPAQUE);
                    alpha = 1f;
                    break;
            }

            /* The +0x20 colour normally behaves like a material-lighting factor, but some
               retail models use (0,0,0) as a sentinel for the unmodulated texture colour
               (booKoo's book page). Multiplying the glTF base colour by black would make
               those textures disappear, so the original value stays in the DOTR metadata and
               renders as neutral. */
            bool isZeroColourSentinel = !isAlt &&
                q1Key.R == 0 && q1Key.G == 0 && q1Key.B == 0;

            /* Omit the exact retail-neutral grey, and the zero sentinel, for a fully opaque
               textured material: an omitted glTF base-colour factor is white and leaves the
               texture unchanged. Transparent/additive materials still need their alpha
               factor, so a zero sentinel there uses white RGB. */
            bool omitBaseColour = textured && blendMode == "opaque" &&
                                  ((!isAlt && q1Key.R == 178 && q1Key.G == 178 && q1Key.B == 178) ||
                                   isZeroColourSentinel);
            var display = isZeroColourSentinel ? new Vector3(1f) : new Vector3(red, green, blue);
            if (!omitBaseColour)
                material.WithBaseColor(new Vector4(display, alpha));

            blendStates[materialName] = new DotrBlendState(
                blendMode, alphaCutoff, alpha, gsAlpha, gsTest,
                q1Key.R, q1Key.G, q1Key.B, key.MaterialColor0, key.MaterialColor2, alphaW, alphaBlendEnable, textured,
                omitBaseColour ? Vector3.One : display, clamped);
            materials[key] = material;
            return material;
        }

        /* The skin's joints are the `_bone` nodes, in slot order. Inverse bind = the bind
           matrix BuildMesh placed the vertices with (rest pose, or the frame BindMatrices
           picked for a part shrunk at rest), shifted by the MrgRoot pivot like the vertices. */
        var joints = built.SortedMslots
            .Select(slot => boneBuilders[mslotMap[slot].Index])
            .ToArray();
        var pivotShift = MatrixMath.Translation(-pivot.X, -pivot.Y, -pivot.Z);
        var inverseBinds = built.SortedMslots
            .Select(slot =>
            {
                Matrix4x4.Invert(bind[slot] * pivotShift, out var inverse);
                return inverse;
            })
            .ToArray();

        /* Rigid mode: when no vertex blends more than one bone (every MODEL.MRG model, and any
           monster made only of INDEP strips), a skin adds nothing: each bone's geometry just
           rides its node. Emit one plain mesh per bone, attached to that bone's `_bone` node,
           with vertices in its bind space (the inverse bind matrix above), so a part at scale 0
           in the rest pose still has real geometry. This is exactly the skinned result (skin
           matrix = jointWorld * inverseBind), minus the armature. GlbImporter recognises this
           shape (extracted, no skin, `_m<slot>` node names) and rebuilds the same joint set. */
        bool rigid = built.VertJoints.All(jointWeights => jointWeights.Count(influence => influence.W > 0f) <= 1);
        // glTF takes influences in sets of four; a vertex carries up to 8
        int maxInfluences = built.VertJoints.Count == 0 ? 0 : built.VertJoints.Max(jointWeights => jointWeights.Count(influence => influence.W > 0f));

        var skinnedPrimitives = new List<(MaterialBuilder Mat, Primitive Prim)>();
        var rigidMeshes = new Dictionary<int, MeshBuilder<VertexPositionNormal, VertexTexture1>>();
        foreach (var primitive in built.Primitives)
        {
            if (primitive.Key.IsAlt && primitive.AlphaW == 0f) continue;
            var material = GetMaterial(primitive.Key, primitive.AlphaW);
            if (!rigid)
            {
                skinnedPrimitives.Add((material, primitive));
                continue;
            }
            foreach (var (corner0, corner1, corner2) in primitive.Faces)
            {
                // a strip binds one bone, so all three corners share it
                int jointIndex = RigidJoint(built, corner0);
                if (!rigidMeshes.TryGetValue(jointIndex, out var meshBuilder))
                {
                    meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1>($"mesh_m{built.SortedMslots[jointIndex]}");
                    rigidMeshes[jointIndex] = meshBuilder;
                }
                var inverseWorld = inverseBinds[jointIndex];
                meshBuilder.UsePrimitive(material).AddTriangle(
                    MakeRigidVertex(built, corner0, pivot, inverseWorld),
                    MakeRigidVertex(built, corner1, pivot, inverseWorld),
                    MakeRigidVertex(built, corner2, pivot, inverseWorld));
            }
        }

        WriteAnimations(nodes, baked, rest, nodeBuilders, boneBuilders);

        var scene = new SceneBuilder("MrgScene");
        scene.AddNode(mrgRoot);
        if (rigid)
        {
            foreach (var (jointIndex, meshBuilder) in rigidMeshes.OrderBy(entry => entry.Key))
                scene.AddRigidMesh(meshBuilder, joints[jointIndex]);
        }
        else if (skinnedPrimitives.Any(skinned => skinned.Prim.Faces.Count > 0))
        {
            var skin = joints.Zip(inverseBinds, (joint, inverseBind) => (joint, inverseBind)).ToArray();
            // the game's influences take 4 slots unless some vertex has more (zero weights count)
            bool gameNeedsEightSlots = built.VertGame.Count > 0 && built.VertGame.Max(influences => influences.Count) > 4;
            if (maxInfluences > 4)
                AddSkinnedMesh<VertexJoints8>(scene, built, skinnedPrimitives, pivot, skin, 8, gameNeedsEightSlots);
            else
                AddSkinnedMesh<VertexJoints4>(scene, built, skinnedPrimitives, pivot, skin, 4, gameNeedsEightSlots);
        }

        // one buffer view per attribute: the game's influences (VertexTexture1Game) make an
        // interleaved vertex wider than glTF's 252-byte stride
        var settings = SceneBuilderSchema2Settings.Default;
        settings.UseStridedBuffers = false;
        var model = scene.ToGltf2(settings);
        InjectBlendMetadata(model, blendStates);
        if (texAnims.Count > 0) InjectTextureAnimations(model, texAnims);
        InjectTextureAnimationRecords(model, texAnimRecords);
        return ReorderGlbAnimations(model.WriteGLB().ToArray());
    }

    private static void AddSkinnedMesh<TJoints>(SceneBuilder scene, BuiltMesh built,
        List<(MaterialBuilder Mat, Primitive Prim)> primitives, Vector3 pivot,
        (NodeBuilder Joint, Matrix4x4 InverseBind)[] joints, int maxInfluences, bool gameNeedsEightSlots)
        where TJoints : struct, IVertexSkinning
    {
        if (gameNeedsEightSlots)
            AddSkinnedMesh<TJoints, GameSlots8>(scene, built, primitives, pivot, joints, maxInfluences);
        else
            AddSkinnedMesh<TJoints, GameSlots4>(scene, built, primitives, pivot, joints, maxInfluences);
    }

    private static void AddSkinnedMesh<TJoints, TGameSlots>(SceneBuilder scene, BuiltMesh built,
        List<(MaterialBuilder Mat, Primitive Prim)> primitives, Vector3 pivot,
        (NodeBuilder Joint, Matrix4x4 InverseBind)[] joints, int maxInfluences)
        where TJoints : struct, IVertexSkinning
        where TGameSlots : IGameSlots
    {
        var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1Game<TGameSlots>, TJoints>("MrgMesh");
        foreach (var (material, primitive) in primitives)
        {
            var primitiveBuilder = meshBuilder.UsePrimitive(material);
            foreach (var (corner0, corner1, corner2) in primitive.Faces)
                primitiveBuilder.AddTriangle(
                    MakeVertex<TJoints, TGameSlots>(built, corner0, pivot, maxInfluences, joints),
                    MakeVertex<TJoints, TGameSlots>(built, corner1, pivot, maxInfluences, joints),
                    MakeVertex<TJoints, TGameSlots>(built, corner2, pivot, maxInfluences, joints));
        }
        scene.AddSkinnedMesh(meshBuilder, joints);
    }

    /* Every motion baked at every frame 0..frames-1 with the game's own evaluator (EvalPose),
       as DotR-PC's tools/gltf.py does. Writing the game's keys as glTF keys would be wrong
       twice over: glTF slerps quaternions where the game lerps each Euler axis the short way
       round, and a list with fewer than 3 keys plays key 0 only. Translation and rotation go
       on the node, accumulated scale on its `_bone` child. A channel is written in every
       motion for each node that moves in any of them, so switching animations never leaves a
       node in another motion's pose. */
    private static void WriteAnimations(List<GameNode> nodes, List<(AnimSlot Anim, List<Pose> Frames)> baked,
        Pose rest, NodeBuilder[] nodeBuilders, Dictionary<int, NodeBuilder> boneBuilders)
    {
        const float FramesPerSecond = 30f;

        float size = 1e-6f;
        foreach (var node in nodes)
            size = Math.Max(size, rest.World[node.Index].Translation.Length());
        float translationEpsilon = size * 1e-6f;
        float scaleEpsilon = 1e-6f * Math.Max(rest.AccumulatedScale[0].Length(), 1e-6f);

        bool[] movesTranslation = new bool[nodes.Count], movesRotation = new bool[nodes.Count], movesScale = new bool[nodes.Count];
        foreach (var (_, frames) in baked)
            foreach (var pose in frames)
                for (int i = 0; i < nodes.Count; i++)
                {
                    movesTranslation[i] |= Vector3.Distance(pose.LocalT[i], rest.LocalT[i]) > translationEpsilon;
                    movesRotation[i] |= 1f - MathF.Abs(Quaternion.Dot(pose.LocalR[i], rest.LocalR[i])) > 1e-7f;
                    movesScale[i] |= Vector3.Distance(pose.AccumulatedScale[i], rest.AccumulatedScale[i]) > scaleEpsilon;
                }

        foreach (var (anim, frames) in baked)
        {
            string animName = $"slot_{anim.SlotId}";
            for (int i = 0; i < nodes.Count; i++)
            {
                if (movesTranslation[i])
                {
                    var curve = nodeBuilders[i].UseTranslation(animName);
                    for (int frame = 0; frame < frames.Count; frame++)
                        curve.SetPoint(frame / FramesPerSecond, frames[frame].LocalT[i]);
                }
                if (movesRotation[i])
                {
                    var curve = nodeBuilders[i].UseRotation(animName);
                    var previous = rest.LocalR[i];
                    for (int frame = 0; frame < frames.Count; frame++)
                    {
                        // keep consecutive keys on one hemisphere so LINEAR slerps the short way
                        var rotation = frames[frame].LocalR[i];
                        if (Quaternion.Dot(rotation, previous) < 0f) rotation = Quaternion.Negate(rotation);
                        curve.SetPoint(frame / FramesPerSecond, rotation);
                        previous = rotation;
                    }
                }
                if (movesScale[i] && boneBuilders.TryGetValue(i, out var bone))
                {
                    var curve = bone.UseScale(animName);
                    for (int frame = 0; frame < frames.Count; frame++)
                        curve.SetPoint(frame / FramesPerSecond, frames[frame].AccumulatedScale[i]);
                }
            }
        }
    }

    /* Blending is the strip's GIF PRIM ABE bit, not the ALPHA register: ABE is set on exactly
       the 8,176 alpha-chain monster strips and on none of the 510,849 opaque ones, while twelve
       opaque blocks carry an additive ALPHA value to no effect. The equation is the ALPHA
       register, Cv = (A - B) * C / 128 + D; the alpha comes from c1.w, not the textures
       (card 682's all decode fully opaque). */
    private static string ClassifyBlendMode(ulong gsAlpha, ulong gsTest, float materialAlpha, bool alphaBlendEnable,
        bool textureHasAlpha)
    {
        int alphaA = (int)(gsAlpha & 0x3);
        int alphaB = (int)((gsAlpha >> 2) & 0x3);
        int alphaC = (int)((gsAlpha >> 4) & 0x3);
        int alphaD = (int)((gsAlpha >> 6) & 0x3);
        int fixedAlpha = (int)((gsAlpha >> 32) & 0xFF);

        bool alphaTest = (gsTest & 1UL) != 0;
        int alphaTestMethod = (int)((gsTest >> 1) & 0x7);
        int alphaReference = (int)((gsTest >> 4) & 0xFF);

        /* A test that only rejects As == 0 (GREATER 0, or ALWAYS) changes nothing once
           blending is scaling by As, so with a sub-1 material alpha it is just a "skip
           empty texels" guard on a blended page, not a cutout (forest foliage: TEST
           0x7000D, ALPHA 0x48, material alpha 0x26). */
        bool trivialTest = (alphaTestMethod == 6 && alphaReference == 0) || alphaTestMethod == 1;

        /* Alpha-tested pages are cutouts first (TEST 0x703FD: ATE, GREATER, AREF 63), even
           when they also blend (the Bagworm flipbook). Retail's alpha chain never has
           material alpha 1; an imported additive page does, with the same trivial test, and
           is still a blend, so ABE alone also rules the cutout out. */
        if (alphaTest && textureHasAlpha && !(trivialTest && (materialAlpha < 0.999f || alphaBlendEnable))) return "mask";
        if (!alphaBlendEnable) return "opaque";

        bool sourceAlphaAdditive = alphaA == 0 && alphaB == 2 && alphaC == 0 && alphaD == 1; // 0x48
        bool fixedAdditive = alphaA == 0 && alphaB == 2 && alphaC == 2 && alphaD == 1 && fixedAlpha > 0;
        bool subtract = alphaA == 2 && alphaB == 0 && alphaC == 0 && alphaD == 1;           // 0x42
        if (sourceAlphaAdditive || fixedAdditive) return "additive";
        if (subtract) return "subtract";
        return "alpha";                                                            // 0x44
    }

    private static float GetAlphaCutoff(ulong gsTest)
    {
        if ((gsTest & 1UL) == 0) return 0.5f;
        int alphaTestMethod = (int)((gsTest >> 1) & 0x7);
        int alphaRef = (int)((gsTest >> 4) & 0xFF);
        // glTF MASK passes alpha >= cutoff; GS GREATER is strict, so step past AREF
        // (otherwise AREF 0 becomes cutoff 0 and every texel renders opaque).
        if (alphaTestMethod == 6) alphaRef++;
        return Math.Clamp(alphaRef / Ps2FullAlpha, 0f, 1f);
    }

    private static void InjectBlendMetadata(ModelRoot model,
        IReadOnlyDictionary<string, DotrBlendState> blendStates)
    {
        foreach (var material in model.LogicalMaterials)
        {
            if (material.Name == null || !blendStates.TryGetValue(material.Name, out var state)) continue;

            var extras = material.Extras as System.Text.Json.Nodes.JsonObject
                         ?? new System.Text.Json.Nodes.JsonObject();
            extras["DOTR_blend"] = new System.Text.Json.Nodes.JsonObject {
                ["mode"] = state.Mode,
                ["alphaCutoff"] = state.AlphaCutoff,
                ["materialAlpha"] = state.MaterialAlpha,
                ["gsAlpha"] = $"0x{state.GsAlpha:X16}",
                ["gsTest"] = $"0x{state.GsTest:X16}",
                ["materialColorR"] = state.MaterialColorR,
                ["materialColorG"] = state.MaterialColorG,
                ["materialColorB"] = state.MaterialColorB,
                ["materialColor0"] = new System.Text.Json.Nodes.JsonArray(state.Color0.R, state.Color0.G, state.Color0.B),
                ["materialColor2"] = new System.Text.Json.Nodes.JsonArray(state.Color2.R, state.Color2.G, state.Color2.B),
                // c1.w as stored (0x80 = 1); an opaque material's glb alpha is always 1
                ["materialAlphaW"] = state.AlphaW,
                ["primAbe"] = state.AlphaBlendEnable,
                ["textured"] = state.Textured,
                // the base colour factor written for this material; GlbImporter restores the
                // raw +0x20 row when the factor is still this
                ["displayColor"] = new System.Text.Json.Nodes.JsonArray(state.Display.X, state.Display.Y, state.Display.Z),
                ["displayColorClamped"] = state.DisplayClamped,
            };
            material.Extras = extras;
        }
    }

    /* Bakes the texture-animation frames into the glTF as extra images and tags each
       affected material with a "DOTR_texAnim" extras block (frame image indices and
       timestamps). The custom viewer swaps the bound texture over time; core glTF can't
       animate base-color images, so this is carried as extras, not a standard channel. */
    private static void InjectTextureAnimations(ModelRoot model, Dictionary<int, TexAnimBake> texAnims)
    {
        foreach (var (textureIndex, bake) in texAnims)
        {
            if (bake.FramesRgba.Count < 2) continue;

            var imageIndices = new int[bake.FramesRgba.Count];
            for (int frame = 0; frame < bake.FramesRgba.Count; frame++)
            {
                var pngBytes = PngEncoder.Encode(bake.Width, bake.Height, bake.FramesRgba[frame]);
                var image = model.CreateImage();
                image.Content = new MemoryImage(pngBytes);
                /* Named so a modder can find the frames in Blender and GlbImporter can route
                   them back (the flipbook path's own "tex{N}_anim{frame}" naming; a slot that
                   also carries DOTR_texAnimRecords takes the write-back path in
                   GlbImporter.ApplyEditedAnimFrames instead of being repacked as a sheet). */
                image.Name = $"tex{textureIndex}_anim{frame}";
                imageIndices[frame] = image.LogicalIndex;
            }

            var frameJson = new System.Text.StringBuilder();
            frameJson.Append('[');
            for (int frame = 0; frame < imageIndices.Length; frame++)
            {
                if (frame > 0) frameJson.Append(',');
                float time = frame < bake.FrameTimes.Count ? bake.FrameTimes[frame] : 0f;
                frameJson.Append("{\"image\":").Append(imageIndices[frame])
                    .Append(",\"t\":").Append(time.ToString(System.Globalization.CultureInfo.InvariantCulture));
                /* The GS copies this frame is made of: which record, which cell of its sheet,
                   and a hash of the pixels as extracted. GlbImporter compares the hash with
                   the frame image it is given: unchanged means the base image still owns those
                   pixels (an edited base cell survives), changed means the modder painted the
                   frame, and the new pixels are written back into the record's source cell. */
                frameJson.Append(",\"stamps\":[");
                var stamps = frame < bake.FrameStamps.Count ? bake.FrameStamps[frame] : new List<TexAnimStamp>();
                for (int k = 0; k < stamps.Count; k++)
                {
                    if (k > 0) frameJson.Append(',');
                    var stamp = stamps[k];
                    frameJson.Append("{\"rec\":").Append(stamp.RecordIndex)
                        .Append(",\"cell\":").Append(stamp.CellIndex)
                        .Append(",\"hash\":").Append(RectHash(bake.FramesRgba[frame], bake.Width, bake.Height,
                            stamp.DstX, stamp.DstY, stamp.Width, stamp.Height))
                        .Append('}');
                }
                frameJson.Append("]}");
            }
            frameJson.Append(']');

            string duration = bake.Duration.ToString(System.Globalization.CultureInfo.InvariantCulture);

            foreach (var material in model.LogicalMaterials)
            {
                if (!MaterialUsesTexture(material.Name, textureIndex)) continue;

                string animJson = "{\"duration\":" + duration +
                                  ",\"frames\":" + frameJson + "}";

                var extras = material.Extras as System.Text.Json.Nodes.JsonObject
                             ?? new System.Text.Json.Nodes.JsonObject();
                extras["DOTR_texAnim"] = System.Text.Json.Nodes.JsonNode.Parse(animJson);
                material.Extras = extras;
            }
        }
    }

    /* The raw records (SzModel_LoadTexAnim_Pipe @0x1EA410), 24 B each, the last with bit 0
       set. mode: 0/1 loop, 2/3 ping-pong (0 and 2 treat cell 0 as "no copy"), 4 window
       p0 <= f <= p1, 5 one shot at p0 for p1 ticks, 6 blink every p0 for p1 ticks.
       motions = per-slot mask; ownClock: runs on its own counter (+0x14), not the motion frame. */
    private static System.Text.Json.Nodes.JsonArray TexAnimRecords(byte[] data, int recordOffset)
    {
        var records = new System.Text.Json.Nodes.JsonArray();
        for (int recordIndex = 0; recordIndex < 256 && recordOffset + 24 <= data.Length; recordIndex++, recordOffset += 24)
        {
            byte flags = data[recordOffset];
            records.Add(new System.Text.Json.Nodes.JsonObject {
                ["mode"] = (flags >> 2) & 7,
                ["direction"] = flags >> 5,
                ["ownClock"] = (flags & 2) != 0,
                ["motions"] = data[recordOffset + 1],
                ["cols"] = data[recordOffset + 2] & 15,
                ["rows"] = data[recordOffset + 2] >> 4,
                ["state"] = data[recordOffset + 3],
                ["p0"] = ReadU16(data, recordOffset + 4),
                ["p1"] = ReadU16(data, recordOffset + 6),
                ["size"] = new System.Text.Json.Nodes.JsonArray(ReadU16(data, recordOffset + 8), ReadU16(data, recordOffset + 10)),
                ["src"] = new System.Text.Json.Nodes.JsonArray(ReadU16(data, recordOffset + 12), ReadU16(data, recordOffset + 14)),
                ["dst"] = new System.Text.Json.Nodes.JsonArray(ReadU16(data, recordOffset + 16), ReadU16(data, recordOffset + 18)),
            });
            // starting own clock (a phased blink); written only when set
            uint clock = ReadU32(data, recordOffset + 20);
            if (clock != 0) ((System.Text.Json.Nodes.JsonObject)records[^1]!)["clock"] = clock;
            if ((flags & 1) != 0) break;
        }
        return records;
    }

    // FNV-1a over one rectangle of an RGBA image, clipped like TexAnim.Stamp. The pair
    // with GlbImporter.RectHash has to stay byte-for-byte the same walk.
    internal static uint RectHash(byte[] rgba, int width, int height, int rectX, int rectY, int rectWidth, int rectHeight)
    {
        uint hash = 2166136261u;
        for (int y = 0; y < rectHeight; y++)
        {
            int pixelY = rectY + y;
            if ((uint)pixelY >= (uint)height) continue;
            for (int x = 0; x < rectWidth; x++)
            {
                int pixelX = rectX + x;
                if ((uint)pixelX >= (uint)width) continue;
                int pixelOffset = (pixelY * width + pixelX) * 4;
                for (int channel = 0; channel < 4; channel++) hash = (hash ^ rgba[pixelOffset + channel]) * 16777619u;
            }
        }
        return hash;
    }

    private static void InjectTextureAnimationRecords(ModelRoot model,
        Dictionary<int, System.Text.Json.Nodes.JsonArray> records)
    {
        foreach (var (textureIndex, textureRecords) in records)
            foreach (var material in model.LogicalMaterials)
            {
                if (!MaterialUsesTexture(material.Name, textureIndex)) continue;
                var extras = material.Extras as System.Text.Json.Nodes.JsonObject
                             ?? new System.Text.Json.Nodes.JsonObject();
                extras["DOTR_texAnimRecords"] = textureRecords.DeepClone();
                material.Extras = extras;
            }
    }

    private static bool MaterialIsAlt(string? name) =>
        name?.EndsWith("_alt", StringComparison.OrdinalIgnoreCase) == true;

    // Materials are named "mat_tex{idx}" optionally with "_alt"/suffixes; match
    // on the leading integer so tex1 doesn't also catch tex10.
    private static bool MaterialUsesTexture(string? name, int textureIndex)
    {
        const string prefix = "mat_tex";
        if (name == null || !name.StartsWith(prefix)) return false;
        int cursor = prefix.Length, parsedIndex = 0;
        if (cursor >= name.Length || !char.IsDigit(name[cursor])) return false;
        while (cursor < name.Length && char.IsDigit(name[cursor]))
        {
            parsedIndex = parsedIndex * 10 + (name[cursor] - '0');
            cursor++;
        }
        return parsedIndex == textureIndex;
    }

    private static byte[] ReorderGlbAnimations(byte[] glb)
    {
        if (glb.Length < 20) return glb;
        if (ReadU32(glb, 0) != 0x46546C67) return glb;
        uint jsonLength = ReadU32(glb, 12);
        uint jsonType = ReadU32(glb, 16);
        if (jsonType != 0x4E4F534A) return glb;
        int jsonStart = 20;
        int jsonEnd = jsonStart + (int)jsonLength;
        if (jsonEnd > glb.Length) return glb;
        string json = System.Text.Encoding.UTF8.GetString(glb, jsonStart, (int)jsonLength);

        var document = System.Text.Json.Nodes.JsonNode.Parse(json);
        if (document?["animations"] is not System.Text.Json.Nodes.JsonArray animations || animations.Count < 2) return glb;

        var items = new List<System.Text.Json.Nodes.JsonNode?>();
        foreach (var animation in animations) items.Add(animation);
        animations.Clear();

        int SlotKey(System.Text.Json.Nodes.JsonNode? animation)
        {
            string? name = animation?["name"]?.GetValue<string>();
            if (name != null && name.StartsWith("slot_") &&
                int.TryParse(name.AsSpan(5), out int slot)) return slot;
            return int.MaxValue;
        }

        foreach (var animation in items.OrderBy(SlotKey))
            animations.Add(animation);

        string newJson = document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        var newJsonBytes = System.Text.Encoding.UTF8.GetBytes(newJson);
        int padding = (4 - (newJsonBytes.Length & 3)) & 3;
        int paddedLength = newJsonBytes.Length + padding;

        byte[] binChunk = Array.Empty<byte>();
        if (jsonEnd + 8 <= glb.Length)
        {
            uint binLength = ReadU32(glb, jsonEnd);
            uint binType = ReadU32(glb, jsonEnd + 4);
            if (binType == 0x004E4942 && jsonEnd + 8 + (int)binLength <= glb.Length)
            {
                binChunk = new byte[8 + (int)binLength];
                Array.Copy(glb, jsonEnd, binChunk, 0, binChunk.Length);
            }
        }

        int totalLength = 12 + 8 + paddedLength + binChunk.Length;
        var stream = new MemoryStream(totalLength);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u);
        writer.Write(2u);
        writer.Write((uint)totalLength);
        writer.Write((uint)paddedLength);
        writer.Write(0x4E4F534Au);
        writer.Write(newJsonBytes);
        for (int i = 0; i < padding; i++) writer.Write((byte)0x20);
        if (binChunk.Length > 0) writer.Write(binChunk);
        writer.Flush();
        return stream.ToArray();
    }

    // Normalize with a zero guard (Vector3.Normalize(0) yields NaN). Used only for
    // winding orientation, so direction matters, magnitude does not.
    private static Vector3 NormalizeOrZero(Vector3 vector)
    {
        float length = vector.Length();
        return length > 1e-6f ? vector / length : Vector3.Zero;
    }

    /* Influences heaviest first, weights normalised across every set together, so a viewer
       that reads only JOINTS_0 degrades to the four heaviest. The game's own influences ride
       along (VertexTexture1Game). */
    private static (VertexPositionNormal, VertexTexture1Game<TGameSlots>, TJoints) MakeVertex<TJoints, TGameSlots>(BuiltMesh mesh, int vertexIndex, Vector3 pivot,
        int maxInfluences, (NodeBuilder Joint, Matrix4x4 InverseBind)[] joints)
        where TJoints : struct, IVertexSkinning where TGameSlots : IGameSlots
    {
        var position = mesh.Vertices[vertexIndex] - pivot;
        var normal = mesh.Normals[vertexIndex];
        if (normal == Vector3.Zero) normal = Vector3.UnitY; // glTF requires a unit normal
        var uv = mesh.Uvs[vertexIndex];
        var heaviest = mesh.VertJoints[vertexIndex].Where(influence => influence.W > 0f).OrderByDescending(influence => influence.W).Take(maxInfluences).ToList();
        float weightSum = heaviest.Sum(influence => influence.W);
        if (heaviest.Count == 0 || weightSum <= 0f)
            heaviest = new List<(int Joint, float W)> { (0, 1f) };
        else
            for (int k = 0; k < heaviest.Count; k++) heaviest[k] = (heaviest[k].Joint, heaviest[k].W / weightSum);
        var bindings = new TJoints();
        bindings.SetBindings(heaviest.Select(influence => (influence.Joint, influence.W)).ToArray());
        var customVertex = new VertexTexture1Game<TGameSlots>(uv);
        var game = mesh.VertGame[vertexIndex];
        if (game.Count > 0 && game.Count <= TGameSlots.Count)
        {
            customVertex.RefPos = Vector3.Transform(position, joints[game[0].Bone].InverseBind);
            for (int k = 0; k < game.Count; k++)
            {
                var gameInfluence = game[k];
                customVertex.Position[k] = new Vector3(gameInfluence.Position.X, gameInfluence.Position.Y, gameInfluence.Position.Z);
                customVertex.Normal[k] = new Vector3(gameInfluence.Normal.X, gameInfluence.Normal.Y, gameInfluence.Normal.Z);
                customVertex.JointWeight[k] = new Vector2(gameInfluence.Bone, gameInfluence.Weight);
            }
        }
        return (new VertexPositionNormal(position, normal), customVertex, bindings);
    }

    // The single bone a rigid vertex rides; an unbound vertex falls back to joint 0, as
    // MakeVertex does.
    private static int RigidJoint(BuiltMesh mesh, int vertexIndex)
    {
        foreach (var (joint, weight) in mesh.VertJoints[vertexIndex])
            if (weight > 0f) return joint;
        return 0;
    }

    private static (VertexPositionNormal, VertexTexture1) MakeRigidVertex(BuiltMesh mesh, int vertexIndex, Vector3 pivot,
        Matrix4x4 inverseNodeWorld)
    {
        var position = Vector3.Transform(mesh.Vertices[vertexIndex] - pivot, inverseNodeWorld);
        var normal = mesh.Normals[vertexIndex];
        normal = normal == Vector3.Zero ? Vector3.UnitY : NormalizeOrZero(Vector3.TransformNormal(normal, inverseNodeWorld));
        return (new VertexPositionNormal(position, normal), new VertexTexture1(mesh.Uvs[vertexIndex]));
    }
}

/* The skinned mesh's material vertex: TEXCOORD_0 plus each influence as RH0 stores it, in the
   game's order with zero weights kept: _DOTR_INFPOS_k / _DOTR_INFNRM_k its own bone-local s16
   position and normal, _DOTR_INFJW_k (joint index, raw 4.12 weight), joint -1 past the last;
   floats, which hold s16 values exactly (the toolkit cannot write unnormalised shorts).
   A glb vertex has one position, so without these an import derives every influence from it
   (the copies then all agree and zero weights are lost). _DOTR_REFPOS is POSITION in the first
   influence's bone space: GlbImporter keeps the influences only while POSITION still matches
   it, so a position edited elsewhere is imported from POSITION.
   A mesh writes 4 slots (GameSlots4) when no vertex has more influences, else 8; GlbImporter
   reads slots 0.. until the first absent one. */
static class VertexTexture1Game
{
    public const int Slots = 8;
    public const string RefPosName = "_DOTR_REFPOS";
    public const string PosName = "_DOTR_INFPOS_";
    public const string NrmName = "_DOTR_INFNRM_";
    public const string JointWeightName = "_DOTR_INFJW_";
}

interface IGameSlots { static abstract int Count { get; } }
struct GameSlots4 : IGameSlots { public static int Count => 4; }
struct GameSlots8 : IGameSlots { public static int Count => VertexTexture1Game.Slots; }

struct VertexTexture1Game<TGameSlots> : IVertexCustom, IEquatable<VertexTexture1Game<TGameSlots>> where TGameSlots : IGameSlots
{
    static readonly int Slots = TGameSlots.Count;
    const string RefPosName = VertexTexture1Game.RefPosName;
    const string PosName = VertexTexture1Game.PosName;
    const string NrmName = VertexTexture1Game.NrmName;
    const string JointWeightName = VertexTexture1Game.JointWeightName;

    public Vector2 TexCoord;
    public Vector3 RefPos;
    public Vector3[] Position;
    public Vector3[] Normal;
    public Vector2[] JointWeight;

    public VertexTexture1Game(Vector2 uv)
    {
        TexCoord = uv;
        RefPos = Vector3.Zero;
        Position = new Vector3[Slots];
        Normal = new Vector3[Slots];
        JointWeight = new Vector2[Slots];
        for (int k = 0; k < Slots; k++) JointWeight[k] = new Vector2(-1, 0);
    }

    public int MaxColors => 0;
    public int MaxTextCoords => 1;

    IEnumerable<KeyValuePair<string, AttributeFormat>> IVertexReflection.GetEncodingAttributes()
    {
        yield return new("TEXCOORD_0", new AttributeFormat(DimensionType.VEC2));
        yield return new(RefPosName, new AttributeFormat(DimensionType.VEC3));
        for (int k = 0; k < Slots; k++)
        {
            yield return new(PosName + k, new AttributeFormat(DimensionType.VEC3));
            yield return new(NrmName + k, new AttributeFormat(DimensionType.VEC3));
            yield return new(JointWeightName + k, new AttributeFormat(DimensionType.VEC2));
        }
    }

    public IEnumerable<string> CustomAttributes
    {
        get
        {
            yield return RefPosName;
            for (int k = 0; k < Slots; k++)
            {
                yield return PosName + k;
                yield return NrmName + k;
                yield return JointWeightName + k;
            }
        }
    }

    public bool TryGetCustomAttribute(string attribute, out object value)
    {
        value = null;
        if (attribute == RefPosName) value = RefPos;
        else if (attribute.StartsWith(PosName)) value = Position == null ? Vector3.Zero : Position[int.Parse(attribute.AsSpan(PosName.Length))];
        else if (attribute.StartsWith(NrmName)) value = Normal == null ? Vector3.Zero : Normal[int.Parse(attribute.AsSpan(NrmName.Length))];
        else if (attribute.StartsWith(JointWeightName)) value = JointWeight == null ? new Vector2(-1, 0) : JointWeight[int.Parse(attribute.AsSpan(JointWeightName.Length))];
        return value != null;
    }

    public void SetCustomAttribute(string attribute, object value)
    {
        throw new NotSupportedException();
    }

    public Vector2 GetTexCoord(int index)
    {
        return TexCoord;
    }

    public void SetTexCoord(int index, Vector2 coord)
    {
        TexCoord = coord;
    }

    public Vector4 GetColor(int index)
    {
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    public void SetColor(int index, Vector4 color)
    {
    }

    public void Validate()
    {
    }

    public VertexMaterialDelta Subtract(IVertexMaterial baseValue)
    {
        throw new NotSupportedException();
    }

    public void Add(in VertexMaterialDelta delta)
    {
        throw new NotSupportedException();
    }

    public bool Equals(VertexTexture1Game<TGameSlots> other)
    {
        return TexCoord == other.TexCoord && RefPos == other.RefPos &&
               Same(Position, other.Position) && Same(Normal, other.Normal) && Same(JointWeight, other.JointWeight);
    }

    static bool Same<T>(T[] first, T[] second) where T : IEquatable<T>
    {
        if (first == null || second == null) return first == second;
        return first.AsSpan().SequenceEqual(second);
    }

    public override bool Equals(object obj)
    {
        return obj is VertexTexture1Game<TGameSlots> other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(TexCoord, RefPos);
    }
}

static class MatrixMath
{
    public const double BamsRad = Math.PI / 32768.0;
    public static Matrix4x4 Translation(float x, float y, float z) => Matrix4x4.CreateTranslation(x, y, z);
    public static Matrix4x4 RotX(int bams) => Matrix4x4.CreateRotationX((float)(bams * BamsRad));
    public static Matrix4x4 RotY(int bams) => Matrix4x4.CreateRotationY((float)(bams * BamsRad));
    public static Matrix4x4 RotZ(int bams) => Matrix4x4.CreateRotationZ((float)(bams * BamsRad));
    public static Matrix4x4 Scale(float scaleX, float scaleY, float scaleZ) => Matrix4x4.CreateScale(scaleX, scaleY, scaleZ);

    public static Vector3 Apply(Matrix4x4 matrix, float x, float y, float z)
        => Vector3.Transform(new Vector3(x, y, z), matrix);

    public static Quaternion EulerToQuatXyz(double angleX, double angleY, double angleZ)
    {
        double cosHalfX = Math.Cos(angleX / 2), sinHalfX = Math.Sin(angleX / 2);
        double cosHalfY = Math.Cos(angleY / 2), sinHalfY = Math.Sin(angleY / 2);
        double cosHalfZ = Math.Cos(angleZ / 2), sinHalfZ = Math.Sin(angleZ / 2);
        var rotationAboutX = new Quaternion((float)sinHalfX, 0, 0, (float)cosHalfX);
        var rotationAboutY = new Quaternion(0, (float)sinHalfY, 0, (float)cosHalfY);
        var rotationAboutZ = new Quaternion(0, 0, (float)sinHalfZ, (float)cosHalfZ);
        return rotationAboutZ * rotationAboutY * rotationAboutX;
    }
}