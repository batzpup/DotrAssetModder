using static DotrAssetModder.Bin;
namespace DotrAssetModder;

/* One sound-effect bank as the IOP sound driver receives it. The driver (IOP/KCEJEAST.IRX, "Remote
   SD Driver version 1.5.0327") ships with its debug info, which names the layout:
     SD_SEBANK { int m_iNumSE; int m_iSpuOffset; SD_SEVAG m_stSEVag[255]; }      0x800 bytes
     SD_SEVAG  { u8 m_cVolumeL; u8 m_cVagVTime; u8 m_cPriority; u8 m_cVoiceGrp;
                 u16 m_wPitch; u16 m_wOffset; }                                 8 bytes
   followed by PS2 SPU ADPCM sample data with no VAG header. On the disc m_iSpuOffset holds the
   sample data's byte size: SD_vTransferSEGrpToIopProc @ 0x10B6C0 sends that + 0x800 bytes.

   The banks: SOUND/SYSTEM/SYSTEM0.GRP and SOUND/CARD/CARD00.GRP are one bank each;
   SOUND/MONSTER/SEGRP.MRG is fixed slots of 0x80800 bytes, slot N = card N's sounds
   (lSzFile_SetReadRequestParam maps the dev path m%03d/m%03d.grp to
   SzDuel_GetAlternateCardAtIndex(model) * 0x80800, the same index as the MONSTER.MRG slot).

   Several entries may share one sample at another pitch, so a bank is a list of entries, not of
   samples. 47 retail entries start one block into a sample (they skip its leading silent block);
   decoding from the entry's offset to the end flag is still the whole sound. */
public sealed class SoundBank
{
    public const int HeaderSize = 0x800;
    public const int EntrySize = 8;
    public const int MaxEntryCount = 255;
    // Highest m_cVolumeL on the disc (retail uses 10-127); how the driver scales it is not traced.
    public const int MaxVolume = 127;
    public const int MonsterSlotSize = 0x80800;

    /* A monster bank loads into SPU RAM bank 2 or 3 (the duelist's side): SD_InitializeDevice
       @ 0x109A90 puts bank 2 at 0x199810, bank 3 at 0x1CB810 and bank 4 at 0x1FD810, so each side
       holds 0x32000 bytes of sample data, less than the SEGRP.MRG slot. */
    public const int MonsterSampleDataLimit = 0x32000;
    public const int AdpcmBlockSize = 16;
    public const int SamplesPerAdpcmBlock = 28;
    public const int BytesPerPcmSample = 2;

    public const string SystemBankPath = "SOUND/SYSTEM/SYSTEM0.GRP";
    public const string CardBankPath = "SOUND/CARD/CARD00.GRP";
    public const string MonsterBanksPath = "SOUND/MONSTER/SEGRP.MRG";

    // ADPCM block flags (byte 1 of a block).
    const int EndFlag = 1;
    const int RepeatFlag = 2;
    const int LoopStartFlag = 4;

    const int HighestShift = 12;

    /* How every retail sample is framed (all 685 banks): a silent first block (shift 12, filter 0,
       flags 0), the sound's blocks with the end flag on the last, then this terminator block. */
    static readonly byte[] SilentFirstBlock = { 0x0C, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    static readonly byte[] TerminatorBlock = { 0x00, 0x07, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77 };

    // SPU ADPCM prediction filters: coefficient pairs in 1/64 units, indexed by the block's filter.
    static readonly int[] PreviousSampleCoefficients = { 0, 60, 115, 98, 122 };
    static readonly int[] SampleBeforePreviousCoefficients = { 0, 0, -52, -55, -60 };

    /* The driver's semitone table (KCEJEAST.IRX .rodata, 12 rows of 128 fine steps): every one of
       its 1536 values equals floor(4096 * 2^(i / 1536)), checked against the retail driver. */
    static readonly ushort[] SemitonePitchTable = BuildSemitonePitchTable();

    public SoundEntry[] Entries = [];

    // The sample data only (the bytes after the 0x800 header).
    public byte[] AdpcmBytes = [];

    // Reads one bank of `availableBytes` (the file, or one SEGRP.MRG slot) from the disc image.
    public static SoundBank Read(FileLoader disc, long imageOffset, long availableBytes)
    {
        if (availableBytes < HeaderSize)
        {
            throw new InvalidDataException($"only {availableBytes} bytes, a bank header needs {HeaderSize}");
        }
        byte[] headerBytes = new byte[HeaderSize];
        disc.ReadAt(imageOffset, headerBytes, 0, HeaderSize);

        uint sampleDataSize = ReadU32(headerBytes, 4);
        if (sampleDataSize > availableBytes - HeaderSize)
        {
            throw new InvalidDataException($"sample data size {sampleDataSize} is past the bank's end ({availableBytes - HeaderSize} bytes after the header)");
        }
        byte[] adpcmBytes = new byte[sampleDataSize];
        disc.ReadAt(imageOffset + HeaderSize, adpcmBytes, 0, adpcmBytes.Length);
        return Parse(headerBytes, adpcmBytes);
    }

    // Every count and offset is checked here, so Decode never reads outside AdpcmBytes.
    public static SoundBank Parse(byte[] headerBytes, byte[] adpcmBytes)
    {
        if (headerBytes.Length < HeaderSize)
        {
            throw new InvalidDataException($"header is {headerBytes.Length} bytes, expected {HeaderSize}");
        }
        uint entryCount = ReadU32(headerBytes, 0);
        if (entryCount > MaxEntryCount)
        {
            throw new InvalidDataException($"{entryCount} sounds, the driver holds at most {MaxEntryCount}");
        }
        uint sampleDataSize = ReadU32(headerBytes, 4);
        if (sampleDataSize != adpcmBytes.Length)
        {
            throw new InvalidDataException($"header says {sampleDataSize} bytes of sample data, {adpcmBytes.Length} given");
        }

        var bank = new SoundBank();
        bank.AdpcmBytes = adpcmBytes;
        bank.Entries = new SoundEntry[entryCount];
        for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
        {
            int entryOffset = EntrySize + entryIndex * EntrySize;
            var entry = new SoundEntry();
            entry.Index = entryIndex;
            entry.RawBytes = headerBytes.AsSpan(entryOffset, EntrySize).ToArray();
            entry.Volume = headerBytes[entryOffset];
            entry.VagVTime = headerBytes[entryOffset + 1];
            entry.Priority = headerBytes[entryOffset + 2];
            entry.VoiceGroup = headerBytes[entryOffset + 3];
            entry.Pitch = ReadU16(headerBytes, entryOffset + 4);
            entry.SampleByteOffset = ReadU16(headerBytes, entryOffset + 6) * AdpcmBlockSize;
            entry.SampleRate = SampleRateOfPitch(entry.Pitch);
            if (entry.SampleRate <= 0)
            {
                throw new InvalidDataException($"sound {entryIndex:00}: pitch 0x{entry.Pitch:X4} gives no playable rate");
            }
            if (entry.SampleByteOffset + AdpcmBlockSize > adpcmBytes.Length)
            {
                throw new InvalidDataException($"sound {entryIndex:00}: starts at sample byte {entry.SampleByteOffset}, past the {adpcmBytes.Length} bytes of sample data");
            }
            MeasureSample(entry, adpcmBytes);
            bank.Entries[entryIndex] = entry;
        }
        return bank;
    }

    /* Counts the entry's blocks up to and including the first with the end flag (the voice stops
       there; a looping sample's last block carries end + repeat, so this plays it once). With no
       end flag the sound runs to the end of the sample data. */
    static void MeasureSample(SoundEntry entry, byte[] adpcmBytes)
    {
        int blockCount = 0;
        int lastWholeBlockOffset = adpcmBytes.Length - AdpcmBlockSize;
        for (int blockOffset = entry.SampleByteOffset; blockOffset <= lastWholeBlockOffset; blockOffset += AdpcmBlockSize)
        {
            int filter = adpcmBytes[blockOffset] >> 4;
            if (filter >= PreviousSampleCoefficients.Length)
            {
                throw new InvalidDataException($"sound {entry.Index:00}: ADPCM filter {filter} at sample byte {blockOffset} (the SPU has 0-4)");
            }
            int flags = adpcmBytes[blockOffset + 1];
            blockCount++;
            if ((flags & (RepeatFlag | LoopStartFlag)) != 0)
            {
                entry.Loops = true;
            }
            if ((flags & EndFlag) != 0)
            {
                break;
            }
        }
        entry.BlockCount = blockCount;
    }

    /* Decodes one entry to mono 16-bit little-endian PCM at the start of pcmBytes, which must hold
       entry.PcmByteCount bytes; returns that count. Allocates nothing. */
    public int Decode(SoundEntry entry, byte[] pcmBytes)
    {
        if (pcmBytes.Length < entry.PcmByteCount)
        {
            throw new ArgumentException($"PCM buffer holds {pcmBytes.Length} bytes, sound {entry.Index:00} needs {entry.PcmByteCount}", nameof(pcmBytes));
        }
        int previousSample = 0;
        int sampleBeforePrevious = 0;
        int pcmOffset = 0;
        int blockOffset = entry.SampleByteOffset;
        for (int block = 0; block < entry.BlockCount; block++)
        {
            int shift = AdpcmBytes[blockOffset] & 0x0F;
            // The SPU treats the undefined shifts 13-15 as 9 (none on the retail disc).
            if (shift > 12)
            {
                shift = 9;
            }
            int filter = AdpcmBytes[blockOffset] >> 4;
            int previousCoefficient = PreviousSampleCoefficients[filter];
            int beforePreviousCoefficient = SampleBeforePreviousCoefficients[filter];
            for (int sampleInBlock = 0; sampleInBlock < SamplesPerAdpcmBlock; sampleInBlock++)
            {
                int packedByte = AdpcmBytes[blockOffset + 2 + (sampleInBlock >> 1)];
                int nibble;
                if ((sampleInBlock & 1) == 0)
                {
                    nibble = packedByte & 0x0F;
                }
                else
                {
                    nibble = packedByte >> 4;
                }
                // Sign-extend the nibble into the top of a 16-bit value, then scale it down.
                int sample = ((nibble << 28) >> 16) >> shift;
                sample += (previousSample * previousCoefficient + sampleBeforePrevious * beforePreviousCoefficient + 32) >> 6;
                sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
                sampleBeforePrevious = previousSample;
                previousSample = sample;
                pcmBytes[pcmOffset] = (byte)sample;
                pcmBytes[pcmOffset + 1] = (byte)(sample >> 8);
                pcmOffset += BytesPerPcmSample;
            }
            blockOffset += AdpcmBlockSize;
        }
        return pcmOffset;
    }

    /* The bank's sample data size once `replacements` (one per entry, null = keep) are laid out by
       Build: nothing is written. */
    public int SampleDataSize(SoundReplacement?[] replacements)
    {
        return LayOutSampleData(replacements, null, new int[Entries.Length]);
    }

    /* The bank's header and sample data as the game loads them (0x800 + data size bytes), with
       `replacements` (one per entry, null = keep) applied. The data is rebuilt compacted: each
       sample still used is copied once, in disc order, with the replaced entries' own new samples
       after the sample they used; samples no entry uses are dropped. A kept entry keeps its six
       other header bytes and its place inside its sample; a replaced entry gets the new volume
       and pitch.
       Throws when the data would pass sampleDataLimit (the room the bank has, see
       MonsterSampleDataLimit) or an offset would not fit m_wOffset. */
    public byte[] Build(SoundReplacement?[] replacements, int sampleDataLimit)
    {
        int sampleDataSize = SampleDataSize(replacements);
        if (sampleDataSize > sampleDataLimit)
        {
            throw new InvalidDataException($"sample data is {sampleDataSize:N0} bytes, the bank holds {sampleDataLimit:N0} ({sampleDataSize - sampleDataLimit:N0} over)");
        }
        byte[] bankBytes = new byte[HeaderSize + sampleDataSize];
        int[] newSampleByteOffsets = new int[Entries.Length];
        LayOutSampleData(replacements, bankBytes, newSampleByteOffsets);

        WriteU32(bankBytes, 0, (uint)Entries.Length);
        WriteU32(bankBytes, 4, (uint)sampleDataSize);
        for (int entryIndex = 0; entryIndex < Entries.Length; entryIndex++)
        {
            int entryOffset = EntrySize + entryIndex * EntrySize;
            Entries[entryIndex].RawBytes.CopyTo(bankBytes, entryOffset);
            if (replacements[entryIndex] != null)
            {
                bankBytes[entryOffset] = replacements[entryIndex]!.Volume;
                WriteU16(bankBytes, entryOffset + 4, replacements[entryIndex]!.Pitch);
            }
            int offsetInBlocks = newSampleByteOffsets[entryIndex] / AdpcmBlockSize;
            if (offsetInBlocks > ushort.MaxValue)
            {
                throw new InvalidDataException($"sound {entryIndex:00} would start at sample byte {newSampleByteOffsets[entryIndex]:N0}, past what m_wOffset can address");
            }
            WriteU16(bankBytes, entryOffset + 6, (ushort)offsetInBlocks);
        }
        return bankBytes;
    }

    /* Lays the sample data out for Build: sets every entry's new byte offset and returns the data
       size; copies the bytes into bankBytes (after the header) unless it is null.

       A sample is found from the entries that use it: an entry's sound runs from its offset to
       its end-flag block, so entries ending on the same block share one sample, which starts at
       the lowest of their offsets. The sample also takes the terminator block after the end
       (flags 7) and, when an entry starts one block in, the silent block before it, as on the
       disc. */
    int LayOutSampleData(SoundReplacement?[] replacements, byte[]? bankBytes, int[] newSampleByteOffsets)
    {
        if (replacements.Length != Entries.Length)
        {
            throw new ArgumentException($"{replacements.Length} replacements for {Entries.Length} sounds", nameof(replacements));
        }
        var samplesByEnd = new Dictionary<int, BankSample>();
        for (int entryIndex = 0; entryIndex < Entries.Length; entryIndex++)
        {
            SoundEntry entry = Entries[entryIndex];
            int soundEnd = entry.SampleByteOffset + entry.BlockCount * AdpcmBlockSize;
            if (!samplesByEnd.TryGetValue(soundEnd, out BankSample? sample))
            {
                sample = new BankSample();
                sample.Start = entry.SampleByteOffset;
                sample.End = soundEnd;
                if (soundEnd + AdpcmBlockSize <= AdpcmBytes.Length && AdpcmBytes[soundEnd + 1] == (EndFlag | RepeatFlag | LoopStartFlag))
                {
                    sample.End = soundEnd + AdpcmBlockSize;
                }
                samplesByEnd.Add(soundEnd, sample);
            }
            sample.Start = Math.Min(sample.Start, entry.SampleByteOffset);
            sample.EntryIndices.Add(entryIndex);
        }
        List<BankSample> samplesInDiscOrder = samplesByEnd.Values.OrderBy(sample => sample.Start).ToList();
        int previousSampleEnd = 0;
        foreach (BankSample sample in samplesInDiscOrder)
        {
            int blockBefore = sample.Start - AdpcmBlockSize;
            if (blockBefore >= previousSampleEnd && AdpcmBytes.AsSpan(blockBefore, AdpcmBlockSize).SequenceEqual(SilentFirstBlock))
            {
                sample.Start = blockBefore;
            }
            previousSampleEnd = sample.End;
        }

        int dataSize = 0;
        foreach (BankSample sample in samplesInDiscOrder)
        {
            bool stillUsed = false;
            foreach (int entryIndex in sample.EntryIndices)
            {
                if (replacements[entryIndex] == null)
                {
                    stillUsed = true;
                }
            }
            if (stillUsed)
            {
                int sampleLength = sample.End - sample.Start;
                if (bankBytes != null)
                {
                    Array.Copy(AdpcmBytes, sample.Start, bankBytes, HeaderSize + dataSize, sampleLength);
                }
                foreach (int entryIndex in sample.EntryIndices)
                {
                    if (replacements[entryIndex] == null)
                    {
                        newSampleByteOffsets[entryIndex] = dataSize + Entries[entryIndex].SampleByteOffset - sample.Start;
                    }
                }
                dataSize += sampleLength;
            }
            foreach (int entryIndex in sample.EntryIndices)
            {
                SoundReplacement? replacement = replacements[entryIndex];
                if (replacement == null)
                {
                    continue;
                }
                if (replacement.AdpcmBytes.Length == 0 || replacement.AdpcmBytes.Length % AdpcmBlockSize != 0)
                {
                    throw new ArgumentException($"sound {entryIndex:00}: replacement is {replacement.AdpcmBytes.Length} bytes, not whole ADPCM blocks", nameof(replacements));
                }
                if (bankBytes != null)
                {
                    replacement.AdpcmBytes.CopyTo(bankBytes, HeaderSize + dataSize);
                }
                newSampleByteOffsets[entryIndex] = dataSize;
                dataSize += replacement.AdpcmBytes.Length;
            }
        }
        return dataSize;
    }

    // One sample in the bank's data and the entries that play it (LayOutSampleData).
    sealed class BankSample
    {
        public int Start;
        public int End;
        public List<int> EntryIndices = new List<int>();
    }

    /* Encodes mono 16-bit samples to one sample's ADPCM bytes framed like retail: the silent first
       block, the sound (the last block padded with silence to 28 samples), the terminator block.
       Looping (the two retail loops' flags): loop start + repeat on the first sound block, repeat
       on the rest, end + repeat on the last, so the whole sound repeats; otherwise only the last
       sound block carries the end flag. Each block takes the filter and shift whose decoded
       samples (the decoder's exact maths, carried from the previous block) are closest. */
    public static byte[] Encode(short[] samples, int sampleCount, bool loops)
    {
        if (sampleCount <= 0 || sampleCount > samples.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), $"{sampleCount} samples, {samples.Length} given");
        }
        int soundBlockCount = (sampleCount + SamplesPerAdpcmBlock - 1) / SamplesPerAdpcmBlock;
        byte[] adpcmBytes = new byte[(soundBlockCount + 2) * AdpcmBlockSize];
        SilentFirstBlock.CopyTo(adpcmBytes, 0);
        TerminatorBlock.CopyTo(adpcmBytes, (soundBlockCount + 1) * AdpcmBlockSize);

        var blockSamples = new int[SamplesPerAdpcmBlock];
        var trialNibbles = new int[SamplesPerAdpcmBlock];
        var bestNibbles = new int[SamplesPerAdpcmBlock];
        int previousSample = 0;
        int sampleBeforePrevious = 0;
        for (int block = 0; block < soundBlockCount; block++)
        {
            int firstSample = block * SamplesPerAdpcmBlock;
            for (int sampleInBlock = 0; sampleInBlock < SamplesPerAdpcmBlock; sampleInBlock++)
            {
                int sampleIndex = firstSample + sampleInBlock;
                if (sampleIndex < sampleCount)
                {
                    blockSamples[sampleInBlock] = samples[sampleIndex];
                }
                else
                {
                    blockSamples[sampleInBlock] = 0;
                }
            }

            long bestError = long.MaxValue;
            int bestFilter = 0;
            int bestShift = 0;
            int bestPreviousSample = 0;
            int bestSampleBeforePrevious = 0;
            for (int filter = 0; filter < PreviousSampleCoefficients.Length; filter++)
            {
                for (int shift = 0; shift <= HighestShift; shift++)
                {
                    int trialPreviousSample = previousSample;
                    int trialSampleBeforePrevious = sampleBeforePrevious;
                    long trialError = EncodeBlockTrial(blockSamples, filter, shift, trialNibbles, ref trialPreviousSample, ref trialSampleBeforePrevious, bestError);
                    if (trialError < bestError)
                    {
                        bestError = trialError;
                        bestFilter = filter;
                        bestShift = shift;
                        bestPreviousSample = trialPreviousSample;
                        bestSampleBeforePrevious = trialSampleBeforePrevious;
                        Array.Copy(trialNibbles, bestNibbles, SamplesPerAdpcmBlock);
                    }
                }
            }
            previousSample = bestPreviousSample;
            sampleBeforePrevious = bestSampleBeforePrevious;

            int flags = 0;
            bool isLastBlock = block == soundBlockCount - 1;
            if (loops)
            {
                flags = RepeatFlag;
                if (block == 0)
                {
                    flags |= LoopStartFlag;
                }
            }
            if (isLastBlock)
            {
                flags |= EndFlag;
            }

            int blockOffset = (block + 1) * AdpcmBlockSize;
            adpcmBytes[blockOffset] = (byte)(bestShift | (bestFilter << 4));
            adpcmBytes[blockOffset + 1] = (byte)flags;
            for (int sampleInBlock = 0; sampleInBlock < SamplesPerAdpcmBlock; sampleInBlock += 2)
            {
                int lowNibble = bestNibbles[sampleInBlock] & 0x0F;
                int highNibble = bestNibbles[sampleInBlock + 1] & 0x0F;
                adpcmBytes[blockOffset + 2 + (sampleInBlock >> 1)] = (byte)(lowNibble | (highNibble << 4));
            }
        }
        return adpcmBytes;
    }

    /* Quantises one block with one filter and shift exactly as Decode will rebuild it, and returns
       the squared error. Stops early (returning long.MaxValue) once the error passes errorToBeat. */
    static long EncodeBlockTrial(int[] blockSamples, int filter, int shift, int[] nibbles, ref int previousSample, ref int sampleBeforePrevious, long errorToBeat)
    {
        int previousCoefficient = PreviousSampleCoefficients[filter];
        int beforePreviousCoefficient = SampleBeforePreviousCoefficients[filter];
        int quantisationStep = 1 << (HighestShift - shift);
        int halfStep = quantisationStep >> 1;
        long squaredError = 0;
        for (int sampleInBlock = 0; sampleInBlock < SamplesPerAdpcmBlock; sampleInBlock++)
        {
            int prediction = (previousSample * previousCoefficient + sampleBeforePrevious * beforePreviousCoefficient + 32) >> 6;
            int residual = blockSamples[sampleInBlock] - prediction;
            int nibble;
            if (residual >= 0)
            {
                nibble = (residual + halfStep) / quantisationStep;
            }
            else
            {
                nibble = (residual - halfStep) / quantisationStep;
            }
            nibble = Math.Clamp(nibble, -8, 7);
            int decodedSample = Math.Clamp(nibble * quantisationStep + prediction, short.MinValue, short.MaxValue);
            nibbles[sampleInBlock] = nibble;
            long sampleError = blockSamples[sampleInBlock] - decodedSample;
            squaredError += sampleError * sampleError;
            if (squaredError >= errorToBeat)
            {
                return long.MaxValue;
            }
            sampleBeforePrevious = previousSample;
            previousSample = decodedSample;
        }
        return squaredError;
    }

    /* The SPU pitch the driver gives a sound: SD_vPlayVAGOnAPort calls MIDI_Note2Pitch @ 0x1FB0
       (KCEJEAST.IRX) with note = m_wPitch >> 8, fine = m_wPitch & 0x7F (1/128 semitone), centre
       note 60, centre fine 0. Same integer maths as the driver, including its 16-bit truncation;
       the final * 147 / 160 (= 44100 / 48000) makes note 60 play at 44.1 kHz. */
    public static int SpuPitchOfPitch(ushort pitch)
    {
        int note = pitch >> 8;
        int fine = pitch & 0x7F;
        int semitonesFromCentre = note - 60;
        int tablePitch;
        if (semitonesFromCentre >= 0)
        {
            int tableIndex = (semitonesFromCentre % 12) * 128 + fine;
            tablePitch = (SemitonePitchTable[tableIndex] << (semitonesFromCentre / 12)) & 0xFFFF;
        }
        else
        {
            int semitonesBelowCentre = -semitonesFromCentre;
            int tableIndex = ((12 - semitonesBelowCentre % 12) % 12) * 128 + fine;
            tablePitch = SemitonePitchTable[tableIndex] >> ((semitonesBelowCentre + 11) / 12);
        }
        return (tablePitch * 147 / 160) & 0xFFFF;
    }

    // The SPU2 plays pitches above 0x3FFF (4 x 48 kHz) as 0x3FFF; the driver does not clamp.
    public const int HighestSpuPitch = 0x3FFF;
    public static readonly int HighestSampleRate = (int)Math.Floor(HighestSpuPitch * 48000.0 / 4096.0);

    /* The m_wPitch whose rate is nearest `sampleRate` among those the SPU plays as written
       (SPU pitch 1..HighestSpuPitch); on a tie the lower pitch. Searches all 65,536 values, so
       call it when the rate changes, not every frame. */
    public static ushort PitchOfSampleRate(int sampleRate)
    {
        ushort nearestPitch = 0;
        int nearestDistance = int.MaxValue;
        for (int pitch = 0; pitch <= ushort.MaxValue; pitch++)
        {
            int spuPitch = SpuPitchOfPitch((ushort)pitch);
            if (spuPitch < 1 || spuPitch > HighestSpuPitch || (pitch & 0x80) != 0)
            {
                continue;
            }
            int distance = Math.Abs(SampleRateOfPitch((ushort)pitch) - sampleRate);
            if (distance < nearestDistance || (distance == nearestDistance && SpuPitchOfPitch(nearestPitch) > spuPitch))
            {
                nearestDistance = distance;
                nearestPitch = (ushort)pitch;
            }
        }
        return nearestPitch;
    }

    // The SPU2 plays pitch 0x1000 at 48 kHz; the rate is rounded to whole hertz for WAV/MP3.
    public static int SampleRateOfPitch(ushort pitch)
    {
        return (int)Math.Round(SpuPitchOfPitch(pitch) * 48000.0 / 4096.0, MidpointRounding.AwayFromZero);
    }

    static ushort[] BuildSemitonePitchTable()
    {
        var table = new ushort[12 * 128];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = (ushort)Math.Floor(4096.0 * Math.Pow(2.0, i / 1536.0));
        }
        return table;
    }
}

// A new sound for one entry (SoundBank.Build): its own sample (SoundBank.Encode), the pitch
// (m_wPitch) and the volume (m_cVolumeL, 0-127) the game plays it at.
public sealed class SoundReplacement
{
    public byte[] AdpcmBytes = [];
    public ushort Pitch;
    public byte Volume;
}

// One SD_SEVAG header entry. RawBytes keeps the 8 bytes as on the disc for SoundBank.Build.
public sealed class SoundEntry
{
    public int Index;
    public byte[] RawBytes = [];
    public byte Volume;           // m_cVolumeL, 0-127
    public byte VagVTime;         // m_cVagVTime: use not traced
    public byte Priority;         // m_cPriority
    public byte VoiceGroup;       // m_cVoiceGrp
    public ushort Pitch;          // m_wPitch: note << 8 | fine
    public int SampleByteOffset;  // m_wOffset * 16, from the start of the sample data
    public int BlockCount;        // ADPCM blocks up to and including the end-flag block
    public bool Loops;            // a block carries the loop start or repeat flag
    public int SampleRate;        // the rate the game plays it at (SoundBank.SampleRateOfPitch)

    public int SampleCount => BlockCount * SoundBank.SamplesPerAdpcmBlock;
    public int PcmByteCount => SampleCount * SoundBank.BytesPerPcmSample;
    public double Seconds => SampleCount / (double)SampleRate;
}
