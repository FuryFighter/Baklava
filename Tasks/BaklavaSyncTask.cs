#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baklava.Helpers;
using Baklava.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Baklava.Tasks
{
    /// <summary>
    /// Scheduled task to sync staged catalog items from Baklava DB to Jellyfin
    /// </summary>
    public class BaklavaSyncTask : IScheduledTask
    {
        private readonly ILogger<BaklavaSyncTask> _logger;
        private readonly BaklavaDbService _dbService;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILibraryManager _libraryManager;
        private readonly ICollectionManager _collectionManager;

        public BaklavaSyncTask(
            ILogger<BaklavaSyncTask> logger,
            BaklavaDbService dbService,
            IServiceProvider serviceProvider,
            ILibraryManager libraryManager,
            ICollectionManager collectionManager)
        {
            _logger = logger;
            _dbService = dbService;
            _serviceProvider = serviceProvider;
            _libraryManager = libraryManager;
            _collectionManager = collectionManager;
        }

        public string Name => "Sync Baklava Staged Items";

        public string Key => "BaklavaSyncTask";

        public string Description => "Syncs catalog items from Baklava staging database to Jellyfin collections";

        public string Category => "Baklava";

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || !cfg.UseBaklavaStaging)
            {
                _logger.LogDebug("[BaklavaSyncTask] Staging disabled, skipping sync");
                return;
            }

            _logger.LogInformation("[BaklavaSyncTask] Starting sync of staged items");
            progress.Report(0);

            try
            {
                // Get pending items (limit to 20 per run to avoid overload)
                var pendingItems = await _dbService.GetCatalogItemsByStatusAsync("pending", 20);
                
                if (pendingItems.Count == 0)
                {
                    _logger.LogInformation("[BaklavaSyncTask] No pending items to sync");
                    progress.Report(100);
                    return;
                }

                _logger.LogInformation("[BaklavaSyncTask] Syncing {Count} pending items", pendingItems.Count);

                var syncedCount = 0;
                var failedCount = 0;

                for (int i = 0; i < pendingItems.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var item = pendingItems[i];
                    progress.Report((double)i / pendingItems.Count * 100);

                    try
                    {
                        // Check if item already exists in Jellyfin
                        var existingItem = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            HasAnyProviderId = new Dictionary<string, string> { { "Imdb", item.ImdbId } },
                            Recursive = true,
                            Limit = 1,
                            IncludeItemTypes = new[] { item.ItemType == "series" ? BaseItemKind.Series : BaseItemKind.Movie }
                        }).FirstOrDefault();

                        string jellyfinItemId;

                        if (existingItem != null)
                        {
                            jellyfinItemId = existingItem.Id.ToString();
                            _logger.LogDebug("[BaklavaSyncTask] Item already exists: {Title} ({ImdbId})", item.Title, item.ImdbId);
                        }
                        else
                        {
                            // Import via Gelato
                            jellyfinItemId = await ImportItemViaGelatoAsync(item, cancellationToken);
                            
                            if (string.IsNullOrEmpty(jellyfinItemId))
                            {
                                await _dbService.UpdateCatalogItemStatusAsync(item.Id, "failed", null, "Gelato import returned null");
                                failedCount++;
                                _logger.LogWarning("[BaklavaSyncTask] Failed to import: {Title} ({ImdbId})", item.Title, item.ImdbId);
                                continue;
                            }

                            _logger.LogInformation("[BaklavaSyncTask] Successfully imported: {Title} ({ImdbId})", item.Title, item.ImdbId);
                        }

                        // Update status to synced
                        await _dbService.UpdateCatalogItemStatusAsync(item.Id, "synced", jellyfinItemId);
                        syncedCount++;

                        // Small delay to prevent DB lock issues
                        await Task.Delay(500, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[BaklavaSyncTask] Error syncing item: {Title} ({ImdbId})", item.Title, item.ImdbId);
                        await _dbService.UpdateCatalogItemStatusAsync(item.Id, "failed", null, ex.Message);
                        failedCount++;
                    }
                }

                _logger.LogInformation(
                    "[BaklavaSyncTask] Sync completed. Synced: {Synced}, Failed: {Failed}",
                    syncedCount, failedCount);

                progress.Report(100);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaSyncTask] Sync task failed");
                throw;
            }
        }

        private async Task<string?> ImportItemViaGelatoAsync(CatalogItemInfo item, CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var gelatoAssembly = GelatoCompatibility.GetAssembly();
                if (gelatoAssembly is null)
                {
                    _logger.LogWarning("[BaklavaSyncTask] Gelato assembly not found");
                    return null;
                }

                var gelatoManager = GelatoCompatibility.GetManager(scope.ServiceProvider, gelatoAssembly);
                var mediaType = GelatoCompatibility.GetMediaType(gelatoAssembly);
                if (gelatoManager is null || mediaType is null)
                {
                    _logger.LogWarning("[BaklavaSyncTask] Gelato manager or media type not available");
                    return null;
                }

                var isSeries = string.Equals(item.ItemType, "series", StringComparison.OrdinalIgnoreCase);
                var parentFolder = GelatoCompatibility.GetRootFolder(
                    gelatoManager,
                    isSeries,
                    Guid.Empty);
                if (parentFolder is null)
                {
                    _logger.LogWarning("[BaklavaSyncTask] Gelato root folder not found for {Type}", item.ItemType);
                    return null;
                }

                var mediaTypeValue = GelatoCompatibility.ParseMediaType(mediaType, isSeries);
                object? meta = null;
                var config = GelatoCompatibility.GetConfig(gelatoAssembly, Guid.Empty);
                var provider = config is null ? null : GelatoCompatibility.GetStremioProvider(config);
                if (provider is not null)
                {
                    meta = await GelatoCompatibility
                        .GetMetaAsync(provider, item.ImdbId, mediaTypeValue)
                        .ConfigureAwait(false);
                }

                meta ??= GelatoCompatibility.CreateMeta(gelatoAssembly, item.ImdbId, mediaTypeValue);
                var metaType = meta.GetType();
                if (string.IsNullOrWhiteSpace(metaType.GetProperty("Name")?.GetValue(meta) as string))
                {
                    metaType.GetProperty("Name")?.SetValue(meta, item.Title);
                }
                if (string.IsNullOrWhiteSpace(metaType.GetProperty("Poster")?.GetValue(meta) as string))
                {
                    metaType.GetProperty("Poster")?.SetValue(meta, item.Poster);
                }
                if (string.IsNullOrWhiteSpace(metaType.GetProperty("Background")?.GetValue(meta) as string))
                {
                    metaType.GetProperty("Background")?.SetValue(meta, item.Background);
                }
                if (string.IsNullOrWhiteSpace(metaType.GetProperty("Description")?.GetValue(meta) as string))
                {
                    metaType.GetProperty("Description")?.SetValue(meta, item.Overview);
                }

                var insertedItem = await GelatoCompatibility.InsertMetaAsync(
                    gelatoManager,
                    parentFolder,
                    meta,
                    user: null,
                    allowRemoteRefresh: true,
                    refreshItem: true,
                    queueRefreshItem: false,
                    cancellationToken).ConfigureAwait(false);
                return GelatoCompatibility.GetItemId(insertedItem);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BaklavaSyncTask] Failed to import via Gelato: {ImdbId}", item.ImdbId);
                return null;
            }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Run every 5 minutes
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromMinutes(5).Ticks
                }
            };
        }
    }
}
