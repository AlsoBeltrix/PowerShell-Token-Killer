using System.Reflection;
using PtkMcpGuardian.Ownership;
using PtkSharedContracts;

namespace PtkMcpGuardian.Tests;

public sealed class FrozenSessionCatalogTests
{
    [Fact]
    public void Snapshot_is_ordinally_sorted_immutable_and_deep_copied()
    {
        var zeta = Template("zeta", RepeatedHex("11"));
        var alpha = Template("alpha", RepeatedHex("22"));
        IFrozenSessionCatalog catalog = new FrozenSessionCatalog([zeta, alpha]);

        var first = catalog.Snapshot();
        var second = catalog.Snapshot();

        Assert.Equal(["alpha", "zeta"], first.Select(value => value.Name.Value));
        Assert.NotSame(alpha, first[0]);
        Assert.NotSame(zeta, first[1]);
        Assert.NotSame(first[0], second[0]);
        var mutableView = Assert.IsAssignableFrom<IList<RecoveryTemplate>>(first);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = zeta);

        var returnedBootstrap = first[0].GetBootstrapBytes();
        returnedBootstrap[0] ^= 0xff;
        Assert.Equal(
            second[0].GetBootstrapBytes(),
            catalog.Snapshot()[0].GetBootstrapBytes());
    }

    [Fact]
    public void TryGet_is_ordinal_and_returns_a_fresh_copy_only_when_found()
    {
        IFrozenSessionCatalog catalog = new FrozenSessionCatalog(
            [Template("alpha", RepeatedHex("33"))]);

        Assert.True(catalog.TryGet(new CanonicalAlias("alpha"), out var first));
        Assert.True(catalog.TryGet(new CanonicalAlias("alpha"), out var second));
        Assert.NotSame(first, second);
        AssertTemplateEquivalent(first, second);

        Assert.False(catalog.TryGet(new CanonicalAlias("missing"), out var missing));
        Assert.Null(missing);
        Assert.False(catalog.TryGet(default, out missing));
        Assert.Null(missing);
    }

    [Fact]
    public void Digest_uses_the_frozen_v1_domain_and_ordinal_name_order()
    {
        var zeta = Template("zeta", RepeatedHex("11"));
        var alpha = Template("alpha", RepeatedHex("22"));

        var forward = new FrozenSessionCatalog([zeta, alpha]);
        var reverse = new FrozenSessionCatalog([alpha, zeta]);
        var empty = new FrozenSessionCatalog([]);

        Assert.Equal(
            "38670f14569399539b4224ab9363fafefdd8bb745dfd49352343cc95925caafc",
            forward.CatalogDigest.Value);
        Assert.Equal(forward.CatalogDigest, reverse.CatalogDigest);
        Assert.Equal(
            "c2d5c00a1d175536658b9ed55cb34dde2740423732f22f7e2ba664afe2d252b9",
            empty.CatalogDigest.Value);
    }

    [Fact]
    public void Duplicate_names_and_catalogs_over_the_contract_limit_are_rejected()
    {
        var duplicate = Template("duplicate", RepeatedHex("44"));
        Assert.Throws<ArgumentException>(() =>
            new FrozenSessionCatalog([duplicate, duplicate]));

        var oversized = Enumerable.Range(0, ContractLimits.MaximumTemplates + 1)
            .Select(index => Template($"t{index:D3}", RepeatedHex("55")));
        Assert.Throws<ArgumentException>(() => new FrozenSessionCatalog(oversized));
    }

    [Fact]
    public void Assembly_internal_surface_is_exact_read_only_and_has_no_reload_hook()
    {
        var interfaceProperties = typeof(IFrozenSessionCatalog)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var interfaceMethods = typeof(IFrozenSessionCatalog)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal);
        var assemblySurfaceMethods = typeof(FrozenSessionCatalog)
            .GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Where(method => method.IsPublic || method.IsAssembly || method.IsFamilyOrAssembly)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal);
        var concreteFields = typeof(FrozenSessionCatalog)
            .GetFields(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
        var assemblyConstructors = typeof(FrozenSessionCatalog)
            .GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Where(constructor => !constructor.IsPrivate)
            .ToArray();

        var property = Assert.Single(interfaceProperties);
        Assert.Equal(nameof(IFrozenSessionCatalog.CatalogDigest), property.Name);
        Assert.False(property.CanWrite);
        Assert.Equal(["Snapshot", "TryGet"], interfaceMethods);
        Assert.Equal(["Snapshot", "TryGet", "get_CatalogDigest"], assemblySurfaceMethods);
        var constructor = Assert.Single(assemblyConstructors);
        Assert.True(constructor.IsAssembly);
        var parameter = Assert.Single(constructor.GetParameters());
        Assert.Equal(typeof(IEnumerable<RecoveryTemplate>), parameter.ParameterType);
        Assert.NotEmpty(concreteFields);
        Assert.All(concreteFields, field =>
        {
            Assert.True(field.IsPrivate, $"Instance field '{field.Name}' leaks into the assembly surface.");
            Assert.True(field.IsInitOnly, $"Instance field '{field.Name}' is mutable.");
        });
    }

    private static RecoveryTemplate Template(string name, string templateDigest)
    {
        var bootstrap = System.Text.Encoding.UTF8.GetBytes($"Write-Output '{name}'");
        return new RecoveryTemplate(
            new CanonicalAlias(name),
            $"Description for {name}",
            30,
            $"target-{name}",
            $"identity-{name}",
            allowColdBackground: false,
            new Sha256Digest(templateDigest),
            Sha256Digest.Compute(bootstrap),
            bootstrap);
    }

    private static string RepeatedHex(string pair) =>
        string.Concat(Enumerable.Repeat(pair, 32));

    private static void AssertTemplateEquivalent(
        RecoveryTemplate expected,
        RecoveryTemplate actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.StartupTimeoutSeconds, actual.StartupTimeoutSeconds);
        Assert.Equal(expected.DeclaredTarget, actual.DeclaredTarget);
        Assert.Equal(expected.DeclaredIdentity, actual.DeclaredIdentity);
        Assert.Equal(expected.AllowColdBackground, actual.AllowColdBackground);
        Assert.Equal(expected.TemplateDigest, actual.TemplateDigest);
        Assert.Equal(expected.BootstrapDigest, actual.BootstrapDigest);
        Assert.Equal(expected.GetBootstrapBytes(), actual.GetBootstrapBytes());
    }
}
