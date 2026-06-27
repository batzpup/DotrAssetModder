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

static class ModelExtractor
{
    public const uint Rh0Magic = 0x00304852;

    public static float AltAlphaDivisor = 255f;

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

    private static List<(int Off, int Len)> FindRh0Offsets(byte[] data)
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


        var mainPages = ParsePagesInSection(data, (int)hdr.Section1, (int)hdr.Section1Size, mainWalks);
        var altPages = (hdr.Section1Alt != 0 && altWalks.Count > 0)
            ? ParsePagesInSection(data, (int)hdr.Section1Alt, (int)hdr.Section1AltSize, altWalks)
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
                ColorQ1 = page.ColorQ1,
                IsAlt = false,
                AlphaW = 128.0f,
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
                ColorQ1 = page.ColorQ1,
                IsAlt = true,
                AlphaW = page.AlphaW,
            });
        }

        var built = BuildMesh(packets, mslotMap, mainDispatches, altDispatches);
        var animations = ParseMotSlots(data, hdr, nodes.Count);
        string outPath = Path.Combine(outDir, slotName + ".glb");
        WriteGlb(outPath, nodes, mslotMap, built, textures, texHasAlpha, animations, hdr.AnimTransScale, texAnims);
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
            var local = R * T;
            var world = local * parentW;
            var worldSkin = M.Scale(newAccum.X, newAccum.Y, newAccum.Z) * world;

            int idx = nodes.Count;
            var n = new GNode {
                Idx = idx,
                Pidx = pidx,
                Flags = flags,
                Mslot = mslot,
                RestPos = (rpx, rpy, rpz),
                RestRotBams = (rrx, rry, rrz),
                TransAccum = parentAccum,
                RestLocal = local,
                RestWorld = world,
                RestWorldSkin = worldSkin,
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

    private static List<PageInfo> ParsePagesInSection(
        byte[] data, int secBase, int secSize, List<int> walkOffsets)
    {
        var pages = new List<PageInfo>();
        var runningBoneMap = new Dictionary<int, int>();
        (float R, float G, float B) curQ1 = (255, 255, 255);
        float curAlphaW = 128f;
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
                    float q1r = U32f(data, secBase + scanP + 0x20);
                    float q1g = U32f(data, secBase + scanP + 0x24);
                    float q1b = U32f(data, secBase + scanP + 0x28);
                    float q1a = U32f(data, secBase + scanP + 0x2C);
                    if (q1r >= 0 && q1r <= 255 && q1g >= 0 && q1g <= 255 && q1b >= 0 && q1b <= 255)
                        curQ1 = (q1r, q1g, q1b);
                    if (q1a >= 0 && q1a <= 255) curAlphaW = q1a;
                    scanP += 0x40;
                }
                else scanP += 4;
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
                ColorQ1 = curQ1,
                AlphaW = curAlphaW,
            });
        }
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
            if (nc != nodeCount)
            {
                motOff += (int)size;
                continue;
            }
            uint motionOff = U32(data, motOff + 0x14);
            int mo = motOff + (int)motionOff;
            uint mdataOff = U32(data, mo);
            uint fc = U32(data, mo + 4);
            ushort mtype = U16(data, mo + 8);
            bool hasScale = (mtype & 4) != 0;
            int entSize = hasScale ? 24 : 16;
            int mdataBase = motOff + (int)mdataOff;
            var tracks = new Dictionary<int, AnimTrack>();
            for (int i = 0; i < nc; i++)
            {
                int eoff = mdataBase + i * entSize;
                uint pT, pR;
                int cT, cR;
                if (hasScale)
                {
                    pT = U32(data, eoff);
                    pR = U32(data, eoff + 4);
                    cT = (int)U32(data, eoff + 12);
                    cR = (int)U32(data, eoff + 16);
                }
                else
                {
                    pT = U32(data, eoff);
                    pR = U32(data, eoff + 4);
                    cT = (int)U32(data, eoff + 8);
                    cR = (int)U32(data, eoff + 12);
                }
                var track = new AnimTrack();
                if (pT != 0 && cT > 0)
                {
                    var kfs = new List<(int, float, float, float)>(cT);
                    for (int k = 0; k < cT; k++)
                    {
                        int koff = motOff + (int)pT + k * 16;
                        int fr = I32(data, koff);
                        float x = U32f(data, koff + 4);
                        float y = U32f(data, koff + 8);
                        float z = U32f(data, koff + 12);
                        kfs.Add((fr, x, y, z));
                    }
                    track.Trans = kfs;
                }
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
                if (track.Trans != null || track.Rot != null) tracks[i] = track;
            }
            anims.Add(new AnimSlot {
                SlotId = slotId,
                FrameCount = (int)fc,
                Tracks = tracks,
            });
            motOff += (int)size;
        }
        return anims;
    }

    private static List<(float A, float B, float C)> InterpTransTrack(
        List<(int Frame, float X, float Y, float Z)> kfs, List<int> frames, (float, float, float) defaultVal)
    {
        var n = kfs.Count;
        if (n == 0) return frames.Select(_ => defaultVal).ToList();
        int k0Frame = kfs[0].Frame;
        int kLastFrame = kfs[n - 1].Frame;
        var outList = new List<(float, float, float)>(frames.Count);
        int cursor = 0;
        foreach (var f in frames)
        {
            if (f <= k0Frame)
            {
                outList.Add((kfs[0].X, kfs[0].Y, kfs[0].Z));
                continue;
            }
            if (f >= kLastFrame)
            {
                outList.Add((kfs[n - 1].X, kfs[n - 1].Y, kfs[n - 1].Z));
                continue;
            }
            while (cursor + 1 < n && kfs[cursor + 1].Frame <= f) cursor++;
            int f0 = kfs[cursor].Frame, f1 = kfs[cursor + 1].Frame;
            if (f1 == f0)
            {
                outList.Add((kfs[cursor].X, kfs[cursor].Y, kfs[cursor].Z));
                continue;
            }
            float t = (f - f0) / (float)(f1 - f0);
            var v0 = kfs[cursor];
            var v1 = kfs[cursor + 1];
            outList.Add((v0.X + t * (v1.X - v0.X), v0.Y + t * (v1.Y - v0.Y), v0.Z + t * (v1.Z - v0.Z)));
        }
        return outList;
    }

    private static List<(float Rx, float Ry, float Rz)> InterpRotTrack(
        List<(int Frame, int Rx, int Ry, int Rz)> kfs, List<int> frames, (int, int, int) defaultVal)
    {
        var n = kfs.Count;
        if (n == 0)
            return frames.Select(_ => ((float)defaultVal.Item1, (float)defaultVal.Item2, (float)defaultVal.Item3)).ToList();
        int k0Frame = kfs[0].Frame;
        int kLastFrame = kfs[n - 1].Frame;
        var outList = new List<(float, float, float)>(frames.Count);
        int cursor = 0;
        foreach (var f in frames)
        {
            if (f <= k0Frame)
            {
                outList.Add(((float)kfs[0].Rx, (float)kfs[0].Ry, (float)kfs[0].Rz));
                continue;
            }
            if (f >= kLastFrame)
            {
                outList.Add(((float)kfs[n - 1].Rx, (float)kfs[n - 1].Ry, (float)kfs[n - 1].Rz));
                continue;
            }
            while (cursor + 1 < n && kfs[cursor + 1].Frame <= f) cursor++;
            int f0 = kfs[cursor].Frame, f1 = kfs[cursor + 1].Frame;
            if (f1 == f0)
            {
                outList.Add(((float)kfs[cursor].Rx, (float)kfs[cursor].Ry, (float)kfs[cursor].Rz));
                continue;
            }
            float t = (f - f0) / (float)(f1 - f0);
            var v0 = kfs[cursor];
            var v1 = kfs[cursor + 1];
            outList.Add((
                v0.Rx + t * (v1.Rx - v0.Rx),
                v0.Ry + t * (v1.Ry - v0.Ry),
                v0.Rz + t * (v1.Rz - v0.Rz)));
        }
        return outList;
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

    private static int PageToTexture(int walkOff, List<(int TexIdx, int Off)> dispatches)
    {
        if (dispatches.Count == 0) return 0;
        int bestTex = 0, bestOff = -1;
        foreach (var (ti, off) in dispatches)
            if (off <= walkOff && off > bestOff)
            {
                bestOff = off;
                bestTex = ti;
            }
        return bestTex;
    }

    private static List<Strip> ParseStripsInRange(byte[] data, int start, int end)
    {
        var strips = new List<Strip>();
        int off = start;

        int FindNextHeader(int scanFrom)
        {
            int scan = (scanFrom + 1) & ~1;
            int maxScan = Math.Min(scanFrom + 256, end - 8);
            while (scan + 4 <= maxScan)
            {
                uint cmd = U32(data, scan);
                int cb = (int)((cmd >> 24) & 0xFF);
                int num = (int)((cmd >> 16) & 0xFF);
                if (num == 1 && (cb == 0x6c || cb == 0x68))
                {
                    int v3p = scan + (cb == 0x6c ? 0x14 : 0x10);
                    if (v3p + 4 <= end)
                    {
                        uint v3 = U32(data, v3p);
                        if (((v3 >> 24) & 0xFF) == 0x69) return scan;
                    }
                }
                scan += 2;
            }
            return -1;
        }

        while (off + 8 < end)
        {
            uint cmd = U32(data, off);
            int cb = (int)((cmd >> 24) & 0xFF);
            int num = (int)((cmd >> 16) & 0xFF);
            string stype;
            int v3Off;
            int vertOff;
            if (cb == 0x6c && num == 1)
            {
                stype = "INDEP";
                v3Off = off + 0x14;
                vertOff = off + 0x18;
            }
            else if (cb == 0x68 && num == 1)
            {
                stype = "SHARED";
                v3Off = off + 0x10;
                vertOff = off + 0x14;
            }
            else
            {
                int n2 = FindNextHeader(off + 4);
                if (n2 < 0 || n2 >= end) break;
                off = n2;
                continue;
            }
            uint v3Cmd = U32(data, v3Off);
            if (((v3Cmd >> 24) & 0xFF) != 0x69)
            {
                int n2 = FindNextHeader(off + 4);
                if (n2 < 0 || n2 >= end) break;
                off = n2;
                continue;
            }
            int v3Num = (int)((v3Cmd >> 16) & 0xFF);
            int streamEnd = vertOff + v3Num * 6;
            if (streamEnd > end) break;

            if (stype == "INDEP")
            {
                int nv = (int)(U32(data, off + 4) & 0x7FFF);
                var strip = new Strip { Type = "INDEP", VuAddr = (int)(U32(data, off + 0x10) & 0x3FF) };
                for (int i = 0; i < nv; i++)
                {
                    int voff = vertOff + i * 18;
                    if (voff + 18 > streamEnd) break;
                    strip.Verts.Add(new StripVertex {
                        Uv = (I16(data, voff), I16(data, voff + 2)),
                        Nrm = (I16(data, voff + 6), I16(data, voff + 8), I16(data, voff + 10)),
                        Pos = (I16(data, voff + 12), I16(data, voff + 14), I16(data, voff + 16)),
                    });
                }
                strips.Add(strip);
            }
            else
            {
                var strip = new Strip { Type = "SHARED" };
                int voff = vertOff;
                while (voff + 6 <= streamEnd)
                {
                    short u = I16(data, voff);
                    short v = I16(data, voff + 2);
                    ushort wc = U16(data, voff + 4);
                    voff += 6;
                    if (wc == 0 || wc > 16) break;
                    var weights = new List<StripWeight>(wc);
                    bool bad = false;
                    for (int w = 0; w < wc; w++)
                    {
                        if (voff + 18 > streamEnd)
                        {
                            bad = true;
                            break;
                        }
                        int bone = U16(data, voff);
                        short ww = I16(data, voff + 2);
                        short pX = I16(data, voff + 12);
                        short pY = I16(data, voff + 14);
                        short pZ = I16(data, voff + 16);
                        short nX = I16(data, voff + 6);
                        short nY = I16(data, voff + 8);
                        short nZ = I16(data, voff + 10);
                        weights.Add(new StripWeight(bone, ww, (pX, pY, pZ), (nX, nY, nZ)));
                        voff += 18;
                    }
                    if (bad) break;
                    strip.Verts.Add(new StripVertex { Uv = (u, v), Weights = weights });
                }
                strips.Add(strip);
            }

            int next = FindNextHeader(streamEnd);
            if (next < 0 || next >= end) break;
            off = next;
        }
        return strips;
    }

    private static BuiltMesh BuildMesh(
        List<Packet> packets,
        Dictionary<int, GNode> mslotMap,
        List<(int, int)> mainDispatches,
        List<(int, int)> altDispatches)
    {
        var bm = new BuiltMesh {
            SortedMslots = mslotMap.Keys.OrderBy(k => k).ToList(),
        };
        for (int i = 0; i < bm.SortedMslots.Count; i++)
            bm.MslotToJoint[bm.SortedMslots[i]] = i;

        var primFaces = new Dictionary<(int Tex, int IsAlt, int Alpha, int R, int G, int B),
            (List<(int A, int B, int C)> Faces, float AlphaW)>();

        foreach (var pkt in packets)
        {
            var dispatches = pkt.IsAlt ? altDispatches : mainDispatches;
            int texIdx = PageToTexture(pkt.WalkOff, dispatches);
            int q1r = (int)Math.Round(pkt.ColorQ1.R);
            int q1g = (int)Math.Round(pkt.ColorQ1.G);
            int q1b = (int)Math.Round(pkt.ColorQ1.B);
            int alphaKey = pkt.IsAlt ? (int)Math.Round(pkt.AlphaW) : 128;

            foreach (var s in pkt.Strips)
            {
                bool allZeroUv = s.Type == "INDEP" && s.Verts.Count > 0
                                                   && s.Verts.All(v => v.Uv.U == 0 && v.Uv.V == 0);
                bool isHitbox = false;
                if (allZeroUv)
                {
                    short minX = short.MaxValue, minY = short.MaxValue, minZ = short.MaxValue;
                    short maxX = short.MinValue, maxY = short.MinValue, maxZ = short.MinValue;
                    foreach (var v in s.Verts)
                    {
                        if (v.Pos.X < minX) minX = v.Pos.X;
                        if (v.Pos.X > maxX) maxX = v.Pos.X;
                        if (v.Pos.Y < minY) minY = v.Pos.Y;
                        if (v.Pos.Y > maxY) maxY = v.Pos.Y;
                        if (v.Pos.Z < minZ) minZ = v.Pos.Z;
                        if (v.Pos.Z > maxZ) maxZ = v.Pos.Z;
                    }
                    bool extentX = maxX != minX, extentY = maxY != minY, extentZ = maxZ != minZ;
                    isHitbox = extentX && extentY && extentZ;
                }
                if (isHitbox) continue;

                int vStart = bm.Vertices.Count;

                if (s.Type == "INDEP")
                {
                    int vu = s.VuAddr;
                    int mslot = pkt.BoneMap.TryGetValue(vu, out var ms) ? ms : 0;
                    bool boneOk = mslotMap.TryGetValue(mslot, out var bone);
                    var matrix = boneOk ? bone!.RestWorldSkin : Matrix4x4.Identity;
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
                        if (v.Weights != null)
                        {
                            foreach (var wt in v.Weights)
                            {
                                int mslot = pkt.BoneMap.TryGetValue(wt.Bone, out var ms) ? ms : 0;
                                if (!mslotMap.TryGetValue(mslot, out var bone)) continue;
                                var p = M.Apply(bone.RestWorldSkin, wt.Pos.X, wt.Pos.Y, wt.Pos.Z);
                                float ww = wt.W;
                                wx += p.X * ww;
                                wy += p.Y * ww;
                                wz += p.Z * ww;
                                var bn = Vector3.TransformNormal(
                                    new Vector3(wt.Nrm.X, wt.Nrm.Y, wt.Nrm.Z), bone.RestWorldSkin);
                                nx += bn.X * ww;
                                ny += bn.Y * ww;
                                nz += bn.Z * ww;
                                wsum += ww;
                                int jidx = bm.MslotToJoint.TryGetValue(mslot, out var ji) ? ji : -1;
                                if (jidx >= 0) jlist.Add((jidx, ww));
                            }
                        }
                        if (wsum != 0)
                        {
                            wx /= wsum;
                            wy /= wsum;
                            wz /= wsum;
                            for (int k = 0; k < jlist.Count; k++)
                                jlist[k] = (jlist[k].Joint, jlist[k].W / wsum);
                        }
                        bm.Vertices.Add(new Vector3(wx, wy, wz));
                        bm.Normals.Add(NSafe(new Vector3(nx, ny, nz)));
                        bm.Uvs.Add(new Vector2(v.Uv.U / 4096f, v.Uv.V / 4096f));
                        bm.VertJoints.Add(jlist);
                    }
                }

                int nv = s.Verts.Count;
                var key = (texIdx, pkt.IsAlt ? 1 : 0, alphaKey, q1r, q1g, q1b);
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
                     .ThenBy(x => x.R).ThenBy(x => x.G).ThenBy(x => x.B))
        {
            var entry = primFaces[k];
            bm.Primitives.Add(new Primitive {
                TexIdx = k.Tex,
                IsAlt = k.IsAlt == 1,
                AlphaKey = k.Alpha,
                AlphaW = entry.AlphaW,
                ColorQ1Key = (k.R, k.G, k.B),
                Faces = entry.Faces,
            });
        }
        return bm;
    }

    private static void WriteGlb(string outPath,
        List<GNode> nodes,
        Dictionary<int, GNode> mslotMap,
        BuiltMesh built,
        List<DecodedTexInfo> textures,
        List<bool> texHasAlpha,
        List<AnimSlot> animations,
        float animTransScale,
        Dictionary<int, TexAnimBake> texAnims)
    {
        var nodeBuilders = new NodeBuilder[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var nb = new NodeBuilder($"node_{n.Idx}_f{n.Flags:X4}" + (n.Mslot != 0 ? $"_m{n.Mslot}" : ""));
            var quat = M.EulerToQuatXyz(
                n.RestRotBams.X * M.BamsRad,
                n.RestRotBams.Y * M.BamsRad,
                n.RestRotBams.Z * M.BamsRad);
            nb.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
                Vector3.One,
                quat,
                new Vector3(n.RestPos.X, n.RestPos.Y, n.RestPos.Z));
            nodeBuilders[i] = nb;
        }
        for (int i = 0; i < nodes.Count; i++)
            foreach (var ch in nodes[i].Children)
                nodeBuilders[i].AddNode(nodeBuilders[ch]);

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

        var materials = new Dictionary<(int Tex, int IsAlt, int Alpha, int R, int G, int B), MaterialBuilder>();

        MaterialBuilder GetMaterial(int texIdx, bool isAlt, int alphaKey, float alphaW, (int R, int G, int B) q1Key)
        {
            if (texIdx < 0 || texIdx >= textures.Count) texIdx = 0;
            var key = (texIdx, isAlt ? 1 : 0, alphaKey, q1Key.R, q1Key.G, q1Key.B);
            if (materials.TryGetValue(key, out var existing)) return existing;
            var tex = textures[texIdx];
            var png = PngEncoder.Encode(tex.Width, tex.Height, tex.Rgba);
            var image = new MemoryImage(png);
            var mat = new MaterialBuilder($"mat_tex{texIdx}" + (isAlt ? "_alt" : ""))
                .WithDoubleSide(true)
                .WithMetallicRoughnessShader()
                .WithMetallicRoughness(0f, 1f);
            mat.WithChannelImage(KnownChannel.BaseColor, image);
            // Export the +0x20 row at FULL magnitude (q1Key/255), not max-normalised:
            // the magnitude is a lighting input the game scales at runtime
            // (lRhModel_SetMaterialIntensity; retail neutral is 178), and dropping it made
            // every re-import default back to over-bright 255.
            float r = q1Key.R / 255f;
            float g = q1Key.G / 255f;
            float b = q1Key.B / 255f;
            float a = 1f;
            if (isAlt)
            {
                a = Math.Clamp(alphaW / ModelExtractor.AltAlphaDivisor, 0f, 1f);
                mat.WithAlpha(AlphaMode.BLEND);
            }
            else if (texIdx < texHasAlpha.Count && texHasAlpha[texIdx])
            {
                mat.WithAlpha(AlphaMode.MASK, 0.5f);
            }
            // Omit only the exact retail-neutral grey: the importer maps an unspecified
            // (White) factor back onto (178,178,178), so neutral round-trips exactly and
            // every other colour is exported with full magnitude.
            bool isNeutral = !isAlt && q1Key.R == 178 && q1Key.G == 178 && q1Key.B == 178;
            if (!isNeutral) mat.WithBaseColor(new Vector4(r, g, b, a));
            materials[key] = mat;
            return mat;
        }

        var joints = built.SortedMslots
            .Select(m => nodeBuilders[mslotMap[m].Idx])
            .ToArray();

        var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>("MrgMesh");
        foreach (var prim in built.Primitives)
        {
            if (prim.IsAlt && prim.AlphaW == 0f) continue;
            var mat = GetMaterial(prim.TexIdx, prim.IsAlt, prim.AlphaKey, prim.AlphaW, prim.ColorQ1Key);
            var pb = meshBuilder.UsePrimitive(mat);
            foreach (var (a, b, c) in prim.Faces)
            {
                pb.AddTriangle(MakeVertex(built, a, pivot), MakeVertex(built, b, pivot), MakeVertex(built, c, pivot));
            }
        }

        const float FPS = 30f;
        foreach (var anim in animations.OrderBy(a => a.SlotId))
        {
            string animName = $"slot_{anim.SlotId}";
            foreach (var kv in anim.Tracks.OrderBy(p => p.Key))
            {
                int gnodeIdx = kv.Key;
                if (gnodeIdx < 0 || gnodeIdx >= nodes.Count) continue;
                var track = kv.Value;
                var n = nodes[gnodeIdx];
                var nb = nodeBuilders[gnodeIdx];

                var frameSet = new SortedSet<int>();
                if (track.Trans != null)
                    foreach (var kf in track.Trans)
                        frameSet.Add(kf.Frame);
                if (track.Rot != null)
                    foreach (var kf in track.Rot)
                        frameSet.Add(kf.Frame);
                if (frameSet.Count == 0) continue;
                var frames = frameSet.ToList();

                if (track.Trans != null && track.Trans.Count > 0)
                {
                    var tAcc = n.TransAccum;
                    float sxA, syA, szA;
                    if (gnodeIdx == 0)
                    {
                        sxA = tAcc.X;
                        syA = tAcc.Y;
                        szA = tAcc.Z;
                    }
                    else
                    {
                        sxA = animTransScale * tAcc.X;
                        syA = animTransScale * tAcc.Y;
                        szA = animTransScale * tAcc.Z;
                    }
                    var defaultT = (n.RestPos.X, n.RestPos.Y, n.RestPos.Z);
                    var vals = InterpTransTrack(track.Trans, frames, defaultT);
                    var curve = nb.UseTranslation(animName);
                    for (int i = 0; i < frames.Count; i++)
                    {
                        float t = frames[i] / FPS;
                        var (tx, ty, tz) = vals[i];
                        curve.SetPoint(t, new Vector3(tx * sxA, ty * syA, tz * szA));
                    }
                }
                if (track.Rot != null && track.Rot.Count > 0)
                {
                    var defaultR = (n.RestRotBams.X, n.RestRotBams.Y, n.RestRotBams.Z);
                    var vals = InterpRotTrack(track.Rot, frames, defaultR);
                    var curve = nb.UseRotation(animName);
                    for (int i = 0; i < frames.Count; i++)
                    {
                        float t = frames[i] / FPS;
                        var (rx, ry, rz) = vals[i];
                        var q = M.EulerToQuatXyz(rx * M.BamsRad, ry * M.BamsRad, rz * M.BamsRad);
                        curve.SetPoint(t, q);
                    }
                }
            }
        }

        var scene = new SceneBuilder("MrgScene");
        scene.AddNode(mrgRoot);
        if (meshBuilder.Primitives.Any(p => p.Vertices.Count > 0))
            scene.AddSkinnedMesh(meshBuilder, Matrix4x4.Identity, joints);
        var model = scene.ToGltf2();
        if (texAnims.Count > 0) InjectTextureAnimations(model, texAnims);
        model.SaveGLB(outPath);
        ReorderGlbAnimations(outPath);
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
                imgIndices[f] = img.LogicalIndex;
            }

            var frameJson = new System.Text.StringBuilder();
            frameJson.Append('[');
            for (int f = 0; f < imgIndices.Length; f++)
            {
                if (f > 0) frameJson.Append(',');
                float t = f < bake.FrameTimes.Count ? bake.FrameTimes[f] : 0f;
                frameJson.Append("{\"image\":").Append(imgIndices[f])
                    .Append(",\"t\":").Append(t.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('}');
            }
            frameJson.Append(']');

            string dur = bake.Duration.ToString(System.Globalization.CultureInfo.InvariantCulture);

            foreach (var mat in model.LogicalMaterials)
            {
                if (!MaterialUsesTexture(mat.Name, texIdx)) continue;
                string js = "{\"DOTR_texAnim\":{\"duration\":" + dur +
                            ",\"frames\":" + frameJson + "}}";
                mat.Extras = System.Text.Json.Nodes.JsonNode.Parse(js);
            }
        }
    }

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

    private static (VertexPositionNormal, VertexTexture1, VertexJoints4) MakeVertex(BuiltMesh bm, int vi, Vector3 pivot)
    {
        var pos = bm.Vertices[vi] - pivot;
        var nrm = bm.Normals[vi];
        if (nrm == Vector3.Zero) nrm = Vector3.UnitY; // glTF requires a unit normal
        var uv = bm.Uvs[vi];
        var src = bm.VertJoints[vi];
        var top = src.Where(x => x.W > 0f).OrderByDescending(x => x.W).Take(4).ToList();
        while (top.Count < 4) top.Add((0, 0f));
        float wsum = top.Sum(x => x.W);
        if (wsum <= 0f)
        {
            top[0] = (top[0].Joint, 1f);
            top[1] = (top[1].Joint, 0f);
            top[2] = (top[2].Joint, 0f);
            top[3] = (top[3].Joint, 0f);
        }
        else
        {
            for (int k = 0; k < 4; k++) top[k] = (top[k].Joint, top[k].W / wsum);
            float check = top[0].W + top[1].W + top[2].W + top[3].W;
            top[0] = (top[0].Joint, top[0].W + (1f - check));
        }
        var jb = new VertexJoints4(
            (top[0].Joint, top[0].W),
            (top[1].Joint, top[1].W),
            (top[2].Joint, top[2].W),
            (top[3].Joint, top[3].W));
        return (new VertexPositionNormal(pos, nrm), new VertexTexture1(uv), jb);
    }
}

internal readonly struct Rh0HeaderFull
{
    public readonly uint Magic;
    public readonly uint SectionFlags;
    public readonly uint MotTable;
    public readonly uint NodeTree;
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

internal sealed class GNode
{
    public int Idx;
    public int Pidx;
    public ushort Flags;
    public int Mslot;
    public (float X, float Y, float Z) RestPos;
    public (int X, int Y, int Z) RestRotBams;
    public Vector3 TransAccum;
    public Matrix4x4 RestLocal;
    public Matrix4x4 RestWorld;
    public Matrix4x4 RestWorldSkin;
    public List<int> Children = new();
}

internal sealed class PageInfo
{
    public int WalkOff;
    public Dictionary<int, int> BoneMap = new();
    public uint Sec2Off;
    public uint PktSize;
    public (float R, float G, float B) ColorQ1;
    public float AlphaW;
}

internal sealed class Packet
{
    public int WalkOff;
    public Dictionary<int, int> BoneMap = new();
    public List<Strip> Strips = new();
    public (float R, float G, float B) ColorQ1;
    public bool IsAlt;
    public float AlphaW;
}

internal sealed class Strip
{
    public string Type = "SHARED";
    public int VuAddr;
    public List<StripVertex> Verts = new();
}

internal sealed class StripVertex
{
    public (short U, short V) Uv;
    public (short X, short Y, short Z) Pos;
    public (short X, short Y, short Z) Nrm; // stored vertex normal (s16, 1.0 ~= 32767)
    public List<StripWeight>? Weights;
}

internal readonly struct StripWeight
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

internal sealed class BuiltMesh
{
    public List<Vector3> Vertices = new();
    public List<Vector3> Normals = new(); // output-space vertex normals (winding orientation only)
    public List<Vector2> Uvs = new();
    public List<List<(int Joint, float W)>> VertJoints = new();
    public List<Primitive> Primitives = new();
    public List<int> SortedMslots = new();
    public Dictionary<int, int> MslotToJoint = new();
}

internal sealed class Primitive
{
    public int TexIdx;
    public (int R, int G, int B) ColorQ1Key;
    public bool IsAlt;
    public int AlphaKey;
    public float AlphaW;
    public List<(int A, int B, int C)> Faces = new();
}

internal sealed class AnimSlot
{
    public int SlotId;
    public int FrameCount;
    public Dictionary<int, AnimTrack> Tracks = new();
}

internal sealed class AnimTrack
{
    public List<(int Frame, float X, float Y, float Z)>? Trans;
    public List<(int Frame, int Rx, int Ry, int Rz)>? Rot;
}

internal readonly struct DecodedTexInfo
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

internal static class M
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