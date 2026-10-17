using System.Buffers.Binary;
using static DotrAssetModder.Bin;
namespace DotrAssetModder;

/* One MOVIE/*.pss as the game's player (Sony's ezmpeg sample, movie_play @ 0x242470) reads it: an
   MPEG-2 program stream of 16 KiB packs. Each pack is a 14-byte pack header and four PES packets
   of 4096 bytes (the last one 14 shorter; in the first pack also 15 shorter for the system header).
     0xE0 video: MPEG-2 Main profile 640x448, 30 fps progressive, one GOP header every 18 pictures.
     0xBD audio: substream header FF A0 00 00, then one byte stream:
          "SShd" u32 0x18, i32 type 1 (PCM), i32 rate 48000, i32 channels 2, i32 interleave 0x200,
          i32 loop start -1, i32 loop end -1, "SSbd" u32 body size, body = s16le blocks of 0x200
          bytes left then 0x200 bytes right.
   Every PES header holds 10 bytes of PTS/DTS or 0xFF stuffing. An audio PES carries the PTS of its
   first byte; a video PES carries the PTS (and the DTS when it differs) of the first picture that
   starts in it. The file ends with padding up to the last pack and the 4-byte end code 00 00 01 B9.
   All four retail files follow this layout (DotR-PC NOTES/fmv.md). */
public sealed class PssMovie
{
    public const int PackSize = 0x4000;
    public const int PesSize = 0x1000;
    public const int PesPerPack = 4;
    public const int PackHeaderSize = 14;
    public const int PesStartSize = 6;                       // 00 00 01 id + u16 length
    public const int PesHeaderDataSize = 10;                 // PTS + DTS, or stuffing
    public const int PesHeaderSize = PesStartSize + 3 + PesHeaderDataSize;
    public const int AudioSubstreamHeaderSize = 4;
    public const int FrameRate = 30;
    public const int Width = 640;
    public const int Height = 448;
    public const int SampleRate = 48000;
    public const int ChannelCount = 2;
    public const int BytesPerSecondOfAudio = SampleRate * ChannelCount * 2;
    public const int AudioInterleaveSize = 0x200;
    public const int AudioHeaderSize = 0x28;                 // SShd block + "SSbd" + size
    public const int ClockRate = 90000;
    public const int ClockTicksPerFrame = ClockRate / FrameRate;
    // The retail first picture is shown about 0.05-0.08 s in; audio starts at the same time.
    public const long StartPresentationTime = 2 * ClockTicksPerFrame;
    // Retail pack header mux rate of the three played movies, in 50-byte units (9.728 Mbit/s).
    public const int MuxRate = 24320;
    // Retail MPEG-2 limits (sequence header of every movie): 8,192 kbit/s, VBV 1,835,008 bits.
    public const int MaxVideoBitRate = 8192000;
    public const int VideoBufferSizeBits = 1835008;

    const byte PackStartCode = 0xBA;
    const byte SystemHeaderCode = 0xBB;
    const byte EndCode = 0xB9;
    const byte VideoStreamId = 0xE0;
    const byte AudioStreamId = 0xBD;
    const byte PaddingStreamId = 0xBE;
    const byte PictureStartCode = 0x00;
    const byte SequenceEndCode = 0xB7;
    static readonly byte[] AudioSubstreamHeader = { 0xFF, 0xA0, 0x00, 0x00 };
    // OPMV_US's system header: rate bound 24,415, video bound 1, stream E0 with a 1,798 KiB buffer.
    static readonly byte[] SystemHeader = { 0x00, 0x00, 0x01, 0xBB, 0x00, 0x09, 0x80, 0xBE, 0x01, 0x00, 0x21, 0x7F, 0xE0, 0xE7, 0x06 };
    // PES extension of the first video packet: P-STD buffer flag, scale 1, size 0x706 KiB.
    static readonly byte[] FirstVideoPesExtension = { 0x1E, 0x67, 0x06 };

    public byte[] VideoStream = Array.Empty<byte>();
    // Interleaved left/right samples.
    public short[] Samples = Array.Empty<short>();
    public int AudioSampleRate;
    public int AudioChannelCount;

    // One encoded picture in decode order; times in frames (1/30 s) as the encoder gave them.
    public sealed class Picture
    {
        public byte[] Bytes = Array.Empty<byte>();
        public long PresentationFrame;
        public long DecodeFrame;
    }

    // ---- reading ------------------------------------------------------------------------------

    public static PssMovie Read(byte[] fileBytes)
    {
        var videoBytes = new MemoryStream();
        var audioBytes = new MemoryStream();
        int position = 0;
        while (position + 4 <= fileBytes.Length)
        {
            if (fileBytes[position] != 0 || fileBytes[position + 1] != 0 || fileBytes[position + 2] != 1)
            {
                throw new InvalidDataException($"No start code at 0x{position:X}.");
            }
            byte streamId = fileBytes[position + 3];
            if (streamId == EndCode)
            {
                break;
            }
            if (streamId == PackStartCode)
            {
                position += PackHeaderSize + (fileBytes[position + 13] & 7);
                continue;
            }
            int packetLength = BinaryPrimitives.ReadUInt16BigEndian(fileBytes.AsSpan(position + 4, 2));
            int packetEnd = position + PesStartSize + packetLength;
            if (packetEnd > fileBytes.Length)
            {
                throw new InvalidDataException($"Packet at 0x{position:X} runs past the end of the file.");
            }
            if (streamId == VideoStreamId || streamId == AudioStreamId)
            {
                int payloadStart = position + PesStartSize + 3 + fileBytes[position + 8];
                if (streamId == VideoStreamId)
                {
                    videoBytes.Write(fileBytes, payloadStart, packetEnd - payloadStart);
                }
                else
                {
                    if (!fileBytes.AsSpan(payloadStart, AudioSubstreamHeaderSize).SequenceEqual(AudioSubstreamHeader))
                    {
                        throw new InvalidDataException($"Audio packet at 0x{position:X} is not substream FF A0 00 00.");
                    }
                    payloadStart += AudioSubstreamHeaderSize;
                    audioBytes.Write(fileBytes, payloadStart, packetEnd - payloadStart);
                }
            }
            position = packetEnd;
        }

        var movie = new PssMovie();
        movie.VideoStream = videoBytes.ToArray();
        movie.ReadAudio(audioBytes.GetBuffer().AsSpan(0, (int)audioBytes.Length));
        return movie;
    }

    void ReadAudio(ReadOnlySpan<byte> audioBytes)
    {
        if (audioBytes.Length < AudioHeaderSize || !audioBytes.Slice(0, 4).SequenceEqual("SShd"u8) || !audioBytes.Slice(0x20, 4).SequenceEqual("SSbd"u8))
        {
            throw new InvalidDataException("The audio stream does not start with SShd/SSbd.");
        }
        int audioType = ReadI32(audioBytes, 0x08);
        AudioSampleRate = ReadI32(audioBytes, 0x0C);
        AudioChannelCount = ReadI32(audioBytes, 0x10);
        int interleaveSize = ReadI32(audioBytes, 0x14);
        int bodySize = ReadI32(audioBytes, 0x24);
        if (audioType != 1 || AudioChannelCount < 1 || interleaveSize <= 0 || interleaveSize % 2 != 0)
        {
            throw new InvalidDataException($"Unsupported audio: type {audioType}, {AudioChannelCount} channel(s), interleave {interleaveSize}.");
        }
        int blockGroupSize = interleaveSize * AudioChannelCount;
        if (bodySize < 0 || bodySize % blockGroupSize != 0 || AudioHeaderSize + bodySize > audioBytes.Length)
        {
            throw new InvalidDataException($"Audio body size {bodySize} does not fit the stream ({audioBytes.Length} B).");
        }
        ReadOnlySpan<byte> body = audioBytes.Slice(AudioHeaderSize, bodySize);
        int samplesPerBlock = interleaveSize / 2;
        Samples = new short[bodySize / 2];
        int sampleFrame = 0;
        for (int groupOffset = 0; groupOffset < bodySize; groupOffset += blockGroupSize)
        {
            for (int channel = 0; channel < AudioChannelCount; channel++)
            {
                int blockOffset = groupOffset + channel * interleaveSize;
                for (int i = 0; i < samplesPerBlock; i++)
                {
                    Samples[(sampleFrame + i) * AudioChannelCount + channel] = ReadI16(body, blockOffset + i * 2);
                }
            }
            sampleFrame += samplesPerBlock;
        }
    }

    // ---- writing ------------------------------------------------------------------------------

    // Audio stream bytes (SShd header + blocked body) for 48 kHz stereo interleaved samples.
    public static byte[] BuildAudioStream(short[] interleavedSamples)
    {
        int blockGroupSize = AudioInterleaveSize * ChannelCount;
        int sampleFrameCount = interleavedSamples.Length / ChannelCount;
        int bodySize = (sampleFrameCount * ChannelCount * 2 + blockGroupSize - 1) / blockGroupSize * blockGroupSize;
        var audioBytes = new byte[AudioHeaderSize + bodySize];
        "SShd"u8.CopyTo(audioBytes.AsSpan(0));
        WriteI32(audioBytes, 0x04, 0x18);
        WriteI32(audioBytes, 0x08, 1);
        WriteI32(audioBytes, 0x0C, SampleRate);
        WriteI32(audioBytes, 0x10, ChannelCount);
        WriteI32(audioBytes, 0x14, AudioInterleaveSize);
        WriteI32(audioBytes, 0x18, -1);
        WriteI32(audioBytes, 0x1C, -1);
        "SSbd"u8.CopyTo(audioBytes.AsSpan(0x20));
        WriteI32(audioBytes, 0x24, bodySize);

        int samplesPerBlock = AudioInterleaveSize / 2;
        Span<byte> body = audioBytes.AsSpan(AudioHeaderSize);
        int groupIndex = 0;
        for (int firstFrame = 0; firstFrame < sampleFrameCount; firstFrame += samplesPerBlock)
        {
            int groupOffset = groupIndex * blockGroupSize;
            int framesInGroup = Math.Min(samplesPerBlock, sampleFrameCount - firstFrame);
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                int blockOffset = groupOffset + channel * AudioInterleaveSize;
                for (int i = 0; i < framesInGroup; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(body.Slice(blockOffset + i * 2, 2), interleavedSamples[(firstFrame + i) * ChannelCount + channel]);
                }
            }
            groupIndex++;
        }
        return audioBytes;
    }

    const int VideoPayloadSize = PesSize - PesHeaderSize;
    const int AudioPayloadSize = PesSize - PesHeaderSize - AudioSubstreamHeaderSize;

    // Video stream bytes that fit in fileSize next to an audio stream of audioStreamSize bytes.
    public static long VideoBytesThatFit(long fileSize, long audioStreamSize)
    {
        long packCount = (fileSize - 4) / PackSize;
        long pesCount = packCount * PesPerPack;
        long audioPesCount = (audioStreamSize + AudioPayloadSize - 1) / AudioPayloadSize;
        // Each pack's last PES is a pack header shorter; the first pack also holds the system header.
        long videoBytes = (pesCount - audioPesCount) * VideoPayloadSize - packCount * PackHeaderSize - SystemHeader.Length - FirstVideoPesExtension.Length;
        return Math.Max(0, videoBytes);
    }

    /* Muxes pictures (decode order) and 48 kHz stereo samples into exactly fileSize bytes in the
       retail layout. Returns null when they need more than fileSize; contentSize then says how much
       (whole packs + the end code). The rest of a smaller movie is padding packs: the player stops
       at the video's sequence end code. */
    public static byte[]? Build(IReadOnlyList<Picture> pictures, short[] interleavedSamples, long fileSize, out long contentSize)
    {
        var muxer = new Muxer(pictures, BuildAudioStream(interleavedSamples));
        return muxer.Write(fileSize, out contentSize);
    }

    sealed class Muxer
    {
        readonly byte[] videoStream;
        readonly byte[] audioStream;
        // Per picture: where its picture start code lies in videoStream, and its times in 90 kHz.
        readonly long[] pictureStarts;
        readonly long[] picturePresentationTimes;
        readonly long[] pictureDecodeTimes;
        readonly long firstDecodeTime;
        int videoPosition;
        int audioPosition;
        int nextPictureIndex;
        long lastSystemClock;
        // One pack is built here, then copied to output while it still fits.
        readonly byte[] packBuffer = new byte[PackSize];
        int packPosition;
        byte[] output = Array.Empty<byte>();
        long outputSize;

        public Muxer(IReadOnlyList<Picture> pictures, byte[] audioStream)
        {
            this.audioStream = audioStream;
            pictureStarts = new long[pictures.Count];
            picturePresentationTimes = new long[pictures.Count];
            pictureDecodeTimes = new long[pictures.Count];
            long firstPresentationFrame = long.MaxValue;
            foreach (Picture picture in pictures)
            {
                firstPresentationFrame = Math.Min(firstPresentationFrame, picture.PresentationFrame);
            }

            var videoBytes = new MemoryStream();
            for (int i = 0; i < pictures.Count; i++)
            {
                Picture picture = pictures[i];
                int pictureStartOffset = FindPictureStartCode(picture.Bytes);
                pictureStarts[i] = videoBytes.Length + Math.Max(0, pictureStartOffset);
                picturePresentationTimes[i] = StartPresentationTime + (picture.PresentationFrame - firstPresentationFrame) * ClockTicksPerFrame;
                pictureDecodeTimes[i] = StartPresentationTime + (picture.DecodeFrame - firstPresentationFrame) * ClockTicksPerFrame;
                videoBytes.Write(picture.Bytes);
            }
            if (!EndsWithSequenceEnd(videoBytes))
            {
                videoBytes.Write(new byte[] { 0x00, 0x00, 0x01, SequenceEndCode });
            }
            videoStream = videoBytes.ToArray();
            firstDecodeTime = StartPresentationTime;
            if (pictures.Count > 0)
            {
                firstDecodeTime = pictureDecodeTimes[0];
            }
        }

        static int FindPictureStartCode(byte[] bytes)
        {
            for (int i = 0; i + 3 < bytes.Length; i++)
            {
                if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1 && bytes[i + 3] == PictureStartCode)
                {
                    return i;
                }
            }
            return -1;
        }

        static bool EndsWithSequenceEnd(MemoryStream videoBytes)
        {
            if (videoBytes.Length < 4)
            {
                return false;
            }
            byte[] buffer = videoBytes.GetBuffer();
            int end = (int)videoBytes.Length;
            return buffer[end - 4] == 0 && buffer[end - 3] == 0 && buffer[end - 2] == 1 && buffer[end - 1] == SequenceEndCode;
        }

        public byte[]? Write(long fileSize, out long contentSize)
        {
            long contentLimit = fileSize - 4;
            output = new byte[fileSize];
            outputSize = 0;
            int packIndex = 0;
            while (videoPosition < videoStream.Length || audioPosition < audioStream.Length)
            {
                packPosition = 0;
                WritePack(packIndex);
                StorePack(contentLimit);
                packIndex++;
            }
            contentSize = outputSize + 4;
            if (contentSize > fileSize)
            {
                return null;
            }
            WriteEndPadding(contentLimit);
            return output;
        }

        // Counts every pack; copies it only while the content still fits.
        void StorePack(long contentLimit)
        {
            if (outputSize + packPosition <= contentLimit)
            {
                Array.Copy(packBuffer, 0, output, outputSize, packPosition);
            }
            outputSize += packPosition;
        }

        void WritePack(int packIndex)
        {
            WritePackHeader(CurrentSystemClock());
            if (packIndex == 0)
            {
                SystemHeader.CopyTo(packBuffer, packPosition);
                packPosition += SystemHeader.Length;
            }
            for (int pesIndex = 0; pesIndex < PesPerPack; pesIndex++)
            {
                int slotEnd = PackSize;
                if (pesIndex < PesPerPack - 1)
                {
                    slotEnd = PackHeaderSize + (pesIndex + 1) * PesSize;
                    if (packIndex == 0)
                    {
                        slotEnd += SystemHeader.Length;
                    }
                }
                int slotSize = slotEnd - packPosition;
                bool videoDone = videoPosition >= videoStream.Length;
                bool audioDone = audioPosition >= audioStream.Length;
                if (videoDone && audioDone)
                {
                    WritePadding(slotSize);
                }
                else if (!audioDone && (videoDone || AudioTimeAt(audioPosition) <= VideoDecodeTimeAt(videoPosition)))
                {
                    WriteAudioPes(slotSize);
                }
                else
                {
                    WriteVideoPes(slotSize, packIndex == 0 && pesIndex == 0);
                }
            }
        }

        long AudioTimeAt(int position)
        {
            return StartPresentationTime + (long)position * ClockRate / BytesPerSecondOfAudio;
        }

        // DTS of the picture the byte at position belongs to.
        long VideoDecodeTimeAt(int position)
        {
            while (nextPictureIndex < pictureStarts.Length && pictureStarts[nextPictureIndex] <= position)
            {
                nextPictureIndex++;
            }
            if (nextPictureIndex == 0)
            {
                return firstDecodeTime;
            }
            return pictureDecodeTimes[nextPictureIndex - 1];
        }

        // Retail: the first pack's SCR is 0 and the clock follows the video DTS.
        long CurrentSystemClock()
        {
            long nextTime;
            if (videoPosition < videoStream.Length)
            {
                nextTime = VideoDecodeTimeAt(videoPosition);
            }
            else
            {
                nextTime = AudioTimeAt(Math.Min(audioPosition, audioStream.Length));
            }
            lastSystemClock = Math.Max(lastSystemClock, nextTime - firstDecodeTime);
            return lastSystemClock;
        }

        void WritePackHeader(long systemClock)
        {
            Span<byte> header = packBuffer.AsSpan(packPosition, PackHeaderSize);
            header[0] = 0; header[1] = 0; header[2] = 1; header[3] = PackStartCode;
            // '01' SCR[32..30] 1 SCR[29..15] 1 SCR[14..0] 1, extension 0, 1
            header[4] = (byte)(0x44 | ((systemClock >> 27) & 0x38) | ((systemClock >> 28) & 0x03));
            header[5] = (byte)(systemClock >> 20);
            header[6] = (byte)(0x04 | ((systemClock >> 12) & 0xF8) | ((systemClock >> 13) & 0x03));
            header[7] = (byte)(systemClock >> 5);
            header[8] = (byte)(0x04 | ((systemClock << 3) & 0xF8));
            header[9] = 0x01;
            header[10] = (byte)(MuxRate >> 14);
            header[11] = (byte)((MuxRate >> 6) & 0xFF);
            header[12] = (byte)(((MuxRate & 0x3F) << 2) | 0x03);
            header[13] = 0xF8;
            packPosition += PackHeaderSize;
        }

        void WriteAudioPes(int slotSize)
        {
            int capacity = slotSize - PesHeaderSize - AudioSubstreamHeaderSize;
            int payloadSize = Math.Min(capacity, audioStream.Length - audioPosition);
            int stuffing = FillerFor(slotSize, PesHeaderSize + AudioSubstreamHeaderSize + payloadSize);
            long presentationTime = AudioTimeAt(audioPosition);
            WritePesStart(AudioStreamId, 0x80, presentationTime, 0, ReadOnlySpan<byte>.Empty, stuffing, AudioSubstreamHeaderSize + payloadSize);
            AudioSubstreamHeader.CopyTo(packBuffer, packPosition);
            packPosition += AudioSubstreamHeaderSize;
            Array.Copy(audioStream, audioPosition, packBuffer, packPosition, payloadSize);
            packPosition += payloadSize;
            audioPosition += payloadSize;
            WritePadding(slotSize - (PesHeaderSize + stuffing + AudioSubstreamHeaderSize + payloadSize));
        }

        void WriteVideoPes(int slotSize, bool firstPes)
        {
            ReadOnlySpan<byte> extension = ReadOnlySpan<byte>.Empty;
            if (firstPes)
            {
                extension = FirstVideoPesExtension;
            }
            int headerSize = PesHeaderSize + extension.Length;
            int payloadSize = Math.Min(slotSize - headerSize, videoStream.Length - videoPosition);
            int stuffing = FillerFor(slotSize, headerSize + payloadSize);

            // The first picture whose start code begins in this payload gives the timestamps.
            int pictureIndex = -1;
            int searchIndex = nextPictureIndex;
            while (searchIndex > 0 && pictureStarts[searchIndex - 1] >= videoPosition)
            {
                searchIndex--;
            }
            for (; searchIndex < pictureStarts.Length && pictureStarts[searchIndex] < videoPosition + payloadSize; searchIndex++)
            {
                if (pictureStarts[searchIndex] >= videoPosition)
                {
                    pictureIndex = searchIndex;
                    break;
                }
            }
            byte timeFlags = 0;
            long presentationTime = 0;
            long decodeTime = 0;
            if (pictureIndex >= 0)
            {
                presentationTime = picturePresentationTimes[pictureIndex];
                decodeTime = pictureDecodeTimes[pictureIndex];
                timeFlags = 0x80;
                if (decodeTime != presentationTime)
                {
                    timeFlags = 0xC0;
                }
            }
            if (firstPes)
            {
                timeFlags |= 0x01;
            }
            WritePesStart(VideoStreamId, timeFlags, presentationTime, decodeTime, extension, stuffing, payloadSize);
            Array.Copy(videoStream, videoPosition, packBuffer, packPosition, payloadSize);
            packPosition += payloadSize;
            videoPosition += payloadSize;
            WritePadding(slotSize - (headerSize + stuffing + payloadSize));
        }

        // A padding packet needs at least 6 bytes; a smaller gap goes into header stuffing instead.
        static int FillerFor(int slotSize, int usedSize)
        {
            int gap = slotSize - usedSize;
            if (gap > 0 && gap < PesStartSize)
            {
                return gap;
            }
            return 0;
        }

        void WritePesStart(byte streamId, byte timeFlags, long presentationTime, long decodeTime, ReadOnlySpan<byte> extension, int extraStuffing, int payloadSize)
        {
            int headerDataSize = PesHeaderDataSize + extension.Length + extraStuffing;
            int packetLength = 3 + headerDataSize + payloadSize;
            Span<byte> header = packBuffer.AsSpan(packPosition, PesStartSize + 3 + headerDataSize);
            header[0] = 0; header[1] = 0; header[2] = 1; header[3] = streamId;
            BinaryPrimitives.WriteUInt16BigEndian(header.Slice(4, 2), (ushort)packetLength);
            header[6] = 0x83;                      // '10', not scrambled, copyright, original
            header[7] = timeFlags;
            header[8] = (byte)headerDataSize;
            int cursor = 9;
            bool hasPresentationTime = (timeFlags & 0x80) != 0;
            bool hasDecodeTime = (timeFlags & 0x40) != 0;
            if (hasPresentationTime)
            {
                byte prefix = 0x20;
                if (hasDecodeTime)
                {
                    prefix = 0x30;
                }
                WriteTimestamp(header.Slice(cursor, 5), prefix, presentationTime);
                cursor += 5;
            }
            if (hasDecodeTime)
            {
                WriteTimestamp(header.Slice(cursor, 5), 0x10, decodeTime);
                cursor += 5;
            }
            extension.CopyTo(header.Slice(cursor));
            cursor += extension.Length;
            header.Slice(cursor).Fill(0xFF);
            packPosition += header.Length;
        }

        static void WriteTimestamp(Span<byte> bytes, byte prefix, long time)
        {
            bytes[0] = (byte)(prefix | ((time >> 29) & 0x0E) | 0x01);
            bytes[1] = (byte)(time >> 22);
            bytes[2] = (byte)(((time >> 14) & 0xFE) | 0x01);
            bytes[3] = (byte)(time >> 7);
            bytes[4] = (byte)(((time << 1) & 0xFE) | 0x01);
        }

        void WritePadding(int size)
        {
            while (size > 0)
            {
                int packetSize = Math.Min(size, PesStartSize + ushort.MaxValue);
                if (size - packetSize > 0 && size - packetSize < PesStartSize)
                {
                    packetSize -= PesStartSize;
                }
                Span<byte> packet = packBuffer.AsSpan(packPosition, packetSize);
                packet[0] = 0; packet[1] = 0; packet[2] = 1; packet[3] = PaddingStreamId;
                BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(4, 2), (ushort)(packetSize - PesStartSize));
                packet.Slice(PesStartSize).Fill(0xFF);
                packPosition += packetSize;
                size -= packetSize;
            }
        }

        /* Padding packs up to contentLimit, then the end code in the last 4 bytes, as retail ends.
           Every retail file is whole packs + 4 bytes, so each padding pack is a full one. */
        void WriteEndPadding(long contentLimit)
        {
            while (outputSize < contentLimit)
            {
                int packSize = (int)Math.Min(PackSize, contentLimit - outputSize);
                packPosition = 0;
                if (packSize >= PackHeaderSize + PesStartSize)
                {
                    WritePackHeader(lastSystemClock);
                    WritePadding(packSize - PackHeaderSize);
                }
                else
                {
                    // Smaller than a pack header and a padding packet: stuffing bytes.
                    packBuffer.AsSpan(0, packSize).Fill(0xFF);
                    packPosition = packSize;
                }
                StorePack(contentLimit);
            }
            output[contentLimit] = 0;
            output[contentLimit + 1] = 0;
            output[contentLimit + 2] = 1;
            output[contentLimit + 3] = EndCode;
        }
    }
}
