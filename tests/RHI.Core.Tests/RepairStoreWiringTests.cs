using System.Collections;
using System.Reflection;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// Integration w1 review (stores minor 1): "Repair after move" must rewrite every per-game store the app builds. The
/// relocation tests pass their own store list, so this checks the production list: it walks the fully wired view
/// model's object graph, collects every <see cref="IGameStateStore"/> instance the app constructed (passed in or
/// defaulted inside a service), and requires each to be in the set the repair stages. Dropping one store from
/// <c>LinuxManagedComponentService.RepairStores</c> or from the view model's UI stores fails it.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class RepairStoreWiringTests
{
    [Fact]
    public async Task RepairAfterMove_StagesEveryPerGameStoreTheAppConstructs()
    {
        using var fx = await ViewModelFixture.StartAsync([]);

        var repaired = fx.Managed.RepairStores(fx.Vm.RepairUiStores);
        var constructed = Reachable(fx.Vm).OfType<IGameStateStore>().ToList();

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

    private static bool IsCompilerGenerated(Type type) => type.Name.Contains('<', StringComparison.Ordinal) || type.Name.StartsWith("<>", StringComparison.Ordinal);
}
