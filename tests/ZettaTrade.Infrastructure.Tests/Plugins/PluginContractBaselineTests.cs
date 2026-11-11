namespace ZettaTrade.Infrastructure.Tests.Plugins;

using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;
using ZettaTrade.Plugins.Abstractions;

/// <summary>
/// The contract baseline (T-131): the public surface of the
/// <see cref="ZettaTradePluginAttribute"/>'s assembly is pinned line by
/// line — every public type's constructors, properties, fields and
/// declared methods, with the type names the reflection services render.
/// An accidental break (a lost type, a renamed member, an incompatible
/// signature) fails this test before any plugin author's build does.
/// </summary>
public sealed class PluginContractBaselineTests
{
    #region Facts

    [Fact]
    public void ThePublicSurfaceStaysPinned()
    {
        var actual = AssembleSurface();

        actual.Should().Equal(Pinned.Lines,
            "the versioned contract never breaks silently: a rewrite is a major-bump or a refusal.");
    }

    #endregion

    #region The Pinned Surface

    private static class Pinned
    {
        /// <summary>The contract 2.1.0 surface (T-168: the strategy seam rides the contract): types alphabetical, sections in the dump order (constructors, properties, fields, methods), members alphabetical inside each section.</summary>
        public static readonly string[] Lines =
        [
            "#ZettaTrade.Plugins.Abstractions.Extensions.PluginPanelDescriptor",
            "ZettaTrade.Plugins.Abstractions.Extensions.PluginPanelDescriptor: ctor(System.String, System.String, System.Func`2[System.IServiceProvider, System.Object])",
            "ZettaTrade.Plugins.Abstractions.Extensions.PluginPanelDescriptor: prop System.String PanelId",
            "ZettaTrade.Plugins.Abstractions.Extensions.PluginPanelDescriptor: prop System.String TitleResxKey",
            "ZettaTrade.Plugins.Abstractions.Extensions.PluginPanelDescriptor: prop System.Func`2[System.IServiceProvider, System.Object] WidgetFactory",
            "#ZettaTrade.Plugins.Abstractions.Extensions.PluginSettingsSectionDescriptor",
            "ZettaTrade.Plugins.Abstractions.Extensions.PluginSettingsSectionDescriptor: ctor(System.String, System.String)",
            "ZettaTrade.Plugins.Abstractions.Extensions.PluginSettingsSectionDescriptor: prop System.String SectionId",
            "ZettaTrade.Plugins.Abstractions.Extensions.PluginSettingsSectionDescriptor: prop System.String TitleResxKey",
            "#ZettaTrade.Plugins.Abstractions.Extensions.PluginToolbarItemDescriptor",
            "ZettaTrade.Plugins.Abstractions.Extensions.PluginToolbarItemDescriptor: ctor(System.String, System.String)",
            "ZettaTrade.Plugins.Abstractions.Extensions.PluginToolbarItemDescriptor: prop System.String ItemId",
            "ZettaTrade.Plugins.Abstractions.Extensions.PluginToolbarItemDescriptor: prop System.String LabelResxKey",
            "#ZettaTrade.Plugins.Abstractions.IIndicator",
            "ZettaTrade.Plugins.Abstractions.IIndicator: prop System.String Name",
            "ZettaTrade.Plugins.Abstractions.IIndicator: method Apply(System.Collections.Generic.IReadOnlyList`1[ZettaTrade.Plugins.Abstractions.IndicatorBar]) : System.Collections.Generic.IReadOnlyList`1[System.Nullable`1[System.Decimal]]",
            "#ZettaTrade.Plugins.Abstractions.IPluginContext",
            "ZettaTrade.Plugins.Abstractions.IPluginContext: prop Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory",
            "ZettaTrade.Plugins.Abstractions.IPluginContext: prop System.String PluginId",
            "ZettaTrade.Plugins.Abstractions.IPluginContext: method GetService() : T",
            "#ZettaTrade.Plugins.Abstractions.IZettaTradePlugin",
            "ZettaTrade.Plugins.Abstractions.IZettaTradePlugin: method ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection) : System.Void",
            "ZettaTrade.Plugins.Abstractions.IZettaTradePlugin: method InitializeAsync(ZettaTrade.Plugins.Abstractions.IPluginContext, System.Threading.CancellationToken) : System.Threading.Tasks.Task",
            "#ZettaTrade.Plugins.Abstractions.IndicatorBar",
            "ZettaTrade.Plugins.Abstractions.IndicatorBar: ctor(System.Decimal, System.Decimal, System.Decimal, System.Decimal)",
            "ZettaTrade.Plugins.Abstractions.IndicatorBar: prop System.Decimal Close",
            "ZettaTrade.Plugins.Abstractions.IndicatorBar: prop System.Decimal High",
            "ZettaTrade.Plugins.Abstractions.IndicatorBar: prop System.Decimal Low",
            "ZettaTrade.Plugins.Abstractions.IndicatorBar: prop System.Decimal Open",
            "#ZettaTrade.Plugins.Abstractions.ZettaTradePluginAttribute",
            "ZettaTrade.Plugins.Abstractions.ZettaTradePluginAttribute: ctor(System.String, System.String, System.String, System.String[])",
            "ZettaTrade.Plugins.Abstractions.ZettaTradePluginAttribute: prop System.Collections.Generic.IReadOnlyList`1[System.String] Dependencies",
            "ZettaTrade.Plugins.Abstractions.ZettaTradePluginAttribute: prop System.String DisplayName",
            "ZettaTrade.Plugins.Abstractions.ZettaTradePluginAttribute: prop System.String PluginId",
            "ZettaTrade.Plugins.Abstractions.ZettaTradePluginAttribute: prop System.String Version",
            "#ZettaTrade.Strategies.IStrategy",
            "ZettaTrade.Strategies.IStrategy: prop System.String Id",
            "ZettaTrade.Strategies.IStrategy: method Evaluate(ZettaTrade.Strategies.StrategyContext) : ZettaTrade.Strategies.StrategyStance",
            "#ZettaTrade.Strategies.StrategyContext",
            "ZettaTrade.Strategies.StrategyContext: ctor(System.String, System.String, System.Collections.Generic.IReadOnlyList`1[ZettaTrade.Plugins.Abstractions.IndicatorBar])",
            "ZettaTrade.Strategies.StrategyContext: prop System.Collections.Generic.IReadOnlyList`1[ZettaTrade.Plugins.Abstractions.IndicatorBar] ClosedBars",
            "ZettaTrade.Strategies.StrategyContext: prop System.String Interval",
            "ZettaTrade.Strategies.StrategyContext: prop System.String Symbol",
            "#ZettaTrade.Strategies.StrategyStance",
            "ZettaTrade.Strategies.StrategyStance: field ZettaTrade.Strategies.StrategyStance Flat",
            "ZettaTrade.Strategies.StrategyStance: field ZettaTrade.Strategies.StrategyStance Long",
            "ZettaTrade.Strategies.StrategyStance: field ZettaTrade.Strategies.StrategyStance Neutral",
            "ZettaTrade.Strategies.StrategyStance: field System.Int32 value__",
        ];
    }

    #endregion

    #region The Reflector

    /// <summary>
    /// The actual surface: one line per public type (prefixed `#`), then
    /// its constructors (by signature), properties (by rendering type and
    /// name) and declared special-name-free methods (by signature and the
    /// rendering return type). Nested and compiler-generated members never
    /// take a line.
    /// </summary>
    private static List<string> AssembleSurface()
    {
        var contractAssembly = typeof(ZettaTradePluginAttribute).Assembly;
        var actual = new List<string>();

        foreach (var type in contractAssembly
            .GetTypes()
            .Where(t => t.IsPublic && !CompilerTagged(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var name = type.FullName!;
            actual.Add($"#{name}");

            foreach (var constructor in type
                .GetConstructors()
                .OrderBy(c => Signature(c.GetParameters()), StringComparer.Ordinal))
            {
                actual.Add(
                    $"{name}: ctor({Signature(constructor.GetParameters())})");
            }

            foreach (var property in type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                actual.Add(
                    $"{name}: prop {Rendered(property.PropertyType)} {property.Name}");
            }

            foreach (var field in type
                .GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                actual.Add($"{name}: field {Rendered(field.FieldType)} {field.Name}");
            }

            foreach (var method in type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && !CompilerTagged(m))
                .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                actual.Add(
                    $"{name}: method {method.Name}({Signature(method.GetParameters())}) : {Rendered(method.ReturnType)}");
            }
        }

        return actual;
    }

    /// <summary>One parameter list: rendered type names, comma separated.</summary>
    private static string Signature(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(p => Rendered(p.ParameterType)));

    /// <summary>
    /// The type name the baseline renders: a version-free shape (generic
    /// parameters print as `T`, closed generics render their definition
    /// name over the recursively rendered arguments, everything else
    /// answers its loadable FullName). The pin must never burn when an
    /// assembly version moves underneath.
    /// </summary>
    private static string Rendered(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsConstructedGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var arguments = string.Join(", ", type.GenericTypeArguments.Select(Rendered));
            return $"{definition.FullName}[{arguments}]";
        }

        return type.FullName ?? type.ToString();
    }

    /// <summary>For records: a compiler-tagged shape (the identity members are pinning noise).</summary>
    private static bool CompilerTagged(MemberInfo member) =>
        member.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    #endregion
}
