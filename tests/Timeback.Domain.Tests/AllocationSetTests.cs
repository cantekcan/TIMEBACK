using FluentAssertions;
using Timeback.Domain.Common;
using Timeback.Domain.ValueObjects;

namespace Timeback.Domain.Tests;

public class AllocationSetTests
{
    private static readonly HashSet<string> Supported = ["GOLD", "BTC", "BIST100"];

    [Theory]
    [InlineData(50, 50, 0)]
    [InlineData(100, 0, 0)]
    [InlineData(34, 33, 33)]
    public void Accepts_weights_that_total_100(int a, int b, int c)
    {
        var act = () => AllocationSet.Create([("GOLD", a), ("BTC", b), ("BIST100", c)], Supported);
        act.Should().NotThrow();
    }

    [Fact]
    public void Rejects_total_other_than_100()
        => FluentActions.Invoking(() => AllocationSet.Create([("GOLD", 50), ("BTC", 40)], Supported))
            .Should().Throw<DomainException>().WithMessage("*total 100%*");

    [Fact]
    public void Rejects_duplicate_asset()
        => FluentActions.Invoking(() => AllocationSet.Create([("GOLD", 50), ("GOLD", 50)], Supported))
            .Should().Throw<DomainException>().WithMessage("*more than once*");

    [Fact]
    public void Rejects_unsupported_asset()
        => FluentActions.Invoking(() => AllocationSet.Create([("DOGE", 100)], Supported))
            .Should().Throw<DomainException>().WithMessage("*not supported*");

    [Fact]
    public void Rejects_negative_weight_via_percentage()
        => FluentActions.Invoking(() => AllocationSet.Create([("GOLD", -10), ("BTC", 110)], Supported))
            .Should().Throw<DomainException>();
}
