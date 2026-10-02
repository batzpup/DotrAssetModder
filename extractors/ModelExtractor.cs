using System.Buffers.Binary;
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
    public const uint Rh0Magic = 0x00304852;

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

    public static ExtractResult Extract(byte[] data, string outDir, string stem,
        IList<string>? names = null,
        ExtractionProgress? progress = null)
    {
        Directory.CreateDirectory(outDir);
        var slots = SliceArchive(data, stem);
        if (slots.Count == 0)
            return new ExtractResult(AssetKind.Rh0Model, 0, 0, 0);

        progress?.SetTotal(slots.Count);

        int written = 0, failed = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<FailureInfo>();
        int maxDop = Math.Max(1, Environment.ProcessorCount - 1);
        var options = new ParallelOptions {
            MaxDegreeOfParallelism = maxDop,
        };

        Parallel.For(0, slots.Count, options, i =>
        {
            if (progress?.IsCancellationRequested == true) return;

            var (off, len) = slots[i];
            if (len < 0x80)
            {
                progress?.Increment();
                return;
            }
            var slice = new byte[len];
            System.Buffer.BlockCopy(data, off, slice, 0, len);

            string slotName = (names != null && i < names.Count && !string.IsNullOrEmpty(names[i]))
                ? names[i]
                : $"{stem}_{i:D3}";
            try
            {
                ExtractSingle(slice, outDir, slotName);
                Interlocked.Increment(ref written);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                string reason = $"{ex.GetType().Name}: {ex.Message}";
                failures.Add(new FailureInfo(i, slotName, reason));
                Console.WriteLine($"[ModelExtractor] FAILED slot {i} ({slotName}): {reason}");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                progress?.Increment();
            }
        });

        var failList = failures.OrderBy(f => f.Slot).ToArray();
        return new ExtractResult(AssetKind.Rh0Model, written, failed, slots.Count) {
            Failures = failList,
        };
    }

    static List<(int Off, int Len)> SliceArchive(byte[] data, string stem)
    {
        bool isMonster = string.Equals(Path.GetFileNameWithoutExtension(stem),
                             "MONSTER", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(stem, "MONSTER.MRG", StringComparison.OrdinalIgnoreCase);
        if (isMonster)
        {
            const int Stride = 0x100000;
            var slots = new List<(int, int)>();
            int n = data.Length / Stride;
            for (int i = 0; i < n; i++)
            {
                int b = i * Stride;
                if (b + 4 > data.Length) break;
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(b, 4)) != Rh0Magic)
                {
                    slots.Add((b, 0));
                    continue;
                }
                slots.Add((b, Math.Min(Stride, data.Length - b)));
            }
            return slots;
        }
        return FindRh0Offsets(data);
    }

     static List<(int Off, int Len)> FindRh0Offsets(byte[] data)
    {
        ReadOnlySpan<byte> needle = stackalloc byte[] { 0x52, 0x48, 0x30, 0x00 };
        var raw = new List<int>();
        int pos = 0;
        while (true)
        {
            int found = data.AsSpan(pos).IndexOf(needle);
            if (found < 0) break;
            raw.Add(pos + found);
            pos += found + 4;
        }
        var slots = new List<(int, int)>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            int start = raw[i];
            int end = (i + 1 < raw.Count) ? raw[i + 1] : data.Length;
            slots.Add((start, end - start));
        }
        return slots;
    }

    private static void ExtractSingle(byte[] data, string outDir, string slotName)
    {
        var hdr = new Rh0HeaderFull(data);
        var (nodes, mslotMap) = WalkGNodes(data, (int)hdr.NodeTree);
        var (mainWalks, altWalks) = ParseWalks(data, hdr);
        if (altWalks.Count == 0 && hdr.Section1Alt != 0 && hdr.Section1AltSize >= 16)
        {
            altWalks = ParseAltPagesByScan(data, hdr);
        }


        // GS registers persist from the opaque chain into the alpha chain, which may have no
        // state block of its own (card 53 draws with the opaque chain's last one).
        var chainState = ChainState.Default;
        var mainPages = ParsePagesInSection(data, (int)hdr.Section1, (int)hdr.Section1Size, mainWalks, ref chainState);
        var altPages = (hdr.Section1Alt != 0 && altWalks.Count > 0)
            ? ParsePagesInSection(data, (int)hdr.Section1Alt, (int)hdr.Section1AltSize, altWalks, ref chainState)
            : new List<PageInfo>();
        var (texOffsets, postOffsets, mainDispatches, altDispatches) = ParseTexTableForModel(data, hdr);

        var textures = new List<DecodedTexInfo>(texOffsets.Length);
        var texHasAlpha = new List<bool>(texOffsets.Length);
        for (int ti = 0; ti < texOffsets.Length; ti++)
        {
            uint off = texOffsets[ti];
            if (off == 0)
            {
                textures.Add(new DecodedTexInfo(1, 1, MagentaPixel));
                texHasAlpha.Add(false);
                continue;
            }
            try
            {
                var t = Rh2Decoder.Decode(data, (int)off);
                textures.Add(new DecodedTexInfo(t.Width, t.Height, t.Rgba));
                texHasAlpha.Add(t.HasAlpha);
            }
            catch
            {
                textures.Add(new DecodedTexInfo(1, 1, MagentaPixel));
                texHasAlpha.Add(false);
            }
        }

        if (textures.Count == 0)
        {
            textures.Add(new DecodedTexInfo(1, 1, MagentaPixel));
            texHasAlpha.Add(false);
        }

        int slotsByte = (int)(hdr.SectionFlags & 0xFF);
        int activeSlotMask = slotsByte != 0 ? (slotsByte & -slotsByte) : 0xFF;
        var texAnims = new Dictionary<int, TexAnimBake>();
        for (int ti = 0; ti < textures.Count && ti < postOffsets.Length; ti++)
        {
            uint post = postOffsets[ti];
            if (post == 0) continue;
            var tex = textures[ti];
            if (tex.Width < 2 || tex.Height < 2) continue;
            try
            {
                var entries = TexAnim.Parse(data, (int)post);
                if (entries.Count == 0) continue;
                TexAnim.ApplyStaticStamps(tex.Rgba, tex.Width, tex.Height, entries, activeSlotMask);
                var bake = TexAnim.Bake(tex.Rgba, tex.Width, tex.Height, entries, activeSlotMask);
                if (bake != null) texAnims[ti] = bake;
            }
            catch
            {
            }
        }

        var packets = new List<Packet>(mainPages.Count + altPages.Count);
        foreach (var page in mainPages)
        {
            int s = (int)hdr.Section2 + (int)page.Sec2Off;
            int e = s + (int)page.PktSize;
            packets.Add(new Packet {
                WalkOff = page.WalkOff,
                BoneMap = page.BoneMap,
                Strips = ParseStripsInRange(data, s, e),
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
            int s = (int)hdr.Section2Alt + (int)page.Sec2Off;
            int e = s + (int)page.PktSize;
            packets.Add(new Packet {
                WalkOff = page.WalkOff,
                BoneMap = page.BoneMap,
                Strips = ParseStripsInRange(data, s, e),
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

        var animations = ParseMotSlots(data, hdr, nodes.Count);
        var bind = BindMatrices(nodes, animations, hdr.AnimTransScale, packets, out var poseSamples,
            out var mslotNode);
        var built = BuildMesh(packets, mslotMap, bind, mainDispatches, altDispatches, poseSamples, mslotNode);
        // The raw records too: the bake above plays the lowest motion slot's records only,
        // and records gated to other motions (byte +1, a slot mask) or on their own clock
        // (bit 1) need the viewer to play them per slot.
        var texAnimRecords = new Dictionary<int, System.Text.Json.Nodes.JsonArray>();
        for (int ti = 0; ti < textures.Count && ti < postOffsets.Length; ti++)
            if (postOffsets[ti] != 0)
                try { texAnimRecords[ti] = TexAnimRecords(data, (int)postOffsets[ti]); }
                catch { }
        string outPath = Path.Combine(outDir, slotName + ".glb");
        WriteGlb(outPath, nodes, mslotMap, built, bind, textures, texHasAlpha, animations, hdr.AnimTransScale,
            (int)hdr.NodeTreeSize, texAnims, texAnimRecords);
    }

    // One pose of the node tree, as lRhModel_SetupFloatMotionRecursive @ 0x210940 and
    // lRhModel_SetupNoMotionRecursive @ 0x210010 build it. World matrices carry no scale:
    // a node's accumulated scale reaches only the matrix it writes into its own slot
    // (SlotMatrix), never its children's stack.
    sealed class Pose
    {
        public Matrix4x4[] World;
        public Vector3[] Acc;
        public Vector3[] LocalT;
        public Quaternion[] LocalR;

        public Pose(int n)
        {
            World = new Matrix4x4[n];
            Acc = new Vector3[n];
            LocalT = new Vector3[n];
            LocalR = new Quaternion[n];
        }

        public Matrix4x4 SlotMatrix(int i) => M.Scale(Acc[i].X, Acc[i].Y, Acc[i].Z) * World[i];
    }

    // anim == null is the node default (rest) pose. Motion entries are in traversal order,
    // which is GNode.Idx, and WalkGNodes adds parents before their children.
    static Pose EvalPose(List<GNode> nodes, AnimSlot? anim, int t, float posScale)
    {
        var p = new Pose(nodes.Count);
        foreach (var n in nodes)
        {
            int f = n.Flags;
            var pos = n.RawPos;
            var rot = n.RawRot;
            var scale = n.RawScale;
            if (anim != null && anim.Tracks.TryGetValue(n.Idx, out var tr))
            {
                if (tr.Trans != null)
                {
                    pos = SampleVec(tr.Trans, t) * (n.Idx == 0 ? 1f : posScale);
                    f &= ~1;
                }
                if (tr.Rot != null)
                {
                    rot = SampleRot(tr.Rot, t);
                    f &= ~2;
                }
                if (tr.Scale != null)
                {
                    scale = SampleVec(tr.Scale, t);
                    f &= ~4;
                }
            }
            // a type-7 motion leaves the root's own scale alone
            if (anim != null && anim.Scaled && n.Idx == 0) scale = n.RawScale;

            var ps = n.Pidx < 0 ? Vector3.One : p.Acc[n.Pidx];
            var pw = n.Pidx < 0 ? Matrix4x4.Identity : p.World[n.Pidx];
            var lt = Vector3.Zero;
            var lr = Quaternion.Identity;
            var w = pw;
            if ((f & 3) != 3)
            {
                var R = Matrix4x4.Identity;
                if ((f & 1) == 0) lt = pos * ps;
                if ((f & 2) == 0)
                {
                    R = M.RotX(rot.X) * M.RotY(rot.Y) * M.RotZ(rot.Z);
                    lr = M.EulerToQuatXyz(rot.X * M.BamsRad, rot.Y * M.BamsRad, rot.Z * M.BamsRad);
                }
                w = R * M.Translation(lt.X, lt.Y, lt.Z) * pw;
            }
            p.World[n.Idx] = w;
            p.Acc[n.Idx] = (f & 4) != 0 ? ps : ps * scale;
            p.LocalT[n.Idx] = lt;
            p.LocalR[n.Idx] = lr;
        }
        return p;
    }

    // lRhModel_GetInterpolateMotionItem @ 0x210CF0: a list with fewer than 3 keys plays
    // key 0 only (`slti a2, 3` at 0x210D70).
    static int KeyBefore<T>(List<T> keys, Func<T, int> frame, int t)
    {
        int k = 0;
        while (k + 1 < keys.Count && frame(keys[k + 1]) <= t) k++;
        return k;
    }

    static Vector3 SampleVec(List<(int Frame, float X, float Y, float Z)> keys, int t)
    {
        var k0 = keys[0];
        if (keys.Count < 3) return new Vector3(k0.X, k0.Y, k0.Z);
        int k = KeyBefore(keys, x => x.Frame, t);
        var a = keys[k];
        if (k + 1 >= keys.Count) return new Vector3(a.X, a.Y, a.Z);
        var b = keys[k + 1];
        float u = (t - a.Frame) / (float)(b.Frame - a.Frame);
        return new Vector3(a.X, a.Y, a.Z) * (1f - u) + new Vector3(b.X, b.Y, b.Z) * u;
    }

    static (int X, int Y, int Z) SampleRot(List<(int Frame, int Rx, int Ry, int Rz)> keys, int t)
    {
        var k0 = keys[0];
        if (keys.Count < 3) return (k0.Rx, k0.Ry, k0.Rz);
        int k = KeyBefore(keys, x => x.Frame, t);
        var a = keys[k];
        if (k + 1 >= keys.Count) return (a.Rx, a.Ry, a.Rz);
        var b = keys[k + 1];
        float r0 = (b.Frame - t) / (float)(b.Frame - a.Frame);
        float r1 = (t - a.Frame) / (float)(b.Frame - a.Frame);
        return (RotLerp(r0, r1, a.Rx, b.Rx), RotLerp(r0, r1, a.Ry, b.Ry), RotLerp(r0, r1, a.Rz, b.Rz));
    }

    // RhGeom_RotationInterpolate @ 0x20D1D0: both angles mod 0x10000, the second moved by
    // 0x10000 to the short side, then fptosi(b*r1 + a*r0 + 0.5) (truncates toward zero).
    static int RotLerp(float a, float b, int r0, int r1)
    {
        int u0 = r0 & 0xFFFF, u1 = r1 & 0xFFFF;
        if (u1 - u0 > 0x8000) u1 -= 0x10000;
        else if (u1 - u0 < -0x8000) u1 += 0x10000;
        return (int)(u1 * b + u0 * a + 0.5f);
    }

    // Per matrix slot, the slot matrix the geometry is bound with. Normally the rest pose's.
    // A part much smaller at rest than at its largest (Meteor Dragon's head and limbs at 0,
    // card 178's arms at 1e-4, card 142's at 1e-2 of their full size) may be bound at the
    // motion frame where it is largest instead: inverting a shrunken matrix multiplies every
    // offset by the shrink, and those bone-local positions overflowed s16 on re-import
    // (178's cloak stretched out to its arms). A part tiny in every frame is bound unscaled
    // apart from the root's scale.
    //
    // A glb vertex has one position, so a vertex blending such a part with another can only
    // match the game's skinning (a separate offset per influence) in some poses, and which
    // ones depends on where the part is bound. No size ratio is right for every card (537's
    // parts at 1/13 want rest, 447's tail at 6e-5 wants its largest frame), so each such
    // part takes whichever of the two bind frames skins its blended vertices closer to the
    // game over the motions.
    static Dictionary<int, Matrix4x4> BindMatrices(List<GNode> nodes, List<AnimSlot> animations, float posScale,
        List<Packet> packets, out List<Pose> sampledPoses, out Dictionary<int, int> mslotNode)
    {
        const float ShrunkBy = 10f;       // rest 10x smaller than the largest frame: try both
        const float NeverVisible = 1e-3f; // relative to the root's scale
        const int SampleEvery = 6;        // motion frames the error is measured on
        var rest = EvalPose(nodes, null, 0, posScale);
        var r0 = rest.Acc[0];
        float rootScale = MathF.Max(MathF.Abs(r0.X), MathF.Max(MathF.Abs(r0.Y), MathF.Abs(r0.Z)));
        static float Size(Vector3 a) => MathF.Min(MathF.Abs(a.X), MathF.Min(MathF.Abs(a.Y), MathF.Abs(a.Z)));

        var slotted = nodes.Where(n => n.Mslot != 0).ToList();
        var bind = new Dictionary<int, Matrix4x4>();
        var best = new Dictionary<int, (float Size, Pose P)>();
        var spread = new Dictionary<int, Vector3>();   // the widest |accumulated scale| per axis
        var samples = new List<Pose>();
        foreach (var n in slotted)
        {
            best[n.Mslot] = (Size(rest.Acc[n.Idx]), rest);
            spread[n.Mslot] = Vector3.Abs(rest.Acc[n.Idx]);
        }
        foreach (var anim in animations)
            for (int t = 0; t < anim.FrameCount; t++)
            {
                var pose = EvalPose(nodes, anim, t, posScale);
                if (t % SampleEvery == 0) samples.Add(pose);
                foreach (var n in slotted)
                {
                    float size = Size(pose.Acc[n.Idx]);
                    if (size > best[n.Mslot].Size) best[n.Mslot] = (size, pose);
                    spread[n.Mslot] = Vector3.Max(spread[n.Mslot], Vector3.Abs(pose.Acc[n.Idx]));
                }
            }

        sampledPoses = samples;
        mslotNode = slotted.GroupBy(n => n.Mslot).ToDictionary(g => g.Key, g => g.First().Idx);

        var shrunk = new List<int>();
        foreach (var n in slotted)
        {
            float restSize = Size(rest.Acc[n.Idx]);
            float maxSize = best[n.Mslot].Size;
            if (maxSize < NeverVisible * rootScale)
                bind[n.Mslot] = M.Scale(r0.X, r0.Y, r0.Z) * rest.World[n.Idx];
            else
            {
                bind[n.Mslot] = rest.SlotMatrix(n.Idx);
                if (restSize * ShrunkBy < maxSize) shrunk.Add(n.Mslot);
            }
        }
        if (shrunk.Count == 0) return bind;

        // The blended vertices that touch a shrunk part: (slot, normalised weight, offset)
        // per influence, as BuildMesh reads them.
        var slotNode = slotted.ToDictionary(n => n.Mslot, n => n.Idx);
        var shrunkSet = shrunk.ToHashSet();
        var blended = new List<(int Slot, float W, Vector3 P)[]>();
        foreach (var pkt in packets)
            foreach (var s in pkt.Strips)
            {
                if (s.Type == "INDEP") continue;
                foreach (var v in s.Verts)
                {
                    if (v.Weights == null) continue;
                    var inf = new List<(int Slot, float W, Vector3 P)>();
                    foreach (var wt in v.Weights)
                    {
                        int mslot = pkt.BoneMap.TryGetValue(wt.Bone, out var ms) ? ms : 0;
                        if (!bind.ContainsKey(mslot)) continue;
                        inf.Add((mslot, wt.W, new Vector3(wt.Pos.X, wt.Pos.Y, wt.Pos.Z)));
                    }
                    float wsum = inf.Sum(x => x.W);
                    if (wsum == 0 || inf.Select(x => x.Slot).Distinct().Count() < 2) continue;
                    // Every blended vertex, not only those touching a shrunk part: moving
                    // a part's bind also moves the vertices it blends with elsewhere, and
                    // those have to be paid for in the same error.
                    blended.Add(inf.Select(x => (x.Slot, x.W / wsum, x.P)).ToArray());
                }
            }

        // How far the glb's skinning (one position x, the weighted average of B_i p_i, then
        // sum w_i S_i B_i^-1 x) lands from the game's (sum w_i S_i p_i), summed over the
        // sampled frames. A bind that cannot be inverted counts as a miss.
        // Every part that shares a blended vertex with a shrunk part may move with it: a
        // vertex is only skinned right where its parts' S_i*B_i^-1 agree, and two parts
        // bound in different poses never agree, however the one position is chosen. So the
        // candidates for such a part are its own default bind (rest), its own largest frame
        // if it is shrunk, and the pose in which any shrunk part of its group is largest -
        // binding the whole group in one pose makes the blend exact in that pose and close
        // wherever the group moves rigidly.
        var cur = new Dictionary<int, Matrix4x4>(bind);
        Matrix4x4 B(int m) => cur[m];
        float Error((int Slot, float W, Vector3 P)[] inf)
        {
            var x = Vector3.Zero;
            foreach (var (m, w, p) in inf) x += w * M.Apply(B(m), p.X, p.Y, p.Z);
            var local = new Vector3[inf.Length];
            for (int i = 0; i < inf.Length; i++)
            {
                if (!Matrix4x4.Invert(B(inf[i].Slot), out var inv)) return float.PositiveInfinity;
                local[i] = M.Apply(inv, x.X, x.Y, x.Z);
                if (!float.IsFinite(local[i].X + local[i].Y + local[i].Z)) return float.PositiveInfinity;
            }
            float err = 0;
            foreach (var pose in samples)
            {
                Vector3 glb = Vector3.Zero, game = Vector3.Zero;
                for (int i = 0; i < inf.Length; i++)
                {
                    var S = pose.SlotMatrix(slotNode[inf[i].Slot]);
                    glb += inf[i].W * M.Apply(S, local[i].X, local[i].Y, local[i].Z);
                    game += inf[i].W * M.Apply(S, inf[i].P.X, inf[i].P.Y, inf[i].P.Z);
                }
                err += (glb - game).Length();
            }
            return err;
        }

        // A part with no blended vertices skins exactly whatever its bind is, and is bound at
        // its largest frame, which keeps its offsets in s16. Parts that share blended
        // vertices (card 537's pair, 447's tail links, 178's cloak and the body under it) are
        // first decided together, every member taking the same candidate: one at a time, a
        // vertex between two parts bound in different poses is worse than with both in
        // either, so the search would stick. Then each part is tried on its own.
        var members = new HashSet<int>(shrunk);
        foreach (var inf in blended)
            foreach (var x in inf)
                members.Add(x.Slot);
        var touching = members.ToDictionary(m => m, m => blended.Where(inf => inf.Any(x => x.Slot == m)).ToList());
        var group = members.ToDictionary(m => m, m => m);
        int Find(int m) => group[m] == m ? m : group[m] = Find(group[m]);
        foreach (var inf in blended)
        {
            var parts = inf.Select(x => x.Slot).Distinct().ToList();
            for (int k = 1; k < parts.Count; k++) group[Find(parts[k])] = Find(parts[0]);
        }

        // A part flattened at rest (a cloak sheet with one accumulated scale axis at or near
        // zero) has a singular rest matrix: glTF cannot even store its inverse bind, and the
        // game's offsets along that axis are free. Binding it in its rest pose with the
        // flattened axes opened out to the width they reach in the motions keeps the part
        // where the rest pose puts it - so it agrees with the parts it blends with, which are
        // bound at rest - while staying invertible.
        Matrix4x4 Unflattened(int m)
        {
            int i = slotNode[m];
            var a = rest.Acc[i];
            var w = spread[m];
            float Axis(float v, float max) => MathF.Abs(v) * 50f >= max ? v : (v < 0 ? -max : max);
            return M.Scale(Axis(a.X, w.X), Axis(a.Y, w.Y), Axis(a.Z, w.Z)) * rest.World[i];
        }

        var groups = members.GroupBy(Find).Select(x => x.ToList()).ToList();
        // Candidate binds, per group: index 0 is each part's default, 1 its own largest
        // frame, then one per pose in which a shrunk member is largest.
        foreach (var g in groups)
        {
            var poses = new List<Pose>();
            foreach (int m in g)
                if (shrunkSet.Contains(m) && !poses.Contains(best[m].P)) poses.Add(best[m].P);
            var cand = new Dictionary<int, List<Matrix4x4>>();
            foreach (int m in g)
            {
                var list = new List<Matrix4x4> { bind[m] };
                list.Add(shrunkSet.Contains(m) ? best[m].P.SlotMatrix(slotNode[m]) : bind[m]);
                list.Add(shrunkSet.Contains(m) ? Unflattened(m) : bind[m]);
                foreach (var p in poses) list.Add(p.SlotMatrix(slotNode[m]));
                cand[m] = list;
            }
            var verts = g.SelectMany(m => touching[m]).Distinct().ToList();
            if (verts.Count == 0)
            {
                // No blended vertex: a shrunk part is bound at its largest frame, as before.
                foreach (int m in g)
                    if (shrunkSet.Contains(m)) bind[m] = cand[m][1];
                continue;
            }

            // Each start (every part on its own default, every shrunk part at its own
            // largest frame as before, or the whole group in one shrunk member's pose) is
            // then refined one part at a time; the configuration with the least total error
            // over the group's vertices wins. The old choice is among the starts, so no
            // card's measured error can get worse.
            int nCand = 3 + poses.Count;
            Dictionary<int, int> bestPick = null;
            float bestErr = float.PositiveInfinity;
            for (int start = 0; start < nCand; start++)
            {
                var pick = g.ToDictionary(m => m, _ => start);
                foreach (int m in g) cur[m] = cand[m][start];
                for (int pass = 0; pass < 3; pass++)
                {
                    bool changed = false;
                    foreach (int m in g)
                    {
                        if (touching[m].Count == 0) continue;
                        int keep = pick[m];
                        float keepErr = touching[m].Sum(Error);
                        for (int k = 0; k < nCand; k++)
                        {
                            if (k == pick[m]) continue;
                            cur[m] = cand[m][k];
                            float e = touching[m].Sum(Error);
                            if (e < keepErr) { keepErr = e; keep = k; }
                        }
                        cur[m] = cand[m][keep];
                        changed |= keep != pick[m];
                        pick[m] = keep;
                    }
                    if (!changed) break;
                }
                float total = verts.Sum(Error);
                if (total < bestErr) { bestErr = total; bestPick = pick; }
            }
            foreach (int m in g) cur[m] = cand[m][bestPick[m]];
            foreach (int m in g) bind[m] = cur[m];
        }
        return bind;
    }

    // The one position a glb vertex gets, for a vertex the game skins from several bones.
    //
    // The game evaluates Sum w_i*S_i(t)*p_i, a separate offset p_i per influence; a glb has
    // one position x and the importer stores p_i' = B_i^-1*x, so the glb skins to
    // Sum w_i*S_i(t)*B_i^-1*x. The plain weighted average Sum w_i*B_i*p_i is exact only in
    // poses where every S_i*B_i^-1 of the vertex agrees - at the bind pose, and wherever the
    // parts move together. When they don't (card 178's cloak hangs off two parts whose binds
    // are frames apart), that vertex drifts in every other frame and pokes through its
    // neighbours. Instead x is the least-squares fit over the sampled motion frames:
    // minimise Sum_t |x*A(t) + a(t) - g(t)|^2, A(t) = Sum w_i*S_i(t)*B_i^-1 and
    // g(t) = Sum w_i*S_i(t)*p_i, which is the same weighted average whenever that one is
    // exact. A ridge toward the average keeps the fit defined when the frames leave a
    // direction free (a part collapsed to zero in every frame).
    sealed class BlendFitter
    {
        readonly Dictionary<int, Matrix4x4> _bind;
        readonly List<Pose> _samples;
        readonly Dictionary<int, int> _node;
        readonly Dictionary<int, Matrix4x4> _inv = new();
        readonly Dictionary<string, Vector3> _memo = new();

        public BlendFitter(Dictionary<int, Matrix4x4> bind, List<Pose> samples, Dictionary<int, int> node)
        {
            _bind = bind;
            _samples = samples;
            _node = node;
        }

        Matrix4x4? Inv(int slot)
        {
            if (_inv.TryGetValue(slot, out var m)) return m.M11 == 0 && m.M22 == 0 && m.M33 == 0 && m.M44 == 0
                ? (Matrix4x4?)null : m;
            if (!_bind.TryGetValue(slot, out var b) || !Matrix4x4.Invert(b, out var inv))
            {
                _inv[slot] = default;
                return null;
            }
            _inv[slot] = inv;
            return inv;
        }

        public Vector3 Position(List<(int Slot, float W, Vector3 P)> infl, Vector3 average)
        {
            if (_samples == null || _samples.Count == 0 || _node == null) return average;
            if (infl.Count < 2) return average;
            bool mixed = false;
            for (int i = 1; i < infl.Count; i++)
                if (infl[i].Slot != infl[0].Slot) { mixed = true; break; }
            if (!mixed) return average;

            var key = new System.Text.StringBuilder();
            foreach (var (s, w, p) in infl)
                key.Append(s).Append(':').Append(w).Append(':')
                   .Append(p.X).Append(',').Append(p.Y).Append(',').Append(p.Z).Append(';');
            string k = key.ToString();
            if (_memo.TryGetValue(k, out var hit)) return hit;

            var inv = new Matrix4x4[infl.Count];
            for (int i = 0; i < infl.Count; i++)
            {
                var mi = Inv(infl[i].Slot);
                if (mi == null || !_node.ContainsKey(infl[i].Slot)) return average;
                inv[i] = mi.Value;
            }

            // Normal equations N*x = r, with a ridge lambda toward the weighted average.
            Span<double> N = stackalloc double[9];
            Span<double> r = stackalloc double[3];
            double scale = 0;
            foreach (var pose in _samples)
            {
                Matrix4x4 A = default;
                Vector3 g = Vector3.Zero;
                for (int i = 0; i < infl.Count; i++)
                {
                    var S = pose.SlotMatrix(_node[infl[i].Slot]);
                    float w = infl[i].W;
                    var C = inv[i] * S;
                    A.M11 += w * C.M11; A.M12 += w * C.M12; A.M13 += w * C.M13;
                    A.M21 += w * C.M21; A.M22 += w * C.M22; A.M23 += w * C.M23;
                    A.M31 += w * C.M31; A.M32 += w * C.M32; A.M33 += w * C.M33;
                    A.M41 += w * C.M41; A.M42 += w * C.M42; A.M43 += w * C.M43;
                    g += w * M.Apply(S, infl[i].P.X, infl[i].P.Y, infl[i].P.Z);
                }
                double[] L = { A.M11, A.M12, A.M13, A.M21, A.M22, A.M23, A.M31, A.M32, A.M33 };
                double b0 = g.X - A.M41, b1 = g.Y - A.M42, b2 = g.Z - A.M43;
                if (!double.IsFinite(L[0] + L[4] + L[8] + b0 + b1 + b2)) return average;
                for (int a = 0; a < 3; a++)
                {
                    for (int b = 0; b < 3; b++)
                        N[a * 3 + b] += L[a * 3 + 0] * L[b * 3 + 0] + L[a * 3 + 1] * L[b * 3 + 1]
                            + L[a * 3 + 2] * L[b * 3 + 2];
                    r[a] += L[a * 3 + 0] * b0 + L[a * 3 + 1] * b1 + L[a * 3 + 2] * b2;
                }
                scale += (N[0] + N[4] + N[8]);
            }
            double lambda = 1e-6 * (N[0] + N[4] + N[8]) / 3.0;
            if (!(lambda > 0)) return average;
            N[0] += lambda; N[4] += lambda; N[8] += lambda;
            r[0] += lambda * average.X; r[1] += lambda * average.Y; r[2] += lambda * average.Z;

            if (!Solve3(N, r, out var x)) return average;
            if (!float.IsFinite(x.X) || !float.IsFinite(x.Y) || !float.IsFinite(x.Z)) x = average;

            // The fit is a least-squares one: it can trade a little of its worst frame for the
            // rest. A vertex that pokes through its neighbours is a worst-frame problem, so
            // keep the fit only where its worst frame is no worse than the average's.
            if (Worst(infl, inv, x) > Worst(infl, inv, average)) x = average;
            _memo[k] = x;
            return x;
        }

        // The largest distance, over the sampled frames, between the glb's skinning of x and
        // the game's own result for this vertex.
        float Worst(List<(int Slot, float W, Vector3 P)> infl, Matrix4x4[] inv, Vector3 x)
        {
            float worst = 0;
            foreach (var pose in _samples)
            {
                Vector3 glb = Vector3.Zero, game = Vector3.Zero;
                for (int i = 0; i < infl.Count; i++)
                {
                    var S = pose.SlotMatrix(_node[infl[i].Slot]);
                    var local = M.Apply(inv[i], x.X, x.Y, x.Z);
                    glb += infl[i].W * M.Apply(S, local.X, local.Y, local.Z);
                    game += infl[i].W * M.Apply(S, infl[i].P.X, infl[i].P.Y, infl[i].P.Z);
                }
                worst = MathF.Max(worst, (glb - game).Length());
            }
            return worst;
        }

        // Symmetric 3x3 solve by Cramer's rule; a singular system falls back to the average.
        static bool Solve3(Span<double> n, Span<double> r, out Vector3 x)
        {
            double a = n[0], b = n[1], c = n[2], d = n[3], e = n[4], f = n[5], g = n[6], h = n[7], i = n[8];
            double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            double norm = Math.Abs(a) + Math.Abs(e) + Math.Abs(i);
            x = Vector3.Zero;
            if (!(Math.Abs(det) > 1e-12 * norm * norm * norm) || !double.IsFinite(det)) return false;
            double r0 = r[0], r1 = r[1], r2 = r[2];
            double dx = r0 * (e * i - f * h) - b * (r1 * i - f * r2) + c * (r1 * h - e * r2);
            double dy = a * (r1 * i - f * r2) - r0 * (d * i - f * g) + c * (d * r2 - r1 * g);
            double dz = a * (e * r2 - r1 * h) - b * (d * r2 - r1 * g) + r0 * (d * h - e * g);
            x = new Vector3((float)(dx / det), (float)(dy / det), (float)(dz / det));
            return true;
        }
    }

    static readonly byte[] MagentaPixel = { 0xff, 0x00, 0xff, 0xff };

    static (List<GNode> Nodes, Dictionary<int, GNode> MslotMap) WalkGNodes(byte[] data, int treeBase)
    {
        var nodes = new List<GNode>();
        var mslotMap = new Dictionary<int, GNode>();

        void Visit(int off, int pidx, Matrix4x4 parentW, Vector3 parentAccum)
        {
            float sx = U32f(data, off + 0x00);
            float sy = U32f(data, off + 0x04);
            float sz = U32f(data, off + 0x08);
            ushort flags = U16(data, off + 0x0C);
            ushort mslot = U16(data, off + 0x0E);
            int rx = I32(data, off + 0x10);
            int ry = I32(data, off + 0x14);
            int rz = I32(data, off + 0x18);

            int childOff = I32(data, off + 0x1C) & 0x0FFFFFFF;
            float px = U32f(data, off + 0x20);
            float py = U32f(data, off + 0x24);
            float pz = U32f(data, off + 0x28);
            int sibOff = I32(data, off + 0x2C) & 0x0FFFFFFF;

            bool skipT = (flags & 1) != 0;
            bool skipR = (flags & 2) != 0;
            bool noScaleProp = (flags & 4) != 0;
            float rpxRaw = skipT ? 0f : px;
            float rpyRaw = skipT ? 0f : py;
            float rpzRaw = skipT ? 0f : pz;
            int rrx = skipR ? 0 : rx;
            int rry = skipR ? 0 : ry;
            int rrz = skipR ? 0 : rz;
            float rpx = rpxRaw * parentAccum.X;
            float rpy = rpyRaw * parentAccum.Y;
            float rpz = rpzRaw * parentAccum.Z;
            Vector3 newAccum = noScaleProp
                ? parentAccum
                : new Vector3(parentAccum.X * sx, parentAccum.Y * sy, parentAccum.Z * sz);

            var T = M.Translation(rpx, rpy, rpz);
            var R = M.RotX(rrx) * M.RotY(rry) * M.RotZ(rrz);
            var world = R * T * parentW;

            int idx = nodes.Count;
            var n = new GNode {
                Idx = idx,
                Pidx = pidx,
                Flags = flags,
                Mslot = mslot,
                RawScale = new Vector3(sx, sy, sz),
                RawPos = new Vector3(px, py, pz),
                RawRot = (rx, ry, rz),
                RestWorld = world,
            };
            nodes.Add(n);
            if (pidx >= 0) nodes[pidx].Children.Add(idx);
            if (mslot != 0) mslotMap[mslot] = n;

            if (childOff != 0) Visit(treeBase + childOff, idx, world, newAccum);
            if (sibOff != 0) Visit(treeBase + sibOff, pidx, parentW, parentAccum);
        }

        Visit(treeBase, -1, Matrix4x4.Identity, new Vector3(1, 1, 1));
        return (nodes, mslotMap);
    }

    static (List<int> Main, List<int> Alt) ParseWalks(byte[] data, in Rh0HeaderFull hdr)
    {
        var main = new List<int>();
        var alt = new List<int>();
        int n = (int)(hdr.RelocWalkSize / 4);
        for (int i = 0; i < n; i++)
        {
            uint v = U32(data, (int)hdr.RelocWalk + i * 4);
            if (v == 0xFFFFFFFF) break;
            if (v == 0) continue;
            uint flag = v & 0xF0000000;
            int off = (int)(v & 0x0FFFFFFF);
            if (flag == 0x20000000) main.Add(off);
            else if (flag == 0x40000000) alt.Add(off);
        }
        return (main, alt);
    }

    // Draw state that persists from one draw chain into the next.
    struct ChainState
    {
        public (float R, float G, float B) Q0, Q1, Q2;
        public float AlphaW;
        public ulong GsAlpha, GsTest, GsClamp;

        public static ChainState Default => new() {
            Q0 = (0, 0, 0), Q1 = (255, 255, 255), Q2 = (255, 255, 255), AlphaW = 128f,
            GsAlpha = DefaultGsAlpha, GsTest = DefaultGsTest, GsClamp = DefaultGsClamp,
        };
    }

    private static List<PageInfo> ParsePagesInSection(
        byte[] data, int secBase, int secSize, List<int> walkOffsets, ref ChainState state)
    {
        var pages = new List<PageInfo>();
        var runningBoneMap = new Dictionary<int, int>();
        var curQ0 = state.Q0;
        var curQ1 = state.Q1;
        var curQ2 = state.Q2;
        float curAlphaW = state.AlphaW;
        ulong curGsAlpha = state.GsAlpha;
        ulong curGsTest = state.GsTest;
        ulong curGsClamp = state.GsClamp;
        int curPos = 0;
        for (int wi = 0; wi < walkOffsets.Count; wi++)
        {
            int walkOff = walkOffsets[wi];
            if (walkOff < 4 || walkOff > secSize) continue;
            int termOff = walkOff - 4;
            int scanEnd = Math.Min(termOff, secSize);
            int scanP = curPos;
            while (scanP + 4 <= scanEnd)
            {
                uint w = U32(data, secBase + scanP);
                if (w == 0x10000003 && scanP + 0x40 <= secSize)
                {
                    // three V4-32 colours unpacked to VU 0x3C: c0 +0x10, c1 +0x20, c2 +0x30
                    float q0r = U32f(data, secBase + scanP + 0x10);
                    float q0g = U32f(data, secBase + scanP + 0x14);
                    float q0b = U32f(data, secBase + scanP + 0x18);
                    if (q0r >= 0 && q0r <= 255 && q0g >= 0 && q0g <= 255 && q0b >= 0 && q0b <= 255)
                        curQ0 = (q0r, q0g, q0b);
                    float q1r = U32f(data, secBase + scanP + 0x20);
                    float q1g = U32f(data, secBase + scanP + 0x24);
                    float q1b = U32f(data, secBase + scanP + 0x28);
                    float q1a = U32f(data, secBase + scanP + 0x2C);
                    if (q1r >= 0 && q1r <= 255 && q1g >= 0 && q1g <= 255 && q1b >= 0 && q1b <= 255)
                        curQ1 = (q1r, q1g, q1b);
                    if (q1a >= 0 && q1a <= 255) curAlphaW = q1a;
                    float q2r = U32f(data, secBase + scanP + 0x30);
                    float q2g = U32f(data, secBase + scanP + 0x34);
                    float q2b = U32f(data, secBase + scanP + 0x38);
                    if (q2r >= 0 && q2r <= 255 && q2g >= 0 && q2g <= 255 && q2b >= 0 && q2b <= 255)
                        curQ2 = (q2r, q2g, q2b);
                    scanP += 0x40;
                    continue;
                }

                // The material setup is a packed GIF A+D list. The first qword is
                // the 64-bit GS value and the second qword's low byte is the GS
                // register (0x42/43 = ALPHA, 0x47/48 = TEST, 0x08/09 = CLAMP). Preserve the state
                // that is active when this page is dispatched instead of inferring
                // blending from the alternate section or texture animation.
                if ((scanP & 0xF) == 0 && scanP + 16 <= scanEnd)
                {
                    uint gsReg = U32(data, secBase + scanP + 8);
                    uint gsRegHi = U32(data, secBase + scanP + 12);
                    if (gsRegHi == 0 && (gsReg == 0x42 || gsReg == 0x43))
                    {
                        curGsAlpha = BinaryPrimitives.ReadUInt64LittleEndian(
                            data.AsSpan(secBase + scanP, 8));
                        scanP += 16;
                        continue;
                    }
                    if (gsRegHi == 0 && (gsReg == 0x47 || gsReg == 0x48))
                    {
                        curGsTest = BinaryPrimitives.ReadUInt64LittleEndian(
                            data.AsSpan(secBase + scanP, 8));
                        scanP += 16;
                        continue;
                    }
                    if (gsRegHi == 0 && (gsReg == 0x08 || gsReg == 0x09))
                    {
                        // Only the plain wrap-mode nibble is used by retail models; anything
                        // larger is unrelated data that happens to look like an A+D pair.
                        ulong clampValue = BinaryPrimitives.ReadUInt64LittleEndian(
                            data.AsSpan(secBase + scanP, 8));
                        if (clampValue <= 0xFUL)
                        {
                            curGsClamp = clampValue;
                            scanP += 16;
                            continue;
                        }
                    }
                }

                scanP += 4;
            }
            curPos = termOff + 0x10;

            var entries = FindStripEntries(data, secBase, termOff);
            foreach (var (vu, so) in entries) runningBoneMap[vu] = so >> 4;

            uint term0 = U32(data, secBase + termOff + 0);
            uint term1 = U32(data, secBase + termOff + 4);

            pages.Add(new PageInfo {
                WalkOff = walkOff,
                BoneMap = new Dictionary<int, int>(runningBoneMap),
                Sec2Off = term1 & 0x0FFFFFFF,
                PktSize = (term0 & 0xFFFF) << 4,
                ColorQ0 = curQ0,
                ColorQ1 = curQ1,
                ColorQ2 = curQ2,
                AlphaW = curAlphaW,
                GsAlpha = curGsAlpha,
                GsTest = curGsTest,
                GsClamp = curGsClamp,
            });
        }
        state = new ChainState {
            Q0 = curQ0, Q1 = curQ1, Q2 = curQ2, AlphaW = curAlphaW,
            GsAlpha = curGsAlpha, GsTest = curGsTest, GsClamp = curGsClamp,
        };
        return pages;
    }

    private static List<int> ParseAltPagesByScan(byte[] data, in Rh0HeaderFull hdr)
    {
        var walks = new List<int>();
        if (hdr.Section1Alt == 0 || hdr.Section1AltSize < 16) return walks;
        int sec1a = (int)hdr.Section1Alt;
        int sz = (int)hdr.Section1AltSize;
        for (int off = 0; off + 16 <= sz; off += 16)
        {
            uint d0 = U32(data, sec1a + off);
            uint d1 = U32(data, sec1a + off + 4);
            uint d2 = U32(data, sec1a + off + 8);
            uint d3 = U32(data, sec1a + off + 12);
            if ((d0 & 0xFFFF0000) != 0x30000000) continue;
            if ((d0 & 0xFFFF) == 0x0007) continue;
            if (d2 != 0 || d3 != 0) continue;
            walks.Add(off + 4);
        }
        return walks;
    }

    private static List<AnimSlot> ParseMotSlots(byte[] data, in Rh0HeaderFull hdr, int nodeCount)
    {
        var anims = new List<AnimSlot>();
        if (hdr.MotTable == 0) return anims;
        var slotIds = new List<int>();
        for (int i = 0; i < 8; i++)
            if ((hdr.SectionFlags & (1u << i)) != 0)
                slotIds.Add(i);
        int motOff = (int)hdr.MotTable;
        const uint MotMagic = 0x1A544F4D;
        foreach (int slotId in slotIds)
        {
            if (motOff + 32 > data.Length) break;
            uint magic = U32(data, motOff);
            if (magic != MotMagic) break;
            uint size = U32(data, motOff + 4);
            ushort nc = U16(data, motOff + 0x10);
            uint motionOff = U32(data, motOff + 0x14);
            int mo = motOff + (int)motionOff;
            uint mdataOff = U32(data, mo);
            uint fc = U32(data, mo + 4);
            ushort mtype = U16(data, mo + 8);
            // type 3: pos ptr, angle ptr, n pos, n angle (16 B); type 7 adds a scale ptr
            // after the angle ptr and its count last (24 B)
            bool hasScale = (mtype & 4) != 0;
            int lists = hasScale ? 3 : 2;
            int mdataBase = motOff + (int)mdataOff;
            var tracks = new Dictionary<int, AnimTrack>();
            // The game walks the tree and reads only the entries of nodes it visits, so a
            // motion with more entries than the tree still plays (2 retail motions).
            int entries = Math.Min((int)nc, nodeCount);
            for (int i = 0; i < entries; i++)
            {
                int eoff = mdataBase + i * lists * 8;
                uint pT = U32(data, eoff);
                uint pR = U32(data, eoff + 4);
                uint pS = hasScale ? U32(data, eoff + 8) : 0;
                int cT = (int)U32(data, eoff + lists * 4);
                int cR = (int)U32(data, eoff + lists * 4 + 4);
                int cS = hasScale ? (int)U32(data, eoff + 20) : 0;
                var track = new AnimTrack();
                if (pT != 0 && cT > 0) track.Trans = ReadVecKeys(data, motOff + (int)pT, cT);
                if (pS != 0 && cS > 0) track.Scale = ReadVecKeys(data, motOff + (int)pS, cS);
                if (pR != 0 && cR > 0)
                {
                    var kfs = new List<(int, int, int, int)>(cR);
                    for (int k = 0; k < cR; k++)
                    {
                        int koff = motOff + (int)pR + k * 16;
                        int fr = I32(data, koff);
                        int rx = I32(data, koff + 4);
                        int ry = I32(data, koff + 8);
                        int rz = I32(data, koff + 12);
                        kfs.Add((fr, rx, ry, rz));
                    }
                    track.Rot = kfs;
                }
                if (track.Trans != null || track.Rot != null || track.Scale != null) tracks[i] = track;
            }
            anims.Add(new AnimSlot {
                SlotId = slotId,
                FrameCount = (int)fc,
                Scaled = hasScale,
                Tracks = tracks,
            });
            motOff += (int)size;
        }
        return anims;
    }

    // 16-byte keys: u32 frame, f32 x3
    private static List<(int Frame, float X, float Y, float Z)> ReadVecKeys(byte[] data, int at, int count)
    {
        var kfs = new List<(int, float, float, float)>(count);
        for (int k = 0; k < count; k++)
        {
            int koff = at + k * 16;
            kfs.Add((I32(data, koff), U32f(data, koff + 4), U32f(data, koff + 8), U32f(data, koff + 12)));
        }
        return kfs;
    }

    private static List<(int Vu, int So)> FindStripEntries(byte[] data, int secBase, int termOff)
    {
        var entries = new List<(int, int)>();
        int eo = termOff - 16;
        while (eo >= 0)
        {
            uint d0 = U32(data, secBase + eo);
            if (d0 != 0x30000007) break;
            uint d1 = U32(data, secBase + eo + 4);
            uint d3 = U32(data, secBase + eo + 12);
            entries.Add(((int)(d3 & 0x3FF), (int)(d1 & 0x0FFFFFFF)));
            eo -= 16;
        }
        entries.Reverse();
        return entries;
    }

    private static (uint[] TexOffsets, uint[] PostOffsets, List<(int TexIdx, int Off)> MainDispatches, List<(int TexIdx, int Off)> AltDispatches)
        ParseTexTableForModel(byte[] data, in Rh0HeaderFull hdr)
    {
        if (hdr.TexTable == 0 || hdr.TexCount == 0)
            return (Array.Empty<uint>(), Array.Empty<uint>(), new List<(int, int)>(), new List<(int, int)>());
        int tt = (int)hdr.TexTable;
        uint count = U32(data, tt);
        uint perTexOff = U32(data, tt + 4);
        if (count == 0 || count > 256)
            return (Array.Empty<uint>(), Array.Empty<uint>(), new List<(int, int)>(), new List<(int, int)>());

        var offs = new uint[count];
        var posts = new uint[count];
        for (int i = 0; i < count; i++)
        {
            offs[i] = U32(data, tt + 16 + i * 16);
            // word[+8] of each 16-byte block points at the texture's "post section"
            // (the packed .anm entry array), or 0 when the texture is not animated.
            posts[i] = U32(data, tt + 16 + i * 16 + 8);
        }

        var main = new List<(int, int)>();
        var alt = new List<(int, int)>();
        for (int i = 0; i < count; i++)
        {
            int eoff = tt + (int)perTexOff + i * 8;
            uint nDisp = U32(data, eoff);
            uint dispOff = U32(data, eoff + 4);
            if (nDisp > 64) continue;
            for (int j = 0; j < nDisp; j++)
            {
                uint v = U32(data, tt + (int)dispOff + j * 4);
                uint flag = v & 0xF0000000;
                int off = (int)(v & 0x0FFFFFFF);
                if (flag == 0x20000000) main.Add((i, off));
                else if (flag == 0x40000000) alt.Add((i, off));
            }
        }
        return (offs, posts, main, alt);
    }

    // The texture a page draws with: the last dispatch at or before it, or -1 if none (the
    // GS keeps whatever TEX0 was set last).
    private static int PageToTexture(int walkOff, List<(int TexIdx, int Off)> dispatches)
    {
        int bestTex = -1, bestOff = -1;
        foreach (var (ti, off) in dispatches)
            if (off <= walkOff && off > bestOff)
            {
                bestOff = off;
                bestTex = ti;
            }
        return bestTex;
    }

    // PCSX2 nVifT: bytes per vector for each VIF UNPACK type
    private static readonly int[] NVif = { 4, 2, 1, 0, 8, 4, 2, 0, 12, 6, 3, 0, 16, 8, 4, 2 };

    // The packet walk of rh0.py's Model._packet: runs of strips ending in MSCALF, each strip
    // a header UNPACK (V4-32 rigid, V3-32 skinned) then one V3-16 UNPACK holding its
    // vertices. The REF's size has to be used up exactly; anything else is not retail data.
    private static List<Strip> ParseStripsInRange(byte[] data, int start, int end)
    {
        var strips = new List<Strip>();
        int off = start;
        int hdrWords = 0;              // 4 = rigid (V4-32), 3 = skinned (V3-32), 0 = none yet
        int hdrCount = 0, hdrVuAddr = 0, hdrPrim = 0;
        while (off < end)
        {
            if (off + 4 > end)
                throw new InvalidDataException($"packet at 0x{start:x} overruns its REF by {off + 4 - end}");
            uint w = U32(data, off);
            int cmd = (int)((w >> 24) & 0x7F);
            int num = (int)((w >> 16) & 0xFF);
            if (cmd < 0x60)            // a REF spans several MSCALF-ended runs
            {
                if (cmd != 0x00 && cmd != 0x15)
                    throw new InvalidDataException($"unexpected VIF 0x{w:x8} at 0x{off:x}");
                off += 4;
                continue;
            }
            int n = num == 0 ? 256 : num;
            int t = cmd & 0xF;
            int body = off + 4;
            off = body + ((NVif[t] * n + 3) & ~3);
            if (off > end)
                throw new InvalidDataException($"packet at 0x{start:x} overruns its REF by {off - end}");

            if (t == 8 || t == 12)     // strip header: GIF tag and registers, rigid also the VU bone address
            {
                uint tag1 = U32(data, body + 4);
                int prim = (int)((tag1 >> 15) & 0x7FF);   // GIF tag PRIM: 4 = tristrip, 0x40 = alpha
                if (n != 1 || (tag1 & 0xF0007FFF) != 0x30004000 || (prim & 7) != 4)
                    throw new InvalidDataException($"strip header 0x{tag1:x8} at 0x{body:x}");
                hdrWords = NVif[t] / 4;
                hdrCount = (int)(U32(data, body) & 0x7FFF);
                hdrPrim = prim;
                hdrVuAddr = hdrWords == 4 ? (int)(U32(data, body + 12) & 0x3FF) : 0;
                continue;
            }
            if (t != 9 || hdrWords == 0)
                throw new InvalidDataException($"unexpected UNPACK type {t} at 0x{body:x}");

            var strip = new Strip {
                Type = hdrWords == 4 ? "INDEP" : "SHARED",
                VuAddr = hdrVuAddr,
                Prim = hdrPrim,
            };
            int i = 0;
            for (int vi = 0; vi < hdrCount; vi++)
            {
                if (i + 1 > n)
                    throw new InvalidDataException($"strip at 0x{body:x} runs past its UNPACK");
                int vp = body + i * 6;
                short u = I16(data, vp), v = I16(data, vp + 2);
                // rigid: the one influence is the header's VU address, at full weight
                int k = hdrWords == 4 ? 1 : I16(data, vp + 4);
                i++;
                if (k < 0 || i + k * (hdrWords == 4 ? 2 : 3) > n)
                    throw new InvalidDataException($"strip at 0x{body:x} runs past its UNPACK");
                var vert = new StripVertex { Uv = (u, v) };
                if (hdrWords == 4)
                {
                    int np = body + i * 6, pp = np + 6;
                    vert.Nrm = (I16(data, np), I16(data, np + 2), I16(data, np + 4));
                    vert.Pos = (I16(data, pp), I16(data, pp + 2), I16(data, pp + 4));
                    i += 2;
                }
                else
                {
                    var weights = new List<StripWeight>(k);
                    int wsum = 0;
                    for (int j = 0; j < k; j++)
                    {
                        int ip = body + i * 6, np = ip + 6, pp = ip + 12;
                        int bone = U16(data, ip);
                        short ww = I16(data, ip + 2);
                        weights.Add(new StripWeight(bone, ww,
                            (I16(data, pp), I16(data, pp + 2), I16(data, pp + 4)),
                            (I16(data, np), I16(data, np + 2), I16(data, np + 4))));
                        wsum += ww;
                        i += 3;
                    }
                    // each 4.12 weight was rounded on its own: off by at most one per influence
                    if (Math.Abs(wsum - 4096) > k)
                        throw new InvalidDataException($"{k} weights at 0x{body:x} sum to {wsum}");
                    vert.Weights = weights;
                }
                strip.Verts.Add(vert);
            }
            if (i != n)
                throw new InvalidDataException($"strip at 0x{body:x} does not consume its UNPACK ({i} of {n})");
            strips.Add(strip);
        }
        if (off != end)
            throw new InvalidDataException($"packet at 0x{start:x} overruns its REF by {off - end}");
        return strips;
    }

    private static BuiltMesh BuildMesh(
        List<Packet> packets,
        Dictionary<int, GNode> mslotMap,
        Dictionary<int, Matrix4x4> bind,
        List<(int, int)> mainDispatches,
        List<(int, int)> altDispatches,
        List<Pose> samples = null,
        Dictionary<int, int> mslotNode = null)
    {
        var fit = new BlendFitter(bind, samples, mslotNode);
        var bm = new BuiltMesh {
            SortedMslots = mslotMap.Keys.OrderBy(k => k).ToList(),
        };
        for (int i = 0; i < bm.SortedMslots.Count; i++)
            bm.MslotToJoint[bm.SortedMslots[i]] = i;

        var primFaces = new Dictionary<PrimKey, (List<(int A, int B, int C)> Faces, float AlphaW)>();

        // TEX0 persists into the alpha chain: a page with no dispatch of its own draws with
        // the opaque chain's last texture
        int lastMainTex = 0;
        foreach (var pkt in packets.Where(p => !p.IsAlt))
        {
            int t = PageToTexture(pkt.WalkOff, mainDispatches);
            if (t >= 0) lastMainTex = t;
        }

        foreach (var pkt in packets)
        {
            var dispatches = pkt.IsAlt ? altDispatches : mainDispatches;
            int texIdx = PageToTexture(pkt.WalkOff, dispatches);
            if (texIdx < 0) texIdx = pkt.IsAlt ? lastMainTex : 0;
            var q0 = ((int)Math.Round(pkt.ColorQ0.R), (int)Math.Round(pkt.ColorQ0.G), (int)Math.Round(pkt.ColorQ0.B));
            var q1 = ((int)Math.Round(pkt.ColorQ1.R), (int)Math.Round(pkt.ColorQ1.G), (int)Math.Round(pkt.ColorQ1.B));
            var q2 = ((int)Math.Round(pkt.ColorQ2.R), (int)Math.Round(pkt.ColorQ2.G), (int)Math.Round(pkt.ColorQ2.B));
            int alphaKey = (int)Math.Round(pkt.AlphaW);

            // The game draws every strip in the chain. Untextured ones (PRIM TME clear: 131
            // retail strips) keep their geometry with a plain-colour material.
            foreach (var s in pkt.Strips)
            {
                int vStart = bm.Vertices.Count;

                if (s.Type == "INDEP")
                {
                    int vu = s.VuAddr;
                    int mslot = pkt.BoneMap.TryGetValue(vu, out var ms) ? ms : 0;
                    var matrix = bind.TryGetValue(mslot, out var slotBind) ? slotBind : Matrix4x4.Identity;
                    int jointIdx = bm.MslotToJoint.TryGetValue(mslot, out var ji) ? ji : -1;
                    foreach (var v in s.Verts)
                    {
                        var p = M.Apply(matrix, v.Pos.X, v.Pos.Y, v.Pos.Z);
                        bm.Vertices.Add(p);
                        bm.Normals.Add(NSafe(Vector3.TransformNormal(
                            new Vector3(v.Nrm.X, v.Nrm.Y, v.Nrm.Z), matrix)));
                        bm.Uvs.Add(new Vector2(v.Uv.U / 4096f, v.Uv.V / 4096f));
                        bm.VertJoints.Add(jointIdx >= 0
                            ? new List<(int, float)> { (jointIdx, 1.0f) }
                            : new List<(int, float)>());
                    }
                }
                else
                {
                    foreach (var v in s.Verts)
                    {
                        float wx = 0, wy = 0, wz = 0, wsum = 0;
                        float nx = 0, ny = 0, nz = 0;
                        var jlist = new List<(int Joint, float W)>();
                        var infl = new List<(int Slot, float W, Vector3 P)>();
                        if (v.Weights != null)
                        {
                            foreach (var wt in v.Weights)
                            {
                                int mslot = pkt.BoneMap.TryGetValue(wt.Bone, out var ms) ? ms : 0;
                                if (!bind.TryGetValue(mslot, out var bone)) continue;
                                var p = M.Apply(bone, wt.Pos.X, wt.Pos.Y, wt.Pos.Z);
                                float ww = wt.W;
                                wx += p.X * ww;
                                wy += p.Y * ww;
                                wz += p.Z * ww;
                                var bn = Vector3.TransformNormal(
                                    new Vector3(wt.Nrm.X, wt.Nrm.Y, wt.Nrm.Z), bone);
                                nx += bn.X * ww;
                                ny += bn.Y * ww;
                                nz += bn.Z * ww;
                                wsum += ww;
                                int jidx = bm.MslotToJoint.TryGetValue(mslot, out var ji) ? ji : -1;
                                if (jidx >= 0) jlist.Add((jidx, ww));
                                infl.Add((mslot, ww, new Vector3(wt.Pos.X, wt.Pos.Y, wt.Pos.Z)));
                            }
                        }
                        if (wsum != 0)
                        {
                            wx /= wsum;
                            wy /= wsum;
                            wz /= wsum;
                            for (int k = 0; k < jlist.Count; k++)
                                jlist[k] = (jlist[k].Joint, jlist[k].W / wsum);
                            for (int k = 0; k < infl.Count; k++)
                                infl[k] = (infl[k].Slot, infl[k].W / wsum, infl[k].P);
                        }
                        bm.Vertices.Add(fit.Position(infl, new Vector3(wx, wy, wz)));
                        bm.Normals.Add(NSafe(new Vector3(nx, ny, nz)));
                        bm.Uvs.Add(new Vector2(v.Uv.U / 4096f, v.Uv.V / 4096f));
                        bm.VertJoints.Add(jlist);
                    }
                }

                int nv = s.Verts.Count;
                var key = new PrimKey(texIdx, pkt.IsAlt, alphaKey, q0, q1, q2, pkt.GsAlpha, pkt.GsTest, pkt.GsClamp,
                    s.Prim & (PrimAbe | PrimTme));
                if (!primFaces.TryGetValue(key, out var entry))
                {
                    entry = (new List<(int, int, int)>(), pkt.AlphaW);
                    primFaces[key] = entry;
                }
                for (int i = 2; i < nv; i++)
                {
                    int a = vStart + i - 2, b_ = vStart + i - 1, c = vStart + i;
                    // Tri-strip base winding (consistent within the strip).
                    (int X, int Y, int Z) tri = (i % 2 == 0) ? (a, b_, c) : (b_, a, c);
                    // PS2 stores strips with hardware-arbitrary winding (the GS never
                    // back-face culls), so faces end up pointing in/out at random. Flip
                    // each triangle whose geometric normal disagrees with the stored
                    // vertex normals, giving consistent outward facing for glTF
                    // back-face culling. (No normal -> vn==0 -> keep strip winding.)
                    Vector3 pa = bm.Vertices[tri.X], pb = bm.Vertices[tri.Y], pc = bm.Vertices[tri.Z];
                    Vector3 vn = bm.Normals[tri.X] + bm.Normals[tri.Y] + bm.Normals[tri.Z];
                    if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), vn) < 0f)
                        tri = (tri.Y, tri.X, tri.Z);
                    entry.Faces.Add((tri.X, tri.Y, tri.Z));
                }
            }
        }

        foreach (var k in primFaces.Keys
                     .OrderBy(x => x.Tex).ThenBy(x => x.IsAlt).ThenBy(x => x.Alpha)
                     .ThenBy(x => x.Q1).ThenBy(x => x.Q0).ThenBy(x => x.Q2)
                     .ThenBy(x => x.GsAlpha).ThenBy(x => x.GsTest).ThenBy(x => x.GsClamp).ThenBy(x => x.Prim))
        {
            var entry = primFaces[k];
            bm.Primitives.Add(new Primitive {
                Key = k,
                AlphaW = entry.AlphaW,
                Faces = entry.Faces,
            });
        }
        return bm;
    }

    private static void WriteGlb(string outPath,
        List<GNode> nodes,
        Dictionary<int, GNode> mslotMap,
        BuiltMesh built,
        Dictionary<int, Matrix4x4> bind,
        List<DecodedTexInfo> textures,
        List<bool> texHasAlpha,
        List<AnimSlot> animations,
        float animTransScale,
        int nodeTreeSize,
        Dictionary<int, TexAnimBake> texAnims,
        Dictionary<int, System.Text.Json.Nodes.JsonArray> texAnimRecords)
    {
        // Ref-bone (particle-effect anchor) index = a node's depth-first visit index, which
        // is exactly GNode.Idx (WalkGNodes visits child-before-sibling, root = 0). The engine
        // can only anchor an effect to a node whose DFS index is < refBoneNum, where
        // refBoneNum = nodeTreeSize >> 6 (lSzModel_LoadCB@0x1e8bf0), nodeTreeSize being
        // section 0's size from header +0x14 (64 B per node on every retail model). Naming
        // each node with its ref index (and a _refOOR tag when the index is out of the usable
        // range) lets you see in Blender which nodes effects can lock onto. Names are
        // descriptive only — the importer matches nodes by tree position, not by name (it
        // special-cases just "MrgRoot" and the "_bone" scale children), so this is
        // round-trip-safe.
        int refBoneNum = nodeTreeSize >> 6;

        // Node layout (as DotR-PC's tools/gltf.py): one glTF node per RH0 node carrying
        // translation and rotation, and for each node with a matrix slot a child `_bone` node
        // carrying the accumulated scale. That is the RH0 rule (scale reaches the node's own
        // output matrix, never its children) expressed in glTF, where scale would otherwise
        // propagate. The `_bone` nodes are the skin's joints and the scale channels' targets.
        // A slotless node gets one too when its accumulated scale differs from its parent's
        // in any pose: retail animates scale on slotless parents (card 679), and without it
        // the importer could only push that scale down into every slot below as extra keys.
        var rest = EvalPose(nodes, null, 0, animTransScale);
        var baked = animations.OrderBy(a => a.SlotId)
            .Select(a => (Anim: a, Frames: Enumerable.Range(0, Math.Max(a.FrameCount, 1))
                .Select(t => EvalPose(nodes, a, t, animTransScale)).ToList()))
            .ToList();
        var ownScale = new bool[nodes.Count];
        foreach (var pose in baked.SelectMany(b => b.Frames).Prepend(rest))
            for (int i = 1; i < nodes.Count; i++)
                ownScale[i] |= pose.Acc[i] != pose.Acc[nodes[i].Pidx];
        var nodeBuilders = new NodeBuilder[nodes.Count];
        var boneBuilders = new Dictionary<int, NodeBuilder>(); // node index -> its _bone child
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            string refTag = n.Idx < refBoneNum ? $"_ref{n.Idx}" : "_refOOR";
            var nb = new NodeBuilder($"node_{n.Idx}{refTag}_f{n.Flags:X4}" + (n.Mslot != 0 ? $"_m{n.Mslot}" : ""));
            nb.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
                Vector3.One, rest.LocalR[i], rest.LocalT[i]);
            nodeBuilders[i] = nb;
        }
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Mslot != 0 || ownScale[i])
            {
                var bone = nodeBuilders[i].CreateNode($"node_{i}_bone");
                bone.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
                    rest.Acc[i], Quaternion.Identity, Vector3.Zero);
                boneBuilders[i] = bone;
            }
            foreach (var ch in nodes[i].Children)
                nodeBuilders[i].AddNode(nodeBuilders[ch]);
        }

        // Recenter the whole model on GNode(1) (the root's first child) — the engine's own
        // centre-of-model (SzModel_GetCenterOfs = GNode(1).pos / animTransScale, placed via
        // SzModel_SetPos), so the extracted glb sits the way the duel frames the monster
        // instead of hanging off the world origin. The shift rides on the MrgRoot wrapper
        // AND the baked vertices together (a skinned mesh won't move from the wrapper alone —
        // the inverse-bind matrices cancel it). Round-trip is byte-identical: GlbImporter reads
        // each node's world matrix with the wrapper folded in while node-0's local stays put,
        // so the quantised locals are invariant (verified) and the importer needs no change.
        // A user-added 'origin' empty overrides this pivot on re-import (see PrepareRoot).
        var pivot = nodes.Count > 1 ? nodes[1].RestWorld.Translation : Vector3.Zero;

        var mrgRoot = new NodeBuilder("MrgRoot");
        mrgRoot.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
            Vector3.One, System.Numerics.Quaternion.Identity, -pivot);
        mrgRoot.AddNode(nodeBuilders[0]);

        var materials = new Dictionary<PrimKey, MaterialBuilder>();
        var blendStates = new Dictionary<string, DotrBlendState>(StringComparer.Ordinal);

        MaterialBuilder GetMaterial(PrimKey key, float alphaW)
        {
            int texIdx = key.Tex;
            if (texIdx < 0 || texIdx >= textures.Count) texIdx = 0;
            key = key with { Tex = texIdx };
            if (materials.TryGetValue(key, out var existing)) return existing;
            bool textured = (key.Prim & PrimTme) != 0;
            bool abe = (key.Prim & PrimAbe) != 0;
            bool isAlt = key.IsAlt;
            var q1Key = key.Q1;
            var (gsAlpha, gsTest, gsClamp) = (key.GsAlpha, key.GsTest, key.GsClamp);
            // An untextured material must not be named mat_tex*: GlbImporter would take the
            // name as a texture slot and fill that slot with its flat colour.
            string matName = (textured ? $"mat_tex{texIdx}" : "mat_untextured")
                             + $"_a{key.Alpha}_gs{(byte)gsAlpha:X2}_t{(uint)(gsTest & 0xFFFFF):X5}_c{(byte)gsClamp:X}"
                             + (textured ? "" : $"_rgb{q1Key.R:X2}{q1Key.G:X2}{q1Key.B:X2}")
                             // the colour block, so pages on one texture with different c0/c1/c2
                             // stay separate materials (DOTR_blend extras are looked up by name)
                             + $"_k{key.Q0.R:X2}{key.Q0.G:X2}{key.Q0.B:X2}{q1Key.R:X2}{q1Key.G:X2}{q1Key.B:X2}{key.Q2.R:X2}{key.Q2.G:X2}{key.Q2.B:X2}"
                             + (isAlt ? "_alt" : "");
            var mat = new MaterialBuilder(matName)
                .WithDoubleSide(true)
                .WithMetallicRoughnessShader()
                .WithMetallicRoughness(0f, 1f);
            var tex = textures[texIdx];
            if (textured)
            {
                var png = PngEncoder.Encode(tex.Width, tex.Height, tex.Rgba);
                mat.WithChannelImage(KnownChannel.BaseColor, new MemoryImage(png));

                // Reproduce the GS sampler. glTF defaults to REPEAT on both axes, which makes
                // bilinear filtering at U=0 blend in the opposite edge of the texture (the
                // atlas-seam line on the world-map arena bases); the game clamps those axes
                // via CLAMP_1. Retail TEX1_1 is always 0x60 (bilinear, no mipmaps), so also
                // keep viewers from generating mips that smear neighbouring atlas regions.
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
                TextureBuilder texBuilder = mat.GetChannel(KnownChannel.BaseColor).Texture;
                texBuilder.WithSampler(wrapS, wrapT, TextureMipMapFilter.LINEAR, TextureInterpolationFilter.LINEAR);
            }

            // Opaque chain: lit by VU1 microcode from c0/c1/c2, which nobody has decoded (and
            // scaled by SzModel_SetIntensity's default 0.8 first). Export the +0x20 row (c1)
            // at FULL magnitude (q1Key/255), not max-normalised: retail neutral is 178, and
            // dropping the magnitude made every re-import default back to over-bright 255.
            // This is an approximation; the raw rows are kept in the extras.
            float r = q1Key.R / 255f;
            float g = q1Key.G / 255f;
            float b = q1Key.B / 255f;
            bool clamped = false;
            if (isAlt)
            {
                // Alpha chain: drawn unlit by the CPU (RhModelCpu_DrawAlpha @0x212FD0), one
                // RGBA per colour block from lRhModelCpu_TraceDynamicBlock @0x2131D0:
                // RGB = c0 + c1 / 2, A = c1.w, which the GS multiplies with the texture
                // (0x80 = 1.0). Typical 127 + 178/2 = 216 is ~1.7x the texture; glTF can't
                // brighten past 1, so it is clamped (noted in the extras).
                var q0 = key.Q0;
                float Lit(int c0, int c1)
                {
                    float v = (c0 + c1 / 2f) / Ps2FullAlpha;
                    clamped |= v > 1f;
                    return Math.Min(v, 1f);
                }
                r = Lit(q0.R, q1Key.R);
                g = Lit(q0.G, q1Key.G);
                b = Lit(q0.B, q1Key.B);
            }

            bool textureHasAlpha = textured && texIdx < texHasAlpha.Count && texHasAlpha[texIdx];

            // PS2 colour alpha uses 0x80 as a factor of 1.0. This factor is
            // multiplied by texture alpha by the GS texture function before the
            // ALPHA equation consumes As.
            float a = Math.Clamp(alphaW / Ps2FullAlpha, 0f, 1f);

            string blendMode = ClassifyBlendMode(gsAlpha, gsTest, a, abe, textureHasAlpha);
            float alphaCutoff = GetAlphaCutoff(gsTest);

            switch (blendMode)
            {
                case "additive":
                case "subtract":
                case "alpha":
                    mat.WithAlpha(AlphaMode.BLEND);
                    break;
                case "mask":
                    mat.WithAlpha(AlphaMode.MASK, alphaCutoff);
                    break;
                default:
                    // Always state OPAQUE explicitly. Every embedded texture is RGBA,
                    // and relying on library/image inference can incorrectly place a
                    // fully opaque model in the transparent no-depth-write pass.
                    mat.WithAlpha(AlphaMode.OPAQUE);
                    a = 1f;
                    break;
            }

            // The +0x20 colour normally behaves like a material-lighting factor, but
            // some retail models use (0,0,0) as a sentinel for the default/unmodulated
            // texture colour. Multiplying the glTF base colour by literal black makes
            // those textures disappear; booKoo's book page is the known example.
            // Preserve the original value in DOTR metadata, but render it as neutral.
            bool isZeroColourSentinel = !isAlt &&
                q1Key.R == 0 && q1Key.G == 0 && q1Key.B == 0;

            // Omit the exact retail-neutral grey, and the zero sentinel, for a fully
            // opaque textured material. An omitted glTF base-colour factor is white, which
            // leaves the decoded texture unchanged. Transparent/additive materials still need
            // their alpha factor, so use white RGB for a zero sentinel in those modes.
            bool omitBaseColour = textured && blendMode == "opaque" &&
                                  ((!isAlt && q1Key.R == 178 && q1Key.G == 178 && q1Key.B == 178) ||
                                   isZeroColourSentinel);
            var display = isZeroColourSentinel ? new Vector3(1f) : new Vector3(r, g, b);
            if (!omitBaseColour)
                mat.WithBaseColor(new Vector4(display, a));

            blendStates[matName] = new DotrBlendState(
                blendMode, alphaCutoff, a, gsAlpha, gsTest,
                q1Key.R, q1Key.G, q1Key.B, key.Q0, key.Q2, alphaW, abe, textured,
                omitBaseColour ? Vector3.One : display, clamped);
            materials[key] = mat;
            return mat;
        }

        // The skin's joints are the `_bone` nodes, in slot order. Inverse bind = the bind
        // matrix BuildMesh placed the vertices with (rest pose, or the frame
        // BindMatrices picked for a part shrunk at rest), shifted by the MrgRoot pivot like the vertices.
        var joints = built.SortedMslots
            .Select(m => boneBuilders[mslotMap[m].Idx])
            .ToArray();
        var pivotShift = M.Translation(-pivot.X, -pivot.Y, -pivot.Z);
        var invBind = built.SortedMslots
            .Select(m =>
            {
                Matrix4x4.Invert(bind[m] * pivotShift, out var inv);
                return inv;
            })
            .ToArray();

        // Rigid mode: when no vertex blends more than one bone (every MODEL.MRG model, and any
        // monster made only of INDEP strips), a skin adds nothing — each bone's geometry just
        // rides its node. Emit one plain mesh per bone, attached to that bone's `_bone` node,
        // with vertices in its bind space (the inverse bind matrix above), so a part at scale 0
        // in the rest pose still has real geometry. glTF-wise this is exactly the skinned
        // result (skin matrix = jointWorld * inverseBind), minus the armature. GlbImporter
        // recognises this shape (extracted, no skin, `_m<slot>` node names) and rebuilds the
        // same joint set.
        bool rigid = built.VertJoints.All(j => j.Count(x => x.W > 0f) <= 1);
        // glTF takes influences in sets of four; a vertex carries up to 8
        int maxInfluences = built.VertJoints.Count == 0 ? 0 : built.VertJoints.Max(j => j.Count(x => x.W > 0f));

        var skinnedPrims = new List<(MaterialBuilder Mat, Primitive Prim)>();
        var rigidMeshes = new Dictionary<int, MeshBuilder<VertexPositionNormal, VertexTexture1>>();
        foreach (var prim in built.Primitives)
        {
            if (prim.Key.IsAlt && prim.AlphaW == 0f) continue;
            var mat = GetMaterial(prim.Key, prim.AlphaW);
            if (!rigid)
            {
                skinnedPrims.Add((mat, prim));
                continue;
            }
            foreach (var (a, b, c) in prim.Faces)
            {
                // a strip binds one bone, so all three corners share it
                int j = RigidJoint(built, a);
                if (!rigidMeshes.TryGetValue(j, out var mb))
                {
                    mb = new MeshBuilder<VertexPositionNormal, VertexTexture1>($"mesh_m{built.SortedMslots[j]}");
                    rigidMeshes[j] = mb;
                }
                var invW = invBind[j];
                mb.UsePrimitive(mat).AddTriangle(
                    MakeRigidVertex(built, a, pivot, invW),
                    MakeRigidVertex(built, b, pivot, invW),
                    MakeRigidVertex(built, c, pivot, invW));
            }
        }

        WriteAnimations(nodes, baked, rest, nodeBuilders, boneBuilders);

        var scene = new SceneBuilder("MrgScene");
        scene.AddNode(mrgRoot);
        if (rigid)
        {
            foreach (var (j, mb) in rigidMeshes.OrderBy(kv => kv.Key))
                scene.AddRigidMesh(mb, joints[j]);
        }
        else if (skinnedPrims.Any(p => p.Prim.Faces.Count > 0))
        {
            var skin = joints.Zip(invBind, (j, ib) => (j, ib)).ToArray();
            if (maxInfluences > 4)
                AddSkinnedMesh<VertexJoints8>(scene, built, skinnedPrims, pivot, skin, 8);
            else
                AddSkinnedMesh<VertexJoints4>(scene, built, skinnedPrims, pivot, skin, 4);
        }

        var model = scene.ToGltf2();
        InjectBlendMetadata(model, blendStates);
        if (texAnims.Count > 0) InjectTextureAnimations(model, texAnims);
        InjectTextureAnimationRecords(model, texAnimRecords);
        model.SaveGLB(outPath);
        ReorderGlbAnimations(outPath);
    }

    private static void AddSkinnedMesh<TJ>(SceneBuilder scene, BuiltMesh built,
        List<(MaterialBuilder Mat, Primitive Prim)> prims, Vector3 pivot,
        (NodeBuilder Joint, Matrix4x4 InverseBind)[] joints, int maxInfluences)
        where TJ : struct, IVertexSkinning
    {
        var mb = new MeshBuilder<VertexPositionNormal, VertexTexture1, TJ>("MrgMesh");
        foreach (var (mat, prim) in prims)
        {
            var pb = mb.UsePrimitive(mat);
            foreach (var (a, b, c) in prim.Faces)
                pb.AddTriangle(
                    MakeVertex<TJ>(built, a, pivot, maxInfluences),
                    MakeVertex<TJ>(built, b, pivot, maxInfluences),
                    MakeVertex<TJ>(built, c, pivot, maxInfluences));
        }
        scene.AddSkinnedMesh(mb, joints);
    }

    // Every motion baked at every frame 0..frames-1 with the game's own evaluator (EvalPose),
    // as DotR-PC's tools/gltf.py does. Writing the game's keys as glTF keys would be wrong
    // twice over: glTF slerps quaternions where the game lerps each Euler axis the short way
    // round, and a list with fewer than 3 keys plays key 0 only. Translation and rotation go
    // on the node, accumulated scale on its `_bone` child. A channel is written in every
    // motion for each node that moves in any of them, so switching animations never leaves a
    // node in another motion's pose.
    private static void WriteAnimations(List<GNode> nodes, List<(AnimSlot Anim, List<Pose> Frames)> baked,
        Pose rest, NodeBuilder[] nodeBuilders, Dictionary<int, NodeBuilder> boneBuilders)
    {
        const float FPS = 30f;

        float size = 1e-6f;
        foreach (var n in nodes)
            size = Math.Max(size, rest.World[n.Idx].Translation.Length());
        float eps = size * 1e-6f;
        float scaleEps = 1e-6f * Math.Max(rest.Acc[0].Length(), 1e-6f);

        bool[] movesT = new bool[nodes.Count], movesR = new bool[nodes.Count], movesS = new bool[nodes.Count];
        foreach (var (_, frames) in baked)
            foreach (var p in frames)
                for (int i = 0; i < nodes.Count; i++)
                {
                    movesT[i] |= Vector3.Distance(p.LocalT[i], rest.LocalT[i]) > eps;
                    movesR[i] |= 1f - MathF.Abs(Quaternion.Dot(p.LocalR[i], rest.LocalR[i])) > 1e-7f;
                    movesS[i] |= Vector3.Distance(p.Acc[i], rest.Acc[i]) > scaleEps;
                }

        foreach (var (anim, frames) in baked)
        {
            string animName = $"slot_{anim.SlotId}";
            for (int i = 0; i < nodes.Count; i++)
            {
                if (movesT[i])
                {
                    var curve = nodeBuilders[i].UseTranslation(animName);
                    for (int t = 0; t < frames.Count; t++)
                        curve.SetPoint(t / FPS, frames[t].LocalT[i]);
                }
                if (movesR[i])
                {
                    var curve = nodeBuilders[i].UseRotation(animName);
                    var prev = rest.LocalR[i];
                    for (int t = 0; t < frames.Count; t++)
                    {
                        // keep consecutive keys on one hemisphere so LINEAR slerps the short way
                        var q = frames[t].LocalR[i];
                        if (Quaternion.Dot(q, prev) < 0f) q = Quaternion.Negate(q);
                        curve.SetPoint(t / FPS, q);
                        prev = q;
                    }
                }
                if (movesS[i] && boneBuilders.TryGetValue(i, out var bone))
                {
                    var curve = bone.UseScale(animName);
                    for (int t = 0; t < frames.Count; t++)
                        curve.SetPoint(t / FPS, frames[t].Acc[i]);
                }
            }
        }
    }

    // Blending is the strip's GIF PRIM ABE bit, not the ALPHA register: ABE is set on exactly
    // the 8,176 alpha-chain monster strips and on none of the 510,849 opaque ones, while twelve
    // opaque blocks carry an additive ALPHA value to no effect. The equation is the ALPHA
    // register, Cv = (A - B) * C / 128 + D; the alpha comes from c1.w, not the textures
    // (card 682's all decode fully opaque).
    private static string ClassifyBlendMode(ulong gsAlpha, ulong gsTest, float materialAlpha, bool abe,
        bool textureHasAlpha)
    {
        int aSel = (int)(gsAlpha & 0x3);
        int bSel = (int)((gsAlpha >> 2) & 0x3);
        int cSel = (int)((gsAlpha >> 4) & 0x3);
        int dSel = (int)((gsAlpha >> 6) & 0x3);
        int fix = (int)((gsAlpha >> 32) & 0xFF);

        bool alphaTest = (gsTest & 1UL) != 0;
        int atst = (int)((gsTest >> 1) & 0x7);
        int aref = (int)((gsTest >> 4) & 0xFF);

        // A test that only rejects As == 0 (GREATER 0, or ALWAYS) changes nothing once
        // blending is scaling by As, so with a sub-1 material alpha it is just a
        // "skip empty texels" guard on a blended page, not a cutout (forest foliage:
        // TEST 0x7000D, ALPHA 0x48, material alpha 0x26).
        bool trivialTest = (atst == 6 && aref == 0) || atst == 1;

        // Alpha-tested pages are cutouts first (TEST 0x703FD: ATE, GREATER, AREF 63), even
        // when they also blend. This is the Bagworm flipbook case.
        if (alphaTest && textureHasAlpha && !(trivialTest && materialAlpha < 0.999f)) return "mask";
        if (!abe) return "opaque";

        bool sourceAlphaAdditive = aSel == 0 && bSel == 2 && cSel == 0 && dSel == 1; // 0x48
        bool fixedAdditive = aSel == 0 && bSel == 2 && cSel == 2 && dSel == 1 && fix > 0;
        bool subtract = aSel == 2 && bSel == 0 && cSel == 0 && dSel == 1;           // 0x42
        if (sourceAlphaAdditive || fixedAdditive) return "additive";
        if (subtract) return "subtract";
        return "alpha";                                                            // 0x44
    }

    private static float GetAlphaCutoff(ulong gsTest)
    {
        if ((gsTest & 1UL) == 0) return 0.5f;
        int atst = (int)((gsTest >> 1) & 0x7);
        int alphaRef = (int)((gsTest >> 4) & 0xFF);
        // glTF MASK passes alpha >= cutoff; GS GREATER is strict, so step past AREF
        // (otherwise AREF 0 becomes cutoff 0 and every texel renders opaque).
        if (atst == 6) alphaRef++;
        return Math.Clamp(alphaRef / Ps2FullAlpha, 0f, 1f);
    }

    private static void InjectBlendMetadata(ModelRoot model,
        IReadOnlyDictionary<string, DotrBlendState> blendStates)
    {
        foreach (var mat in model.LogicalMaterials)
        {
            if (mat.Name == null || !blendStates.TryGetValue(mat.Name, out var state)) continue;

            var extras = mat.Extras as System.Text.Json.Nodes.JsonObject
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
                ["primAbe"] = state.Abe,
                ["textured"] = state.Textured,
                // the base colour factor written for this material; GlbImporter restores the
                // raw +0x20 row when the factor is still this
                ["displayColor"] = new System.Text.Json.Nodes.JsonArray(state.Display.X, state.Display.Y, state.Display.Z),
                ["displayColorClamped"] = state.DisplayClamped,
            };
            mat.Extras = extras;
        }
    }

    // Bakes the texture-animation frames into the glTF as extra images and tags
    // each affected material with a "DOTR_texAnim" extras block describing the
    // frame image indices and their timestamps. The custom viewer reads this and
    // swaps the bound texture over time (core glTF can't animate base-color
    // images, so this is carried as extras rather than a standard channel).
    private static void InjectTextureAnimations(ModelRoot model, Dictionary<int, TexAnimBake> texAnims)
    {
        foreach (var (texIdx, bake) in texAnims)
        {
            if (bake.FramesRgba.Count < 2) continue;

            var imgIndices = new int[bake.FramesRgba.Count];
            for (int f = 0; f < bake.FramesRgba.Count; f++)
            {
                var png = PngEncoder.Encode(bake.Width, bake.Height, bake.FramesRgba[f]);
                var img = model.CreateImage();
                img.Content = new MemoryImage(png);
                // Named so a modder can find the frames in Blender and so GlbImporter can
                // route them back (the flipbook path's own "tex{N}_anim{frame}" naming; a
                // slot that also carries DOTR_texAnimRecords takes the write-back path in
                // GlbImporter.ApplyEditedAnimFrames instead of being repacked as a sheet).
                img.Name = $"tex{texIdx}_anim{f}";
                imgIndices[f] = img.LogicalIndex;
            }

            var frameJson = new System.Text.StringBuilder();
            frameJson.Append('[');
            for (int f = 0; f < imgIndices.Length; f++)
            {
                if (f > 0) frameJson.Append(',');
                float t = f < bake.FrameTimes.Count ? bake.FrameTimes[f] : 0f;
                frameJson.Append("{\"image\":").Append(imgIndices[f])
                    .Append(",\"t\":").Append(t.ToString(System.Globalization.CultureInfo.InvariantCulture));
                // The GS copies this frame is made of: which record, which cell of its
                // sheet, and a hash of the pixels as extracted. GlbImporter compares the
                // hash with the frame image it is given: unchanged means the base image
                // still owns those pixels (an edited base cell survives), changed means
                // the modder painted the frame, and the new pixels are written back into
                // the record's source cell.
                frameJson.Append(",\"stamps\":[");
                var stamps = f < bake.FrameStamps.Count ? bake.FrameStamps[f] : new List<TexAnimStamp>();
                for (int k = 0; k < stamps.Count; k++)
                {
                    if (k > 0) frameJson.Append(',');
                    var st = stamps[k];
                    frameJson.Append("{\"rec\":").Append(st.Rec)
                        .Append(",\"cell\":").Append(st.Cell)
                        .Append(",\"hash\":").Append(RectHash(bake.FramesRgba[f], bake.Width, bake.Height,
                            st.DstX, st.DstY, st.W, st.H))
                        .Append('}');
                }
                frameJson.Append("]}");
            }
            frameJson.Append(']');

            string dur = bake.Duration.ToString(System.Globalization.CultureInfo.InvariantCulture);

            foreach (var mat in model.LogicalMaterials)
            {
                if (!MaterialUsesTexture(mat.Name, texIdx)) continue;

                string animJson = "{\"duration\":" + dur +
                                  ",\"frames\":" + frameJson + "}";

                var extras = mat.Extras as System.Text.Json.Nodes.JsonObject
                             ?? new System.Text.Json.Nodes.JsonObject();
                extras["DOTR_texAnim"] = System.Text.Json.Nodes.JsonNode.Parse(animJson);
                mat.Extras = extras;
            }
        }
    }

    // The raw records (SzModel_LoadTexAnim_Pipe @0x1EA410), 24 B each, the last with bit 0
    // set. mode: 0/1 loop, 2/3 ping-pong (0 and 2 treat cell 0 as "no copy"), 4 window
    // p0 <= f <= p1, 5 one shot at p0 for p1 ticks, 6 blink every p0 for p1 ticks.
    // motions = per-slot mask; ownClock: runs on its own counter (+0x14), not the motion frame.
    private static System.Text.Json.Nodes.JsonArray TexAnimRecords(byte[] data, int at)
    {
        var recs = new System.Text.Json.Nodes.JsonArray();
        for (int n = 0; n < 256 && at + 24 <= data.Length; n++, at += 24)
        {
            byte fl = data[at];
            recs.Add(new System.Text.Json.Nodes.JsonObject {
                ["mode"] = (fl >> 2) & 7,
                ["direction"] = fl >> 5,
                ["ownClock"] = (fl & 2) != 0,
                ["motions"] = data[at + 1],
                ["cols"] = data[at + 2] & 15,
                ["rows"] = data[at + 2] >> 4,
                ["state"] = data[at + 3],
                ["p0"] = U16(data, at + 4),
                ["p1"] = U16(data, at + 6),
                ["size"] = new System.Text.Json.Nodes.JsonArray(U16(data, at + 8), U16(data, at + 10)),
                ["src"] = new System.Text.Json.Nodes.JsonArray(U16(data, at + 12), U16(data, at + 14)),
                ["dst"] = new System.Text.Json.Nodes.JsonArray(U16(data, at + 16), U16(data, at + 18)),
            });
            if ((fl & 1) != 0) break;
        }
        return recs;
    }

    // FNV-1a over one rectangle of an RGBA image, clipped like TexAnim.Stamp. The pair
    // with GlbImporter.RectHash has to stay byte-for-byte the same walk.
    internal static uint RectHash(byte[] rgba, int w, int h, int x0, int y0, int rw, int rh)
    {
        uint hash = 2166136261u;
        for (int y = 0; y < rh; y++)
        {
            int yy = y0 + y;
            if ((uint)yy >= (uint)h) continue;
            for (int x = 0; x < rw; x++)
            {
                int xx = x0 + x;
                if ((uint)xx >= (uint)w) continue;
                int o = (yy * w + xx) * 4;
                for (int c = 0; c < 4; c++) hash = (hash ^ rgba[o + c]) * 16777619u;
            }
        }
        return hash;
    }

    private static void InjectTextureAnimationRecords(ModelRoot model,
        Dictionary<int, System.Text.Json.Nodes.JsonArray> records)
    {
        foreach (var (texIdx, recs) in records)
            foreach (var mat in model.LogicalMaterials)
            {
                if (!MaterialUsesTexture(mat.Name, texIdx)) continue;
                var extras = mat.Extras as System.Text.Json.Nodes.JsonObject
                             ?? new System.Text.Json.Nodes.JsonObject();
                extras["DOTR_texAnimRecords"] = recs.DeepClone();
                mat.Extras = extras;
            }
    }

    private static bool MaterialIsAlt(string? name) =>
        name?.EndsWith("_alt", StringComparison.OrdinalIgnoreCase) == true;

    // Materials are named "mat_tex{idx}" optionally with "_alt"/suffixes; match
    // on the leading integer so tex1 doesn't also catch tex10.
    private static bool MaterialUsesTexture(string? name, int texIdx)
    {
        const string prefix = "mat_tex";
        if (name == null || !name.StartsWith(prefix)) return false;
        int i = prefix.Length, val = 0;
        if (i >= name.Length || !char.IsDigit(name[i])) return false;
        while (i < name.Length && char.IsDigit(name[i]))
        {
            val = val * 10 + (name[i] - '0');
            i++;
        }
        return val == texIdx;
    }

    private static void ReorderGlbAnimations(string glbPath)
    {
        byte[] glb;
        try
        {
            glb = File.ReadAllBytes(glbPath);
        }
        catch
        {
            return;
        }
        if (glb.Length < 20) return;
        if (BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(0, 4)) != 0x46546C67) return;
        uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        uint jsonType = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16, 4));
        if (jsonType != 0x4E4F534A) return;
        int jsonStart = 20;
        int jsonEnd = jsonStart + (int)jsonLen;
        if (jsonEnd > glb.Length) return;
        string json = System.Text.Encoding.UTF8.GetString(glb, jsonStart, (int)jsonLen);

        var doc = System.Text.Json.Nodes.JsonNode.Parse(json);
        if (doc?["animations"] is not System.Text.Json.Nodes.JsonArray anims || anims.Count < 2) return;

        var items = new List<System.Text.Json.Nodes.JsonNode?>();
        foreach (var a in anims) items.Add(a);
        anims.Clear();

        int SlotKey(System.Text.Json.Nodes.JsonNode? n)
        {
            string? name = n?["name"]?.GetValue<string>();
            if (name != null && name.StartsWith("slot_") &&
                int.TryParse(name.AsSpan(5), out int s)) return s;
            return int.MaxValue;
        }

        foreach (var a in items.OrderBy(SlotKey))
            anims.Add(a);

        string newJson = doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        var newJsonBytes = System.Text.Encoding.UTF8.GetBytes(newJson);
        int pad = (4 - (newJsonBytes.Length & 3)) & 3;
        int paddedLen = newJsonBytes.Length + pad;

        byte[] binChunk = Array.Empty<byte>();
        if (jsonEnd + 8 <= glb.Length)
        {
            uint binLen = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(jsonEnd, 4));
            uint binType = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(jsonEnd + 4, 4));
            if (binType == 0x004E4942 && jsonEnd + 8 + (int)binLen <= glb.Length)
            {
                binChunk = new byte[8 + (int)binLen];
                Array.Copy(glb, jsonEnd, binChunk, 0, binChunk.Length);
            }
        }

        int totalLen = 12 + 8 + paddedLen + binChunk.Length;
        using var fs = new FileStream(glbPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(0x46546C67u);
        bw.Write(2u);
        bw.Write((uint)totalLen);
        bw.Write((uint)paddedLen);
        bw.Write(0x4E4F534Au);
        bw.Write(newJsonBytes);
        for (int i = 0; i < pad; i++) bw.Write((byte)0x20);
        if (binChunk.Length > 0) bw.Write(binChunk);
    }

    // Normalize with a zero guard (Vector3.Normalize(0) yields NaN). Used only for
    // winding orientation, so direction matters, magnitude does not.
    private static Vector3 NSafe(Vector3 v)
    {
        float m = v.Length();
        return m > 1e-6f ? v / m : Vector3.Zero;
    }

    // Influences heaviest first, weights normalised across every set together, so a viewer
    // that reads only JOINTS_0 degrades to the four heaviest.
    private static (VertexPositionNormal, VertexTexture1, TJ) MakeVertex<TJ>(BuiltMesh bm, int vi, Vector3 pivot,
        int maxInfluences) where TJ : struct, IVertexSkinning
    {
        var pos = bm.Vertices[vi] - pivot;
        var nrm = bm.Normals[vi];
        if (nrm == Vector3.Zero) nrm = Vector3.UnitY; // glTF requires a unit normal
        var uv = bm.Uvs[vi];
        var top = bm.VertJoints[vi].Where(x => x.W > 0f).OrderByDescending(x => x.W).Take(maxInfluences).ToList();
        float wsum = top.Sum(x => x.W);
        if (top.Count == 0 || wsum <= 0f)
            top = new List<(int Joint, float W)> { (0, 1f) };
        else
            for (int k = 0; k < top.Count; k++) top[k] = (top[k].Joint, top[k].W / wsum);
        var jb = new TJ();
        jb.SetBindings(top.Select(x => (x.Joint, x.W)).ToArray());
        return (new VertexPositionNormal(pos, nrm), new VertexTexture1(uv), jb);
    }

    // The single bone a rigid vertex rides; an unbound vertex falls back to joint 0, as
    // MakeVertex does.
    private static int RigidJoint(BuiltMesh bm, int vi)
    {
        foreach (var (joint, w) in bm.VertJoints[vi])
            if (w > 0f) return joint;
        return 0;
    }

    private static (VertexPositionNormal, VertexTexture1) MakeRigidVertex(BuiltMesh bm, int vi, Vector3 pivot,
        Matrix4x4 invNodeWorld)
    {
        var pos = Vector3.Transform(bm.Vertices[vi] - pivot, invNodeWorld);
        var nrm = bm.Normals[vi];
        nrm = nrm == Vector3.Zero ? Vector3.UnitY : NSafe(Vector3.TransformNormal(nrm, invNodeWorld));
        return (new VertexPositionNormal(pos, nrm), new VertexTexture1(bm.Uvs[vi]));
    }
}

readonly struct Rh0HeaderFull
{
    public readonly uint Magic;
    public readonly uint SectionFlags;
    public readonly uint MotTable;
    public readonly uint NodeTree, NodeTreeSize;
    public readonly uint Section1, Section1Size;
    public readonly uint Section2, Section2Size;
    public readonly uint Section1Alt, Section1AltSize;
    public readonly uint Section2Alt, Section2AltSize;
    public readonly uint TexTable, TexCount;
    public readonly uint RelocWalk, RelocWalkSize;
    public readonly float AnimTransScale;

    public Rh0HeaderFull(ReadOnlySpan<byte> data)
    {
        Magic = U32(data, 0);
        if (Magic != 0x00304852) throw new InvalidDataException($"Not RH0 magic=0x{Magic:x8}");
        SectionFlags = U32(data, 0x04);
        MotTable = U32(data, 0x0C);
        NodeTree = U32(data, 0x10);
        NodeTreeSize = U32(data, 0x14);
        Section1 = U32(data, 0x18);
        Section1Size = U32(data, 0x1C);
        Section2 = U32(data, 0x20);
        Section2Size = U32(data, 0x24);
        Section1Alt = U32(data, 0x28);
        Section1AltSize = U32(data, 0x2C);
        Section2Alt = U32(data, 0x30);
        Section2AltSize = U32(data, 0x34);
        TexTable = U32(data, 0x38);
        TexCount = U32(data, 0x3C);
        RelocWalk = U32(data, 0x50);
        RelocWalkSize = U32(data, 0x54);
        AnimTransScale = BitConverter.Int32BitsToSingle(unchecked((int)U32(data, 0x64)));
    }
}

sealed class GNode
{
    public int Idx;
    public int Pidx;
    public ushort Flags;
    public int Mslot;
    // as stored in the node (+0x00 scale, +0x10 rotation, +0x20 position)
    public Vector3 RawScale;
    public Vector3 RawPos;
    public (int X, int Y, int Z) RawRot;
    public Matrix4x4 RestWorld;
    public List<int> Children = new();
}

readonly record struct DotrBlendState(
    string Mode,
    float AlphaCutoff,
    float MaterialAlpha,
    ulong GsAlpha,
    ulong GsTest,
    int MaterialColorR,
    int MaterialColorG,
    int MaterialColorB,
    (int R, int G, int B) Color0,
    (int R, int G, int B) Color2,
    float AlphaW,
    bool Abe,
    bool Textured,
    Vector3 Display,
    bool DisplayClamped);

// One material's worth of draw state: the texture, the chain, the colour block (c0, c1 and
// c1.w as Alpha), the GS registers, and the strip's PRIM bits (ABE | TME).
readonly record struct PrimKey(
    int Tex,
    bool IsAlt,
    int Alpha,
    (int R, int G, int B) Q0,
    (int R, int G, int B) Q1,
    (int R, int G, int B) Q2,
    ulong GsAlpha,
    ulong GsTest,
    ulong GsClamp,
    int Prim);

sealed class PageInfo
{
    public int WalkOff;
    public Dictionary<int, int> BoneMap = new();
    public uint Sec2Off;
    public uint PktSize;
    public (float R, float G, float B) ColorQ0;
    public (float R, float G, float B) ColorQ1;
    public (float R, float G, float B) ColorQ2;
    public float AlphaW;
    public ulong GsAlpha;
    public ulong GsTest;
    public ulong GsClamp;
}

sealed class Packet
{
    public int WalkOff;
    public Dictionary<int, int> BoneMap = new();
    public List<Strip> Strips = new();
    public (float R, float G, float B) ColorQ0;
    public (float R, float G, float B) ColorQ1;
    public (float R, float G, float B) ColorQ2;
    public bool IsAlt;
    public float AlphaW;
    public ulong GsAlpha;
    public ulong GsTest;
    public ulong GsClamp;
}

sealed class Strip
{
    public string Type = "SHARED";
    public int VuAddr;
    public int Prim;   // GIF tag PRIM: 0x10 TME (textured), 0x40 ABE (alpha blended)
    public List<StripVertex> Verts = new();
}

sealed class StripVertex
{
    public (short U, short V) Uv;
    public (short X, short Y, short Z) Pos;
    public (short X, short Y, short Z) Nrm; // stored vertex normal (s16, 1.0 ~= 32767)
    public List<StripWeight>? Weights;
}

readonly struct StripWeight
{
    public readonly int Bone;
    public readonly short W;
    public readonly (short X, short Y, short Z) Pos;
    public readonly (short X, short Y, short Z) Nrm; // bind-space normal for this influence

    public StripWeight(int bone, short w, (short X, short Y, short Z) pos, (short X, short Y, short Z) nrm)
    {
        Bone = bone;
        W = w;
        Pos = pos;
        Nrm = nrm;
    }
}

sealed class BuiltMesh
{
    public List<Vector3> Vertices = new();
    public List<Vector3> Normals = new(); // output-space vertex normals (winding orientation only)
    public List<Vector2> Uvs = new();
    public List<List<(int Joint, float W)>> VertJoints = new();
    public List<Primitive> Primitives = new();
    public List<int> SortedMslots = new();
    public Dictionary<int, int> MslotToJoint = new();
}

sealed class Primitive
{
    public PrimKey Key;
    public float AlphaW;
    public List<(int A, int B, int C)> Faces = new();
}

sealed class AnimSlot
{
    public int SlotId;
    public int FrameCount;
    public bool Scaled;                     // type 7: entries carry a scale key list
    public Dictionary<int, AnimTrack> Tracks = new();
}

sealed class AnimTrack
{
    public List<(int Frame, float X, float Y, float Z)>? Trans;
    public List<(int Frame, int Rx, int Ry, int Rz)>? Rot;
    public List<(int Frame, float X, float Y, float Z)>? Scale;
}

readonly struct DecodedTexInfo
{
    public readonly int Width;
    public readonly int Height;
    public readonly byte[] Rgba;

    public DecodedTexInfo(int w, int h, byte[] rgba)
    {
        Width = w;
        Height = h;
        Rgba = rgba;
    }
}

static class M
{
    public const double BamsRad = Math.PI / 32768.0;
    public static Matrix4x4 Translation(float x, float y, float z) => Matrix4x4.CreateTranslation(x, y, z);
    public static Matrix4x4 RotX(int bams) => Matrix4x4.CreateRotationX((float)(bams * BamsRad));
    public static Matrix4x4 RotY(int bams) => Matrix4x4.CreateRotationY((float)(bams * BamsRad));
    public static Matrix4x4 RotZ(int bams) => Matrix4x4.CreateRotationZ((float)(bams * BamsRad));
    public static Matrix4x4 Scale(float sx, float sy, float sz) => Matrix4x4.CreateScale(sx, sy, sz);

    public static Vector3 Apply(Matrix4x4 m, float x, float y, float z)
        => Vector3.Transform(new Vector3(x, y, z), m);

    public static Quaternion EulerToQuatXyz(double rx, double ry, double rz)
    {
        double cx = Math.Cos(rx / 2), sx = Math.Sin(rx / 2);
        double cy = Math.Cos(ry / 2), sy = Math.Sin(ry / 2);
        double cz = Math.Cos(rz / 2), sz = Math.Sin(rz / 2);
        var qx = new Quaternion((float)sx, 0, 0, (float)cx);
        var qy = new Quaternion(0, (float)sy, 0, (float)cy);
        var qz = new Quaternion(0, 0, (float)sz, (float)cz);
        return qz * qy * qx;
    }
}