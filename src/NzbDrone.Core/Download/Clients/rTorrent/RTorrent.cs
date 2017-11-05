﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NLog;
using NzbDrone.Core.Validation;
using FluentValidation.Results;
using NzbDrone.Core.Download.Clients.rTorrent;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Download.Clients.RTorrent
{
    public class RTorrent : TorrentClientBase<RTorrentSettings>
    {
        private readonly IRTorrentProxy _proxy;
        private readonly IRTorrentDirectoryValidator _rTorrentDirectoryValidator;

        public RTorrent(IRTorrentProxy proxy,
                        ITorrentFileInfoReader torrentFileInfoReader,
                        IHttpClient httpClient,
                        IConfigService configService,
                        IDiskProvider diskProvider,
                        IRemotePathMappingService remotePathMappingService,
                        IRTorrentDirectoryValidator rTorrentDirectoryValidator,
                        Logger logger)
            : base(torrentFileInfoReader, httpClient, configService, diskProvider, remotePathMappingService, logger)
        {
            _proxy = proxy;
            _rTorrentDirectoryValidator = rTorrentDirectoryValidator;
        }

        protected override string AddFromMagnetLink(RemoteEpisode remoteEpisode, string hash, string magnetLink)
        {
            var priority = (RTorrentPriority)(remoteEpisode.IsRecentEpisode() ? Settings.RecentTvPriority : Settings.OlderTvPriority);

            _proxy.AddTorrentFromUrl(magnetLink, Settings.TvCategory, priority, Settings.TvDirectory, Settings);

            var tries = 10;
            var retryDelay = 500;

            // Wait a bit for the magnet to be resolved.
            if (!WaitForTorrent(hash, tries, retryDelay))
            {
                _logger.Warn("rTorrent could not resolve magnet within {0} seconds, download may remain stuck: {1}.", tries * retryDelay / 1000, magnetLink);

                return hash;
            }

            return hash;
        }

        protected override string AddFromTorrentFile(RemoteEpisode remoteEpisode, string hash, string filename, byte[] fileContent)
        {
            var priority = (RTorrentPriority)(remoteEpisode.IsRecentEpisode() ? Settings.RecentTvPriority : Settings.OlderTvPriority);

            _proxy.AddTorrentFromFile(filename, fileContent, Settings.TvCategory, priority, Settings.TvDirectory, Settings);

            var tries = 10;
            var retryDelay = 500;
            if (!WaitForTorrent(hash, tries, retryDelay))
            {
                _logger.Debug("rTorrent didn't add the torrent within {0} seconds: {1}.", tries * retryDelay / 1000, filename);

                throw new ReleaseDownloadException(remoteEpisode.Release, "Downloading torrent failed");
            }

            return hash;
        }

        public override string Name => "rTorrent";

        public override ProviderMessage Message => new ProviderMessage("Sonarr is unable to remove torrents that have finished seeding when using rTorrent", ProviderMessageType.Warning);

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var torrents = _proxy.GetTorrents(Settings);

            _logger.Debug("Retrieved metadata of {0} torrents in client", torrents.Count);

            var items = new List<DownloadClientItem>();
            foreach (RTorrentTorrent torrent in torrents)
            {
                // Don't concern ourselves with categories other than specified
                if (torrent.Category != Settings.TvCategory) continue;

                if (torrent.Path.StartsWith("."))
                {
                    throw new DownloadClientException("Download paths paths must be absolute. Please specify variable \"directory\" in rTorrent.");
                }

                var item = new DownloadClientItem();
                item.DownloadClient = Definition.Name;
                item.Title = torrent.Name;
                item.DownloadId = torrent.Hash;
                item.OutputPath = _remotePathMappingService.RemapRemoteToLocal(Settings.Host, new OsPath(torrent.Path));
                item.TotalSize = torrent.TotalSize;
                item.RemainingSize = torrent.RemainingSize;
                item.Category = torrent.Category;
                item.SeedRatio = torrent.Ratio;

                if (torrent.DownRate > 0)
                {
                    var secondsLeft = torrent.RemainingSize / torrent.DownRate;
                    item.RemainingTime = TimeSpan.FromSeconds(secondsLeft);
                }
                else
                {
                    item.RemainingTime = TimeSpan.Zero;
                }

                if (torrent.IsFinished)
                {
                    item.Status = DownloadItemStatus.Completed;
                }
                else if (torrent.IsActive)
                {
                    item.Status = DownloadItemStatus.Downloading;
                }
                else if (!torrent.IsActive)
                {
                    item.Status = DownloadItemStatus.Paused;
                }

                // No stop ratio data is present, so do not delete
                item.CanMoveFiles = item.CanBeRemoved = false;

                items.Add(item);
            }

            return items;
        }

        public override void RemoveItem(string downloadId, bool deleteData)
        {
            if (deleteData)
            {
                DeleteItemData(downloadId);
            }

            _proxy.RemoveTorrent(downloadId, Settings);
        }

        public override DownloadClientInfo GetStatus()
        {
            // XXX: This function's correctness has not been considered

            var status = new DownloadClientInfo
            {
                IsLocalhost = Settings.Host == "127.0.0.1" || Settings.Host == "localhost"
            };

            return status;
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestConnection());
            if (failures.Any()) return;
            failures.AddIfNotNull(TestGetTorrents());
            failures.AddIfNotNull(TestDirectory());
        }

        private ValidationFailure TestConnection()
        {
            try
            {
                var version = _proxy.GetVersion(Settings);

                if (new Version(version) < new Version("0.9.0"))
                {
                    return new ValidationFailure(string.Empty, "rTorrent version should be at least 0.9.0. Version reported is {0}", version);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to test rTorrent");
                return new NzbDroneValidationFailure(string.Empty, "Unknown exception: " + ex.Message);
            }

            return null;
        }

        private ValidationFailure TestGetTorrents()
        {
            try
            {
                _proxy.GetTorrents(Settings);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get torrents");
                return new NzbDroneValidationFailure(string.Empty, "Failed to get the list of torrents: " + ex.Message);
            }

            return null;
        }

        private ValidationFailure TestDirectory()
        {
            var result = _rTorrentDirectoryValidator.Validate(Settings);

            if (result.IsValid)
            {
                return null;
            }

            return result.Errors.First();
        }

        private bool WaitForTorrent(string hash, int tries, int retryDelay)
        {
            for (var i = 0; i < tries; i++)
            {
                if (_proxy.HasHashTorrent(hash, Settings))
                {
                    return true;
                }

                Thread.Sleep(retryDelay);
            }

            _logger.Debug("Could not find hash {0} in {1} tries at {2} ms intervals.", hash, tries, retryDelay);

            return false;
        }
    }
}
