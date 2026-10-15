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

        // Review finding 2: the config the install created counts as RHI's write, so a later Deploy of a changed template
        // replaces it without asking; a file the user changed asks, and its backup survives an update of the add-on.
        File.WriteAllText(templates.PathOf(UserIniTemplate.ReLimiter), "[FrameLimiter]\nosd_toggle_key=F10\n");
        var ini = new UserIniService(templates, store, _ => null);
        Assert.Equal(UserIniService.AddonIniState.RhiCopy, ini.InspectAddonIni(game, ManagedComponentKind.ReLimiter));
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "relimiter.ini"), "[FrameLimiter]\nmine=1\n");
        Assert.True((await ini.DeployAddonIniAsync(game, ManagedComponentKind.ReLimiter, replaceChanged: true)).Success);
        var backup = ini.AddonIniBackup(game, ManagedComponentKind.ReLimiter);
        Assert.NotNull(backup);

        var updated = await service.InstallAsync(game);

        Assert.True(updated.Success, updated.Message);
        Assert.Equal(backup, ini.AddonIniBackup(game, ManagedComponentKind.ReLimiter));
        Assert.Equal(UserIniService.AddonIniState.Template, ini.InspectAddonIni(game, ManagedComponentKind.ReLimiter));
        File.WriteAllText(templates.PathOf(UserIniTemplate.ReLimiter), "[FrameLimiter]\nosd_toggle_key=F9\n");
        Assert.Equal(UserIniService.AddonIniState.RhiCopy, ini.InspectAddonIni(game, ManagedComponentKind.ReLimiter));
    }
}
