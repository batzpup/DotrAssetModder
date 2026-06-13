using System.Buffers.Binary;

namespace DotrAssetModder;

// === Step 6.2 — Geometry encoder, Layer A =============================================
// Writes Section2 (VIF geometry packets), Section1 (page records), RelocWalk and the
// aux0/aux1 dynamic-bind lists for a set of pre-built pages/strips.
//
// Section2 format (validated BYTE-IDENTICAL against timeater.mrg):
//   Section2 = sequence of BATCHES; batch = strip{1..K} then MSCALF (0x15000000).
//     INDEP  strip: UNPACK V4-32 num=1 (16B header) + UNPACK V3-16 num=nv*3 + nv*18B verts
//     SHARED strip: UNPACK V3-32 num=1 (12B header) + UNPACK V3-16 num=K   + skinned blob
//     - VU address starts at 0 each batch; header at A, verts at A+1, next strip at A+1+vnum
//     - last strip of a batch sets bit15 of its nv field
//     - flush a batch (emit MSCALF, reset VU addr) before the running addr would exceed 224
//   Each page's Section2 packet is padded with NOP (0) to a 16-byte boundary.
//
// Section1 layout (per RhModel_Draw@0x20d3b8 / lRhModel_DrawCommonPart@0x20d920 /
// lRhModel_DynamicAddressBind@0x20dd00 / lRhModel_SetupNoMotionRecursive@0x20de40,
// byte-verified against timeater + blue-eyes):
//   +0x00  opening DMA "next" tag qword:
//            { 0x20000000, 0x20000000|pagesStart, 0x03000080 (VIF BASE 0x80),
//              0x020001C0 (VIF OFFSET 0x1C0) }
//          The engine reads pagesStart from this ADDR word (DAT_700000d8) and copies
//          Section1[pagesStart..size] into a per-frame scratch buffer.
//   +0x10  per-frame matrix table, zero on disk. One 0x70-byte entry per mesh slot;
//          the engine stores each node's matrix at (meshSlot * 0x10), so mesh slots
//          MUST be on the 1,8,15,... (1+7k) grid and
//          pagesStart = maxMeshSlot*0x10 + 0x70.
//   +pagesStart  page records:
//          [texture-context 0x10000008, 0x90B, on texture change]
//          [material 0x10000003, 0x40B, on colour change][bone-binds 0x10 each][terminator]
//          bone-bind: { 0x30000007, d1, 0x10000000, 0x6C070000|vuAddr }
//            d1 = (0x2|0x4)<<28 | (slot*0x10): matrix-table offset, tag 0x2 in Section1,
//            0x4 in Section1Alt. Rebased per frame via aux0/aux1 (DynamicAddressBind).
//          terminator: term0 = 0x30000000|(pktSize>>4), term1 = 0x30000000|sec2Off;
//            walkOff = termOff+4 (relocated to absolute at load via RelocWalk).
//   Section1Alt has NO opening tag / matrix table (the engine chains the alt copy
//   manually and alt binds reference the MAIN copy's matrix table).
//
// RelocWalk: one u32 per page = (tag<<28)|walkOff (tag 0x2 main / 0x4 alt), then 0xFFFFFFFF.
// aux0: 0x20000004 (opening-tag ADDR) + one 0x20000000|(bindOff+4) per main bind,
//       ascending, 0xFFFFFFFF-terminated, 0xCD-padded to 16 bytes.
// aux1: one 0x40000000|(bindOff+4) per alt bind, 0xFFFFFFFF-terminated, 0xCD-padded.
// ======================================================================================

public enum StripKind { Indep, Shared }

/// One skinning influence inside a SHARED vertex (serialises to 9 shorts).
public struct EncInfluence
{
    public ushort Bone;   // VU address (resolved to a mesh slot via the page bone-binds)
    public short Weight;  // fixed-point influence weight (~4096 == 1.0)
    public short Flag;
    public short Nx, Ny, Nz; // bind-space normal (1.0 ~ 32767)
    public short X, Y, Z;    // bind-space position
}

/// A vertex of an encoded strip. INDEP uses the scalar fields; SHARED uses Influences.
public sealed class EncVertex
{
    public short U, V;                       // texcoord (value/4096 on read)
    public short Flag;                       // INDEP: per-vertex flag word
    public short Nx, Ny, Nz;                 // INDEP: normal (1.0 ~ 32767)
    public short X, Y, Z;                    // INDEP: position (model fixed-point)
    public List<EncInfluence>? Influences;   // SHARED: non-null => skinned vertex
}

public sealed class EncStrip
{
    public StripKind Kind;
    public List<EncVertex> Verts = new();
    public int IndepVuAddr = 4;  // INDEP only: header-W = this strip's bone VU slot (palette {4,11,18,25,32,39,46,53}); resolved to a mesh slot via the page bone-binds
}

/// VU-address -> mesh/bone-slot binding emitted into a page's Section1 records.
public struct EncBoneBind
{
    public int VuAddr;   // VU address referenced by strips (INDEP W field / SHARED bone)
    public int Slot;     // mesh slot (node meshSlot value, on the 1+7k grid); d1 = slot*0x10
    public uint VifEcho; // d3 echo word; low 10 bits must equal VuAddr (0 => auto)
}

/// One page == one material group: its strips, bone-binds and material colour.
public sealed class EncPage
{
    public bool IsAlt;
    public (float R, float G, float B) Color = (178, 178, 178); // retail-neutral +0x20 row
    public float Alpha = 128f;          // 0..128
    public List<EncStrip> Strips = new();
    public List<EncBoneBind> BoneBinds = new();
    public int TexIdx;                  // used later for the dispatch table (6.4)

    // Filled in by GeometryWriter.Write:
    public int Sec2Off;                 // byte offset of this page's packet within its Section2
    public int PktSize;                 // packet size incl. NOP padding (multiple of 16)
    public int WalkOff;                 // offset of term1 within its Section1 (RelocWalk/dispatch)
}

public sealed class GeometryWriteResult
{
    public byte[] Section2 = Array.Empty<byte>();
    public byte[] Section1 = Array.Empty<byte>();
    public byte[] Section2Alt = Array.Empty<byte>();
    public byte[] Section1Alt = Array.Empty<byte>();
    public byte[] RelocWalk = Array.Empty<byte>();
    public byte[] Aux0 = Array.Empty<byte>();    // per-frame bind list for Section1
    public byte[] Aux1 = Array.Empty<byte>();    // per-frame bind list for Section1Alt

    /// Texture dispatch entries for the TexTable: one per texture-context record emitted,
    /// in emission order (main section first, then alt). Entry = (0x2|0x4)<<28 | offset of
    /// the record's TEX0_1 qword (recordOff + 0x70) within its Section1. lRhModel_SetupTexture
    /// @0x20fda0 stamps 2 qwords (TEX0_1/TEX1_1 from RH2+0x30) there EVERY FRAME.
    public List<(int TexIdx, uint Entry)> TexDispatch = new();
}

public static class GeometryWriter
{
    private const int VuBudget = 224;          // proven-safe per-batch VU address ceiling
    private const uint TemplateY = 0x301E4000; // header word 1 (const)
    private const uint TemplateZIndep = 0x00000412;
    private const uint TemplateZShared = 0x00008412;
    private const int MatrixEntrySize = 0x70;  // 7 qwords per mesh-slot matrix entry

    /// Encode all pages. Main pages (IsAlt==false) go to Section1/Section2; alt pages to the
    /// *Alt sections. `maxMeshSlot` = highest meshSlot assigned in the node tree (1+7k grid);
    /// it sizes the Section1 matrix table, which the engine writes at slot*0x10 for EVERY
    /// node with a non-zero meshSlot (lRhModel_SetupNoMotionRecursive@0x20de40).
    public static GeometryWriteResult Write(IReadOnlyList<EncPage> pages, int maxMeshSlot)
    {
        var main = new List<EncPage>();
        var alt = new List<EncPage>();
        foreach (var p in pages) (p.IsAlt ? alt : main).Add(p);
        // Group each section's pages by texture (stable) so every texture needs exactly one
        // texture-context record per section; pages of the same texture keep their order.
        main = main.OrderBy(p => p.TexIdx).ToList();
        alt = alt.OrderBy(p => p.TexIdx).ToList();

        var res = new GeometryWriteResult();

        // ---- main: opening tag + matrix table + pages ----
        int pagesStart = Math.Max(maxMeshSlot, 1) * 0x10 + MatrixEntrySize;
        var sec2 = new MemoryStream();
        var sec1 = new MemoryStream();
        WriteU32(sec1, 0x20000000);                        // DMA "next" tag, QWC=0
        WriteU32(sec1, 0x20000000u | (uint)pagesStart);    // ADDR -> first page (rebased per frame; aux0[0])
        WriteU32(sec1, 0x03000080);                        // VIF BASE   = 0x80
        WriteU32(sec1, 0x020001C0);                        // VIF OFFSET = 0x1C0
        while (sec1.Length < pagesStart) sec1.WriteByte(0); // matrix table (engine fills per frame)

        var mainBindOffs = new List<int>();
        WriteSectionGroup(main, sec2, sec1, isAlt: false, mainBindOffs, res.TexDispatch);
        res.Section2 = sec2.ToArray();
        res.Section1 = sec1.ToArray();

        // ---- alt: pages only (no opening tag / matrix table) ----
        var sec2a = new MemoryStream();
        var sec1a = new MemoryStream();
        var altBindOffs = new List<int>();
        WriteSectionGroup(alt, sec2a, sec1a, isAlt: true, altBindOffs, res.TexDispatch);
        res.Section2Alt = sec2a.ToArray();
        res.Section1Alt = sec1a.ToArray();

        res.RelocWalk = BuildRelocWalk(pages);
        res.Aux0 = BuildAuxList(0x20000000u, mainBindOffs, includeOpeningTag: true);
        res.Aux1 = BuildAuxList(0x40000000u, altBindOffs, includeOpeningTag: false);
        return res;
    }

    // ---- Section2 + Section1 for one group (main or alt) -----------------------------
    private static void WriteSectionGroup(List<EncPage> pages, MemoryStream sec2, MemoryStream sec1,
                                          bool isAlt, List<int> bindOffsets, List<(int TexIdx, uint Entry)> texDispatch)
    {
        // Sticky state across pages: texture-context record on texture change, material
        // record on colour/alpha change (matches retail page streams).
        int lastTex = -1;
        (int R, int G, int B, int A) lastMat = (-1, -1, -1, -1);
        uint secTag = isAlt ? 0x40000000u : 0x20000000u;

        foreach (var page in pages)
        {
            // --- Section2: emit this page's batches, then pad packet to 16 bytes ---
            page.Sec2Off = (int)sec2.Length;
            WritePageGeometry(page, sec2);
            int packed = (int)sec2.Length - page.Sec2Off;
            int pad = (16 - (packed & 15)) & 15;
            for (int i = 0; i < pad; i++) sec2.WriteByte(0);
            page.PktSize = packed + pad;

            // --- Section1: texture record (on change) + material (on change) + binds + terminator ---
            if (page.TexIdx != lastTex)
            {
                texDispatch.Add((page.TexIdx, secTag | (uint)(((int)sec1.Length + 0x70) & 0x0FFFFFFF)));
                WriteTexRecord(sec1, isAlt);
                lastTex = page.TexIdx;
            }
            int curR = (int)MathF.Round(page.Color.R);
            int curG = (int)MathF.Round(page.Color.G);
            int curB = (int)MathF.Round(page.Color.B);
            int curA = (int)MathF.Round(page.Alpha);
            if ((curR, curG, curB, curA) != lastMat)
            {
                WriteMaterial(sec1, curR, curG, curB, curA);
                lastMat = (curR, curG, curB, curA);
            }
            foreach (var bb in page.BoneBinds)
            {
                bindOffsets.Add((int)sec1.Length);
                WriteBoneBind(sec1, bb, isAlt);
            }

            // terminator (16B): term0, term1, 0, 0 ; walkOff points at term1
            uint term0 = 0x30000000u | (uint)(page.PktSize >> 4);
            uint term1 = 0x30000000u | (uint)page.Sec2Off;
            WriteU32(sec1, term0);
            page.WalkOff = (int)sec1.Length; // == termOff + 4
            WriteU32(sec1, term1);
            WriteU32(sec1, 0);
            WriteU32(sec1, 0);
        }
    }

    private static void WritePageGeometry(EncPage page, MemoryStream sec2)
    {
        var batch = new List<EncStrip>();
        int vu = 0;

        void Flush()
        {
            if (batch.Count == 0) return;
            int a = 0;
            for (int i = 0; i < batch.Count; i++)
            {
                bool last = i == batch.Count - 1;
                a = WriteStrip(sec2, batch[i], a, last);
            }
            WriteU32(sec2, 0x15000000); // MSCALF
            batch.Clear();
            vu = 0;
        }

        foreach (var s in page.Strips)
        {
            int vnum = VertUnpackNum(s);
            // flush before this strip would push the running VU address past the budget
            if (batch.Count > 0 && vu + 1 + vnum > VuBudget) Flush();
            batch.Add(s);
            vu += 1 + vnum;
        }
        Flush();
    }

    /// Emits one strip at VU address `a`; returns the next free VU address.
    private static int WriteStrip(MemoryStream s, EncStrip strip, int a, bool last)
    {
        int vnum = VertUnpackNum(strip);
        int nvField = strip.Verts.Count | (last ? 0x8000 : 0);

        if (strip.Kind == StripKind.Indep)
        {
            WriteU32(s, 0x6C010000u | (uint)(a | 0x8000));           // UNPACK V4-32 num=1
            WriteU32(s, (uint)nvField);                              // X = nv (|bit15)
            WriteU32(s, TemplateY);                                  // Y
            WriteU32(s, TemplateZIndep);                             // Z
            WriteU32(s, (uint)strip.IndepVuAddr);                    // W = this strip's bone VU slot
            WriteU32(s, 0x69000000u | ((uint)vnum << 16) | (uint)((a + 1) | 0x8000)); // V3-16
            int wrote = 0;
            foreach (var v in strip.Verts)
            {
                WriteI16(s, v.U); WriteI16(s, v.V); WriteI16(s, v.Flag);
                WriteI16(s, v.Nx); WriteI16(s, v.Ny); WriteI16(s, v.Nz);
                WriteI16(s, v.X); WriteI16(s, v.Y); WriteI16(s, v.Z);
                wrote += 18;
            }
            Pad4(s, wrote);
        }
        else
        {
            WriteU32(s, 0x68010000u | (uint)(a | 0x8000));           // UNPACK V3-32 num=1
            WriteU32(s, (uint)nvField);
            WriteU32(s, TemplateY);
            WriteU32(s, TemplateZShared);
            WriteU32(s, 0x69000000u | ((uint)vnum << 16) | (uint)((a + 1) | 0x8000));
            int wrote = 0;
            foreach (var v in strip.Verts)
            {
                var infs = v.Influences ?? new List<EncInfluence>();
                WriteI16(s, v.U); WriteI16(s, v.V); WriteU16(s, (ushort)infs.Count);
                wrote += 6;
                foreach (var inf in infs)
                {
                    WriteU16(s, inf.Bone); WriteI16(s, inf.Weight); WriteI16(s, inf.Flag);
                    WriteI16(s, inf.Nx); WriteI16(s, inf.Ny); WriteI16(s, inf.Nz);
                    WriteI16(s, inf.X); WriteI16(s, inf.Y); WriteI16(s, inf.Z);
                    wrote += 18;
                }
            }
            Pad4(s, wrote);
        }
        return a + 1 + vnum;
    }

    /// VIF V3-16 `num` field for a strip = total shorts / 3.
    private static int VertUnpackNum(EncStrip s)
    {
        if (s.Kind == StripKind.Indep) return s.Verts.Count * 3; // 9 shorts/vertex
        int shorts = 0;
        foreach (var v in s.Verts) shorts += 3 + 9 * (v.Influences?.Count ?? 0);
        return shorts / 3;
    }

    // ---- Section1 records ------------------------------------------------------------
    /// Texture-context record (0x90 bytes), byte-identical across all retail instances:
    /// DMA cnt tag QWC=8 + VIF FLUSH + VIF DIRECT(8) + GIF A+D (NLOOP=7, EOP) setting
    /// CLAMP_1=5, MIPTBP1_1=0, MIPTBP2_1=0, TEST_1 (0x0007000C main / 0x0007000D alt),
    /// ALPHA_1=0x80_00000044, then placeholder TEX0_1 (=0) and TEX1_1 (=0x60) qwords at
    /// +0x70/+0x80 — the +0x70 qword pair is stamped per frame by lRhModel_SetupTexture
    /// with the active texture's registers from its RH2 header (+0x30).
    private static void WriteTexRecord(MemoryStream s, bool isAlt)
    {
        WriteU32(s, 0x10000008); WriteU32(s, 0x00000000); WriteU32(s, 0x11000000); WriteU32(s, 0x51000008);
        WriteU32(s, 0x00008007); WriteU32(s, 0x10000000); WriteU32(s, 0x0000000E); WriteU32(s, 0x00000000);
        WriteU32(s, 0x00000005); WriteU32(s, 0x00000000); WriteU32(s, 0x00000008); WriteU32(s, 0x00000000); // CLAMP_1
        WriteU32(s, 0x00000000); WriteU32(s, 0x00000000); WriteU32(s, 0x00000034); WriteU32(s, 0x00000000); // MIPTBP1_1
        WriteU32(s, 0x00000000); WriteU32(s, 0x00000000); WriteU32(s, 0x00000036); WriteU32(s, 0x00000000); // MIPTBP2_1
        WriteU32(s, isAlt ? 0x0007000Du : 0x0007000Cu);
        WriteU32(s, 0x00000000); WriteU32(s, 0x00000047); WriteU32(s, 0x00000000);                          // TEST_1
        WriteU32(s, 0x00000044); WriteU32(s, 0x00000080); WriteU32(s, 0x00000042); WriteU32(s, 0x00000000); // ALPHA_1
        WriteU32(s, 0x00000000); WriteU32(s, 0x00000000); WriteU32(s, 0x00000006); WriteU32(s, 0x00000000); // TEX0_1 (stamped)
        WriteU32(s, 0x00000060); WriteU32(s, 0x00000000); WriteU32(s, 0x00000014); WriteU32(s, 0x00000000); // TEX1_1 (stamped)
    }

    /// Material record (0x40 bytes): cnt tag + VIF FLUSHE + UNPACK V4-32 num=3 to VU 0x3C.
    /// +0x10 / +0x30 are per-material lighting triples (vary in retail; glb can't carry them)
    /// — we emit the retail defaults (127,127,127 / 255,255,255). +0x20 = page RGBA; these
    /// rows are also what lRhModel_SetMaterialIntensity@0x210080 patches at runtime.
    private static void WriteMaterial(MemoryStream s, int r, int g, int b, int a)
    {
        WriteU32(s, 0x10000003);
        WriteU32(s, 0x00000000);
        WriteU32(s, 0x10000000);
        WriteU32(s, 0x6C03003C);
        WriteF32(s, 127f); WriteF32(s, 127f); WriteF32(s, 127f); WriteU32(s, 0);
        WriteF32(s, r); WriteF32(s, g); WriteF32(s, b); WriteF32(s, a); // +0x20 RGBA
        WriteF32(s, 255f); WriteF32(s, 255f); WriteF32(s, 255f); WriteU32(s, 0);
    }

    /// Bone-bind: ref tag DMA-ing this slot's 7-qword matrix from the Section1 scratch copy
    /// to the strip's VU address. d1 = matrix-table offset (slot*0x10), tagged 0x2 (main
    /// Section1) / 0x4 (Section1Alt); rebased per frame by DynamicAddressBind via aux0/aux1.
    private static void WriteBoneBind(MemoryStream s, EncBoneBind bb, bool isAlt)
    {
        uint echo = bb.VifEcho != 0 ? bb.VifEcho : (0x6C070000u | (uint)(bb.VuAddr & 0x3FF));
        uint tag = isAlt ? 0x40000000u : 0x20000000u;
        WriteU32(s, 0x30000007);
        WriteU32(s, tag | ((uint)bb.Slot << 4));
        WriteU32(s, 0x10000000);
        WriteU32(s, echo);
    }

    // ---- RelocWalk -------------------------------------------------------------------
    private static byte[] BuildRelocWalk(IReadOnlyList<EncPage> pages)
    {
        var ms = new MemoryStream();
        foreach (var p in pages)
        {
            uint tag = p.IsAlt ? 0x4u : 0x2u;
            WriteU32(ms, (tag << 28) | ((uint)p.WalkOff & 0x0FFFFFFF));
        }
        WriteU32(ms, 0xFFFFFFFF);
        return ms.ToArray();
    }

    // ---- aux0 / aux1 (per-frame DynamicAddressBind lists) -----------------------------
    /// Entries point at the ADDR (d1) word of every bind record (= bindOff+4); aux0 also
    /// leads with 0x20000004 for the opening tag's ADDR. 0xFFFFFFFF-terminated, padded to
    /// 16 bytes with 0xCD (matches retail; the engine never reads past the terminator).
    private static byte[] BuildAuxList(uint tag, List<int> bindOffsets, bool includeOpeningTag)
    {
        var ms = new MemoryStream();
        if (includeOpeningTag) WriteU32(ms, tag | 0x4u);
        foreach (int off in bindOffsets) WriteU32(ms, tag | (uint)((off + 4) & 0x0FFFFFFF));
        WriteU32(ms, 0xFFFFFFFF);
        while ((ms.Length & 15) != 0) ms.WriteByte(0xCD);
        return ms.ToArray();
    }

    // ---- LE helpers ------------------------------------------------------------------
    private static void Pad4(MemoryStream s, int wrote)
    {
        int pad = (4 - (wrote & 3)) & 3;
        for (int i = 0; i < pad; i++) s.WriteByte(0);
    }
    private static void WriteU32(MemoryStream s, uint v)
    {
        Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b);
    }
    private static void WriteI16(MemoryStream s, short v)
    {
        Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, v); s.Write(b);
    }
    private static void WriteU16(MemoryStream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); s.Write(b);
    }
    private static void WriteF32(MemoryStream s, float v)
    {
        Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); s.Write(b);
    }
}