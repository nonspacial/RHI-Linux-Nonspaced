using System.Net;
using System.Security.Cryptography;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #181: a new ReLimiter / Display Commander install copies the user's template, not RHI's stub.
public sealed partial class GitHubReShadeAddonDeploymentServiceTests
{
    [Fact]
    public async Task Install_ReLimiter_WithTemplates_CopiesTheUsersTemplate()
    {
        var game = CreateGame();
        WriteFakeReShade(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var payload = MakePePayload(150 * 1024);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var json = ReleaseJson("v3.3.5", "relimiter.addon64", "https://github.com/rhi-tests/relimiter", digest);
        using var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("api.github", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var templates = new UserIniTemplates(Path.Combine(_root, "inis"));
        Directory.CreateDirectory(templates.Directory);
        File.WriteAllText(templates.PathOf(UserIniTemplate.ReLimiter), "[FrameLimiter]\nosd_toggle_key=Ctrl+F12\n");
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "relimiter-template-records.json"));
        var service = NewReLimiter(http, store);
        service.IniTemplates = templates;

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        Assert.Equal("[FrameLimiter]\nosd_toggle_key=Ctrl+F12\n", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "relimiter.ini")));
    }
}
