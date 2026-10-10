using System.Buffers.Binary;
using static DotrAssetModder.Bin;
namespace DotrAssetModder;

// ---------------------------------------------------------------------------
// PS2 "RH2" texture animation (the retail game's ".anm" data).
//
// Reverse-engineered from SzModel_LoadTexAnim_Pipe (0x1e80a0) and its per-mode
// handler blocks (0x1e8220 modes0/1, 0x1e826c modes2/3, 0x1e8380 mode6, ...).
//
// Each frame the game issues a GS local->local copy (BITBLTBUF/TRXPOS/TRXREG/
// TRXDIR=2) stamping a BlockW x BlockH tile from the source sheet onto the
// displayed region (DstX,DstY). The source tile is a 2D sprite-sheet walk:
//
//     srcX = SrcX + BlockW * (frame %  Ncols)
//     srcY = SrcY + BlockH * (frame /  Ncols)
//
//   Ncols = byte2 low nibble, Nrows = byte2 high nibble, total = Ncols*Nrows
//   (the prologue computes `s3 = Ncols * Nrows`, the running frame count).
//
// The frame index is persisted in byte[+3] of the entry and stepped by the
// handler off a counter: byte0 bit1 selects a free-running counter (60Hz) vs
// the model's animation frame. The step cadence is set by Aux words:
//
//   modes 0/1 (handler 0x1e8220): forward loop. Advance frame by 1 each time
//             counter % Aux0(+4) == 0.  -> per-frame rate = baseHz / Aux0.
//   modes 2/3 (handler 0x1e826c): ping-pong. Aux0(+4) = up speed divisor,
//             Aux1(+6) = down speed divisor; reverses at 0 and total-1.
//   mode 6    (handler 0x1e8380): periodic flash/blink. Waits until
//             counter % Aux0(+4) == 0, then stamps the (single) src->dst blit
//             for byte[+6] ticks, then turns it off. Not frame-cycling.
//   modes 4/5 (handlers 0x1e84ac / 0x1e8418): one-shot timed reveal. The blit
//             is drawn only while the free-running counter is inside its
//             [Aux0,Aux1] window (mode 4) or after a one-shot trigger (mode 5),
//             so it plays once at startup and is off forever after. These are
//             not part of the steady-state loop and are skipped when baking.
//
// In these packed .mrg files the converted 24-byte entry array is the "post
// section" pointed to by word[+8] of each 16-byte texture-table block.
//
// Entry layout (24 bytes):
//   +0x00 u8   flags : bit0 = last/terminator, bit1 = counter source,
//                      bits2-4 = mode
//   +0x01 u8   slotMask
//   +0x02 u8   grid     : low nibble = Ncols, high nibble = Nrows
//   +0x04 u16  Aux0     : forward/flash speed divisor (ticks per step)
//   +0x06 u16  Aux1     : ping-pong down speed; low byte = mode-6 on-duration
//   +0x08 u16  BlockW    +0x0A u16 BlockH
//   +0x0C u16  SrcX      +0x0E u16 SrcY
//   +0x10 u16  DstX      +0x12 u16 DstY
//   +0x14 u32  counter   (runtime; 0 in stored data)
// ---------------------------------------------------------------------------
internal readonly struct TexAnimEntry
{
    public readonly int Mode;
    public readonly int SlotMask;
    public readonly int Cols, Rows;
    public readonly int Aux0, Aux1;
    public readonly int BlockW, BlockH;
    public readonly int SrcX, SrcY;
    public readonly int DstX, DstY;
    public readonly int OnTicks;   // mode-6 on-duration (byte +6)
    public readonly bool Last;
    public readonly int Clock;     // starting counter (+0x14): 0 in retail, a blink's phase in DoD imports

    public TexAnimEntry(int mode, int slotMask, int cols, int rows, int aux0, int aux1,
        int bw, int bh, int sx, int sy, int dx, int dy, int onTicks, bool last, int clock = 0)
    {
        Mode = mode; SlotMask = slotMask; Cols = cols; Rows = rows; Aux0 = aux0; Aux1 = aux1;
        BlockW = bw; BlockH = bh; SrcX = sx; SrcY = sy; DstX = dx; DstY = dy;
        OnTicks = onTicks; Last = last; Clock = clock;
    }

    public int FrameCount => Cols * Rows;
    public bool IsFlash => Mode == 6;
    public bool IsPingPong => Mode == 2 || Mode == 3;
    // Modes 4/5 only draw while a free-running counter is inside [Aux0,Aux1] (or
    // a one-shot trigger window): a startup reveal, off forever after. They are
    // not part of the steady-state loop, so we neither loop nor bake them.
    public bool IsTimed => Mode == 4 || Mode == 5;
    public bool Valid => BlockW > 0 && BlockH > 0;

    // Does this entry produce visible motion in steady state?
    public bool Animates =>
        Valid && !IsTimed && (IsFlash ? OnTicks > 0 && Aux0 > 0 : FrameCount > 1);

    // A one-time setup copy: a genuine single-frame cycling entry (not a flash,
    // not a timed reveal). None exist in the sampled models, but kept for safety.
    public bool IsStaticStamp => Valid && !IsFlash && !IsTimed && FrameCount == 1;
}

// One GS copy that made a baked frame: entry Rec of the record array stamped cell
// Cell of its sheet onto (DstX,DstY)+(W,H). The extractor writes these alongside the
// frame image (ModelExtractor.InjectTextureAnimations) so the importer can push an
// edited frame's pixels back into the cell the game copies them from, without having
// to re-simulate the animation.
internal readonly struct TexAnimStamp
{
    public readonly int Rec, Cell, DstX, DstY, W, H;
    public TexAnimStamp(int rec, int cell, int dx, int dy, int w, int h)
    { Rec = rec; Cell = cell; DstX = dx; DstY = dy; W = w; H = h; }
}

internal sealed class TexAnimBake
{
    public int Width;
    public int Height;
    public List<byte[]> FramesRgba = new();
    public List<float> FrameTimes = new();   // seconds
    public List<List<TexAnimStamp>> FrameStamps = new();  // per kept frame, in stamp order
    public float Duration;                    // loop length (seconds)
}

internal static class TexAnim
{
    // Rate at which the per-entry tick counter advances (one step every Aux0
    // ticks). 30 reproduces the observed speeds (e.g. carot energy Aux0=2/4 ->
    // 15 / 7.5 fps). Lower this to slow everything proportionally.
    public const float BaseTicksPerSecond = 30f;

    // Anti-strobe floor for mode-6 flashes only. Disabled (0) by default so
    // flashes play exactly as authored in game (e.g. bagworm's fast EM-static).
    // Raise it (e.g. 0.30f) only if you want to tame a too-fast strobe; it
    // stretches any flash whose full on/off cycle is shorter, leaving slower
    // flashes and all non-flash anims untouched.
    public const float MinFlashPeriodSeconds = 0f;

    private const int MaxEntries = 64;
    private const int MaxPeriodTicks = 600;
    private const int MaxBakedFrames = 96;

    public static List<TexAnimEntry> Parse(byte[] data, int postOff)
    {
        var list = new List<TexAnimEntry>();
        if (postOff <= 0 || postOff + 0x18 > data.Length) return list;

        int o = postOff;
        for (int i = 0; i < MaxEntries; i++)
        {
            if (o + 0x18 > data.Length) break;
            byte f0 = data[o];
            byte grid = data[o + 2];
            int mode = (f0 >> 2) & 7;
            int slotMask = data[o + 1];
            int cols = grid & 0xF;
            int rows = (grid >> 4) & 0xF;
            int aux0 = U16(data, o + 0x04);
            int aux1 = U16(data, o + 0x06);
            int bw = U16(data, o + 0x08);
            int bh = U16(data, o + 0x0A);
            int sx = U16(data, o + 0x0C);
            int sy = U16(data, o + 0x0E);
            int dx = U16(data, o + 0x10);
            int dy = U16(data, o + 0x12);
            int onTicks = data[o + 0x06];   // byte +6 (mode-6 on-duration)
            bool last = (f0 & 1) != 0;
            int clock = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 0x14)), int.MaxValue);

            if (bw > 2048 || bh > 2048) break;
            list.Add(new TexAnimEntry(mode, slotMask, cols, rows, aux0, aux1,
                bw, bh, sx, sy, dx, dy, onTicks, last, clock));
            if (last) break;
            o += 0x18;
        }
        return list;
    }

    private static void Stamp(byte[] dst, int w, int h, byte[] src,
        int sx, int sy, int dx, int dy, int bw, int bh)
    {
        for (int y = 0; y < bh; y++)
        {
            int syy = sy + y, dyy = dy + y;
            if ((uint)syy >= (uint)h || (uint)dyy >= (uint)h) continue;
            for (int x = 0; x < bw; x++)
            {
                int sxx = sx + x, dxx = dx + x;
                if ((uint)sxx >= (uint)w || (uint)dxx >= (uint)w) continue;
                int s = (syy * w + sxx) * 4, d = (dyy * w + dxx) * 4;
                dst[d] = src[s]; dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2]; dst[d + 3] = src[s + 3];
            }
        }
    }

    private static void StampFrame(byte[] dst, int w, int h, byte[] src, in TexAnimEntry e, int frame)
    {
        int n = Math.Max(1, e.FrameCount);
        int f = ((frame % n) + n) % n;
        int cols = Math.Max(1, e.Cols);
        int sx = e.SrcX + e.BlockW * (f % cols);
        int sy = e.SrcY + e.BlockH * (f / cols);
        Stamp(dst, w, h, src, sx, sy, e.DstX, e.DstY, e.BlockW, e.BlockH);
    }

    /// <summary>Apply one-time static setup copies (non-flash, single-frame) to the base image.</summary>
    public static void ApplyStaticStamps(byte[] rgba, int w, int h, IEnumerable<TexAnimEntry> entries, int activeSlotMask)
    {
        if (rgba.Length < w * h * 4) return;
        var snapshot = (byte[])rgba.Clone();
        foreach (var e in entries)
            if (e.IsStaticStamp && (e.SlotMask & activeSlotMask) != 0)
                Stamp(rgba, w, h, snapshot, e.SrcX, e.SrcY, e.DstX, e.DstY, e.BlockW, e.BlockH);
    }

    private static long Gcd(long a, long b) { while (b != 0) { (a, b) = (b, a % b); } return a < 0 ? -a : a; }
    private static long Lcm(long a, long b) { if (a == 0 || b == 0) return Math.Max(a, b); return a / Gcd(a, b) * b; }

    // Exact analytic period (ticks) and frame for each mode. Using exact
    // periods guarantees the baked loop is seamless.
    private static int FlashOffGap(in TexAnimEntry e)
    {
        int p = Math.Max(1, e.Aux0);
        int on = Math.Max(1, e.OnTicks);
        // Activation fires at multiples of Aux0; stays on for `on` ticks, then
        // waits for the next multiple strictly after that. off = nextMult - on.
        int nextMult = ((on + 1 + p - 1) / p) * p;
        return Math.Max(1, nextMult - on);
    }

    private sealed class Sim
    {
        public TexAnimEntry E;
        public int Index;        // position in the record array (for TexAnimStamp.Rec)
        public int Period;       // ticks for one full loop
        public int Scale = 1;    // anti-strobe slowdown (flash only)
        public int OnTicks;      // flash on (scaled)
        public int OffTicks;     // flash off (scaled)
    }

    private static Sim MakeSim(in TexAnimEntry e)
    {
        var s = new Sim { E = e };
        if (e.IsFlash)
        {
            int on = Math.Max(1, e.OnTicks);
            int off = FlashOffGap(e);
            int natural = on + off;
            // Anti-strobe: stretch flashes whose full cycle is shorter than the
            // minimum so a stark on/off toggle doesn't strobe. Leaves slower
            // flashes (and all non-flash anims) untouched.
            int minTicks = (int)Math.Ceiling(MinFlashPeriodSeconds * BaseTicksPerSecond);
            s.Scale = natural < minTicks ? (minTicks + natural - 1) / natural : 1;
            s.OnTicks = on * s.Scale;
            s.OffTicks = off * s.Scale;
            s.Period = s.OnTicks + s.OffTicks;
        }
        else
        {
            int n = Math.Max(1, e.FrameCount);
            int spd0 = Math.Max(1, e.Aux0);
            s.Period = e.IsPingPong && n > 1 ? (n - 1) * (spd0 + Math.Max(1, e.Aux1)) : n * spd0;
        }
        return s;
    }

    // Returns the frame index to stamp at tick t, or -1 for "draw nothing"
    // (flash in its off phase).
    private static int FrameAt(Sim s, long t)
    {
        var e = s.E;
        if (e.IsFlash)
        {
            // the game copies when (starting clock + t) % Aux0 == 0
            int tc = (int)((t + (long)e.Clock * s.Scale) % s.Period);
            return tc < s.OnTicks ? 0 : -1;
        }
        int n = Math.Max(1, e.FrameCount);
        if (n <= 1) return 0;
        int spd0 = Math.Max(1, e.Aux0);
        if (e.IsPingPong)
        {
            int spd1 = Math.Max(1, e.Aux1);
            int up = (n - 1) * spd0;
            int tc = (int)(t % s.Period);
            return tc < up ? tc / spd0 : (n - 1) - (tc - up) / spd1;
        }
        return (int)((t / spd0) % n);
    }

    /// <summary>
    /// Bake the animation into composite RGBA frames. 'baseRgba' must already
    /// have static stamps applied. Returns null if nothing animates.
    /// </summary>
    public static TexAnimBake? Bake(byte[] baseRgba, int w, int h, IReadOnlyList<TexAnimEntry> entries, int activeSlotMask)
    {
        if (baseRgba.Length < w * h * 4) return null;

        var sims = new List<Sim>();
        var seen = new HashSet<(bool, bool, int, int, int, int, int, int, int, int, int)>();
        for (int ei = 0; ei < entries.Count; ei++)
        {
            var e = entries[ei];
            if (!e.Animates) continue;
            // Runtime gate (SzModel_LoadTexAnim_Pipe @0x1e81b8): an entry only draws
            // when (slotMask & (1 << currentAnimSlot)) != 0. The extracted glb shows
            // the model's default animation (lowest present slot), so only entries
            // active in that slot belong in the baked steady-state loop. Dropping the
            // rest keeps event-only effects (e.g. attack-slot flashes) static.
            if ((e.SlotMask & activeSlotMask) == 0) continue;
            // De-dupe slot-mask variants: the same patch is often repeated once
            // per model-animation slot with different speeds. Only one plays at
            // a time in game, so collapse by geometry and keep the first speed.
            // (A phased blink is a different patch: same rectangles, another moment in the loop.)
            var key = (e.IsFlash, e.IsPingPong, e.Cols, e.Rows, e.BlockW, e.BlockH, e.SrcX, e.SrcY, e.DstX, e.DstY, e.Clock);
            if (!seen.Add(key)) continue;
            var sim = MakeSim(e);
            sim.Index = ei;
            sims.Add(sim);
        }
        if (sims.Count == 0) return null;

        long period = 1;
        foreach (var s in sims) period = Lcm(period, Math.Max(1, s.Period));
        if (period <= 0) period = 1;
        if (period > MaxPeriodTicks) period = MaxPeriodTicks;

        int step = (int)Math.Max(1, (period + MaxBakedFrames - 1) / MaxBakedFrames);

        var bake = new TexAnimBake { Width = w, Height = h, Duration = period / BaseTicksPerSecond };
        byte[]? prev = null;
        for (long t = 0; t < period; t += step)
        {
            var frame = (byte[])baseRgba.Clone();
            var stamps = new List<TexAnimStamp>();
            foreach (var s in sims)
            {
                int f = FrameAt(s, t);
                if (f < 0) continue;
                StampFrame(frame, w, h, baseRgba, s.E, f);
                int n2 = Math.Max(1, s.E.FrameCount);
                stamps.Add(new TexAnimStamp(s.Index, ((f % n2) + n2) % n2,
                    s.E.DstX, s.E.DstY, s.E.BlockW, s.E.BlockH));
            }
            if (prev != null && frame.AsSpan().SequenceEqual(prev)) continue;
            bake.FramesRgba.Add(frame);
            bake.FrameTimes.Add(t / BaseTicksPerSecond);
            bake.FrameStamps.Add(stamps);
            prev = frame;
        }

        if (bake.FramesRgba.Count < 2) return null;
        return bake;
    }
}