using System.Reflection;

namespace UltimateUpdater.Tests;

/// <summary>
/// The suite is split into three layer groups by one trait: UI (needs the WPF runtime or renders XAML),
/// Integration (starts a process, or touches the real registry, a pipe or a host-wide name) and Logic (the
/// rest, no trait). These checks keep the trait values spelled right and keep every test that needs the WPF
/// harness in the UI group, so a group command never runs a test without its runtime or hides one.
/// </summary>
public class LayerTraitTests
{
    private static readonly string[] Layers = ["UI", "Integration"];

    private static IEnumerable<Type> TestClasses() =>
        typeof(LayerTraitTests).Assembly.GetTypes().Where(t => t.IsClass
            && t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any()));

    private static IEnumerable<string> LayerValues(MemberInfo member) =>
        member.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(TraitAttribute) && (string?)a.ConstructorArguments[0].Value == "Layer")
            .Select(a => (string)a.ConstructorArguments[1].Value!);

    [Fact]
    public void Every_layer_trait_uses_a_known_value_and_none_is_doubled()
    {
        var bad = new List<string>();
        foreach (var type in TestClasses())
        {
            var onType = LayerValues(type).ToList();
            if (onType.Count > 1 || onType.Any(v => !Layers.Contains(v)))
                bad.Add($"{type.Name}: [{string.Join(", ", onType)}]");

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var onMethod = LayerValues(method).ToList();
                if (onMethod.Count > 1 || onMethod.Any(v => !Layers.Contains(v)))
                    bad.Add($"{type.Name}.{method.Name}: [{string.Join(", ", onMethod)}]");
            }
        }

        Assert.True(bad.Count == 0, "Layer trait values must be UI or Integration, once per member: " + string.Join("; ", bad));
    }

    [Fact]
    public void Every_class_in_the_wpf_collection_is_in_the_ui_group()
    {
        var missing = TestClasses()
            .Where(t => t.GetCustomAttributesData().Any(a =>
                a.AttributeType == typeof(CollectionAttribute) && (string?)a.ConstructorArguments[0].Value == "wpf"))
            .Where(t => !LayerValues(t).Contains("UI"))
            .Select(t => t.Name)
            .ToList();

        Assert.True(missing.Count == 0, "Classes that need the WPF harness must carry [Trait(\"Layer\", \"UI\")]: " + string.Join(", ", missing));
    }

    [Fact]
    public void No_class_is_marked_both_ui_and_integration()
    {
        var both = TestClasses()
            .Where(t => LayerValues(t).Contains("UI")
                && t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(m => LayerValues(m).Contains("Integration")))
            .Select(t => t.Name)
            .ToList();

        Assert.True(both.Count == 0, "A class in the UI group must not also mark methods Integration: " + string.Join(", ", both));
    }
}
