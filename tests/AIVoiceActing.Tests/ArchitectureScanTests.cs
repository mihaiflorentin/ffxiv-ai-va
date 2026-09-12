namespace AIVoiceActing.Tests;

using System.Reflection;
using Xunit;

/// <summary>
/// Enforces the hexagonal dependency rule: types in AIVoiceActing.Domain.* and
/// AIVoiceActing.Ports.* must not reference AIVoiceActing.Infrastructure.*, AIVoiceActing.UI.*,
/// or any Dalamud / FFXIVClientStructs / Lumina root namespace.
///
/// Limitation: this scan inspects declared metadata only (base types, interfaces, fields,
/// properties, events, method signatures, nested types). It does NOT decompile method bodies,
/// so a forbidden reference used solely inside a method body would not be caught.
/// </summary>
public sealed class ArchitectureScanTests
{
    private static readonly string[] ForbiddenNamespaces =
    [
        "AIVoiceActing.Infrastructure.",
        "AIVoiceActing.UI.",
    ];

    private static readonly string[] ForbiddenRoots =
    [
        "Dalamud",
        "FFXIVClientStructs",
        "Lumina",
    ];

    [Fact]
    public void DomainAndPorts_ReferenceNoInfrastructureUiOrDalamud()
    {
        // The test assembly compiles Domain/Ports (plus the Dalamud-free container) from the
        // plugin sources directly; the Dalamud-typed plugin shell cannot load on this host
        // (x64-only assemblies). SafeGetTypes still tolerates unloaded types defensively.
        var scanned = SafeGetTypes(typeof(AIVoiceActing.Ports.ILogSink).Assembly)
            .Where(IsScanned)
            .ToArray();

        // Guard against an empty/false-pass scan: 3 domain records + 13 ports + their DTOs.
        Assert.True(scanned.Length >= 16, $"Expected a real scan; found only {scanned.Length} types.");
        Assert.Contains(scanned, t => t.Name == "ISpeechSynthesizer");

        var violations = FindViolations(scanned);

        Assert.True(violations.Count == 0, "Dependency-rule violations:\n" + string.Join("\n", violations));
    }

    /// <summary>Guard: constructed generic arguments must be scanned too — a Task&lt;ForbiddenType&gt;
    /// property otherwise yields only System.Threading.Tasks as its namespace.</summary>
    [Fact]
    public void Scan_CatchesGenericTypeArguments()
    {
        var violations = FindViolations([typeof(Ports.TestFixtures.GenericScanFixture)]);

        Assert.NotEmpty(violations);
        Assert.All(violations, v =>
        {
            Assert.Contains("GenericScanFixture", v);
            Assert.Contains("ForbiddenProbe", v);
        });
    }

    private static List<string> FindViolations(IEnumerable<Type> scanned)
    {
        var violations = new List<string>();
        foreach (var type in scanned)
        {
            foreach (var referenced in ReferencedTypes(type).SelectMany(ExpandGenerics))
            {
                var ns = referenced.Namespace;
                if (ns is null)
                {
                    continue;
                }

                if (ForbiddenNamespaces.Any(prefix => ns.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    violations.Add($"{type.FullName} references {referenced.FullName}");
                    continue;
                }

                var root = ns.Split('.')[0];
                if (ForbiddenRoots.Contains(root))
                {
                    violations.Add($"{type.FullName} references {referenced.FullName}");
                }
            }
        }

        return violations;
    }

    private static IEnumerable<Type> ExpandGenerics(Type type)
    {
        yield return type;
        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var expanded in ExpandGenerics(argument))
            {
                yield return expanded;
            }
        }
    }

    private static bool IsScanned(Type type) =>
        type.Namespace is not null
        && !type.Namespace.Contains(".TestFixtures", StringComparison.Ordinal)
        && (type.Namespace.StartsWith("AIVoiceActing.Domain", StringComparison.Ordinal)
            || type.Namespace.StartsWith("AIVoiceActing.Ports", StringComparison.Ordinal));

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Dalamud-typed entries (plugin entry, composition root) may fail to load outside
            // the game; they are outside the scanned namespaces anyway.
            return ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
        }
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        if (type.BaseType is { } baseType)
        {
            yield return baseType;
        }

        foreach (var iface in type.GetInterfaces())
        {
            yield return iface;
        }

        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(All))
        {
            yield return field.FieldType;
        }

        foreach (var property in type.GetProperties(All))
        {
            yield return property.PropertyType;
            foreach (var parameter in property.GetIndexParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var @event in type.GetEvents(All))
        {
            if (@event.EventHandlerType is { } handler)
            {
                yield return handler;
            }
        }

        foreach (var ctor in type.GetConstructors(All))
        {
            foreach (var parameter in ctor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var method in type.GetMethods(All))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }

            foreach (var generic in method.GetGenericArguments())
            {
                yield return generic;
            }
        }

        foreach (var nested in type.GetNestedTypes(All))
        {
            foreach (var referenced in ReferencedTypes(nested))
            {
                yield return referenced;
            }
        }
    }
}
