using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FFMpegCore;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles.MovieImport;

namespace NzbDrone.Core.MediaFiles.MediaInfo
{
    public interface IVideoFileInfoReader
    {
        MediaInfoModel GetMediaInfo(string filename);
        TimeSpan? GetRunTime(string filename);
        string TakeUnparseableReason(string filename);
    }

    public class VideoFileInfoReader : IVideoFileInfoReader
    {
        // fork28: the line ffprobe prints for AVERROR_INVALIDDATA, i.e. it read the bytes and they are not media.
        private const string InvalidDataMessage = "Invalid data found when processing input";

        private static readonly Regex DemuxerAddressRegex = new Regex(@" @ 0x[0-9a-f]+\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;
        private readonly List<FFProbePixelFormat> _pixelFormats;
        private readonly ConcurrentDictionary<string, string> _unparseableReasons = new ();

        public const int MINIMUM_MEDIA_INFO_SCHEMA_REVISION = 14;
        public const int CURRENT_MEDIA_INFO_SCHEMA_REVISION = 14;

        // fork7: the exact fixed argument prefixes Servarr.FFMpegCore's GetStreamJson / GetFrameJson emit
        // (confirmed against the 4.7-servarr source and the live container argv). RunFfprobe replicates them
        // byte-for-byte so FFProbe.AnalyseStreamJson / AnalyseFrameJson parse the output unchanged.
        private const string StreamProbeArgs = "-loglevel error -print_format json -show_format -sexagesimal -show_streams";
        private const string FrameProbeArgs = "-loglevel error -print_format json -show_frames -v quiet -sexagesimal";

        private static readonly string[] ValidHdrColourPrimaries = { "bt2020" };
        private static readonly string[] HlgTransferFunctions = { "arib-std-b67" };
        private static readonly string[] PqTransferFunctions = { "smpte2084" };
        private static readonly string[] ValidHdrTransferFunctions = HlgTransferFunctions.Concat(PqTransferFunctions).ToArray();

        public VideoFileInfoReader(IDiskProvider diskProvider, Logger logger)
        {
            _diskProvider = diskProvider;
            _logger = logger;

            // We bundle ffprobe for all platforms
            GlobalFFOptions.Configure(options => options.BinaryFolder = AppDomain.CurrentDomain.BaseDirectory);

            try
            {
                _pixelFormats = FFProbe.GetPixelFormats();
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to get supported pixel formats from ffprobe");
                _pixelFormats = new List<FFProbePixelFormat>();
            }
        }

        public MediaInfoModel GetMediaInfo(string filename)
        {
            if (!_diskProvider.FileExists(filename))
            {
                throw new FileNotFoundException("Media file does not exist: " + filename);
            }

            if (MediaFileExtensions.DiskExtensions.Contains(Path.GetExtension(filename)))
            {
                return null;
            }

            // fork7 (upstream 32d9cd9ea): .strm / .m3u point at a remote URL, so probing them makes ffprobe
            // read the network and can wedge it indefinitely. Skip the probe entirely, same as disk images.
            if (MediaFileExtensions.StreamingExtensions.Contains(Path.GetExtension(filename)))
            {
                return null;
            }

            // TODO: Cache media info by path, mtime and length so we don't need to read files multiple times
            _unparseableReasons.TryRemove(filename, out _);

            try
            {
                _logger.Debug("Getting media info from {0}", filename);
                var probe = RunFfprobe(StreamProbeArgs, "-probesize 50000000", filename);

                // fork28: a probe that ran to completion and reported the content itself as invalid is a
                // definitive "this is not media" answer, not a transient fault. Record it so the import path can
                // reject the file with a retraceable reason instead of reading the null as "unknown".
                var unparseableReason = GetUnparseableReason(probe);

                if (unparseableReason != null)
                {
                    _unparseableReasons[filename] = unparseableReason;
                    _logger.Warn("Not a valid media file, ffprobe could not parse it: {0} ({1})", filename, unparseableReason);

                    return null;
                }

                var ffprobeOutput = probe.StandardOutput;

                var analysis = FFProbe.AnalyseStreamJson(ffprobeOutput);
                var primaryVideoStream = GetPrimaryVideoStream(analysis);

                if (analysis.PrimaryAudioStream?.ChannelLayout.IsNullOrWhiteSpace() ?? true)
                {
                    ffprobeOutput = RunFfprobe(StreamProbeArgs, "-probesize 150000000 -analyzeduration 150000000", filename).StandardOutput;
                    analysis = FFProbe.AnalyseStreamJson(ffprobeOutput);
                }

                var mediaInfoModel = new MediaInfoModel();
                mediaInfoModel.ContainerFormat = analysis.Format.FormatName;
                mediaInfoModel.VideoFormat = primaryVideoStream?.CodecName;
                mediaInfoModel.VideoCodecID = primaryVideoStream?.CodecTagString;
                mediaInfoModel.VideoProfile = primaryVideoStream?.Profile;
                mediaInfoModel.VideoBitrate = GetBitrate(primaryVideoStream);
                mediaInfoModel.VideoMultiViewCount = primaryVideoStream?.Tags?.ContainsKey("stereo_mode") ?? false ? 2 : 1;
                mediaInfoModel.VideoBitDepth = GetPixelFormat(primaryVideoStream?.PixelFormat)?.Components.Min(x => x.BitDepth) ?? 8;
                mediaInfoModel.VideoColourPrimaries = primaryVideoStream?.ColorPrimaries;
                mediaInfoModel.VideoTransferCharacteristics = primaryVideoStream?.ColorTransfer;
                mediaInfoModel.DoviConfigurationRecord = primaryVideoStream?.SideDataList?.Find(x => x.GetType().Name == nameof(DoviConfigurationRecordSideData)) as DoviConfigurationRecordSideData;
                mediaInfoModel.Height = primaryVideoStream?.Height ?? 0;
                mediaInfoModel.Width = primaryVideoStream?.Width ?? 0;
                mediaInfoModel.AudioFormat = analysis.PrimaryAudioStream?.CodecName;
                mediaInfoModel.AudioCodecID = analysis.PrimaryAudioStream?.CodecTagString;
                mediaInfoModel.AudioProfile = analysis.PrimaryAudioStream?.Profile;
                mediaInfoModel.AudioBitrate = GetBitrate(analysis.PrimaryAudioStream);
                mediaInfoModel.RunTime = GetBestRuntime(analysis.PrimaryAudioStream?.Duration, primaryVideoStream?.Duration, analysis.Format.Duration);
                mediaInfoModel.AudioStreamCount = analysis.AudioStreams.Count;
                mediaInfoModel.AudioChannels = analysis.PrimaryAudioStream?.Channels ?? 0;
                mediaInfoModel.AudioChannelPositions = analysis.PrimaryAudioStream?.ChannelLayout;
                mediaInfoModel.VideoFps = primaryVideoStream?.FrameRate ?? 0;
                mediaInfoModel.AudioLanguages = analysis.AudioStreams?.Select(x => x.Language)
                    .Where(l => l.IsNotNullOrWhiteSpace())
                    .ToList();
                mediaInfoModel.Subtitles = analysis.SubtitleStreams?.Select(x => x.Language)
                    .Where(l => l.IsNotNullOrWhiteSpace())
                    .ToList();
                mediaInfoModel.ScanType = "Progressive";
                mediaInfoModel.RawStreamData = ffprobeOutput;
                mediaInfoModel.SchemaRevision = CURRENT_MEDIA_INFO_SCHEMA_REVISION;

                if (analysis.Format.Tags?.TryGetValue("title", out var title) ?? false)
                {
                    mediaInfoModel.Title = title;
                }

                FFProbeFrames frames = null;

                // if it looks like PQ10 or similar HDR, do a frame analysis to figure out which type it is
                if (PqTransferFunctions.Contains(mediaInfoModel.VideoTransferCharacteristics))
                {
                    var videoStreamIndex = analysis.VideoStreams.FindIndex(stream => stream.Index == primaryVideoStream?.Index);
                    var frameOutput = RunFfprobe(FrameProbeArgs, $"-read_intervals \"%+#1\" -select_streams v:{(videoStreamIndex == -1 ? 0 : videoStreamIndex)}", filename).StandardOutput;
                    mediaInfoModel.RawFrameData = frameOutput;

                    frames = FFProbe.AnalyseFrameJson(frameOutput);
                }

                var streamSideData = primaryVideoStream?.SideDataList ?? new ();
                var framesSideData = frames?.Frames?.Count > 0 ? frames?.Frames[0]?.SideDataList ?? new () : new ();

                var sideData = streamSideData.Concat(framesSideData).ToList();
                mediaInfoModel.VideoHdrFormat = GetHdrFormat(mediaInfoModel.VideoBitDepth, mediaInfoModel.VideoColourPrimaries, mediaInfoModel.VideoTransferCharacteristics, sideData);

                return mediaInfoModel;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to parse media info from file: {0}", filename);
            }

            return null;
        }

        public TimeSpan? GetRunTime(string filename)
        {
            var info = GetMediaInfo(filename);

            return info?.RunTime;
        }

        // fork28: returns (and clears) the reason the most recent probe of this file found it unparseable, or null
        // if that probe did not classify it so. Callers read it right after their own GetMediaInfo call.
        public string TakeUnparseableReason(string filename)
        {
            return _unparseableReasons.TryRemove(filename, out var reason) ? reason : null;
        }

        // fork28: a probe is UNPARSEABLE only when ffprobe ran to completion (we did not kill it at the deadline),
        // failed, and named the content itself as invalid. Measured against the bundled ffprobe: random bytes,
        // zero-filled and empty files all exit 1 with "Invalid data found when processing input". A file that is
        // missing or unreachable fails differently ("No such file or directory", "Input/output error", or a
        // timeout kill), so a transport fault is never classified as unparseable and never rejected for it.
        public static string GetUnparseableReason(TimeBoundedProcessResult probe)
        {
            if (probe == null || probe.TimedOut || probe.ExitCode == 0 || probe.StandardError.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (!probe.StandardError.Contains(InvalidDataMessage, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var lines = probe.StandardError
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Contains(InvalidDataMessage, StringComparison.OrdinalIgnoreCase) ? InvalidDataMessage : DemuxerAddressRegex.Replace(line, "]"))
                .Distinct()
                .ToList();

            return string.Join("; ", lines);
        }

        // fork7/fork8: spawn ffprobe as a Process WE own, with a hard deadline, so no probe can wedge in
        // D-state. The args are byte-identical to what Servarr.FFMpegCore's GetStreamJson / GetFrameJson emit,
        // so the returned stdout parses unchanged through FFProbe.AnalyseStreamJson / AnalyseFrameJson. fork8:
        // TimeBoundedProcess self-bounds the process at IMPORT_PROBE_TIMEOUT so EVERY spawn site is killed at
        // the deadline, including the off-pool ones (media-info refresh on Series/MovieScannedEvent, script
        // import, subtitle extras) that ImportProbePool never sees. The ProbeProcessRegistry Attach/Detach keeps
        // the pool's own kill wired for pooled probes as belt-and-suspenders; it is a no-op off-pool
        // (CurrentSlot is null). On a kill the stdout pipe closes, the partial buffer is returned,
        // AnalyseStreamJson throws on the truncated JSON and GetMediaInfo returns null; a pooled item was also
        // flagged timed-out, so the null is not a real read.
        private TimeBoundedProcessResult RunFfprobe(string baseArgs, string extraArgs, string filename)
        {
            var binary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

            var startInfo = new ProcessStartInfo
            {
                FileName = binary,
                Arguments = $"{baseArgs} {extraArgs} \"{filename}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            return TimeBoundedProcess.RunWithResult(
                startInfo,
                ImportProbePool.GetTimeout(),
                ProbeProcessRegistry.Attach,
                ProbeProcessRegistry.Detach);
        }

        private static TimeSpan GetBestRuntime(TimeSpan? audio, TimeSpan? video, TimeSpan general)
        {
            if (!video.HasValue || video.Value.TotalMilliseconds == 0)
            {
                if (!audio.HasValue || audio.Value.TotalMilliseconds == 0)
                {
                    return general;
                }

                return audio.Value;
            }

            return video.Value;
        }

        private static long GetBitrate(MediaStream mediaStream)
        {
            if (mediaStream?.BitRate is > 0)
            {
                return mediaStream.BitRate;
            }

            if ((mediaStream?.Tags?.TryGetValue("BPS", out var bitratePerSecond) ?? false) && bitratePerSecond.IsNotNullOrWhiteSpace())
            {
                return Convert.ToInt64(bitratePerSecond);
            }

            return 0;
        }

        private VideoStream GetPrimaryVideoStream(IMediaAnalysis mediaAnalysis)
        {
            if (mediaAnalysis.VideoStreams.Count <= 1)
            {
                return mediaAnalysis.PrimaryVideoStream;
            }

            // motion image codec streams are often in front of the main video stream
            var codecFilter = new[] { "mjpeg", "png" };

            return mediaAnalysis.VideoStreams.FirstOrDefault(s => !codecFilter.Contains(s.CodecName)) ?? mediaAnalysis.PrimaryVideoStream;
        }

        private FFProbePixelFormat GetPixelFormat(string format)
        {
            return _pixelFormats.Find(x => x.Name == format);
        }

        public static HdrFormat GetHdrFormat(int bitDepth, string colorPrimaries, string transferFunction, List<SideData> sideData)
        {
            if (bitDepth < 10)
            {
                return HdrFormat.None;
            }

            if (TryGetSideData<DoviConfigurationRecordSideData>(sideData, out var dovi))
            {
                var hasHdr10Plus = TryGetSideData<HdrDynamicMetadataSpmte2094>(sideData, out _);

                return dovi.DvBlSignalCompatibilityId switch
                {
                    1 => hasHdr10Plus ? HdrFormat.DolbyVisionHdr10Plus : HdrFormat.DolbyVisionHdr10,
                    2 => HdrFormat.DolbyVisionSdr,
                    4 => HdrFormat.DolbyVisionHlg,
                    6 => hasHdr10Plus ? HdrFormat.DolbyVisionHdr10Plus : HdrFormat.DolbyVisionHdr10,
                    _ => HdrFormat.DolbyVision
                };
            }

            if (!ValidHdrColourPrimaries.Contains(colorPrimaries) || !ValidHdrTransferFunctions.Contains(transferFunction))
            {
                return HdrFormat.None;
            }

            if (HlgTransferFunctions.Contains(transferFunction))
            {
                return HdrFormat.Hlg10;
            }

            if (PqTransferFunctions.Contains(transferFunction))
            {
                if (TryGetSideData<HdrDynamicMetadataSpmte2094>(sideData, out _))
                {
                    return HdrFormat.Hdr10Plus;
                }

                if (TryGetSideData<MasteringDisplayMetadata>(sideData, out _) ||
                    TryGetSideData<ContentLightLevelMetadata>(sideData, out _))
                {
                    return HdrFormat.Hdr10;
                }

                return HdrFormat.Pq10;
            }

            return HdrFormat.None;
        }

        private static bool TryGetSideData<T>(List<SideData> list, out T result)
        where T : SideData
        {
            result = (T)list?.FirstOrDefault(x => x.GetType().Name == typeof(T).Name);

            return result != null;
        }
    }
}
