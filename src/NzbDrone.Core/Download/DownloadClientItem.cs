using System;
using System.Diagnostics;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Download
{
    [DebuggerDisplay("{DownloadClientInfo?.Name}:{Title}")]
    public class DownloadClientItem
    {
        public DownloadClientItemClientInfo DownloadClientInfo { get; set; }
        public string DownloadId { get; set; }
        public string Category { get; set; }
        public string Title { get; set; }

        public long TotalSize { get; set; }
        public long RemainingSize { get; set; }
        public TimeSpan? RemainingTime { get; set; }
        public double? SeedRatio { get; set; }
        public OsPath OutputPath { get; set; }
        public string Message { get; set; }
        public DownloadItemStatus Status { get; set; }
        public bool IsEncrypted { get; set; }
        public bool CanMoveFiles { get; set; }
        public bool CanBeRemoved { get; set; }
        public bool Removed { get; set; }

        // fork11: set by the qBittorrent client when it maps an 'error' torrent to Failed (error-as-failed) AND the
        // per-client "Blocklist on Errored-as-Failed" setting is off. BlocklistService.Handle honours it to skip
        // blocklisting that specific failure. Default false = blocklist as normal.
        public bool SkipBlocklistOnFailure { get; set; }

        // fork15: set by the qBittorrent client from its "Delete data when removing completed downloads" setting.
        // DownloadEventHub passes this as deleteData when removing a COMPLETED/imported download (failed removals
        // always pass true). Field-initialized true so every other download client keeps the stock deleteData=true.
        public bool DeleteDataOnCompletedRemoval { get; set; } = true;

        public DownloadClientItem Clone()
        {
            return MemberwiseClone() as DownloadClientItem;
        }
    }

    public class DownloadClientItemClientInfo
    {
        public DownloadProtocol Protocol { get; set; }
        public string Type { get; set; }
        public int Id { get; set; }
        public string Name { get; set; }
        public bool RemoveCompletedDownloads { get; set; }
        public bool HasPostImportCategory { get; set; }

        public static DownloadClientItemClientInfo FromDownloadClient<TSettings>(
            DownloadClientBase<TSettings> downloadClient, bool hasPostImportCategory)
            where TSettings : IProviderConfig, new()
        {
            return new DownloadClientItemClientInfo
            {
                Protocol = downloadClient.Protocol,
                Type = downloadClient.Name,
                Id = downloadClient.Definition.Id,
                Name = downloadClient.Definition.Name,
                RemoveCompletedDownloads = downloadClient.Definition is DownloadClientDefinition { RemoveCompletedDownloads: true },
                HasPostImportCategory = hasPostImportCategory
            };
        }
    }
}
