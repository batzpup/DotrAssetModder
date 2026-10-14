using static DotrAssetModder.Bin;

namespace DotrAssetModder;
// Mostly AI

/* Geometry encoder. Writes Section2 (VIF geometry packets), Section1 (page records),
   RelocWalk and the aux0/aux1 dynamic-bind lists for a set of pre-built pages/strips.

   Section2 format (validated byte-identical against timeater.mrg):
     Section2 = sequence of BATCHES; batch = strip{1..K} then MSCALF (0x15000000).
       INDEP  strip: UNPACK V4-32 num=1 (16B header) + UNPACK V3-16 num=nv*3 + nv*18B verts
       SHARED strip: UNPACK V3-32 num=1 (12B header) + UNPACK V3-16 num=vnum + skinned blob
       - VU address starts at 0 each batch; header at A, verts at A+1, next strip at A+1+vnum
       - last strip of a batch sets bit15 of its nv field
       - flush a batch (emit MSCALF, reset VU addr) before the running addr would exceed 224
     Each page's Section2 packet is padded with NOP (0) to a 16-byte boundary.

   Section1 layout (per RhModel_Draw@0x20f4e0 / lRhModel_DrawCommonPart@0x20faf0 /
   lRhModel_DynamicAddressBind@0x20fed0 / lRhModel_SetupNoMotionRecursive@0x210010,
   byte-verified against timeater + blue-eyes):
     +0x00  opening DMA "next" tag qword:
              { 0x20000000, 0x20000000|pagesStart, 0x03000080 (VIF BASE 0x80),
                0x020001C0 (VIF OFFSET 0x1C0) }
            The engine reads pagesStart from this ADDR word (DAT_700000d8) and copies
            Section1[pagesStart..size] into a per-frame scratch buffer.
     +0x10  per-frame matrix table, zero on disk. One 0x70-byte entry per mesh slot;
            the engine stores each node's matrix at (meshSlot * 0x10), so mesh slots
            MUST be on the 1,8,15,... (1+7k) grid and
            pagesStart = maxMeshSlot*0x10 + 0x70.
     +pagesStart  page records:
            [texture-context 0x10000008, 0x90B, on texture change]
            [material 0x10000003, 0x40B, on colour change][bone-binds 0x10 each][terminator]
            bone-bind: { 0x30000007, d1, 0x10000000, 0x6C070000|vuAddr }
              d1 = (0x2|0x4)<<28 | (slot*0x10): matrix-table offset, tag 0x2 in Section1,
              0x4 in Section1Alt. Rebased per frame via aux0/aux1 (DynamicAddressBind).
            terminator: term0 = 0x30000000|(pktSize>>4), term1 = (0x3|0x5)<<28|sec2Off
              (tag 3 = Section2 main, 5 = Section2Alt);
              walkOff = termOff+4 (relocated to absolute at load via RelocWalk).
     Section1Alt has no opening tag / matrix table (the engine chains the alt copy
     manually and alt binds reference the MAIN copy's matrix table).

   RelocWalk: one u32 per page = (tag<<28)|walkOff (tag 0x2 main / 0x4 alt), then 0xFFFFFFFF.
   aux0: 0x20000004 (opening-tag ADDR) + one 0x20000000|(bindOff+4) per main bind,
         ascending, 0xFFFFFFFF-terminated, 0xCD-padded to 16 bytes.
   aux1: one 0x40000000|(bindOff+4) per alt bind, 0xFFFFFFFF-terminated, 0xCD-padded. */

public enum StripKind { Indep, Shared }

/// One skinning influence inside a SHARED vertex (serialises to 9 shorts).
public struct EncInfluence
{
    public ushort Bone;   // VU address (resolved to a mesh slot via the page bone-binds)
    public short Weight;  // fixed-point influence weight (~4096 == 1.0)
    public short Flag;
    public short NormalX, NormalY, NormalZ; // bind-space normal (1.0 ~ 32767)
    public short X, Y, Z;    // bind-space position
}

/// A vertex of an encoded strip. INDEP uses the scalar fields; SHARED uses Influences.
public sealed class EncVertex
{
    public short U, V;                       // texcoord (value/4096 on read)
    public short Flag;                       // INDEP: per-vertex flag word
    public short NormalX, NormalY, NormalZ;                 // INDEP: normal (1.0 ~ 32767)
    public short X, Y, Z;                    // INDEP: position (model fixed-point)
    public List<EncInfluence>? Influences;   // SHARED: non-null => skinned vertex
}

public sealed class EncStrip
{
    public StripKind Kind;
    public List<EncVertex> Vertices = new();
    public int IndependentVuAddress = 4;  // INDEP only: header-W = this strip's bone VU slot (palette {4,11,18,25,32,39,46,53}); resolved to a mesh slot via the page bone-binds
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
    public byte GsAlpha = 0x44;         // ALPHA_1 low byte: 0x44 normal, 0x48 additive, 0x42 subtract
    public uint GsTest;                 // TEST_1 (0: 0x7000C main / 0x7000D alt)
    public (float R, float G, float B) Color = (178, 178, 178); // retail-neutral +0x20 row
    public float Alpha = 128f;          // 0..128
    public (int R, int G, int B) Color0 = (127, 127, 127); // material record +0x10
    public (int R, int G, int B) Color2 = (255, 255, 255); // material record +0x30
    public bool Untextured;             // strips drawn with PRIM TME off (retail's 131 untextured strips)
    public List<EncStrip> Strips = new();
    public List<EncBoneBind> BoneBinds = new();
    public int TextureIndex;                  // keys the texture dispatch table

    // Filled in by GeometryWriter.Write:
    public int Section2Offset;                 // byte offset of this page's packet within its Section2
    public int PacketSize;                 // packet size incl. NOP padding (multiple of 16)
    public int WalkOffset;                 // offset of term1 within its Section1 (RelocWalk/dispatch)
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
    /// @0x211f70 stamps 2 qwords (TEX0_1/TEX1_1 from RH2+0x30) there EVERY FRAME.
    public List<(int TextureIndex, uint Entry)> TextureDispatch = new();
}

public static class GeometryWriter
{
    private const int VuBudget = 224;          // proven-safe per-batch VU address ceiling
    private const uint TemplateY = 0x301E4000; // header word 1: GIF tag, PRIM 0x3C (tristrip, IIP, TME, FGE)
    // Alpha-chain strips also set PRIM ABE (0x40), which makes the GS blend. Retail sets it
    // on exactly the alpha-chain strips (8,176) and on no opaque one.
    private const uint PrimAbe = 0x40u << 15;
    private const uint PrimTme = 0x10u << 15;
    private const uint TemplateZIndep = 0x00000412;
    private const uint TemplateZShared = 0x00008412;
    private const int MatrixEntrySize = 0x70;  // 7 qwords per mesh-slot matrix entry

    /// Encode all pages. Main pages (IsAlt==false) go to Section1/Section2; alt pages to the
    /// *Alt sections. `maxMeshSlot` = highest meshSlot assigned in the node tree (1+7k grid);
    /// it sizes the Section1 matrix table, which the engine writes at slot*0x10 for EVERY
    /// node with a non-zero meshSlot (lRhModel_SetupNoMotionRecursive@0x210010).
    public static GeometryWriteResult Write(IReadOnlyList<EncPage> pages, int maxMeshSlot)
    {
        var mainPages = new List<EncPage>();
        var altPages = new List<EncPage>();
        foreach (var page in pages) (page.IsAlt ? altPages : mainPages).Add(page);
        // Stable group by texture: one texture-context record per texture per section.
        mainPages = mainPages.OrderBy(page => page.TextureIndex).ToList();
        altPages = altPages.OrderBy(page => page.TextureIndex).ToList();

        var result = new GeometryWriteResult();

        int pagesStart = Math.Max(maxMeshSlot, 1) * 0x10 + MatrixEntrySize;
        var section2Stream = new MemoryStream();
        var section1Stream = new MemoryStream();
        AppendU32(section1Stream, 0x20000000);                        // DMA "next" tag, QWC=0
        AppendU32(section1Stream, 0x20000000u | (uint)pagesStart);    // ADDR -> first page (rebased per frame; aux0[0])
        AppendU32(section1Stream, 0x03000080);                        // VIF BASE   = 0x80
        AppendU32(section1Stream, 0x020001C0);                        // VIF OFFSET = 0x1C0
        while (section1Stream.Length < pagesStart) section1Stream.WriteByte(0); // matrix table (engine fills per frame)

        var mainBindOffsets = new List<int>();
        WriteSectionGroup(mainPages, section2Stream, section1Stream, isAlt: false, mainBindOffsets, result.TextureDispatch);
        result.Section2 = section2Stream.ToArray();
        result.Section1 = section1Stream.ToArray();

        var altSection2Stream = new MemoryStream();
        var altSection1Stream = new MemoryStream();
        var altBindOffsets = new List<int>();
        WriteSectionGroup(altPages, altSection2Stream, altSection1Stream, isAlt: true, altBindOffsets, result.TextureDispatch);
        result.Section2Alt = altSection2Stream.ToArray();
        result.Section1Alt = altSection1Stream.ToArray();

        result.RelocWalk = BuildRelocWalk(pages);
        result.Aux0 = BuildAuxList(0x20000000u, mainBindOffsets, includeOpeningTag: true);
        result.Aux1 = BuildAuxList(0x40000000u, altBindOffsets, includeOpeningTag: false);
        return result;
    }

    private static void WriteSectionGroup(List<EncPage> pages, MemoryStream section2Stream, MemoryStream section1Stream,
                                          bool isAlt, List<int> bindOffsets, List<(int TextureIndex, uint Entry)> texDispatch)
    {
        // Sticky state across pages, as in retail page streams: texture-context record on
        // texture change, material record on colour/alpha change.
        int lastTexIndex = -1;
        byte lastGsAlpha = 0x44;
        uint lastGsTest = 0;
        (int Red, int Green, int Blue, float Alpha, (int, int, int) Color0, (int, int, int) Color2) lastMaterial = (-1, -1, -1, -1, (-1, -1, -1), (-1, -1, -1));
        var lastBinds = new List<(int VuAddr, int Slot, uint VifEcho)>(); // sticky bind state
        uint sectionTag = isAlt ? 0x40000000u : 0x20000000u;

        foreach (var page in pages)
        {
            page.Section2Offset = (int)section2Stream.Length;
            WritePageGeometry(page, section2Stream);
            int packedLength = (int)section2Stream.Length - page.Section2Offset;
            int paddingLength = (16 - (packedLength & 15)) & 15;
            for (int i = 0; i < paddingLength; i++) section2Stream.WriteByte(0);
            page.PacketSize = packedLength + paddingLength;

            // the texture record also carries ALPHA_1/TEST_1, so a blend change needs one as well
            if (page.TextureIndex != lastTexIndex || page.GsAlpha != lastGsAlpha || page.GsTest != lastGsTest)
            {
                texDispatch.Add((page.TextureIndex, sectionTag | (uint)(((int)section1Stream.Length + 0x70) & 0x0FFFFFFF)));
                WriteTexRecord(section1Stream, isAlt, page.GsAlpha, page.GsTest);
                lastTexIndex = page.TextureIndex;
                lastGsAlpha = page.GsAlpha;
                lastGsTest = page.GsTest;
            }
            int roundedRed = (int)MathF.Round(page.Color.R);
            int roundedGreen = (int)MathF.Round(page.Color.G);
            int roundedBlue = (int)MathF.Round(page.Color.B);
            float currentAlpha = page.Alpha;   // retail has fractional alphas (63.5)
            if ((roundedRed, roundedGreen, roundedBlue, currentAlpha, page.Color0, page.Color2) != lastMaterial)
            {
                WriteMaterial(section1Stream, page.Color0, roundedRed, roundedGreen, roundedBlue, page.Alpha, page.Color2);
                lastMaterial = (roundedRed, roundedGreen, roundedBlue, currentAlpha, page.Color0, page.Color2);
            }
            /* Bone-binds are sticky like the texture and material records: a bind DMAs a matrix
               into VU memory and nothing between two consecutive pages of the same chain
               overwrites it, so retail re-emits the set only when it changes (3 repeats in 638
               retail pages). Repeating costs 16 B a page: enough to push 19 MODEL.MRG models
               16-120 B over their slot. */
            var binds = page.BoneBinds.Select(bind => (bind.VuAddr, bind.Slot, bind.VifEcho)).ToList();
            if (!binds.SequenceEqual(lastBinds))
            {
                foreach (var boneBind in page.BoneBinds)
                {
                    bindOffsets.Add((int)section1Stream.Length);
                    WriteBoneBind(section1Stream, boneBind, isAlt);
                }
                lastBinds = binds;
            }

            /* terminator (16B): term0, term1, 0, 0; walkOff points at term1. term1 is a tagged
               pointer that RhModel_BindAddress rebases by its tag: 3 = Section2, 5 = Section2Alt.
               Retail alt chains use tag 5 on every packet REF. */
            uint term0 = 0x30000000u | (uint)(page.PacketSize >> 4);
            uint term1 = (isAlt ? 0x50000000u : 0x30000000u) | (uint)page.Section2Offset;
            AppendU32(section1Stream, term0);
            page.WalkOffset = (int)section1Stream.Length; // == termOff + 4
            AppendU32(section1Stream, term1);
            AppendU32(section1Stream, 0);
            AppendU32(section1Stream, 0);
        }
    }

    private static void WritePageGeometry(EncPage page, MemoryStream section2Stream)
    {
        var batch = new List<EncStrip>();
        int vuAddress = 0;

        void Flush()
        {
            if (batch.Count == 0) return;
            int stripVuAddress = 0;
            for (int i = 0; i < batch.Count; i++)
            {
                bool isLastStrip = i == batch.Count - 1;
                stripVuAddress = WriteStrip(section2Stream, batch[i], stripVuAddress, isLastStrip, page.IsAlt, page.Untextured);
            }
            AppendU32(section2Stream, 0x15000000); // MSCALF
            batch.Clear();
            vuAddress = 0;
        }

        foreach (var strip in page.Strips)
        {
            int vnum = VertUnpackNum(strip);
            if (batch.Count > 0 && vuAddress + 1 + vnum > VuBudget) Flush();
            batch.Add(strip);
            vuAddress += 1 + vnum;
        }
        Flush();
    }

    /// Emits one strip at VU address `vuAddress`; returns the next free VU address.
    private static int WriteStrip(MemoryStream stream, EncStrip strip, int vuAddress, bool isLastStrip, bool isBlended, bool untextured)
    {
        uint gifTag = (TemplateY | (isBlended ? PrimAbe : 0u)) & (untextured ? ~PrimTme : ~0u);
        int vnum = VertUnpackNum(strip);
        int vertexCountField = strip.Vertices.Count | (isLastStrip ? 0x8000 : 0);

        if (strip.Kind == StripKind.Indep)
        {
            AppendU32(stream, 0x6C010000u | (uint)(vuAddress | 0x8000));           // UNPACK V4-32 num=1
            AppendU32(stream, (uint)vertexCountField);                              // X = nv (|bit15)
            AppendU32(stream, gifTag);                                  // Y
            AppendU32(stream, TemplateZIndep);                             // Z
            AppendU32(stream, (uint)strip.IndependentVuAddress);                    // W = this strip's bone VU slot
            AppendU32(stream, 0x69000000u | ((uint)vnum << 16) | (uint)((vuAddress + 1) | 0x8000)); // V3-16
            int bytesWritten = 0;
            foreach (var vertex in strip.Vertices)
            {
                AppendI16(stream, vertex.U); AppendI16(stream, vertex.V); AppendI16(stream, vertex.Flag);
                AppendI16(stream, vertex.NormalX); AppendI16(stream, vertex.NormalY); AppendI16(stream, vertex.NormalZ);
                AppendI16(stream, vertex.X); AppendI16(stream, vertex.Y); AppendI16(stream, vertex.Z);
                bytesWritten += 18;
            }
            Pad4(stream, bytesWritten);
        }
        else
        {
            AppendU32(stream, 0x68010000u | (uint)(vuAddress | 0x8000));           // UNPACK V3-32 num=1
            AppendU32(stream, (uint)vertexCountField);
            AppendU32(stream, gifTag);
            AppendU32(stream, TemplateZShared);
            AppendU32(stream, 0x69000000u | ((uint)vnum << 16) | (uint)((vuAddress + 1) | 0x8000));
            int bytesWritten = 0;
            foreach (var vertex in strip.Vertices)
            {
                var influences = vertex.Influences ?? new List<EncInfluence>();
                AppendI16(stream, vertex.U); AppendI16(stream, vertex.V); AppendU16(stream, (ushort)influences.Count);
                bytesWritten += 6;
                foreach (var influence in influences)
                {
                    AppendU16(stream, influence.Bone); AppendI16(stream, influence.Weight); AppendI16(stream, influence.Flag);
                    AppendI16(stream, influence.NormalX); AppendI16(stream, influence.NormalY); AppendI16(stream, influence.NormalZ);
                    AppendI16(stream, influence.X); AppendI16(stream, influence.Y); AppendI16(stream, influence.Z);
                    bytesWritten += 18;
                }
            }
            Pad4(stream, bytesWritten);
        }
        return vuAddress + 1 + vnum;
    }

    /// VIF V3-16 `num` field for a strip = total shorts / 3.
    private static int VertUnpackNum(EncStrip strip)
    {
        if (strip.Kind == StripKind.Indep) return strip.Vertices.Count * 3; // 9 shorts/vertex
        int shorts = 0;
        foreach (var vertex in strip.Vertices) shorts += 3 + 9 * (vertex.Influences?.Count ?? 0);
        return shorts / 3;
    }

    /// Texture-context record (0x90 bytes), byte-identical across all retail instances:
    /// DMA cnt tag QWC=8 + VIF FLUSH + VIF DIRECT(8) + GIF A+D (NLOOP=7, EOP) setting
    /// CLAMP_1=5, MIPTBP1_1=0, MIPTBP2_1=0, TEST_1 (0x0007000C main / 0x0007000D alt),
    /// ALPHA_1=0x80_00000044, then placeholder TEX0_1 (=0) and TEX1_1 (=0x60) qwords at
    /// +0x70/+0x80 — the +0x70 qword pair is stamped per frame by lRhModel_SetupTexture
    /// with the active texture's registers from its RH2 header (+0x30).
    private static void WriteTexRecord(MemoryStream stream, bool isAlt, byte gsAlpha = 0x44, uint gsTest = 0)
    {
        AppendU32(stream, 0x10000008); AppendU32(stream, 0x00000000); AppendU32(stream, 0x11000000); AppendU32(stream, 0x51000008);
        AppendU32(stream, 0x00008007); AppendU32(stream, 0x10000000); AppendU32(stream, 0x0000000E); AppendU32(stream, 0x00000000);
        AppendU32(stream, 0x00000005); AppendU32(stream, 0x00000000); AppendU32(stream, 0x00000008); AppendU32(stream, 0x00000000); // CLAMP_1
        AppendU32(stream, 0x00000000); AppendU32(stream, 0x00000000); AppendU32(stream, 0x00000034); AppendU32(stream, 0x00000000); // MIPTBP1_1
        AppendU32(stream, 0x00000000); AppendU32(stream, 0x00000000); AppendU32(stream, 0x00000036); AppendU32(stream, 0x00000000); // MIPTBP2_1
        AppendU32(stream, gsTest != 0 ? gsTest : isAlt ? 0x0007000Du : 0x0007000Cu);
        AppendU32(stream, 0x00000000); AppendU32(stream, 0x00000047); AppendU32(stream, 0x00000000);                          // TEST_1
        AppendU32(stream, gsAlpha); AppendU32(stream, 0x00000080); AppendU32(stream, 0x00000042); AppendU32(stream, 0x00000000); // ALPHA_1 (FIX 0x80; 0x44/0x48/0x42 as retail)
        AppendU32(stream, 0x00000000); AppendU32(stream, 0x00000000); AppendU32(stream, 0x00000006); AppendU32(stream, 0x00000000); // TEX0_1 (stamped)
        AppendU32(stream, 0x00000060); AppendU32(stream, 0x00000000); AppendU32(stream, 0x00000014); AppendU32(stream, 0x00000000); // TEX1_1 (stamped)
    }

    /// Material record (0x40 bytes): cnt tag + VIF FLUSHE + UNPACK V4-32 num=3 to VU 0x3C.
    /// +0x10 / +0x30 are per-material lighting triples (vary in retail; an extracted glb
    /// carries them in DOTR_blend, otherwise the retail defaults 127,127,127 / 255,255,255).
    /// +0x20 = page RGBA; these rows are also what lRhModel_SetMaterialIntensity@0x212250
    /// patches at runtime.
    private static void WriteMaterial(MemoryStream stream, (int R, int G, int B) lightingTriple0, int red, int green, int blue, float alpha,
                                      (int R, int G, int B) lightingTriple2)
    {
        AppendU32(stream, 0x10000003);
        AppendU32(stream, 0x00000000);
        AppendU32(stream, 0x10000000);
        AppendU32(stream, 0x6C03003C);
        AppendF32(stream, lightingTriple0.R); AppendF32(stream, lightingTriple0.G); AppendF32(stream, lightingTriple0.B); AppendU32(stream, 0);
        AppendF32(stream, red); AppendF32(stream, green); AppendF32(stream, blue); AppendF32(stream, alpha); // +0x20 RGBA
        AppendF32(stream, lightingTriple2.R); AppendF32(stream, lightingTriple2.G); AppendF32(stream, lightingTriple2.B); AppendU32(stream, 0);
    }

    /// Bone-bind: ref tag DMA-ing this slot's 7-qword matrix from the Section1 scratch copy
    /// to the strip's VU address. d1 = matrix-table offset (slot*0x10), tagged 0x2 (main
    /// Section1) / 0x4 (Section1Alt); rebased per frame by DynamicAddressBind via aux0/aux1.
    private static void WriteBoneBind(MemoryStream stream, EncBoneBind boneBind, bool isAlt)
    {
        uint echo = boneBind.VifEcho != 0 ? boneBind.VifEcho : (0x6C070000u | (uint)(boneBind.VuAddr & 0x3FF));
        uint tag = isAlt ? 0x40000000u : 0x20000000u;
        AppendU32(stream, 0x30000007);
        AppendU32(stream, tag | ((uint)boneBind.Slot << 4));
        AppendU32(stream, 0x10000000);
        AppendU32(stream, echo);
    }

    private static byte[] BuildRelocWalk(IReadOnlyList<EncPage> pages)
    {
        var walkStream = new MemoryStream();
        foreach (var page in pages)
        {
            uint tag = page.IsAlt ? 0x4u : 0x2u;
            AppendU32(walkStream, (tag << 28) | ((uint)page.WalkOffset & 0x0FFFFFFF));
        }
        AppendU32(walkStream, 0xFFFFFFFF);
        return walkStream.ToArray();
    }

    /// aux0 / aux1 (per-frame DynamicAddressBind lists). Entries point at the ADDR (d1) word of every bind record (= bindOff+4); aux0 also
    /// leads with 0x20000004 for the opening tag's ADDR. 0xFFFFFFFF-terminated, padded to
    /// 16 bytes with 0xCD (matches retail; the engine never reads past the terminator).
    private static byte[] BuildAuxList(uint tag, List<int> bindOffsets, bool includeOpeningTag)
    {
        var auxStream = new MemoryStream();
        if (includeOpeningTag) AppendU32(auxStream, tag | 0x4u);
        foreach (int bindOffset in bindOffsets) AppendU32(auxStream, tag | (uint)((bindOffset + 4) & 0x0FFFFFFF));
        AppendU32(auxStream, 0xFFFFFFFF);
        while ((auxStream.Length & 15) != 0) auxStream.WriteByte(0xCD);
        return auxStream.ToArray();
    }

    private static void Pad4(MemoryStream stream, int bytesWritten)
    {
        int paddingLength = (4 - (bytesWritten & 3)) & 3;
        for (int i = 0; i < paddingLength; i++) stream.WriteByte(0);
    }
}