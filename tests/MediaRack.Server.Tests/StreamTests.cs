using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MediaRack.Server.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaRack.Server.Tests;

/// <summary>
/// 流端点测试。Range 是这一层的核心——手机端 seek、4K 拖进度条全靠它，
/// 而它又最容易在"顺手改成一个自写中间件"时悄悄坏掉。
/// </summary>
public sealed class StreamTests(MediaRackFactory factory) : IClassFixture<MediaRackFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<HttpResponseMessage> Get(string path, RangeHeaderValue? range = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (range is not null) request.Headers.Range = range;

        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    [Fact]
    public async Task VideoTrack_ReturnsPartialContentForRange()
    {
        using var response = await Get(
            $"/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}/video.m4s",
            new RangeHeaderValue(0, 99));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(100, response.Content.Headers.ContentLength);
        Assert.Equal($"bytes 0-99/{MediaRackFactory.VideoSize}",
            response.Content.Headers.ContentRange!.ToString());
        Assert.Equal("video/mp4", response.Content.Headers.ContentType!.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(100, bytes.Length);
        // 源文件内容按 i % 251 生成，所以能确认返回的确实是那一段，不是从头开始。
        Assert.Equal(50, bytes[50]);
    }

    [Fact]
    public async Task VideoTrack_MidFileRangeReturnsCorrectBytes()
    {
        using var response = await Get(
            $"/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}/video.m4s",
            new RangeHeaderValue(1000, 1099));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(100, bytes.Length);
        Assert.Equal(1000 % 251, bytes[0]);
        Assert.Equal(1099 % 251, bytes[99]);
    }

    [Fact]
    public async Task VideoTrack_ServesWholeFileWithoutRange()
    {
        using var response = await Get($"/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}/video.m4s");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MediaRackFactory.VideoSize, response.Content.Headers.ContentLength);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
    }

    [Fact]
    public async Task VideoTrack_UnsatisfiableRangeReturns416()
    {
        using var response = await Get(
            $"/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}/video.m4s",
            new RangeHeaderValue(MediaRackFactory.VideoSize + 500, null));

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal($"bytes */{MediaRackFactory.VideoSize}",
            response.Content.Headers.ContentRange!.ToString());
    }

    [Fact]
    public async Task AudioTrack_UsesAudioMimeAndSupportsRange()
    {
        using var response = await Get(
            $"/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}/audio.m4s",
            new RangeHeaderValue(0, 9));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        // 给 application/octet-stream 会让部分播放器在探测阶段就放弃。
        Assert.Equal("audio/mp4", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task SingleFile_IsServedWithRange()
    {
        using var response = await Get(
            $"/media/{MediaRackFactory.Single}/{MediaRackFactory.KeySingle}/file",
            new RangeHeaderValue(0, 15));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes 0-15/{MediaRackFactory.SingleSize}",
            response.Content.Headers.ContentRange!.ToString());
    }

    [Fact]
    public async Task DualEntry_RejectsFileEndpoint()
    {
        using var response = await Get($"/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}/file");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_single", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SingleEntry_RejectsVideoTrackEndpoint()
    {
        using var response = await Get($"/media/{MediaRackFactory.Single}/{MediaRackFactory.KeySingle}/video.m4s");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_dual", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Poster_ReturnsImage()
    {
        using var response = await Get($"/poster/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Poster_WhenMissing_ReturnsSpecificCode()
    {
        // 缩略图没有不是错误：客户端显示占位图即可，所以给一个能区分的 code。
        using var response = await Get($"/poster/{MediaRackFactory.DualA}/{MediaRackFactory.KeyShared}.jpg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("poster_not_found", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Thumb_UnknownMediaReturns404()
    {
        using var response = await Get($"/media/{MediaRackFactory.DualA}/nope/thumb.jpg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Thumb_WithoutFfmpeg_ReturnsSpecificCode()
    {
        // 没有 ffmpeg 时首帧拿不到，但要给客户端可区分的 code（回退封面海报）。
        var degraded = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton(Capabilities.Unavailable("测试注入"))));

        using var client = degraded.CreateClient();
        using var response = await client.GetAsync(
            $"/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}/thumb.jpg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("thumb_unavailable", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Play_DirectModeRedirectsToDetailWith307()
    {
        // 默认 HttpClient 会自动跟随重定向，那样就看不到 307 本体了。
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/play/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}?mode=direct");

        using var response = await client.SendAsync(request);

        // 必须是 307 而不是 302：一条 Location 装不下两条流，所以指向详情让客户端取 tracks[]。
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(
            $"/api/v1/media/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}",
            response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Play_TranscodeIsNotAvailableYet()
    {
        using var response = await Get(
            $"/play/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}?mode=transcode");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("capability_unavailable", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Play_SingleSourceReturnsOriginalFile()
    {
        using var response = await Get($"/play/{MediaRackFactory.Single}/{MediaRackFactory.KeySingle}?mode=auto");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MediaRackFactory.SingleSize, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Play_RemuxWithoutFfmpeg_Returns409()
    {
        // 降级设计要点：缺 ffmpeg 时不能 500，要让客户端知道"换 direct"。
        var degraded = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton(Capabilities.Unavailable("测试注入"))));

        using var client = degraded.CreateClient();
        using var response = await client.GetAsync(
            $"/play/{MediaRackFactory.DualA}/{MediaRackFactory.KeyOnlyA}?mode=remux");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("capability_unavailable", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Export_SingleSourceStreamsOriginalWithoutCopying()
    {
        using var response = await Get($"/api/v1/export/{MediaRackFactory.Single}/{MediaRackFactory.KeySingle}.mp4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MediaRackFactory.SingleSize, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Export_UnknownMediaReturns404()
    {
        using var response = await Get($"/api/v1/export/{MediaRackFactory.DualA}/nope.mp4");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WebPage_IsServedFromEmbeddedResources()
    {
        // 首页与静态资源必须来自程序集内的资源，而不是 wwwroot 目录——
        // 否则「只拷 exe」的部署方式会得到一个 404 的空服务。
        var index = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains("MediaRack", await index.Content.ReadAsStringAsync());

        var script = await _client.GetAsync("/app.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);

        var style = await _client.GetAsync("/style.css");
        Assert.Equal(HttpStatusCode.OK, style.StatusCode);
        Assert.Equal("text/css", style.Content.Headers.ContentType!.MediaType);
    }
}
