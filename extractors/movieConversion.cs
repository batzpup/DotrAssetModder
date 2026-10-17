using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Filters;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;
using MediaPacket = Sdcb.FFmpeg.Codecs.Packet;
namespace DotrAssetModder;

/* One movie job on a worker thread, through the FFmpeg libraries the Sdcb.FFmpeg packages ship:
   a disc movie to MP4 (H.264 + AAC), or any video file to a PSS that fits a disc movie's file.
   PssMovie reads and writes the PS2 container; FFmpeg only decodes and encodes. The window reads
   Progress/Stage every frame and cancels through Cancellation. */
public sealed class MovieConversion
{
    // The console showed the 640x448 picture on a 4:3 TV: pixels 14:15 wide.
    static readonly AVRational DisplayPixelAspect = new AVRational(14, 15);
    static readonly AVRational FrameTime = new AVRational(1, PssMovie.FrameRate);
    const int SamplesPerFrame = PssMovie.SampleRate / PssMovie.FrameRate;
    const int Mp4AudioBitRate = 192000;
    // Highest average the encoder is asked for; the peak stays under PssMovie.MaxVideoBitRate.
    const long HighestVideoBitRate = 8000000;
    const long LowestVideoBitRate = 500000;
    // Share of the measured room asked of the encoder: its average lands a few percent off.
    const double BitRateMargin = 0.96;
    const int MaxEncodeAttempts = 3;
    const int SliceThreading = 2;   // FF_THREAD_SLICE
    // Square pixels at 4:3, then the console's 640x448: wider or taller sources get black bars.
    const string ReplacementVideoFilters = "scale=iw*sar:ih,setsar=1,scale=640:480:force_original_aspect_ratio=decrease,pad=640:480:(ow-iw)/2:(oh-ih)/2,scale=640:448,setsar=1,fps=30,format=yuv420p";
    const string ReplacementAudioFilters = "aresample=48000,aformat=sample_fmts=s16:channel_layouts=stereo";

    public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
    public Task? Task;
    public string MovieName = string.Empty;
    // 0..1, written by the worker.
    public volatile float Progress;
    public volatile string Stage = string.Empty;
    public string? Error;
    public string Summary = string.Empty;
    // Replace only: the whole new file, exactly the disc file's size.
    public byte[]? ReplacementBytes;

    static MovieConversion()
    {
        // FFmpeg writes encoder statistics to the console otherwise.
        FFmpegLogger.LogLevel = LogLevel.Error;
    }

    // ---- disc movie -> MP4 --------------------------------------------------------------------

    public void ExtractToMp4(byte[] pssBytes, string outputPath)
    {
        CancellationToken cancellationToken = Cancellation.Token;
        Stage = "Reading";
        PssMovie movie = PssMovie.Read(pssBytes);
        int pictureCount = Math.Max(1, CountPictures(movie.VideoStream));

        using var videoBytes = new MemoryStream(movie.VideoStream, false);
        using IOContext inputIo = IOContext.ReadStream(videoBytes);
        using FormatContext input = FormatContext.OpenInputIO(inputIo, InputFormat.FindByShortName("mpegvideo"));
        input.LoadStreamInfo();
        MediaStream videoStream = input.GetVideoStream();
        using CodecContext videoDecoder = OpenDecoder(videoStream);

        using FormatContext output = FormatContext.AllocOutput(formatName: "mp4", fileName: outputPath);
        bool globalHeader = output.OutputFormat != null && (output.OutputFormat.Value.Flags & AVFMT.Globalheader) != 0;

        Codec videoCodec = Codec.FindEncoderByName("libx264") ?? throw new InvalidOperationException("The H.264 encoder is missing from the FFmpeg libraries.");
        using var videoEncoder = new CodecContext(videoCodec);
        videoEncoder.Width = PssMovie.Width;
        videoEncoder.Height = PssMovie.Height;
        videoEncoder.TimeBase = FrameTime;
        videoEncoder.Framerate = new AVRational(PssMovie.FrameRate, 1);
        videoEncoder.PixelFormat = AVPixelFormat.Yuv420p;
        videoEncoder.SampleAspectRatio = DisplayPixelAspect;
        if (globalHeader)
        {
            videoEncoder.Flags |= AV_CODEC_FLAG.GlobalHeader;
        }
        var videoOptions = new MediaDictionary();
        videoOptions["preset"] = "medium";
        videoOptions["crf"] = "18";
        videoEncoder.Open(videoCodec, videoOptions);
        MediaStream videoOut = output.NewStream(videoCodec);
        videoOut.Codecpar.CopyFrom(videoEncoder);
        videoOut.TimeBase = videoEncoder.TimeBase;
        videoOut.SampleAspectRatio = DisplayPixelAspect;

        Codec audioCodec = Codec.FindEncoderById(AVCodecID.Aac);
        using var audioEncoder = new CodecContext(audioCodec);
        audioEncoder.SampleRate = movie.AudioSampleRate;
        audioEncoder.SampleFormat = AVSampleFormat.Fltp;
        audioEncoder.ChLayout = DefaultChannelLayout(movie.AudioChannelCount);
        audioEncoder.BitRate = Mp4AudioBitRate;
        audioEncoder.TimeBase = new AVRational(1, movie.AudioSampleRate);
        if (globalHeader)
        {
            audioEncoder.Flags |= AV_CODEC_FLAG.GlobalHeader;
        }
        audioEncoder.Open(audioCodec);
        MediaStream audioOut = output.NewStream(audioCodec);
        audioOut.Codecpar.CopyFrom(audioEncoder);
        audioOut.TimeBase = audioEncoder.TimeBase;

        output.Pb = IOContext.OpenWrite(outputPath);
        output.WriteHeader();

        var audioWriter = new Mp4AudioWriter(movie, audioEncoder, output, audioOut);
        using var packet = new MediaPacket();
        using var decodedFrame = new Frame();
        using var encodedPacket = new MediaPacket();
        long framesWritten = 0;
        Stage = "Converting";
        while (input.ReadFrame(packet) == CodecResult.Success)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (packet.StreamIndex == videoStream.Index)
            {
                videoDecoder.SendPacket(packet);
                while (videoDecoder.ReceiveFrame(decodedFrame) == CodecResult.Success)
                {
                    decodedFrame.Pts = framesWritten;
                    decodedFrame.PictType = AVPictureType.None;
                    decodedFrame.SampleAspectRatio = DisplayPixelAspect;
                    EncodeAndWrite(videoEncoder, decodedFrame, encodedPacket, output, videoOut);
                    decodedFrame.Unref();
                    framesWritten++;
                    audioWriter.WriteUpTo(framesWritten * SamplesPerFrame * movie.AudioSampleRate / PssMovie.SampleRate);
                    Progress = Math.Min(1f, framesWritten / (float)pictureCount);
                }
            }
            packet.Unref();
        }
        videoDecoder.SendPacket(null);
        while (videoDecoder.ReceiveFrame(decodedFrame) == CodecResult.Success)
        {
            decodedFrame.Pts = framesWritten;
            decodedFrame.PictType = AVPictureType.None;
            decodedFrame.SampleAspectRatio = DisplayPixelAspect;
            EncodeAndWrite(videoEncoder, decodedFrame, encodedPacket, output, videoOut);
            decodedFrame.Unref();
            framesWritten++;
        }
        EncodeAndWrite(videoEncoder, null, encodedPacket, output, videoOut);
        audioWriter.WriteUpTo(long.MaxValue);
        audioWriter.Finish();
        output.WriteTrailer();
        output.Pb.Close();
        Progress = 1f;
        Summary = $"{MovieName}: {framesWritten} frames ({framesWritten / (double)PssMovie.FrameRate:0.00} s) to {outputPath}";
    }

    static int CountPictures(byte[] videoStream)
    {
        int count = 0;
        for (int i = 0; i + 3 < videoStream.Length; i++)
        {
            if (videoStream[i] == 0 && videoStream[i + 1] == 0 && videoStream[i + 2] == 1 && videoStream[i + 3] == 0)
            {
                count++;
            }
        }
        return count;
    }

    static void EncodeAndWrite(CodecContext encoder, Frame? frame, MediaPacket encodedPacket, FormatContext output, MediaStream stream)
    {
        encoder.SendFrame(frame);
        while (encoder.ReceivePacket(encodedPacket) == CodecResult.Success)
        {
            encodedPacket.RescaleTimestamp(encoder.TimeBase, stream.TimeBase);
            encodedPacket.StreamIndex = stream.Index;
            output.InterleavedWritePacket(encodedPacket);
        }
    }

    // The movie's samples to AAC in the encoder's frame size, kept level with the video.
    sealed class Mp4AudioWriter
    {
        readonly PssMovie movie;
        readonly CodecContext encoder;
        readonly FormatContext output;
        readonly MediaStream stream;
        readonly MediaPacket encodedPacket = new MediaPacket();
        long samplesWritten;

        public Mp4AudioWriter(PssMovie movie, CodecContext encoder, FormatContext output, MediaStream stream)
        {
            this.movie = movie;
            this.encoder = encoder;
            this.output = output;
            this.stream = stream;
        }

        public unsafe void WriteUpTo(long sampleTarget)
        {
            int channelCount = movie.AudioChannelCount;
            long sampleCount = movie.Samples.Length / channelCount;
            long end = Math.Min(sampleTarget, sampleCount);
            int frameSize = encoder.FrameSize;
            while (samplesWritten + frameSize <= end || (sampleTarget == long.MaxValue && samplesWritten < sampleCount))
            {
                int samplesInFrame = (int)Math.Min(frameSize, sampleCount - samplesWritten);
                using Frame frame = Frame.CreateAudio(AVSampleFormat.Fltp, encoder.ChLayout, encoder.SampleRate, frameSize);
                for (int channel = 0; channel < channelCount; channel++)
                {
                    float* plane = (float*)frame.Data[channel];
                    for (int i = 0; i < frameSize; i++)
                    {
                        float sample = 0f;
                        if (i < samplesInFrame)
                        {
                            sample = movie.Samples[(samplesWritten + i) * channelCount + channel] / 32768f;
                        }
                        plane[i] = sample;
                    }
                }
                frame.Pts = samplesWritten;
                EncodeAndWrite(encoder, frame, encodedPacket, output, stream);
                samplesWritten += samplesInFrame;
            }
        }

        public void Finish()
        {
            EncodeAndWrite(encoder, null, encodedPacket, output, stream);
            encodedPacket.Dispose();
        }
    }

    // ---- any video -> PSS ---------------------------------------------------------------------

    /* Encodes sourcePath at the bitrate the file's room allows; if the result still does not fit
       (the encoder's average is not exact), encodes again at the bitrate scaled down by the
       overflow. ReplacementBytes is exactly fileSize bytes. */
    public void EncodeReplacement(string sourcePath, long fileSize)
    {
        CancellationToken cancellationToken = Cancellation.Token;
        double durationSeconds = MeasureDuration(sourcePath);
        long frameCount = Math.Max(1, (long)Math.Round(durationSeconds * PssMovie.FrameRate));
        long audioStreamSize = PssMovie.AudioHeaderSize + frameCount * SamplesPerFrame * PssMovie.ChannelCount * 2 + PssMovie.AudioInterleaveSize * PssMovie.ChannelCount;
        long videoRoom = PssMovie.VideoBytesThatFit(fileSize, audioStreamSize);
        long bitRate = Math.Min(HighestVideoBitRate, (long)(videoRoom * 8 / (frameCount / (double)PssMovie.FrameRate) * BitRateMargin));

        for (int attempt = 1; attempt <= MaxEncodeAttempts; attempt++)
        {
            if (bitRate < LowestVideoBitRate)
            {
                throw new InvalidOperationException($"The movie is too long for {MovieName}: only {bitRate / 1000} kbit/s of video would fit ({fileSize:N0} B, {durationSeconds:0.0} s).");
            }
            Progress = 0f;
            Stage = attempt == 1 ? $"Encoding at {bitRate / 1000} kbit/s" : $"Encoding again at {bitRate / 1000} kbit/s (try {attempt})";
            List<PssMovie.Picture> pictures = EncodePass(sourcePath, bitRate, durationSeconds, cancellationToken, out short[] samples);
            cancellationToken.ThrowIfCancellationRequested();
            Stage = "Writing PSS";
            byte[]? fileBytes = PssMovie.Build(pictures, samples, fileSize, out long contentSize);
            if (fileBytes != null)
            {
                ReplacementBytes = fileBytes;
                Progress = 1f;
                Summary = $"{pictures.Count} frames ({pictures.Count / (double)PssMovie.FrameRate:0.00} s), video {bitRate / 1000} kbit/s, {contentSize:N0} of {fileSize:N0} B used.";
                return;
            }
            long videoSize = 0;
            foreach (PssMovie.Picture picture in pictures)
            {
                videoSize += picture.Bytes.Length;
            }
            double shrink = (videoSize - (contentSize - fileSize)) / (double)videoSize;
            bitRate = (long)(bitRate * Math.Min(shrink, 1.0) * BitRateMargin);
        }
        throw new InvalidOperationException($"The encoded movie still did not fit {MovieName} after {MaxEncodeAttempts} tries.");
    }

    static double MeasureDuration(string sourcePath)
    {
        using FormatContext input = FormatContext.OpenInputUrl(sourcePath);
        input.LoadStreamInfo();
        MediaStream? videoStream = input.FindBestStreamOrNull(AVMediaType.Video);
        if (videoStream == null)
        {
            throw new InvalidDataException("The file has no video stream.");
        }
        double seconds = 0;
        if (videoStream.Value.Duration > 0)
        {
            seconds = videoStream.Value.Duration * videoStream.Value.TimeBase.ToDouble();
        }
        else if (input.Duration > 0)
        {
            seconds = input.Duration / (double)ffmpeg.AV_TIME_BASE;
        }
        if (seconds <= 0)
        {
            throw new InvalidDataException("The file does not say how long its video is.");
        }
        return seconds;
    }

    // One decode of the source: MPEG-2 pictures for the console and its audio as 48 kHz stereo.
    List<PssMovie.Picture> EncodePass(string sourcePath, long bitRate, double durationSeconds, CancellationToken cancellationToken, out short[] samples)
    {
        using FormatContext input = FormatContext.OpenInputUrl(sourcePath);
        input.LoadStreamInfo();
        MediaStream videoStream = input.FindBestStreamOrNull(AVMediaType.Video)!.Value;
        MediaStream? audioStream = input.FindBestStreamOrNull(AVMediaType.Audio);
        using CodecContext videoDecoder = OpenDecoder(videoStream);
        CodecContext? audioDecoder = null;
        if (audioStream != null)
        {
            audioDecoder = OpenDecoder(audioStream.Value);
        }

        Codec videoCodec = Codec.FindEncoderById(AVCodecID.Mpeg2video);
        using var videoEncoder = new CodecContext(videoCodec);
        videoEncoder.Width = PssMovie.Width;
        videoEncoder.Height = PssMovie.Height;
        videoEncoder.TimeBase = FrameTime;
        videoEncoder.Framerate = new AVRational(PssMovie.FrameRate, 1);
        videoEncoder.PixelFormat = AVPixelFormat.Yuv420p;
        videoEncoder.SampleAspectRatio = new AVRational(1, 1);
        // Retail: IBBP, a GOP every 18 pictures, Main profile at Main level limits.
        videoEncoder.GopSize = 18;
        videoEncoder.MaxBFrames = 2;
        videoEncoder.BitRate = bitRate;
        videoEncoder.RcMaxRate = PssMovie.MaxVideoBitRate;
        videoEncoder.RcBufferSize = PssMovie.VideoBufferSizeBits;
        videoEncoder.MbDecision = 2;
        videoEncoder.ThreadCount = 0;
        videoEncoder.Open(videoCodec);

        var pictures = new List<PssMovie.Picture>();
        var collectedSamples = new MemoryStream();
        long firstVideoTime = ffmpeg.AV_NOPTS_VALUE;
        long firstAudioTime = ffmpeg.AV_NOPTS_VALUE;
        long framesEncoded = 0;
        long expectedFrames = Math.Max(1, (long)Math.Round(durationSeconds * PssMovie.FrameRate));

        using var packet = new MediaPacket();
        using var decodedFrame = new Frame();
        using var filteredFrame = new Frame();
        using var encodedPacket = new MediaPacket();
        FilterChain? videoFilters = null;
        FilterChain? audioFilters = null;
        try
        {
            while (input.ReadFrame(packet) == CodecResult.Success)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (packet.StreamIndex == videoStream.Index)
                {
                    videoDecoder.SendPacket(packet);
                    while (videoDecoder.ReceiveFrame(decodedFrame) == CodecResult.Success)
                    {
                        decodedFrame.Pts = decodedFrame.BestEffortTimestamp;
                        if (firstVideoTime == ffmpeg.AV_NOPTS_VALUE)
                        {
                            firstVideoTime = decodedFrame.Pts;
                        }
                        videoFilters ??= FilterChain.ForVideo(videoDecoder, videoStream.TimeBase, ReplacementVideoFilters);
                        videoFilters.Push(decodedFrame);
                        decodedFrame.Unref();
                        framesEncoded = EncodeFiltered(videoFilters, filteredFrame, videoEncoder, encodedPacket, pictures, framesEncoded);
                        Progress = Math.Min(0.99f, framesEncoded / (float)expectedFrames);
                    }
                }
                else if (audioStream != null && audioDecoder != null && packet.StreamIndex == audioStream.Value.Index)
                {
                    audioDecoder.SendPacket(packet);
                    while (audioDecoder.ReceiveFrame(decodedFrame) == CodecResult.Success)
                    {
                        decodedFrame.Pts = decodedFrame.BestEffortTimestamp;
                        if (firstAudioTime == ffmpeg.AV_NOPTS_VALUE)
                        {
                            firstAudioTime = decodedFrame.Pts;
                        }
                        audioFilters ??= FilterChain.ForAudio(audioDecoder, audioStream.Value.TimeBase, ReplacementAudioFilters);
                        audioFilters.Push(decodedFrame);
                        decodedFrame.Unref();
                        CollectSamples(audioFilters, filteredFrame, collectedSamples);
                    }
                }
                packet.Unref();
            }

            videoDecoder.SendPacket(null);
            while (videoDecoder.ReceiveFrame(decodedFrame) == CodecResult.Success)
            {
                decodedFrame.Pts = decodedFrame.BestEffortTimestamp;
                videoFilters ??= FilterChain.ForVideo(videoDecoder, videoStream.TimeBase, ReplacementVideoFilters);
                videoFilters.Push(decodedFrame);
                decodedFrame.Unref();
            }
            if (videoFilters == null)
            {
                throw new InvalidDataException("No picture could be decoded from the file.");
            }
            videoFilters.Push(null);
            framesEncoded = EncodeFiltered(videoFilters, filteredFrame, videoEncoder, encodedPacket, pictures, framesEncoded);
            EncodePictures(videoEncoder, null, encodedPacket, pictures);

            if (audioDecoder != null)
            {
                audioDecoder.SendPacket(null);
                while (audioDecoder.ReceiveFrame(decodedFrame) == CodecResult.Success)
                {
                    decodedFrame.Pts = decodedFrame.BestEffortTimestamp;
                    audioFilters ??= FilterChain.ForAudio(audioDecoder, audioStream!.Value.TimeBase, ReplacementAudioFilters);
                    audioFilters.Push(decodedFrame);
                    decodedFrame.Unref();
                }
                if (audioFilters != null)
                {
                    audioFilters.Push(null);
                    CollectSamples(audioFilters, filteredFrame, collectedSamples);
                }
            }
        }
        finally
        {
            videoFilters?.Dispose();
            audioFilters?.Dispose();
            audioDecoder?.Dispose();
        }

        // Audio starting after the video gets that much silence in front; starting before, it loses it.
        long leadingSamples = 0;
        if (audioStream != null && firstAudioTime != ffmpeg.AV_NOPTS_VALUE && firstVideoTime != ffmpeg.AV_NOPTS_VALUE)
        {
            double audioStartSeconds = firstAudioTime * audioStream.Value.TimeBase.ToDouble();
            double videoStartSeconds = firstVideoTime * videoStream.TimeBase.ToDouble();
            leadingSamples = (long)Math.Round((audioStartSeconds - videoStartSeconds) * PssMovie.SampleRate);
        }
        samples = FitSamples(collectedSamples, leadingSamples, pictures.Count * (long)SamplesPerFrame);
        return pictures;
    }

    long EncodeFiltered(FilterChain videoFilters, Frame filteredFrame, CodecContext videoEncoder, MediaPacket encodedPacket, List<PssMovie.Picture> pictures, long framesEncoded)
    {
        while (videoFilters.Pull(filteredFrame))
        {
            filteredFrame.Pts = framesEncoded;
            filteredFrame.PictType = AVPictureType.None;
            EncodePictures(videoEncoder, filteredFrame, encodedPacket, pictures);
            filteredFrame.Unref();
            framesEncoded++;
        }
        return framesEncoded;
    }

    static void EncodePictures(CodecContext encoder, Frame? frame, MediaPacket encodedPacket, List<PssMovie.Picture> pictures)
    {
        encoder.SendFrame(frame);
        while (encoder.ReceivePacket(encodedPacket) == CodecResult.Success)
        {
            var picture = new PssMovie.Picture();
            picture.Bytes = encodedPacket.Data.ToArray();
            picture.PresentationFrame = encodedPacket.Pts;
            picture.DecodeFrame = encodedPacket.Dts;
            pictures.Add(picture);
            encodedPacket.Unref();
        }
    }

    static unsafe void CollectSamples(FilterChain audioFilters, Frame filteredFrame, MemoryStream collectedSamples)
    {
        while (audioFilters.Pull(filteredFrame))
        {
            int byteCount = filteredFrame.NbSamples * PssMovie.ChannelCount * 2;
            collectedSamples.Write(new ReadOnlySpan<byte>((void*)filteredFrame.Data[0], byteCount));
            filteredFrame.Unref();
        }
    }

    // Interleaved s16 bytes -> exactly sampleFrameCount stereo frames, shifted by leadingSamples.
    static short[] FitSamples(MemoryStream collectedSamples, long leadingSamples, long sampleFrameCount)
    {
        var samples = new short[sampleFrameCount * PssMovie.ChannelCount];
        ReadOnlySpan<short> source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(collectedSamples.GetBuffer().AsSpan(0, (int)collectedSamples.Length));
        long sourceFrameCount = source.Length / PssMovie.ChannelCount;
        for (long frame = 0; frame < sampleFrameCount; frame++)
        {
            long sourceFrame = frame - leadingSamples;
            if (sourceFrame < 0 || sourceFrame >= sourceFrameCount)
            {
                continue;
            }
            for (int channel = 0; channel < PssMovie.ChannelCount; channel++)
            {
                samples[frame * PssMovie.ChannelCount + channel] = source[(int)(sourceFrame * PssMovie.ChannelCount + channel)];
            }
        }
        return samples;
    }

    // ---- shared -------------------------------------------------------------------------------

    static CodecContext OpenDecoder(MediaStream stream)
    {
        Codec codec = Codec.FindDecoderById(stream.Codecpar.CodecId);
        var decoder = new CodecContext(codec);
        decoder.FillParameters(stream.Codecpar);
        decoder.PktTimebase = stream.TimeBase;
        decoder.ThreadCount = 0;
        /* Slice threads only. Frame threads are named ("av:h264:df0"), and the FFmpeg DLLs' thread
           library names a thread by raising exception 0x406D1388 whenever a debugger is attached;
           a managed debugger (Rider) passes it on and the process dies. */
        decoder.ThreadType = SliceThreading;
        decoder.Open(codec);
        return decoder;
    }

    static unsafe AVChannelLayout DefaultChannelLayout(int channelCount)
    {
        AVChannelLayout layout;
        ffmpeg.av_channel_layout_default(&layout, channelCount);
        return layout;
    }

    // buffer/abuffer -> the filters in description -> buffersink, one frame in, frames out.
    sealed class FilterChain : IDisposable
    {
        readonly FilterGraph graph = new FilterGraph();
        FilterContext source;
        FilterContext sink;

        FilterChain(string sourceFilter, string sourceArguments, string sinkFilter, string description)
        {
            source = graph.CreateFilter(sourceFilter, "in", sourceArguments);
            sink = graph.CreateFilter(sinkFilter, "out", (string?)null);
            FilterContext previous = source;
            int filterNumber = 0;
            foreach (string step in description.Split(','))
            {
                string[] nameAndArguments = step.Split('=', 2);
                string? arguments = null;
                if (nameAndArguments.Length > 1)
                {
                    arguments = nameAndArguments[1];
                }
                FilterContext next = graph.CreateFilter(nameAndArguments[0], $"f{filterNumber}", arguments);
                previous.Link(next, 0, 0);
                previous = next;
                filterNumber++;
            }
            previous.Link(sink, 0, 0);
            graph.Configure(null);
        }

        public static FilterChain ForVideo(CodecContext decoder, AVRational timeBase, string description)
        {
            AVRational pixelAspect = decoder.SampleAspectRatio;
            if (pixelAspect.Num <= 0 || pixelAspect.Den <= 0)
            {
                pixelAspect = new AVRational(1, 1);
            }
            string arguments = $"video_size={decoder.Width}x{decoder.Height}:pix_fmt={(int)decoder.PixelFormat}:time_base={timeBase.Num}/{timeBase.Den}:pixel_aspect={pixelAspect.Num}/{pixelAspect.Den}";
            return new FilterChain("buffer", arguments, "buffersink", description);
        }

        public static FilterChain ForAudio(CodecContext decoder, AVRational timeBase, string description)
        {
            string arguments = $"time_base={timeBase.Num}/{timeBase.Den}:sample_rate={decoder.SampleRate}:sample_fmt={NameUtils.GetSampleFormatName(decoder.SampleFormat)}:channel_layout={decoder.ChLayout.Describe()}";
            return new FilterChain("abuffer", arguments, "abuffersink", description);
        }

        // null = end of input.
        public void Push(Frame? frame)
        {
            source.AddFrame(frame, AV_BUFFERSRC_FLAG.KeepRef);
        }

        public bool Pull(Frame frame)
        {
            return sink.GetFrame(frame, 0) >= 0;
        }

        public void Dispose()
        {
            graph.Dispose();
        }
    }
}
