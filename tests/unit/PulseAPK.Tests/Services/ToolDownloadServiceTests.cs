// Modified for SendaAPK (2026); original work remains credited in NOTICE.
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PulseAPK.Core.Abstractions;
using PulseAPK.Core.Services;
using Xunit;

namespace PulseAPK.Tests.Services;

public class ToolDownloadServiceTests
{
    [Fact]
    public async Task DownloadApktoolAsync_DownloadsVersionedJarAssetName()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"pulseapk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            const string versionedAssetName = "apktool_2.11.1.jar";
            const string downloadUrl = "https://example.test/assets/apktool_2.11.1.jar";
            var payload = Encoding.UTF8.GetBytes("fake-apktool-binary");

            using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
            {
                if (request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/iBotPeaches/Apktool/releases/latest")
                {
                    var release = new
                    {
                        assets = new[]
                        {
                            new { name = "notes.txt", browser_download_url = "https://example.test/assets/notes.txt" },
                            new { name = versionedAssetName, browser_download_url = downloadUrl }
                        }
                    };

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(release), Encoding.UTF8, "application/json")
                    };
                }

                if (request.RequestUri?.AbsoluteUri == downloadUrl)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(payload)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

            var toolRepository = new TestToolRepository(tempRoot);
            var service = new ToolDownloadService(httpClient, toolRepository);

            var result = await service.DownloadApktoolAsync();

            Assert.True(result.Downloaded);
            Assert.Equal(toolRepository.GetToolPath("apktool.jar"), result.Path);
            Assert.True(File.Exists(result.Path));
            Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class TestToolRepository : IToolRepository
    {
        public TestToolRepository(string toolsDirectory)
        {
            ToolsDirectory = toolsDirectory;
        }

        public string ToolsDirectory { get; }

        public string GetToolPath(string fileName)
        {
            return Path.Combine(ToolsDirectory, fileName);
        }

        public bool TryGetCachedToolPath(string fileName, out string path)
        {
            path = GetToolPath(fileName);
            return File.Exists(path);
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}
