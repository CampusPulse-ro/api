using CampusPulse.Domain;

namespace CampusPulse.Domain.Tests;

/// <summary>Placeholder so the project runs in CI; replace with real tests as entities land.</summary>
public sealed class AssemblyMarkerTests
{
    [Fact]
    public void Domain_assembly_has_no_web_or_ef_dependencies()
    {
        var referenced = typeof(AssemblyMarker).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? "");

        Assert.DoesNotContain(referenced, n =>
            n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }
}
