using MediaRack.Server.Hosting;
using MediaRack.Server.Json;
using MediaRack.Store;

namespace MediaRack.Server.Endpoints;

/// <summary>
/// 登录。存在的前提是：浏览器的 <c>&lt;img&gt;</c> / <c>&lt;video&gt;</c> 没法带 <c>Authorization</c> 头，
/// 所以必须有一条"换一张票据放进 cookie"的路，之后就由浏览器自动带上。
/// <para>
/// 多服务下这里只认<b>当前端口那个服务</b>的密码：在 8080 拿到的 cookie 只喂 8080
/// （cookie 名带端口，见 <see cref="Auth.CookieNameFor"/>）。
/// </para>
/// </summary>
internal static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // 免鉴权：页面靠它决定要不要显示登录框。
        app.MapGet("/api/v1/auth/state", (HttpContext context, ServiceScope scope) =>
        {
            var password = scope.Profile.Password;

            return Results.Ok(new AuthStateDto(
                AuthRequired: password is not null,
                Authenticated: password is null ||
                    Auth.IsAuthorized(context.Request, password, scope.Profile.CookieName)));
        });

        app.MapPost("/api/v1/auth/login", (HttpContext context, AuthRequestDto body, ServiceScope scope) =>
        {
            var password = scope.Profile.Password;

            // 没配密码时登录没有意义，直接告诉调用方"不用登录"。
            if (password is null) return Results.Ok(new AuthStateDto(false, true));

            if (!Auth.Matches(body.Token?.Trim(), password))
            {
                return Api.Error(StatusCodes.Status401Unauthorized, "unauthorized", "令牌不正确");
            }

            context.Response.Cookies.Append(scope.Profile.CookieName, password, new CookieOptions
            {
                HttpOnly = true,
                // Lax 而不是 Strict：从聊天工具点链接进来时 Strict 会把 cookie 挡掉，
                // 表现为"刚登录完又跳回登录框"。
                SameSite = SameSiteMode.Lax,
                // 门户下收窄到 /s/{slug}：同一浏览器开两个源，令牌不会互相串门。
                Path = scope.Profile.CookiePath,
                MaxAge = TimeSpan.FromDays(30),
            });

            return Results.Ok(new AuthStateDto(true, true));
        });

        app.MapPost("/api/v1/auth/logout", (HttpContext context, ServiceScope scope) =>
        {
            // 只清当前服务的 cookie，不能把别的服务的会话一起注销掉。
            context.Response.Cookies.Delete(scope.Profile.CookieName,
                new CookieOptions { Path = scope.Profile.CookiePath });

            return Results.Ok(new AuthStateDto(scope.Profile.Password is not null, false));
        });

        // ── 配对（主路径见 docs/design.md §10）：手机扫码 = 一步拿到地址 + 端口 + 令牌 ──

        /// <summary>本机可达的 IPv4 地址。Tailscale（CGNAT 段）排最前——实测它往往是唯一通路。</summary>
        static List<string> PairingHosts() => HostCatalog.Addresses().ToList();

        /// <summary>鉴权检查。配对响应里带令牌本身，必须先过这道门。</summary>
        static IResult? DenyIfUnauthorized(HttpContext context, ServiceScope scope)
        {
            var password = scope.Profile.Password;
            if (password is not null && !Auth.IsAuthorized(context.Request, password, scope.Profile.CookieName))
            {
                return Api.Error(StatusCodes.Status401Unauthorized, "unauthorized", "请先登录");
            }
            return null;
        }

        app.MapGet("/api/v1/auth/pairing", (HttpContext context, ServiceScope scope) =>
        {
            var deny = DenyIfUnauthorized(context, scope);
            if (deny is not null) return deny;

            return Results.Ok(new PairingDto(PairingHosts(), scope.Profile.Password));
        });

        // 二维码出 SVG：浏览器 <img> 直用（cookie 自动带上），前端不引任何 JS 二维码库。
        //
        // host 可选：
        //   - 给了 host → 单地址码（内容还是纯 URL，老客户端兼容）。
        //   - 省略 host → **配对码**：打包全部候选地址（Tailscale 优先 + 手填公网地址），
        //     内容为 `MR1|{json}`。手机端扫到后并发探活、自动选第一个可达的——
        //     用户不用再判断"手机现在在哪个网络、该扫哪张码"。
        //     loopback 不进候选：手机永远连不到 127.0.0.1。
        // host 必须在本机地址列表里——这个端点不能被当成通用二维码生成器。
        app.MapGet("/api/v1/auth/pairing/qr",
            (HttpContext context, ServiceScope scope, LibraryAccess lib, ServiceCatalog catalog,
                string? host, string? source) =>
        {
            var deny = DenyIfUnauthorized(context, scope);
            if (deny is not null) return deny;

            var hosts = PairingHosts();
            var hasHost = !string.IsNullOrWhiteSpace(host);
            if (hasHost && !hosts.Contains(host!.Trim()))
            {
                return Api.Error(StatusCodes.Status400BadRequest, "bad_host", "host 不是本机地址");
            }

            // 默认：当前这件事本身（根路径下是端口服务，门户下是这个源）。
            var path = scope.Profile.Slug is null ? "/" : $"{SourcePortal.Prefix}{scope.Profile.Slug}";
            var token = scope.Profile.Password;

            if (!string.IsNullOrWhiteSpace(source))
            {
                // 跨源出码只许在本机管理页（根路径）用。门户下放行等于
                // "拿 A 的令牌换 B 的令牌"，源级隔离当场作废。
                if (scope.Profile.Slug is not null)
                {
                    return Api.Forbidden("源门户只能生成自己这张二维码，请到本机管理页操作");
                }

                var picked = lib.With(db => new SourceRepository(db).GetBySlug(source.Trim()));
                if (picked is null) return Api.SourceNotFound(source);

                path = $"{SourcePortal.Prefix}{picked.Slug}";
                token = picked.Token;
            }

            var port = scope.Profile.Port;

            string content;
            if (hasHost)
            {
                content = $"http://{host!.Trim()}:{port}{path}";
                if (!string.IsNullOrEmpty(token)) content += $"#token={token!}";
            }
            else
            {
                // 候选地址：本机网卡地址（HostCatalog 已按 Tailscale 优先排序）+ 手填公网地址。
                var candidates = new List<string>();
                foreach (var h in hosts.Where(h => h != "127.0.0.1"))
                {
                    candidates.Add($"http://{h}:{port}{path}");
                }

                var publicUrl = PublicUrlStore.Read(catalog);
                if (!string.IsNullOrWhiteSpace(publicUrl))
                {
                    // 公网地址手填时可能已带端口甚至路径；拼上源路径与令牌。
                    var trimmed = publicUrl.TrimEnd('/');
                    if (!trimmed.Contains("/s/", StringComparison.Ordinal) &&
                        !string.IsNullOrEmpty(scope.Profile.Slug))
                    {
                        trimmed += $"{SourcePortal.Prefix}{pickedSlug(path)}";
                    }

                    candidates.Add(trimmed);
                }

                var payload = System.Text.Json.JsonSerializer.Serialize(
                    new PairingPayload(token, candidates), MediaRackConfig.Json);
                content = $"MR1|{payload}";
            }

            using var generator = new QRCoder.QRCodeGenerator();
            var data = generator.CreateQrCode(content, QRCoder.QRCodeGenerator.ECCLevel.M);
            var svg = new QRCoder.SvgQRCode(data).GetGraphic(6);
            return Results.Content(svg, "image/svg+xml");
        });

        static string pickedSlug(string path) => path.StartsWith(SourcePortal.Prefix, StringComparison.Ordinal)
            ? path[SourcePortal.Prefix.Length..]
            : path;
    }

    /// <summary>配对码内容（<c>MR1|</c> 前缀 + JSON）。token 是<b>这一个源</b>的令牌。</summary>
    public sealed record PairingPayload(string? Token, List<string> Urls);
}
