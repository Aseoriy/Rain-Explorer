using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

public sealed class XwaylandScalingTests
{
    [Theory]
    [InlineData("""{"option":"xwayland:force_zero_scaling","int":1,"set":true}""", true)]
    [InlineData("""{"option":"xwayland:force_zero_scaling","int":0,"set":false}""", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ZeroScalingIsReadFromHyprctlOutput(string? json, bool expected) =>
        Assert.Equal(expected, XwaylandScaling.IsZeroScaling(json));

    [Fact]
    public void FocusedMonitorScaleWinsOverOtherMonitors()
    {
        const string json = """
            [{"name":"DP-2","scale":1.0,"focused":false},
             {"name":"DP-1","scale":1.25,"focused":true}]
            """;
        Assert.Equal(1.25, XwaylandScaling.FocusedMonitorScale(json));
    }

    [Fact]
    public void FirstMonitorScaleIsUsedWhenNoneIsFocused()
    {
        const string json = """[{"name":"DP-1","scale":1.5,"focused":false},{"name":"DP-2","scale":1.0,"focused":false}]""";
        Assert.Equal(1.5, XwaylandScaling.FocusedMonitorScale(json));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData(null)]
    public void MissingOrBadMonitorDataGivesNoScale(string? json) =>
        Assert.Null(XwaylandScaling.FocusedMonitorScale(json));
}
