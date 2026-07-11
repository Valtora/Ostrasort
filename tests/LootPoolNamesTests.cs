using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

public class LootPoolNamesTests
{
    private static LootPoolNames Index(params (string Pool, string Friendly)[] refs) =>
        new(refs.Select(r => new CoreIndexCache.LootRef(r.Pool, r.Friendly)));

    [Fact]
    public void Describe_PrefersReferencingObjectsFriendlyName()
    {
        var idx = Index(("CNDOLKioskEmbassyOKLG", "Embassy Services: K-Leg, 1036-Ganymed"));
        Assert.Equal("Embassy Services: K-Leg, 1036-Ganymed", idx.Describe("CNDOLKioskEmbassyOKLG"));
    }

    [Fact]
    public void Describe_MultipleReferencers_SummarisesTheRest()
    {
        var idx = Index(("CNDOLKioskCargo01", "Cargo Kiosk (BCER)"),
                        ("CNDOLKioskCargo01", "Cargo Kiosk (BCRS)"));
        Assert.Equal("Cargo Kiosk (BCER) (+1 more)", idx.Describe("CNDOLKioskCargo01"));
    }

    [Fact]
    public void Describe_FallsBackToPatternForShopLikeIds()
    {
        var idx = LootPoolNames.Empty;
        Assert.Equal("OKLG medical kiosk inventory", idx.Describe("ItmOKLGMedKioskInv"));
        Assert.Equal("cargo kiosk inventory", idx.Describe("ItmCargoKioskInv"));
        Assert.Equal("embassy kiosk inventory", idx.Describe("CNDOLKioskEmbassy"));
    }

    [Fact]
    public void Describe_ReturnsNullWhenNothingDerivable()
    {
        // a non-shop pool with no referencer stays as its raw id (null = leave it)
        Assert.Null(LootPoolNames.Empty.Describe("ItmPocketsPouchSmx4"));
        Assert.Null(LootPoolNames.Empty.Describe("WOUNDArmLowerL"));
    }
}
