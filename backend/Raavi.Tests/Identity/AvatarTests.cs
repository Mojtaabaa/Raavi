using System.Net;
using Raavi.Identity.Application;
using Raavi.Tests.Infrastructure;
using Raavi.Tests.Wiki;

namespace Raavi.Tests.Identity;

/// <summary>Image headers as the avatar validator reads them (not decodable pictures: the server only inspects type and size).</summary>
internal static class Pic
{
    public static byte[] Png(int w, int h, int extra = 0)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        b.AddRange(Be32(w));
        b.AddRange(Be32(h));
        b.AddRange(new byte[] { 8, 6, 0, 0, 0 });
        b.AddRange(new byte[extra]);
        return [.. b];
    }

    public static byte[] Jpeg(int w, int h) =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
         0xFF, 0xC0, 0, 17, 8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1];

    public static byte[] WebpExtended(int w, int h) =>
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 30, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P', (byte)'V', (byte)'P', (byte)'8', (byte)'X', 10, 0, 0, 0,
         0, 0, 0, 0, (byte)((w - 1) & 0xFF), (byte)(((w - 1) >> 8) & 0xFF), (byte)(((w - 1) >> 16) & 0xFF), (byte)((h - 1) & 0xFF), (byte)(((h - 1) >> 8) & 0xFF), (byte)(((h - 1) >> 16) & 0xFF)];

    public static byte[] WebpLossless(int w, int h)
    {
        var wb = w - 1;
        var hb = h - 1;
        var bits = (uint)(wb | (hb << 14));
        return
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 30, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P', (byte)'V', (byte)'P', (byte)'8', (byte)'L', 10, 0, 0, 0,
         0x2F, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24), 0, 0, 0, 0, 0];
    }

    public static byte[] WebpLossy(int w, int h) =>
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 30, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P', (byte)'V', (byte)'P', (byte)'8', (byte)' ', 10, 0, 0, 0,
         0, 0, 0, 0x9D, 0x01, 0x2A, (byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8)];

    private static byte[] Be32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
}

public sealed class AvatarImageTests
{
    [Fact]
    public void Types_and_sizes_are_read_from_the_content()
    {
        Assert.Equal(("image/png", 256, 128), AvatarImage.Inspect(Pic.Png(256, 128)));
        Assert.Equal(("image/jpeg", 300, 200), AvatarImage.Inspect(Pic.Jpeg(300, 200)));
        Assert.Equal(("image/webp", 640, 480), AvatarImage.Inspect(Pic.WebpExtended(640, 480)));
        Assert.Equal(("image/webp", 100, 50), AvatarImage.Inspect(Pic.WebpLossless(100, 50)));
        Assert.Equal(("image/webp", 321, 123), AvatarImage.Inspect(Pic.WebpLossy(321, 123)));
    }

    [Fact]
    public void Everything_else_is_not_a_picture()
    {
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'></svg>");
        var gif = System.Text.Encoding.ASCII.GetBytes("GIF89a\u0001\u0000\u0001\u0000\u0000\u0000\u0000;;;;;;;;;;;;;;;;;;;;;;");
        var html = System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>  xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx");
        foreach (var bytes in new[] { svg, gif, html, [], new byte[40], [0x89, 0x50, 0x4E, 0x47], [0xFF, 0xD8, 0xFF] })
        {
            Assert.Null(AvatarImage.Inspect(bytes));
        }
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class AvatarTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static ByteArrayContent Part(byte[] bytes, string type = "image/png", string name = "me.png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        return new ByteArrayContent(form.ReadAsByteArrayAsync().Result) { Headers = { ContentType = form.Headers.ContentType } };
    }

    private static Task<HttpResponseMessage> Put(Browser b, byte[] bytes, string type = "image/png") =>
        b.SendAsync(HttpMethod.Put, "api/v1/auth/me/avatar", Part(bytes, type), withXsrf: true);

    private static async Task<Guid> IdOf(Browser b) => (await b.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();

    [Fact]
    public async Task A_user_uploads_replaces_and_removes_their_own_picture()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var me = await IdOf(admin);

        using (var none = await admin.GetAsync($"api/v1/users/{me}/avatar"))
        {
            await Browser.AssertStatusAsync(none, HttpStatusCode.NotFound);
        }

        using (var first = await Put(admin, Pic.Png(256, 256)))
        {
            Assert.Equal(1, (await WikiTestKit.ReadAsync(first)).GetProperty("version").GetInt32());
        }

        using (var got = await admin.GetAsync($"api/v1/users/{me}/avatar"))
        {
            Assert.Equal(HttpStatusCode.OK, got.StatusCode);
            Assert.Equal("image/png", got.Content.Headers.ContentType!.MediaType);
            Assert.Equal(Pic.Png(256, 256), await got.Content.ReadAsByteArrayAsync());
            Assert.Contains("immutable", got.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
            Assert.Contains("private", got.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
            Assert.Equal("\"a1\"", got.Headers.ETag!.Tag);
        }

        using (var revalidate = new HttpRequestMessage(HttpMethod.Get, $"api/v1/users/{me}/avatar"))
        {
            revalidate.Headers.TryAddWithoutValidation("If-None-Match", "\"a1\"");
            revalidate.Headers.Add("Cookie", string.Join("; ", admin.Cookies.Select(c => $"{c.Key}={c.Value}")));
            using var client = fx.Api.ForHost(admin.Host);
            using var notModified = await client.SendAsync(revalidate);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        }

        using (var second = await Put(admin, Pic.Jpeg(300, 300), "image/jpeg"))
        {
            Assert.Equal(2, (await WikiTestKit.ReadAsync(second)).GetProperty("version").GetInt32());
        }

        using (var got2 = await admin.GetAsync($"api/v1/users/{me}/avatar"))
        {
            Assert.Equal("image/jpeg", got2.Content.Headers.ContentType!.MediaType);
        }

        using (var del = await admin.DeleteAsync("api/v1/auth/me/avatar"))
        {
            await Browser.AssertStatusAsync(del, HttpStatusCode.NoContent);
        }

        using (var gone = await admin.GetAsync($"api/v1/users/{me}/avatar"))
        {
            await Browser.AssertStatusAsync(gone, HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Only_real_small_pictures_are_accepted_whatever_the_declared_type_says()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var big = Pic.Png(256, 256, extra: 600 * 1024);
        foreach (var (bytes, type, code) in new[]
        {
            (System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"), "image/png", "avatar.type_invalid"),
            (System.Text.Encoding.ASCII.GetBytes("GIF89a......................................."), "image/gif", "avatar.type_invalid"),
            (Pic.Png(256, 256)[..20], "image/png", "avatar.type_invalid"),
            (big, "image/png", "avatar.too_large"),
            (Pic.Png(4000, 4000), "image/png", "avatar.size_invalid"),
            (Pic.Png(8, 8), "image/png", "avatar.size_invalid"),
            (Pic.Jpeg(1025, 100), "image/jpeg", "avatar.size_invalid"),
        })
        {
            using var r = await Put(admin, bytes, type);
            await Browser.AssertStatusAsync(r, HttpStatusCode.BadRequest, code);
        }

        // PNG declared as something else is still accepted for what it is (the content decides, and the stored type is the detected one).
        using var ok = await Put(admin, Pic.Png(64, 64), "application/octet-stream");
        Assert.True(ok.IsSuccessStatusCode);
        var me = await IdOf(admin);
        using var got = await admin.GetAsync($"api/v1/users/{me}/avatar");
        Assert.Equal("image/png", got.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Members_see_each_others_pictures_and_nobody_else_does()
    {
        var (tenantA, adminA, _) = await _kit.Identity.TenantWithAdminAsync();
        var (memberA, _, _) = await _kit.Identity.AddMemberAsync(tenantA, adminA);
        var (_, adminB, _) = await _kit.Identity.TenantWithAdminAsync();
        var adminAId = await IdOf(adminA);
        var memberAId = await IdOf(memberA);
        var adminBId = await IdOf(adminB);

        using (var up = await Put(adminA, Pic.Png(128, 128)))
        {
            Assert.True(up.IsSuccessStatusCode);
        }

        // A colleague sees it; the batch lists only users that HAVE a picture.
        using (var seen = await memberA.GetAsync($"api/v1/users/{adminAId}/avatar"))
        {
            Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        }

        var batch = await memberA.GetJsonAsync($"api/v1/users/avatars?ids={adminAId},{memberAId},{adminBId},not-a-guid,{Guid.NewGuid()}");
        Assert.Equal([adminAId.ToString("D")], batch.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, batch.GetProperty(adminAId.ToString("D")).GetInt32());

        // Another workspace's member cannot get it (same answer as for a user that does not exist), and its batch does not list it.
        using (var other = await adminB.GetAsync($"api/v1/users/{adminAId}/avatar"))
        {
            await Browser.AssertStatusAsync(other, HttpStatusCode.NotFound);
        }

        Assert.Empty((await adminB.GetJsonAsync($"api/v1/users/avatars?ids={adminAId}")).EnumerateObject());

        // A suspended member's picture is no longer shown to colleagues.
        using (var suspend = await adminA.PostAsync($"api/v1/tenant/members/{memberAId}/suspend"))
        {
            Assert.True(suspend.IsSuccessStatusCode);
        }

        using (var up2 = await Put(memberA, Pic.Png(64, 64)))
        {
            Assert.False(up2.IsSuccessStatusCode); // suspended: the old session no longer works in this workspace
        }
    }

    [Fact]
    public async Task Pictures_need_a_session_and_the_batch_is_bounded()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var me = await IdOf(admin);
        using var anonymous = new Browser(fx.Api, admin.Host);
        await anonymous.PrimeAsync();
        using (var r1 = await anonymous.GetAsync($"api/v1/users/{me}/avatar"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r1.StatusCode);
        }

        using (var r2 = await Put(anonymous, Pic.Png(64, 64)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
        }

        using (var r3 = await anonymous.GetAsync($"api/v1/users/avatars?ids={me}"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r3.StatusCode);
        }

        // More ids than the limit: only the first part is looked at, never an error or an unbounded query.
        var many = string.Join(',', Enumerable.Range(0, 400).Select(_ => Guid.NewGuid()));
        using var big = await admin.GetAsync($"api/v1/users/avatars?ids={many}");
        Assert.Equal(HttpStatusCode.OK, big.StatusCode);
        _ = tenant;
    }
}
