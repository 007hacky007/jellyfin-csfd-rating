using System.Net;
using System.Net.Http;
using Jellyfin.Plugin.CsfdRatingOverlay.Cache;
using Jellyfin.Plugin.CsfdRatingOverlay.Client;
using Jellyfin.Plugin.CsfdRatingOverlay.Configuration;
using Jellyfin.Plugin.CsfdRatingOverlay.Queue;
using Jellyfin.Plugin.CsfdRatingOverlay.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
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
                <div class="film-header">Movie page with no rating</div>
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
