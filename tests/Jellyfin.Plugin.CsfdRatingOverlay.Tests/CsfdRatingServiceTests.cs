using System.Net;
using System.Net.Http;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Client;
using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using Jellyfin.Plugin.CsfdRatingOverlay.Infrastructure;
using Jellyfin.Plugin.CsfdRatingOverlay.Models;
using Jellyfin.Plugin.CsfdRatingOverlay.Queue;
using Jellyfin.Plugin.CsfdRatingOverlay.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.CsfdRatingOverlay.Tests;

[Collection("PluginSerial")]
public class CsfdRatingServiceTests
{
    [Fact]
    public async Task ManualMatchAsync_NormalizesId_UpdatesItemAndInvalidatesClientCache()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var plugin = CreatePlugin(tempRoot);
            plugin.UpdateConfiguration(new PluginConfiguration
            {
                NativeRatingTarget = NativeRatingTarget.Both,
                ClientCacheVersion = 0
            });

            var itemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = itemId,
                Name = "Example Movie",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemById(itemId)).Returns(movie);

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var client = CreateClientReturningPercent(63);

            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                client,
                NullLogger<CsfdRatingService>.Instance);

            await sut.ManualMatchAsync(itemId.ToString("N"), "9499", CancellationToken.None);

            var entry = await cacheStore.GetAsync(itemId.ToString(), CancellationToken.None);

            Assert.NotNull(entry);
            Assert.Equal(itemId.ToString(), entry!.ItemId);
            Assert.Equal("9499", entry.CsfdId);
            Assert.Equal(63, entry.Percent);
            Assert.True(movie.UpdateCalled);
            Assert.Equal(ItemUpdateType.MetadataEdit, movie.LastUpdateType);
            Assert.Equal("9499", movie.ProviderIds["Csfd"]);
            Assert.NotNull(movie.CommunityRating);
            Assert.NotNull(movie.CriticRating);
            Assert.True(Math.Abs(movie.CommunityRating!.Value - 6.3f) < 0.001f);
            Assert.True(Math.Abs(movie.CriticRating!.Value - 63f) < 0.001f);
            Assert.True(plugin.Configuration.ClientCacheVersion > 0);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static Plugin CreatePlugin(string tempRoot)
    {
        var appPaths = CreateAppPathsMock(tempRoot);
        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer.Setup(x => x.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()));
        xmlSerializer.Setup(x => x.SerializeToStream(It.IsAny<object>(), It.IsAny<Stream>()));
        xmlSerializer.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>())).Returns((Type _, string _) => null!);
        xmlSerializer.Setup(x => x.DeserializeFromStream(It.IsAny<Type>(), It.IsAny<Stream>())).Returns((Type _, Stream _) => null!);
        xmlSerializer.Setup(x => x.DeserializeFromBytes(It.IsAny<Type>(), It.IsAny<byte[]>())).Returns((Type _, byte[] _) => null!);

        return new Plugin(appPaths.Object, xmlSerializer.Object);
    }

    private static Mock<IApplicationPaths> CreateAppPathsMock(string tempRoot)
    {
        Directory.CreateDirectory(tempRoot);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetupGet(x => x.ProgramDataPath).Returns(tempRoot);
        appPaths.SetupGet(x => x.DataPath).Returns(tempRoot);
        appPaths.SetupGet(x => x.WebPath).Returns(tempRoot);
        appPaths.SetupGet(x => x.ProgramSystemPath).Returns(tempRoot);
        appPaths.SetupGet(x => x.ImageCachePath).Returns(tempRoot);
        appPaths.SetupGet(x => x.PluginsPath).Returns(tempRoot);
        appPaths.SetupGet(x => x.PluginConfigurationsPath).Returns(tempRoot);
        appPaths.SetupGet(x => x.LogDirectoryPath).Returns(tempRoot);
        appPaths.SetupGet(x => x.ConfigurationDirectoryPath).Returns(tempRoot);
        appPaths.SetupGet(x => x.SystemConfigurationFilePath).Returns(Path.Combine(tempRoot, "system.xml"));
        appPaths.SetupGet(x => x.CachePath).Returns(tempRoot);
        appPaths.SetupGet(x => x.TempDirectory).Returns(tempRoot);
        appPaths.SetupGet(x => x.VirtualDataPath).Returns(tempRoot);
        return appPaths;
    }

    [Fact]
    public async Task ManualMatchAsync_NoRating_SavesResolvedNoRatingWithCsfdId()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var plugin = CreatePlugin(tempRoot);
            plugin.UpdateConfiguration(new PluginConfiguration
            {
                NativeRatingTarget = NativeRatingTarget.Both,
                ClientCacheVersion = 0
            });

            var itemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = itemId,
                Name = "Unreleased Movie",
                ProductionYear = 2027,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemById(itemId)).Returns(movie);

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var client = CreateClientReturningNoRating();

            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                client,
                NullLogger<CsfdRatingService>.Instance);

            await sut.ManualMatchAsync(itemId.ToString("N"), "999999", CancellationToken.None);

            var entry = await cacheStore.GetAsync(itemId.ToString(), CancellationToken.None);

            Assert.NotNull(entry);
            Assert.Equal(CsfdCacheEntryStatus.ResolvedNoRating, entry!.Status);
            Assert.Equal("999999", entry.CsfdId);
            Assert.Null(entry.Percent);
            Assert.Null(entry.Stars);
            Assert.Null(entry.DisplayText);
            Assert.NotNull(entry.RetryAfterUtc);
            Assert.True(movie.UpdateCalled);
            Assert.Equal("999999", movie.ProviderIds["Csfd"]);
            Assert.Null(movie.CommunityRating);
            Assert.Null(movie.CriticRating);
            Assert.True(plugin.Configuration.ClientCacheVersion > 0);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ManualMatchAsync_NoRating_ClearsStaleNativeRatings()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var plugin = CreatePlugin(tempRoot);
            plugin.UpdateConfiguration(new PluginConfiguration
            {
                NativeRatingTarget = NativeRatingTarget.Both,
                ClientCacheVersion = 0
            });

            var itemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = itemId,
                Name = "Previously Rated Movie",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string> { { "Csfd", "old-id" } },
                CommunityRating = 7.5f,
                CriticRating = 75f
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemById(itemId)).Returns(movie);

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var client = CreateClientReturningNoRating();

            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                client,
                NullLogger<CsfdRatingService>.Instance);

            await sut.ManualMatchAsync(itemId.ToString("N"), "999999", CancellationToken.None);

            // CSFD ID should be updated
            Assert.Equal("999999", movie.ProviderIds["Csfd"]);

            // Stale ratings should be cleared
            Assert.Null(movie.CommunityRating);
            Assert.Null(movie.CriticRating);
            Assert.True(movie.UpdateCalled);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FetchProcessor_Resolved_PersistsProviderIdAndRating()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var plugin = CreatePlugin(tempRoot);
            plugin.UpdateConfiguration(new PluginConfiguration
            {
                Enabled = true,
                NativeRatingTarget = NativeRatingTarget.CommunityRating,
            });

            var itemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = itemId,
                Name = "Test Movie",
                ProductionYear = 2023,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemById(itemId)).Returns(movie);

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);

            // Pre-populate cache with a known CsfdId so processor skips search
            var existingEntry = new CsfdCacheEntry
            {
                ItemId = itemId.ToString(),
                Status = CsfdCacheEntryStatus.ResolvedNoRating,
                CsfdId = "12345",
                CreatedUtc = DateTimeOffset.UtcNow,
                AttemptedUtc = DateTimeOffset.UtcNow,
                RetryAfterUtc = DateTimeOffset.UtcNow.AddHours(-1) // expired cooldown
            };
            await cacheStore.UpsertAsync(existingEntry, CancellationToken.None);

            var client = CreateClientReturningPercent(75);
            var rateLimiter = new CsfdRateLimiter(TimeSpan.Zero, TimeSpan.FromSeconds(1), NullLogger<CsfdRateLimiter>.Instance);

            var processor = new CsfdFetchProcessor(
                libraryManager.Object,
                cacheStore,
                client,
                rateLimiter,
                new DebugLogger(),
                NullLogger<CsfdFetchProcessor>.Instance);

            var request = new CsfdFetchRequest { ItemId = itemId.ToString(), Force = true };
            await processor.ProcessAsync(request, CancellationToken.None);

            var entry = await cacheStore.GetAsync(itemId.ToString(), CancellationToken.None);

            Assert.NotNull(entry);
            Assert.Equal(CsfdCacheEntryStatus.Resolved, entry!.Status);
            Assert.Equal(75, entry.Percent);
            Assert.Equal("12345", entry.CsfdId);

            // Verify metadata was persisted to library item
            Assert.True(movie.UpdateCalled);
            Assert.Equal("12345", movie.ProviderIds["Csfd"]);
            Assert.NotNull(movie.CommunityRating);
            Assert.True(Math.Abs(movie.CommunityRating!.Value - 7.5f) < 0.001f);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FetchProcessor_NoRating_PersistsProviderIdWithoutRating()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var plugin = CreatePlugin(tempRoot);
            plugin.UpdateConfiguration(new PluginConfiguration
            {
                Enabled = true,
                NativeRatingTarget = NativeRatingTarget.CommunityRating,
            });

            var itemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = itemId,
                Name = "New Movie",
                ProductionYear = 2027,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemById(itemId)).Returns(movie);

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);

            // Pre-populate cache with known CsfdId
            var existingEntry = new CsfdCacheEntry
            {
                ItemId = itemId.ToString(),
                Status = CsfdCacheEntryStatus.ResolvedNoRating,
                CsfdId = "99999",
                CreatedUtc = DateTimeOffset.UtcNow,
                AttemptedUtc = DateTimeOffset.UtcNow,
                RetryAfterUtc = DateTimeOffset.UtcNow.AddHours(-1)
            };
            await cacheStore.UpsertAsync(existingEntry, CancellationToken.None);

            var client = CreateClientReturningNoRating();
            var rateLimiter = new CsfdRateLimiter(TimeSpan.Zero, TimeSpan.FromSeconds(1), NullLogger<CsfdRateLimiter>.Instance);

            var processor = new CsfdFetchProcessor(
                libraryManager.Object,
                cacheStore,
                client,
                rateLimiter,
                new DebugLogger(),
                NullLogger<CsfdFetchProcessor>.Instance);

            var request = new CsfdFetchRequest { ItemId = itemId.ToString(), Force = true };
            await processor.ProcessAsync(request, CancellationToken.None);

            var entry = await cacheStore.GetAsync(itemId.ToString(), CancellationToken.None);

            Assert.NotNull(entry);
            Assert.Equal(CsfdCacheEntryStatus.ResolvedNoRating, entry!.Status);
            Assert.Equal("99999", entry.CsfdId);
            Assert.Null(entry.Percent);

            // Verify CSFD ID persisted to library item even without rating
            Assert.True(movie.UpdateCalled);
            Assert.Equal("99999", movie.ProviderIds["Csfd"]);
            Assert.Null(movie.CommunityRating);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FetchProcessor_OrphanErrorPermanent_DeletesStaleCacheEntry()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var plugin = CreatePlugin(tempRoot);
            plugin.UpdateConfiguration(new PluginConfiguration
            {
                Enabled = true,
            });

            var orphanItemId = Guid.NewGuid();
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemById(orphanItemId)).Returns((Movie?)null);

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);

            var existingEntry = new CsfdCacheEntry
            {
                ItemId = orphanItemId.ToString(),
                Status = CsfdCacheEntryStatus.ErrorPermanent,
                AttemptCount = 1,
                CreatedUtc = DateTimeOffset.UtcNow.AddMonths(-4),
                AttemptedUtc = DateTimeOffset.UtcNow.AddMonths(-4),
                LastError = "legacy ghost entry"
            };
            await cacheStore.UpsertAsync(existingEntry, CancellationToken.None);

            var client = CreateClientReturningPercent(75);
            var rateLimiter = new CsfdRateLimiter(TimeSpan.Zero, TimeSpan.FromSeconds(1), NullLogger<CsfdRateLimiter>.Instance);

            var processor = new CsfdFetchProcessor(
                libraryManager.Object,
                cacheStore,
                client,
                rateLimiter,
                new DebugLogger(),
                NullLogger<CsfdFetchProcessor>.Instance);

            var result = await processor.ProcessAsync(
                new CsfdFetchRequest { ItemId = orphanItemId.ToString(), Force = true },
                CancellationToken.None);

            var entry = await cacheStore.GetAsync(orphanItemId.ToString(), CancellationToken.None);

            Assert.Equal(FetchWorkResultKind.Success, result.Kind);
            Assert.Null(entry);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FetchProcessor_ItemRemovedMidFetch_SkipsUpsertAndDeletesCache()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var plugin = CreatePlugin(tempRoot);
            plugin.UpdateConfiguration(new PluginConfiguration
            {
                Enabled = true,
                NativeRatingTarget = NativeRatingTarget.CommunityRating,
            });

            var itemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = itemId,
                Name = "Disappearing Movie",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>()
            };

            // Item exists when ProcessAsync starts, then is removed before persist.
            // Without the guard the upsert would resurrect the entry that OnItemRemoved already deleted.
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.SetupSequence(x => x.GetItemById(itemId))
                .Returns(movie)
                .Returns((BaseItem?)null);

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);

            var existingEntry = new CsfdCacheEntry
            {
                ItemId = itemId.ToString(),
                Status = CsfdCacheEntryStatus.ResolvedNoRating,
                CsfdId = "55555",
                CreatedUtc = DateTimeOffset.UtcNow,
                AttemptedUtc = DateTimeOffset.UtcNow,
                RetryAfterUtc = DateTimeOffset.UtcNow.AddHours(-1)
            };
            await cacheStore.UpsertAsync(existingEntry, CancellationToken.None);

            var client = CreateClientReturningPercent(80);
            var rateLimiter = new CsfdRateLimiter(TimeSpan.Zero, TimeSpan.FromSeconds(1), NullLogger<CsfdRateLimiter>.Instance);

            var processor = new CsfdFetchProcessor(
                libraryManager.Object,
                cacheStore,
                client,
                rateLimiter,
                new DebugLogger(),
                NullLogger<CsfdFetchProcessor>.Instance);

            var result = await processor.ProcessAsync(
                new CsfdFetchRequest { ItemId = itemId.ToString(), Force = true },
                CancellationToken.None);

            var entry = await cacheStore.GetAsync(itemId.ToString(), CancellationToken.None);

            Assert.Equal(FetchWorkResultKind.Success, result.Kind);
            Assert.Null(entry);
            Assert.False(movie.UpdateCalled);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetStatusAsync_DoesNotMutateCache()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            var activeItemId = Guid.NewGuid();
            var deletedItemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = activeItemId,
                Name = "Active Movie",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { movie });
            libraryManager.Setup(x => x.GetItemById(activeItemId)).Returns(movie);

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = activeItemId.ToString(),
                Status = CsfdCacheEntryStatus.Resolved,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = deletedItemId.ToString(),
                Status = CsfdCacheEntryStatus.NotFound,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);

            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);

            var status = await sut.GetStatusAsync(CancellationToken.None);
            var deletedEntry = await cacheStore.GetAsync(deletedItemId.ToString(), CancellationToken.None);
            var activeEntry = await cacheStore.GetAsync(activeItemId.ToString(), CancellationToken.None);

            Assert.Equal(1, status.TotalLibraryItems);
            Assert.Equal(2, status.CacheStats.TotalEntries);
            Assert.Equal(1, status.CacheStats.Resolved);
            Assert.Equal(1, status.CacheStats.NotFound);
            Assert.NotNull(activeEntry);
            Assert.NotNull(deletedEntry);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetStatusAsync_DeduplicatesItemsBySharedPresentationUniqueKey()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            // Two physical Movie rows for the same logical film (alternate versions or
            // BoxSet/virtual-folder cross-references) plus one independent movie.
            // Jellyfin's UI collapses these via PresentationUniqueKey when the query
            // carries a User; our hosted-service code path has no User, so we have to
            // dedupe on our side. Without the fix Total Library Items would be 3.
            const string sharedKey = "logical-movie-x";
            var movieA = new TestMovie
            {
                Id = Guid.NewGuid(),
                Name = "Movie X 4K",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>(),
                PresentationUniqueKey = sharedKey
            };
            var movieB = new TestMovie
            {
                Id = Guid.NewGuid(),
                Name = "Movie X 1080p",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>(),
                PresentationUniqueKey = sharedKey
            };
            var movieC = new TestMovie
            {
                Id = Guid.NewGuid(),
                Name = "Movie Y",
                ProductionYear = 2025,
                ProviderIds = new Dictionary<string, string>(),
                PresentationUniqueKey = "logical-movie-y"
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movieA, movieB, movieC });

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);

            var status = await sut.GetStatusAsync(CancellationToken.None);

            Assert.Equal(2, status.TotalLibraryItems);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetStatusAsync_ScopesQueryToRealMediaLibrariesAndExcludesInternalRows()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            var movieLibraryId = Guid.NewGuid();
            var collectionLibraryId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = Guid.NewGuid(),
                Name = "Real Movie",
                ProductionYear = 2024,
                ParentId = movieLibraryId,
                ProviderIds = new Dictionary<string, string>(),
                PresentationUniqueKey = "real-movie"
            };

            InternalItemsQuery? capturedQuery = null;
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
            {
                new VirtualFolderInfo
                {
                    ItemId = movieLibraryId.ToString(),
                    CollectionType = CollectionTypeOptions.movies
                },
                new VirtualFolderInfo
                {
                    ItemId = collectionLibraryId.ToString(),
                    CollectionType = CollectionTypeOptions.boxsets
                }
            });
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Callback<InternalItemsQuery>(query => capturedQuery = query)
                .Returns(new List<BaseItem> { movie });

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);

            var status = await sut.GetStatusAsync(CancellationToken.None);

            Assert.NotNull(capturedQuery);
            Assert.Contains(movieLibraryId, capturedQuery.TopParentIds);
            Assert.DoesNotContain(collectionLibraryId, capturedQuery.TopParentIds);
            Assert.Equal(new[] { BaseItemKind.Movie, BaseItemKind.Series }, capturedQuery.IncludeItemTypes);
            Assert.Equal(new[] { SourceType.Library }, capturedQuery.SourceTypes);
            Assert.False(capturedQuery.IsVirtualItem);
            Assert.False(capturedQuery.IsMissing);
            Assert.False(capturedQuery.IsPlaceHolder);
            Assert.False(capturedQuery.HasDeadParentId);
            Assert.Equal(1, status.TotalLibraryItems);
            Assert.Equal(1, status.LibraryDiagnostics.SupportedMediaLibraries);
            Assert.Equal(1, status.LibraryDiagnostics.RawQueryItems);
            Assert.Equal(1, status.LibraryDiagnostics.FinalItems);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BackfillLibraryAsync_DeduplicatesItemsBySharedPresentationUniqueKey()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            // Backfill must not enqueue the same logical movie twice when Jellyfin
            // returns multiple presentation rows for it.
            const string sharedKey = "logical-movie";
            var movieA = new TestMovie
            {
                Id = Guid.NewGuid(),
                Name = "Movie 4K",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>(),
                PresentationUniqueKey = sharedKey
            };
            var movieB = new TestMovie
            {
                Id = Guid.NewGuid(),
                Name = "Movie 1080p",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>(),
                PresentationUniqueKey = sharedKey
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movieA, movieB });

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);

            var processor = new Mock<ICsfdFetchProcessor>();
            processor
                .Setup(x => x.ProcessAsync(It.IsAny<CsfdFetchRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(FetchWorkResult.Success);
            var queue = new CsfdFetchQueue(processor.Object, NullLogger<CsfdFetchQueue>.Instance);

            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);

            var enqueued = await sut.BackfillLibraryAsync(CancellationToken.None);

            Assert.Equal(1, enqueued);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RetryNotFoundAsync_PrunesDeletedLibraryItemsBeforeEnqueue()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            var activeItemId = Guid.NewGuid();
            var deletedItemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = activeItemId,
                Name = "Active Movie",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { movie });

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = activeItemId.ToString(),
                Status = CsfdCacheEntryStatus.NotFound,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = deletedItemId.ToString(),
                Status = CsfdCacheEntryStatus.NotFound,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);

            var processor = new Mock<ICsfdFetchProcessor>();
            processor
                .Setup(x => x.ProcessAsync(It.IsAny<CsfdFetchRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(FetchWorkResult.Success);
            var queue = new CsfdFetchQueue(processor.Object, NullLogger<CsfdFetchQueue>.Instance);
            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);

            var enqueued = await sut.RetryNotFoundAsync(CancellationToken.None);
            var deletedEntry = await cacheStore.GetAsync(deletedItemId.ToString(), CancellationToken.None);
            var activeEntry = await cacheStore.GetAsync(activeItemId.ToString(), CancellationToken.None);

            Assert.Equal(1, enqueued);
            Assert.NotNull(activeEntry);
            Assert.Null(deletedEntry);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BackfillLibraryAsync_PrunesDeletedLibraryItemsBeforeQueueing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            var activeItemId = Guid.NewGuid();
            var deletedItemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = activeItemId,
                Name = "Active Movie",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { movie });

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = activeItemId.ToString(),
                Status = CsfdCacheEntryStatus.Resolved,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = deletedItemId.ToString(),
                Status = CsfdCacheEntryStatus.NotFound,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);

            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);

            var enqueued = await sut.BackfillLibraryAsync(CancellationToken.None);
            var activeEntry = await cacheStore.GetAsync(activeItemId.ToString(), CancellationToken.None);
            var deletedEntry = await cacheStore.GetAsync(deletedItemId.ToString(), CancellationToken.None);

            Assert.Equal(0, enqueued);
            Assert.NotNull(activeEntry);
            Assert.Null(deletedEntry);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BackfillLibraryAsync_SkipsPruneWhenLibraryReturnsNoItems()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            var existingItemId = Guid.NewGuid();
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem>());

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = existingItemId.ToString(),
                Status = CsfdCacheEntryStatus.Resolved,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);

            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);

            var enqueued = await sut.BackfillLibraryAsync(CancellationToken.None);
            var entry = await cacheStore.GetAsync(existingItemId.ToString(), CancellationToken.None);

            Assert.Equal(0, enqueued);
            Assert.NotNull(entry);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PruneStaleCacheEntriesAsync_KeepsEntriesFoundViaGetItemById()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            // Simulates a partial library load: bulk GetItemList only reports one item,
            // but the cached entries still exist according to per-id GetItemById.
            // Per-id verification must keep the cached entries.
            var bulkItem = new TestMovie
            {
                Id = Guid.NewGuid(),
                Name = "Bulk Item",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { bulkItem });

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);

            var cachedIds = new List<Guid>();
            for (var i = 0; i < 30; i++)
            {
                var id = Guid.NewGuid();
                cachedIds.Add(id);
                var movie = new TestMovie
                {
                    Id = id,
                    Name = $"Cached {i}",
                    ProductionYear = 2024,
                    ProviderIds = new Dictionary<string, string>()
                };
                libraryManager.Setup(x => x.GetItemById(id)).Returns(movie);
                await cacheStore.UpsertAsync(new CsfdCacheEntry
                {
                    ItemId = id.ToString(),
                    Status = CsfdCacheEntryStatus.Resolved,
                    CreatedUtc = DateTimeOffset.UtcNow
                }, CancellationToken.None);
            }

            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);

            var deleted = await sut.PruneStaleCacheEntriesAsync(CancellationToken.None);

            Assert.Equal(0, deleted);
            foreach (var id in cachedIds)
            {
                Assert.NotNull(await cacheStore.GetAsync(id.ToString(), CancellationToken.None));
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PruneStaleCacheEntriesAsync_DeletesEntriesUnknownToGetItemById()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            // User deleted some movies while the plugin was off. Bulk GetItemList
            // returns the surviving items; per-id GetItemById returns null for the
            // rest. Stale entries must be pruned, but the deleted ratio stays under
            // the 50% safety threshold so the guardrail does not engage.
            var liveItems = new List<BaseItem>();
            var libraryManager = new Mock<ILibraryManager>();

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);

            for (var i = 0; i < 30; i++)
            {
                var id = Guid.NewGuid();
                var movie = new TestMovie
                {
                    Id = id,
                    Name = $"Live {i}",
                    ProductionYear = 2024,
                    ProviderIds = new Dictionary<string, string>()
                };
                liveItems.Add(movie);
                libraryManager.Setup(x => x.GetItemById(id)).Returns(movie);
                await cacheStore.UpsertAsync(new CsfdCacheEntry
                {
                    ItemId = id.ToString(),
                    Status = CsfdCacheEntryStatus.Resolved,
                    CreatedUtc = DateTimeOffset.UtcNow
                }, CancellationToken.None);
            }

            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(liveItems);

            var deletedIds = new List<Guid>();
            for (var i = 0; i < 10; i++)
            {
                var id = Guid.NewGuid();
                deletedIds.Add(id);
                await cacheStore.UpsertAsync(new CsfdCacheEntry
                {
                    ItemId = id.ToString(),
                    Status = CsfdCacheEntryStatus.Resolved,
                    CreatedUtc = DateTimeOffset.UtcNow
                }, CancellationToken.None);
            }

            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);

            var deleted = await sut.PruneStaleCacheEntriesAsync(CancellationToken.None);

            Assert.Equal(10, deleted);
            foreach (var live in liveItems)
            {
                Assert.NotNull(await cacheStore.GetAsync(live.Id.ToString(), CancellationToken.None));
            }
            foreach (var id in deletedIds)
            {
                Assert.Null(await cacheStore.GetAsync(id.ToString(), CancellationToken.None));
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PruneStaleCacheEntriesAsync_SkipsWhenStaleRatioExceedsHalfThreshold()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            // Library is still loading: bulk reports a single item, and per-id lookup
            // returns null for everything else. Without the guardrail this would wipe
            // most of the cache. The guardrail must skip the prune entirely.
            var liveItem = new TestMovie
            {
                Id = Guid.NewGuid(),
                Name = "Bulk Item",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { liveItem });
            libraryManager.Setup(x => x.GetItemById(liveItem.Id)).Returns(liveItem);

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);

            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = liveItem.Id.ToString(),
                Status = CsfdCacheEntryStatus.Resolved,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);

            var possiblyStaleIds = new List<Guid>();
            for (var i = 0; i < 20; i++)
            {
                var id = Guid.NewGuid();
                possiblyStaleIds.Add(id);
                await cacheStore.UpsertAsync(new CsfdCacheEntry
                {
                    ItemId = id.ToString(),
                    Status = CsfdCacheEntryStatus.Resolved,
                    CreatedUtc = DateTimeOffset.UtcNow
                }, CancellationToken.None);
            }

            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);

            var deleted = await sut.PruneStaleCacheEntriesAsync(CancellationToken.None);

            Assert.Equal(0, deleted);
            Assert.NotNull(await cacheStore.GetAsync(liveItem.Id.ToString(), CancellationToken.None));
            foreach (var id in possiblyStaleIds)
            {
                Assert.NotNull(await cacheStore.GetAsync(id.ToString(), CancellationToken.None));
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RetryErrorsAsync_PrunesDeletedLibraryItemsBeforeEnqueue()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            var activeItemId = Guid.NewGuid();
            var deletedItemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = activeItemId,
                Name = "Active Movie",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { movie });

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = activeItemId.ToString(),
                Status = CsfdCacheEntryStatus.ErrorTransient,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = deletedItemId.ToString(),
                Status = CsfdCacheEntryStatus.ErrorPermanent,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);

            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);

            var enqueued = await sut.RetryErrorsAsync(CancellationToken.None);
            var activeEntry = await cacheStore.GetAsync(activeItemId.ToString(), CancellationToken.None);
            var deletedEntry = await cacheStore.GetAsync(deletedItemId.ToString(), CancellationToken.None);

            Assert.Equal(1, enqueued);
            Assert.NotNull(activeEntry);
            Assert.Null(deletedEntry);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RetryNoRatingAsync_PrunesDeletedLibraryItemsBeforeEnqueue()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            var activeItemId = Guid.NewGuid();
            var deletedItemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = activeItemId,
                Name = "Active Movie",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { movie });

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = activeItemId.ToString(),
                Status = CsfdCacheEntryStatus.ResolvedNoRating,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = deletedItemId.ToString(),
                Status = CsfdCacheEntryStatus.ResolvedNoRating,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);

            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);

            var enqueued = await sut.RetryNoRatingAsync(CancellationToken.None);
            var activeEntry = await cacheStore.GetAsync(activeItemId.ToString(), CancellationToken.None);
            var deletedEntry = await cacheStore.GetAsync(deletedItemId.ToString(), CancellationToken.None);

            Assert.Equal(1, enqueued);
            Assert.NotNull(activeEntry);
            Assert.Null(deletedEntry);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HostedService_ItemRemoved_DeletesCacheEntry()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            var itemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = itemId,
                Name = "Removed Movie",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = itemId.ToString(),
                Status = CsfdCacheEntryStatus.Resolved,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);

            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var ratingService = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);
            var hostedService = new CsfdHostedService(
                queue,
                ratingService,
                libraryManager.Object,
                cacheStore,
                NullLogger<CsfdHostedService>.Instance);

            await hostedService.StartAsync(CancellationToken.None);
            libraryManager.Raise(x => x.ItemRemoved += null, libraryManager.Object, new ItemChangeEventArgs { Item = movie });

            CsfdCacheEntry? entry = null;
            for (var i = 0; i < 40; i++)
            {
                entry = await cacheStore.GetAsync(itemId.ToString(), CancellationToken.None);
                if (entry is null)
                {
                    break;
                }

                await Task.Delay(50);
            }

            Assert.Null(entry);

            await hostedService.StopAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HostedService_ItemRemoved_IgnoresUnsupportedItemTypes()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            var itemId = Guid.NewGuid();
            var episode = new Episode
            {
                Id = itemId,
                Name = "Removed Episode",
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = itemId.ToString(),
                Status = CsfdCacheEntryStatus.Resolved,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);

            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var ratingService = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);
            var hostedService = new CsfdHostedService(
                queue,
                ratingService,
                libraryManager.Object,
                cacheStore,
                NullLogger<CsfdHostedService>.Instance);

            await hostedService.StartAsync(CancellationToken.None);
            libraryManager.Raise(x => x.ItemRemoved += null, libraryManager.Object, new ItemChangeEventArgs { Item = episode });

            var entry = await cacheStore.GetAsync(itemId.ToString(), CancellationToken.None);

            Assert.NotNull(entry);

            await hostedService.StopAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HostedService_StartAsync_PrunesStaleCacheEntries()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            var activeItemId = Guid.NewGuid();
            var deletedItemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = activeItemId,
                Name = "Active Movie",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { movie });

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = activeItemId.ToString(),
                Status = CsfdCacheEntryStatus.Resolved,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = deletedItemId.ToString(),
                Status = CsfdCacheEntryStatus.NotFound,
                CreatedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);

            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var ratingService = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningPercent(75),
                NullLogger<CsfdRatingService>.Instance);
            var hostedService = new CsfdHostedService(
                queue,
                ratingService,
                libraryManager.Object,
                cacheStore,
                NullLogger<CsfdHostedService>.Instance,
                TimeSpan.Zero);

            await hostedService.StartAsync(CancellationToken.None);

            CsfdCacheEntry? deletedEntry = null;
            for (var i = 0; i < 40; i++)
            {
                deletedEntry = await cacheStore.GetAsync(deletedItemId.ToString(), CancellationToken.None);
                if (deletedEntry is null)
                {
                    break;
                }

                await Task.Delay(50);
            }

            var activeEntry = await cacheStore.GetAsync(activeItemId.ToString(), CancellationToken.None);

            Assert.NotNull(activeEntry);
            Assert.Null(deletedEntry);

            await hostedService.StopAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FetchProcessor_LowConfidenceCandidate_MarksNotFound()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var plugin = CreatePlugin(tempRoot);
            plugin.UpdateConfiguration(new PluginConfiguration
            {
                Enabled = true,
                MatchConfidenceThreshold = 1.0
            });

            var itemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = itemId,
                Name = "Rare Film",
                ProductionYear = 2024,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemById(itemId)).Returns(movie);

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);

            var handler = new RoutingResponseHandler(request =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains("/hledat/?q=", StringComparison.Ordinal))
                {
                    return """
                        <html>
                          <body>
                            <h3 class="film-title-nooverflow">
                              <a href="/film/111111-completely-different/" >Completely Different</a>
                              <span>2024</span>
                            </h3>
                            <p class="search-name">(Nothing Similar)</p>
                          </body>
                        </html>
                        """;
                }

                return """
                    <html>
                      <body>
                        <div class="film-rating-average">81%</div>
                      </body>
                    </html>
                    """;
            });

            var client = new CsfdClient(
                new HttpClient(handler),
                new DebugLogger(),
                NullLogger<CsfdClient>.Instance,
                new AnubisChallengeSolver(NullLogger<AnubisChallengeSolver>.Instance));
            var rateLimiter = new CsfdRateLimiter(TimeSpan.Zero, TimeSpan.FromSeconds(1), NullLogger<CsfdRateLimiter>.Instance);

            var processor = new CsfdFetchProcessor(
                libraryManager.Object,
                cacheStore,
                client,
                rateLimiter,
                new DebugLogger(),
                NullLogger<CsfdFetchProcessor>.Instance);

            var result = await processor.ProcessAsync(
                new CsfdFetchRequest { ItemId = itemId.ToString(), Force = true },
                CancellationToken.None);

            var entry = await cacheStore.GetAsync(itemId.ToString(), CancellationToken.None);

            Assert.Equal(FetchWorkResultKind.Success, result.Kind);
            Assert.NotNull(entry);
            Assert.Equal(CsfdCacheEntryStatus.NotFound, entry!.Status);
            Assert.Null(entry.CsfdId);
            Assert.Equal("Rare Film 2024", entry.QueryUsed);
            Assert.False(movie.UpdateCalled);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetReviewItemsAsync_ResolvedNoRating_ReturnsMatchMetadata()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "csfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            CreatePlugin(tempRoot);

            var itemId = Guid.NewGuid();
            var movie = new TestMovie
            {
                Id = itemId,
                Name = "Awaiting Movie",
                OriginalTitle = "Original Awaiting Movie",
                ProductionYear = 2026,
                ProviderIds = new Dictionary<string, string>()
            };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.GetItemById(itemId)).Returns(movie);
            libraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem> { movie });

            var appPaths = CreateAppPathsMock(tempRoot);
            var cacheStore = new FileCsfdCacheStore(appPaths.Object, NullLogger<FileCsfdCacheStore>.Instance);
            await cacheStore.UpsertAsync(new CsfdCacheEntry
            {
                ItemId = itemId.ToString(),
                Status = CsfdCacheEntryStatus.ResolvedNoRating,
                CsfdId = "5555",
                MatchedTitle = "Wrong Match Maybe",
                MatchedYear = 2024,
                QueryUsed = "Awaiting Movie 2026",
                CreatedUtc = DateTimeOffset.UtcNow,
                AttemptedUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);

            var queue = new CsfdFetchQueue(Mock.Of<ICsfdFetchProcessor>(), NullLogger<CsfdFetchQueue>.Instance);
            var sut = new CsfdRatingService(
                libraryManager.Object,
                cacheStore,
                queue,
                CreateClientReturningNoRating(),
                NullLogger<CsfdRatingService>.Instance);

            var items = await sut.GetReviewItemsAsync(new[] { CsfdCacheEntryStatus.ResolvedNoRating }, includeUncached: false, CancellationToken.None);

            var reviewItem = Assert.Single(items);
            Assert.Equal(itemId.ToString(), reviewItem.ItemId);
            Assert.Equal("Awaiting Movie", reviewItem.Title);
            Assert.Equal("Original Awaiting Movie", reviewItem.OriginalTitle);
            Assert.Equal("ResolvedNoRating", reviewItem.Status);
            Assert.Equal("5555", reviewItem.CsfdId);
            Assert.Equal("Wrong Match Maybe", reviewItem.MatchedTitle);
            Assert.Equal(2024, reviewItem.MatchedYear);
            Assert.Equal("Awaiting Movie 2026", reviewItem.QueryUsed);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static CsfdClient CreateClientReturningPercent(int percent)
    {
        var handler = new StaticResponseHandler($"""
            <html>
              <body>
                <div class="film-rating-average">{percent}%</div>
              </body>
            </html>
            """);

        return new CsfdClient(
            new HttpClient(handler),
            new DebugLogger(),
            NullLogger<CsfdClient>.Instance,
            new AnubisChallengeSolver(NullLogger<AnubisChallengeSolver>.Instance));
    }

    private static CsfdClient CreateClientReturningNoRating()
    {
        var handler = new StaticResponseHandler("""
            <html>
              <body>
                <h1 class="film-header-name">Movie page with no rating</h1>
              </body>
            </html>
            """);

        return new CsfdClient(
            new HttpClient(handler),
            new DebugLogger(),
            NullLogger<CsfdClient>.Instance,
            new AnubisChallengeSolver(NullLogger<AnubisChallengeSolver>.Instance));
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly string _responseContent;

        public StaticResponseHandler(string responseContent)
        {
            _responseContent = responseContent;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseContent)
            });
        }
    }

    private sealed class RoutingResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _router;

        public RoutingResponseHandler(Func<HttpRequestMessage, string> router)
        {
            _router = router;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_router(request))
            });
        }
    }

    private sealed class TestMovie : Movie
    {
        public bool UpdateCalled { get; private set; }

        public ItemUpdateType? LastUpdateType { get; private set; }

        public override Task UpdateToRepositoryAsync(ItemUpdateType itemUpdateType, CancellationToken cancellationToken)
        {
            UpdateCalled = true;
            LastUpdateType = itemUpdateType;
            return Task.CompletedTask;
        }
    }
}

[CollectionDefinition("PluginSerial", DisableParallelization = true)]
public sealed class PluginSerialCollection
{
}
