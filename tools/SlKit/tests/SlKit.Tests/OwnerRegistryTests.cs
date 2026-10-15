using System;
using SlKit.Core;
using static SlKit.TestSupport.TestKit;

namespace SlKit.Tests;

/// <summary>Tests of OwnerRegistry: values keyed by name and owner.</summary>
internal static class OwnerRegistryTests
{
    internal static void Register()
    {
        // Owner registry (Background.ForOwner keyed by name and assembly)
        Run(nameof(Owners_SameOwnerSameNameFindsTheValue), Owners_SameOwnerSameNameFindsTheValue);
        Run(nameof(Owners_OtherOwnerSameNameIsSeparateAndFirstOwnerIsKnown), Owners_OtherOwnerSameNameIsSeparateAndFirstOwnerIsKnown);
        Run(nameof(Owners_KeepsRegistrationOrderAndRefusesDuplicates), Owners_KeepsRegistrationOrderAndRefusesDuplicates);
    }

    // ---------------------------------------------------------------------------------------
    // Owner registry. Regression for "two plugins passing the same name to Background.ForOwner
    // silently share one worker". Owners are compared by reference, like Assembly objects.
    // ---------------------------------------------------------------------------------------

    private static void Owners_SameOwnerSameNameFindsTheValue()
    {
        OwnerRegistry<string> registry = new OwnerRegistry<string>();
        object pluginA = new object();
        False(registry.TryGet("Stats", pluginA, out string missing), "empty registry");
        True(missing == null, "value is null when missing");
        True(registry.FirstOwnerOf("Stats") == null, "unused name has no owner");

        registry.Add("Stats", pluginA, "worker-A");
        True(registry.TryGet("Stats", pluginA, out string found), "found again");
        Equal("worker-A", found);
        True(ReferenceEquals(pluginA, registry.FirstOwnerOf("Stats")), "first owner recorded");
        False(registry.TryGet("stats", pluginA, out _), "names are case-sensitive (ordinal)");
        Throws<ArgumentNullException>(() => registry.TryGet(null, pluginA, out _), "null name");
    }

    private static void Owners_OtherOwnerSameNameIsSeparateAndFirstOwnerIsKnown()
    {
        OwnerRegistry<string> registry = new OwnerRegistry<string>();
        object pluginA = new object();
        object pluginB = new object();
        registry.Add("Stats", pluginA, "worker-A");

        False(registry.TryGet("Stats", pluginB, out _), "another owner does NOT get A's value");
        True(ReferenceEquals(pluginA, registry.FirstOwnerOf("Stats")), "the collision is visible: A had the name first");

        registry.Add("Stats", pluginB, "worker-B");
        True(registry.TryGet("Stats", pluginB, out string b) && b == "worker-B", "B's own value");
        True(registry.TryGet("Stats", pluginA, out string a) && a == "worker-A", "A's value untouched");
        True(ReferenceEquals(pluginA, registry.FirstOwnerOf("Stats")), "first owner stays A");
        Equal(2, registry.Count);

        // A null owner is an owner of its own, distinct from every object.
        registry.Add("Stats", null, "worker-null");
        True(registry.TryGet("Stats", null, out string n) && n == "worker-null", "null owner");
    }

    private static void Owners_KeepsRegistrationOrderAndRefusesDuplicates()
    {
        OwnerRegistry<string> registry = new OwnerRegistry<string>();
        object a = new object();
        object b = new object();
        registry.Add("x", a, "1");
        registry.Add("y", a, "2");
        registry.Add("x", b, "3");
        SequenceEqual(new[] { "1", "2", "3" }, registry.ToArray(), "registration order");
        Throws<InvalidOperationException>(() => registry.Add("x", a, "again"), "same owner and name twice");
        Throws<ArgumentNullException>(() => registry.Add("z", a, null), "null value");
        Equal(3, registry.Count, "a refused add changes nothing");
    }
}
