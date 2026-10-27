using MediaRack.Server.Hosting;
using Xunit;

namespace MediaRack.Server.Tests;

/// <summary>
/// 服务表的解析与查找。纯逻辑，不启 HTTP 管线。
/// </summary>
public sealed class ServiceCatalogTests
{
    private static ServerOptions Options(string? token = null) => new()
    {
        MetaRoot = Path.GetTempPath(),
        Token = token,
        Port = 8080,
    };

    [Fact]
    public void No_config_file_falls_back_to_single_service()
    {
        var catalog = ServiceCatalog.Resolve(Options(), configPath: null, MediaRackConfig.Default);

        Assert.False(catalog.IsExplicit);

        var only = Assert.Single(catalog.Services);
        Assert.Equal(8080, only.Port);
        Assert.True(only.IsPublic);
        Assert.True(only.Writable);
        Assert.True(only.SeesAllSources);
    }

    /// <summary>传统模式仍认 --token，否则升级后原本有密码的部署会突然变成公开。</summary>
    [Fact]
    public void Legacy_token_becomes_the_service_password()
    {
        var catalog = ServiceCatalog.Resolve(Options("abc"), configPath: null, MediaRackConfig.Default);

        var only = Assert.Single(catalog.Services);
        Assert.Equal("abc", only.Password);
        Assert.False(only.IsPublic);
    }

    [Fact]
    public void Config_file_services_are_parsed()
    {
        var config = MediaRackConfig.FromServicesJson("""
            [
              {"name":"全部","port":8080,"writable":true,"sources":[]},
              {"name":"家庭","port":8081,"password":"x","writable":false,"sources":["home"]}
            ]
            """);

        var catalog = ServiceCatalog.Resolve(Options(), "cfg.json", config);

        Assert.True(catalog.IsExplicit);
        Assert.Equal(2, catalog.Services.Count);

        var restricted = catalog.ByPort(8081)!;
        Assert.Equal("家庭", restricted.Name);
        Assert.Equal("x", restricted.Password);
        Assert.False(restricted.Writable);
        Assert.False(restricted.SeesAllSources);
        Assert.True(restricted.CanSee("home"));
        Assert.False(restricted.CanSee("bili"));
    }

    [Fact]
    public void Port_lookup_misses_cleanly()
    {
        var config = MediaRackConfig.FromServicesJson("""[{"name":"a","port":9000}]""");
        var catalog = ServiceCatalog.Resolve(Options(), "cfg.json", config);

        Assert.Null(catalog.ByPort(1234));
        Assert.Equal(9000, catalog.Default.Port);
    }

    /// <summary>同端口监听两次在 Kestrel 里报得含糊，这里要提前给出人话。</summary>
    [Fact]
    public void Duplicate_ports_are_rejected()
    {
        var config = MediaRackConfig.FromServicesJson("""
            [{"name":"a","port":8080},{"name":"b","port":8080}]
            """);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ServiceCatalog.Resolve(Options(), "cfg.json", config));

        Assert.Contains("同一个端口", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Out_of_range_ports_are_rejected(int port)
    {
        var config = new MediaRackConfig { Services = [new ServiceConfig { Port = port }] };

        Assert.Throws<InvalidOperationException>(
            () => ServiceCatalog.Resolve(Options(), "cfg.json", config));
    }

    [Fact]
    public void Missing_name_gets_a_readable_placeholder()
    {
        var config = MediaRackConfig.FromServicesJson("""[{"port":9090}]""");
        var catalog = ServiceCatalog.Resolve(Options(), "cfg.json", config);

        Assert.Equal("服务 9090", Assert.Single(catalog.Services).Name);
    }

    /// <summary>空白密码等同于不设密码——配置文件里留个 "" 是很常见的写法。</summary>
    [Fact]
    public void Blank_password_means_public()
    {
        var config = MediaRackConfig.FromServicesJson("""[{"name":"a","port":8080,"password":"   "}]""");
        var catalog = ServiceCatalog.Resolve(Options(), "cfg.json", config);

        Assert.True(Assert.Single(catalog.Services).IsPublic);
    }

    /// <summary>空源数组是"全部"，不是"一个都看不到"。</summary>
    [Fact]
    public void Empty_source_list_means_everything()
    {
        var profile = new ServiceProfile { Name = "x", Port = 1, Sources = [] };

        Assert.True(profile.SeesAllSources);
        Assert.True(profile.CanSee("anything"));
    }

    /// <summary>配置里一个服务都没写时，不该起一个什么都不监听的进程。</summary>
    [Fact]
    public void Empty_service_array_falls_back_to_default()
    {
        var config = MediaRackConfig.FromJson("""{"services":[]}""");
        var catalog = ServiceCatalog.Resolve(Options(), "cfg.json", config);

        Assert.Equal(8080, Assert.Single(catalog.Services).Port);
    }

    /// <summary>cookie 名必须随端口变化，否则两个服务共享一份会话。</summary>
    [Fact]
    public void Cookie_names_differ_per_port()
    {
        var a = new ServiceProfile { Name = "a", Port = 8080 };
        var b = new ServiceProfile { Name = "b", Port = 8081 };

        Assert.Equal("mrack_token_s8080", a.CookieName);
        Assert.Equal("mrack_token_s8081", b.CookieName);
        Assert.NotEqual(a.CookieName, b.CookieName);
    }
}
