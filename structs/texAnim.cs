using static DotrAssetModder.Bin;
namespace DotrAssetModder;

/* PS2 "RH2" texture animation (the retail game's ".anm" data).
   Reverse-engineered from SzModel_LoadTexAnim_Pipe (0x1e80a0) and its per-mode
   handler blocks (0x1e8220 modes0/1, 0x1e826c modes2/3, 0x1e8380 mode6, ...).

   Each frame the game issues a GS local->local copy (BITBLTBUF/TRXPOS/TRXREG/
   TRXDIR=2) stamping a BlockW x BlockH tile from the source sheet onto the
   displayed region (DstX,DstY). The source tile is a 2D sprite-sheet walk:

       srcX = SrcX + BlockW * (frame %  Ncols)
       srcY = SrcY + BlockH * (frame /  Ncols)

     Ncols = byte2 low nibble, Nrows = byte2 high nibble, total = Ncols*Nrows
     (the prologue computes `s3 = Ncols * Nrows`, the running frame count).

   The frame index is persisted in byte[+3] of the entry and stepped by the
   handler off a counter: byte0 bit1 selects a free-running counter (60Hz) vs
   the model's animation frame. The step cadence is set by Aux words:

     modes 0/1 (handler 0x1e8220): forward loop. Advance frame by 1 each time
               counter % Aux0(+4) == 0.  -> per-frame rate = baseHz / Aux0.
     modes 2/3 (handler 0x1e826c): ping-pong. Aux0(+4) = up speed divisor,
               Aux1(+6) = down speed divisor; reverses at 0 and total-1.
     mode 6    (handler 0x1e8380): periodic flash/blink. Waits until
               counter % Aux0(+4) == 0, then stamps the (single) src->dst blit
               for byte[+6] ticks, then turns it off. Not frame-cycling.
     modes 4/5 (handlers 0x1e84ac / 0x1e8418): one-shot timed reveal. The blit
               is drawn only while the free-running counter is inside its
               [Aux0,Aux1] window (mode 4) or after a one-shot trigger (mode 5),
               so it plays once at startup and is off forever after. Not part
               of the steady-state loop; skipped when baking.

   In these packed .mrg files the converted 24-byte entry array is the "post
   section" pointed to by word[+8] of each 16-byte texture-table block.

   Entry layout (24 bytes):
     +0x00 u8   flags : bit0 = last/terminator, bit1 = counter source,
                        bits2-4 = mode
     +0x01 u8   slotMask
     +0x02 u8   grid     : low nibble = Ncols, high nibble = Nrows
     +0x04 u16  Aux0     : forward/flash speed divisor (ticks per step)
     +0x06 u16  Aux1     : ping-pong down speed; low byte = mode-6 on-duration
     +0x08 u16  BlockW    +0x0A u16 BlockH
     +0x0C u16  SrcX      +0x0E u16 SrcY
     +0x10 u16  DstX      +0x12 u16 DstY
     +0x14 u32  counter   (runtime; 0 in stored data) */
internal readonly struct TexAnimEntry
{
    public readonly int Mode;
    public readonly int SlotMask;
    public readonly int Columns, Rows;
    public readonly int Aux0, Aux1;
    public readonly int BlockWidth, BlockHeight;
    public readonly int SourceX, SourceY;
    public readonly int DestinationX, DestinationY;
    public readonly int OnTicks;   // mode-6 on-duration (byte +6)
    public readonly bool Last;
    public readonly int Clock;     // starting counter (+0x14): 0 in retail, a blink's phase in DoD imports

    public TexAnimEntry(int mode, int slotMask, int columns, int rows, int aux0, int aux1,
        int blockWidth, int blockHeight, int sourceX, int sourceY, int destinationX, int destinationY, int onTicks, bool last, int clock = 0)
    {
        Mode = mode; SlotMask = slotMask; Columns = columns; Rows = rows; Aux0 = aux0; Aux1 = aux1;
        BlockWidth = blockWidth; BlockHeight = blockHeight; SourceX = sourceX; SourceY = sourceY; DestinationX = destinationX; DestinationY = destinationY;
        OnTicks = onTicks; Last = last; Clock = clock;
    }

    public int FrameCount => Columns * Rows;
    public bool IsFlash => Mode == 6;
    public bool IsPingPong => Mode == 2 || Mode == 3;
    // Modes 4/5: startup reveal, off forever after; neither looped nor baked.
    public bool IsTimed => Mode == 4 || Mode == 5;
    public bool Valid => BlockWidth > 0 && BlockHeight > 0;

    public bool Animates =>
        Valid && !IsTimed && (IsFlash ? OnTicks > 0 && Aux0 > 0 : FrameCount > 1);

    // One-time setup copy: single-frame entry that is not a flash or timed reveal.
    // None exist in the sampled models.
    public bool IsStaticStamp => Valid && !IsFlash && !IsTimed && FrameCount == 1;
}

/* One GS copy that made a baked frame: record RecordIndex stamped cell CellIndex of its
   sheet onto (DstX,DstY)+(Width,Height). The extractor writes these beside the frame
   image (ModelExtractor.InjectTextureAnimations) so the importer can push an edited
   frame's pixels back into the cell the game copies from, without re-simulating. */
internal readonly struct TexAnimStamp
{
    public readonly int RecordIndex, CellIndex, DstX, DstY, Width, Height;
    public TexAnimStamp(int recordIndex, int cellIndex, int destinationX, int destinationY, int width, int height)
    { RecordIndex = recordIndex; CellIndex = cellIndex; DstX = destinationX; DstY = destinationY; Width = width; Height = height; }
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
    // Tick rate of the per-entry counter (one step every Aux0 ticks). 30 reproduces the
    // observed speeds (e.g. carot energy Aux0=2/4 -> 15 / 7.5 fps).
    public const float BaseTicksPerSecond = 30f;

    /* Anti-strobe floor for mode-6 flashes only: stretches any flash whose full on/off
       cycle is shorter. 0 = flashes play as authored (e.g. bagworm's fast EM-static);
       e.g. 0.30f tames a too-fast strobe. */
    public const float MinFlashPeriodSeconds = 0f;

    private const int MaxEntries = 64;
    private const int MaxPeriodTicks = 600;
    private const int MaxBakedFrames = 96;

    public static List<TexAnimEntry> Parse(byte[] bytes, int postSectionOffset)
    {
        var entries = new List<TexAnimEntry>();
        if (postSectionOffset <= 0 || postSectionOffset + 0x18 > bytes.Length) return entries;

        int entryOffset = postSectionOffset;
        for (int i = 0; i < MaxEntries; i++)
        {
            if (entryOffset + 0x18 > bytes.Length) break;
            byte flagsByte = bytes[entryOffset];
            byte grid = bytes[entryOffset + 2];
            int mode = (flagsByte >> 2) & 7;
            int slotMask = bytes[entryOffset + 1];
            int columns = grid & 0xF;
            int rows = (grid >> 4) & 0xF;
            int aux0 = ReadU16(bytes, entryOffset + 0x04);
            int aux1 = ReadU16(bytes, entryOffset + 0x06);
            int blockWidth = ReadU16(bytes, entryOffset + 0x08);
            int blockHeight = ReadU16(bytes, entryOffset + 0x0A);
            int sourceX = ReadU16(bytes, entryOffset + 0x0C);
            int sourceY = ReadU16(bytes, entryOffset + 0x0E);
            int destinationX = ReadU16(bytes, entryOffset + 0x10);
            int destinationY = ReadU16(bytes, entryOffset + 0x12);
            int onTicks = bytes[entryOffset + 0x06];   // byte +6 (mode-6 on-duration)
            bool last = (flagsByte & 1) != 0;
            int clock = (int)Math.Min(ReadU32(bytes, entryOffset + 0x14), int.MaxValue);

            if (blockWidth > 2048 || blockHeight > 2048) break;
            entries.Add(new TexAnimEntry(mode, slotMask, columns, rows, aux0, aux1,
                blockWidth, blockHeight, sourceX, sourceY, destinationX, destinationY, onTicks, last, clock));
            if (last) break;
            entryOffset += 0x18;
        }
        return entries;
    }

    private static void Stamp(byte[] destinationPixels, int width, int height, byte[] sourcePixels,
        int sourceX, int sourceY, int destinationX, int destinationY, int blockWidth, int blockHeight)
    {
        for (int y = 0; y < blockHeight; y++)
        {
            int sourceRowY = sourceY + y, destinationRowY = destinationY + y;
            if ((uint)sourceRowY >= (uint)height || (uint)destinationRowY >= (uint)height) continue;
            for (int x = 0; x < blockWidth; x++)
            {
                int sourceColumnX = sourceX + x, destinationColumnX = destinationX + x;
                if ((uint)sourceColumnX >= (uint)width || (uint)destinationColumnX >= (uint)width) continue;
                int sourceIndex = (sourceRowY * width + sourceColumnX) * 4, destinationIndex = (destinationRowY * width + destinationColumnX) * 4;
                destinationPixels[destinationIndex] = sourcePixels[sourceIndex]; destinationPixels[destinationIndex + 1] = sourcePixels[sourceIndex + 1];
                destinationPixels[destinationIndex + 2] = sourcePixels[sourceIndex + 2]; destinationPixels[destinationIndex + 3] = sourcePixels[sourceIndex + 3];
            }
        }
    }

    private static void StampFrame(byte[] destinationPixels, int width, int height, byte[] sourcePixels, in TexAnimEntry entry, int frame)
    {
        int frameCount = Math.Max(1, entry.FrameCount);
        int wrappedFrame = ((frame % frameCount) + frameCount) % frameCount;
        int columns = Math.Max(1, entry.Columns);
        int sourceX = entry.SourceX + entry.BlockWidth * (wrappedFrame % columns);
        int sourceY = entry.SourceY + entry.BlockHeight * (wrappedFrame / columns);
        Stamp(destinationPixels, width, height, sourcePixels, sourceX, sourceY, entry.DestinationX, entry.DestinationY, entry.BlockWidth, entry.BlockHeight);
    }

    /// <summary>Apply one-time static setup copies (non-flash, single-frame) to the base image.</summary>
    public static void ApplyStaticStamps(byte[] rgba, int width, int height, IEnumerable<TexAnimEntry> entries, int activeSlotMask)
    {
        if (rgba.Length < width * height * 4) return;
        var snapshot = (byte[])rgba.Clone();
        foreach (var entry in entries)
            if (entry.IsStaticStamp && (entry.SlotMask & activeSlotMask) != 0)
                Stamp(rgba, width, height, snapshot, entry.SourceX, entry.SourceY, entry.DestinationX, entry.DestinationY, entry.BlockWidth, entry.BlockHeight);
    }

    private static long Gcd(long first, long second) { while (second != 0) { (first, second) = (second, first % second); } return first < 0 ? -first : first; }
    private static long Lcm(long first, long second) { if (first == 0 || second == 0) return Math.Max(first, second); return first / Gcd(first, second) * second; }

    // Exact analytic periods (ticks) per mode keep the baked loop seamless.
    private static int FlashOffGap(in TexAnimEntry entry)
    {
        int activationPeriod = Math.Max(1, entry.Aux0);
        int onTickCount = Math.Max(1, entry.OnTicks);
        // Activation fires at multiples of Aux0; stays on for `onTickCount` ticks, then
        // waits for the next multiple strictly after that. off = nextMultiple - onTickCount.
        int nextMultiple = ((onTickCount + 1 + activationPeriod - 1) / activationPeriod) * activationPeriod;
        return Math.Max(1, nextMultiple - onTickCount);
    }

    private sealed class Sim
    {
        public TexAnimEntry Entry;
        public int Index;        // position in the record array (for TexAnimStamp.RecordIndex)
        public int Period;       // ticks for one full loop
        public int Scale = 1;    // anti-strobe slowdown (flash only)
        public int OnTicks;      // flash on (scaled)
        public int OffTicks;     // flash off (scaled)
    }

    private static Sim MakeSim(in TexAnimEntry entry)
    {
        var simulation = new Sim { Entry = entry };
        if (entry.IsFlash)
        {
            int onTickCount = Math.Max(1, entry.OnTicks);
            int offTickCount = FlashOffGap(entry);
            int naturalPeriod = onTickCount + offTickCount;
            // Anti-strobe: stretch flashes whose full cycle is shorter than the minimum.
            int minTicks = (int)Math.Ceiling(MinFlashPeriodSeconds * BaseTicksPerSecond);
            simulation.Scale = naturalPeriod < minTicks ? (minTicks + naturalPeriod - 1) / naturalPeriod : 1;
            simulation.OnTicks = onTickCount * simulation.Scale;
            simulation.OffTicks = offTickCount * simulation.Scale;
            simulation.Period = simulation.OnTicks + simulation.OffTicks;
        }
        else
        {
            int frameCount = Math.Max(1, entry.FrameCount);
            int upSpeedDivisor = Math.Max(1, entry.Aux0);
            simulation.Period = entry.IsPingPong && frameCount > 1 ? (frameCount - 1) * (upSpeedDivisor + Math.Max(1, entry.Aux1)) : frameCount * upSpeedDivisor;
        }
        return simulation;
    }

    // Frame index to stamp at the tick, or -1 (flash in its off phase).
    private static int FrameAt(Sim simulation, long tick)
    {
        var entry = simulation.Entry;
        if (entry.IsFlash)
        {
            // the game copies when (starting clock + t) % Aux0 == 0
            int tickInCycle = (int)((tick + (long)entry.Clock * simulation.Scale) % simulation.Period);
            return tickInCycle < simulation.OnTicks ? 0 : -1;
        }
        int frameCount = Math.Max(1, entry.FrameCount);
        if (frameCount <= 1) return 0;
        int upSpeedDivisor = Math.Max(1, entry.Aux0);
        if (entry.IsPingPong)
        {
            int downSpeedDivisor = Math.Max(1, entry.Aux1);
            int upTicks = (frameCount - 1) * upSpeedDivisor;
            int tickInCycle = (int)(tick % simulation.Period);
            return tickInCycle < upTicks ? tickInCycle / upSpeedDivisor : (frameCount - 1) - (tickInCycle - upTicks) / downSpeedDivisor;
        }
        return (int)((tick / upSpeedDivisor) % frameCount);
    }

    /// <summary>
    /// Bake the animation into composite RGBA frames. 'baseRgba' must already
    /// have static stamps applied. Returns null if nothing animates.
    /// </summary>
    public static TexAnimBake? Bake(byte[] baseRgba, int width, int height, IReadOnlyList<TexAnimEntry> entries, int activeSlotMask)
    {
        if (baseRgba.Length < width * height * 4) return null;

        var simulations = new List<Sim>();
        var seen = new HashSet<(bool, bool, int, int, int, int, int, int, int, int, int)>();
        for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            var entry = entries[entryIndex];
            if (!entry.Animates) continue;
            /* Runtime gate (SzModel_LoadTexAnim_Pipe @0x1ea528): an entry only draws
               when (slotMask & (1 << currentAnimSlot)) != 0. The extracted glb shows
               the model's default animation (lowest present slot), so only entries
               active in that slot belong in the baked loop; event-only effects
               (e.g. attack-slot flashes) stay static. */
            if ((entry.SlotMask & activeSlotMask) == 0) continue;
            /* De-dupe slot-mask variants: the same patch is often repeated once per
               model-animation slot with different speeds, and only one plays at a time
               in game, so collapse by geometry and keep the first speed. A phased blink
               (same rectangles, another moment in the loop) is a different patch. */
            var geometryKey = (entry.IsFlash, entry.IsPingPong, entry.Columns, entry.Rows, entry.BlockWidth, entry.BlockHeight, entry.SourceX, entry.SourceY, entry.DestinationX, entry.DestinationY, entry.Clock);
            if (!seen.Add(geometryKey)) continue;
            var simulation = MakeSim(entry);
            simulation.Index = entryIndex;
            simulations.Add(simulation);
        }
        if (simulations.Count == 0) return null;

        long period = 1;
        foreach (var periodSimulation in simulations) period = Lcm(period, Math.Max(1, periodSimulation.Period));
        if (period <= 0) period = 1;
        if (period > MaxPeriodTicks) period = MaxPeriodTicks;

        int step = (int)Math.Max(1, (period + MaxBakedFrames - 1) / MaxBakedFrames);

        var bake = new TexAnimBake { Width = width, Height = height, Duration = period / BaseTicksPerSecond };
        byte[]? previousFrame = null;
        for (long tick = 0; tick < period; tick += step)
        {
            var frame = (byte[])baseRgba.Clone();
            var stamps = new List<TexAnimStamp>();
            foreach (var frameSimulation in simulations)
            {
                int frameIndex = FrameAt(frameSimulation, tick);
                if (frameIndex < 0) continue;
                StampFrame(frame, width, height, baseRgba, frameSimulation.Entry, frameIndex);
                int cellCount = Math.Max(1, frameSimulation.Entry.FrameCount);
                stamps.Add(new TexAnimStamp(frameSimulation.Index, ((frameIndex % cellCount) + cellCount) % cellCount,
                    frameSimulation.Entry.DestinationX, frameSimulation.Entry.DestinationY, frameSimulation.Entry.BlockWidth, frameSimulation.Entry.BlockHeight));
            }
            if (previousFrame != null && frame.AsSpan().SequenceEqual(previousFrame)) continue;
            bake.FramesRgba.Add(frame);
            bake.FrameTimes.Add(tick / BaseTicksPerSecond);
            bake.FrameStamps.Add(stamps);
            previousFrame = frame;
        }

        if (bake.FramesRgba.Count < 2) return null;
        return bake;
    }
}