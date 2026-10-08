using System.Collections;
using System.Reflection;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// Integration w1 review (stores minor 1): "Repair after move" must rewrite every per-game store the app builds. The
/// relocation tests pass their own store list, so this checks the production list: it builds the app's real
/// composition (<see cref="RHI.Desktop.AppComposition"/>, the same factory <c>App</c> calls; final ui-stores review
/// nit 10), walks its and the view model's object graph, collects every <see cref="IGameStateStore"/> instance
/// constructed (passed in or defaulted inside a service), and requires each to be in the set the repair stages.
/// Dropping one store from <c>LinuxManagedComponentService.RepairStores</c> or from the view model's UI stores, or
/// building a new store in the composition without adding it, fails it. Paths resolve under the test run's isolated
/// HOME/XDG root, and every request fails at once (no network).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class RepairStoreWiringTests
{
    [Fact]
    public async Task RepairAfterMove_StagesEveryPerGameStoreTheAppConstructs()
    {
        using var http = new HttpClient(new Offline());
        var wineOverrides = new RHI.Platform.Linux.Deployment.WineDllOverrideService();
        var services = RHI.Desktop.AppComposition.Create(http, new RHI.Platform.Linux.Deployment.LinuxDeploymentRecordStore(), wineOverrides,
            RHI.Platform.Linux.Deployment.LinuxDeploymentRecovery.CreateJournal(wineOverrides));
        using var vm = services.CreateViewModel();
        await vm.Initialization.WaitAsync(TimeSpan.FromSeconds(60));

        var repaired = services.ManagedComponents.RepairStores(vm.RepairUiStores);
        var constructed = Reachable(new object[] { services, vm }).OfType<IGameStateStore>().ToList();

        var missing = constructed.Where(store => !repaired.Any(r => ReferenceEquals(r, store)))
            .Select(store => store.GetType().Name).ToList();
        Assert.True(missing.Count == 0, "not repaired: " + string.Join(", ", missing));

        // Every store type that exists is actually built by the app (else the walk above proves nothing for it).
        var storeTypes = typeof(IGameStateStore).Assembly.GetTypes()
            .Where(t => typeof(IGameStateStore).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(storeTypes, constructed.Select(s => s.GetType().Name).Distinct().Order(StringComparer.Ordinal).ToList());
        Assert.Equal(repaired.Count, repaired.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    /// <summary>Every object reachable from <paramref name="root"/> through fields of RHI types, collections and closures.</summary>
    private static IEnumerable<object> Reachable(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>([root]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current)) continue;
            yield return current;

            switch (current)
            {
                case string:
                    continue;
                case Delegate d:
                    foreach (var target in d.GetInvocationList().Select(x => x.Target).OfType<object>()) queue.Enqueue(target);
                    continue;
                case IDictionary dictionary when !IsRhi(current.GetType()):
                    foreach (var value in dictionary.Values.OfType<object>()) queue.Enqueue(value);
                    continue;
                case ICollection collection when !IsRhi(current.GetType()):
                    foreach (var item in collection.OfType<object>()) queue.Enqueue(item);
                    continue;
            }

            var type = current.GetType();
            if (!IsRhi(type) && !IsCompilerGenerated(type)) continue;
            for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType.IsPrimitive || field.FieldType.IsEnum) continue;
                    if (field.GetValue(current) is { } value && !value.GetType().IsPrimitive) queue.Enqueue(value);
                }
        }
    }

    private static bool IsRhi(Type type) => type.Namespace?.StartsWith("RHI.", StringComparison.Ordinal) == true;

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline test");
    }

    private static bool IsCompilerGenerated(Type type) => type.Name.Contains('<', StringComparison.Ordinal) || type.Name.StartsWith("<>", StringComparison.Ordinal);
}
