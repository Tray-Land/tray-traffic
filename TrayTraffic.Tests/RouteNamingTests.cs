using TrayTraffic.Core;

namespace TrayTraffic.Tests;

public class RouteNamingTests
{
    [Theory]
    [InlineData("West Elm Street", "Elm St")]
    [InlineData("N 92nd Street", "92nd St")]
    [InlineData("West North Avenue", "North Ave")]
    [InlineData("West Avenue", "West Ave")]
    [InlineData("Main Street East", "Main St")]
    [InlineData("North Doctor Martin Luther King Drive", "Doctor Martin Luther King Dr")]
    [InlineData("Lake Rd", "Lake Rd")]
    [InlineData("Broadway", "Broadway")]
    [InlineData("Avenue of the Americas", "Avenue of the Americas")]
    public void ShortStreetName_DropsDirectionsAndAbbreviatesTheType(string name, string expected) =>
        Assert.Equal(expected, RouteNaming.ShortStreetName(name));

    [Fact]
    public void Summarize_NamesTheThreeLongestRoadsInDrivingOrder()
    {
        RouteRoad[] roads =
        [
            new(0, "Example Court", []),
            new(200, "West Elm Street", []),
            new(4_000, "North Oak Avenue", ["US-12"]),
            new(4_500, null, ["I-94 E"]),
            new(5_000, "Lakeshore Freeway", ["I-94"], Motorway: true),
            new(15_000, "Pine Road", []),
            new(17_000, "Example Court", []),
        ];
        Assert.Equal("Elm St to I-94 E to Pine Rd", RouteNaming.Summarize(roads, 17_300));
    }

    [Fact]
    public void Summarize_JoinsAStreetThatChangesDirectionAcrossTheGrid()
    {
        RouteRoad[] roads = [new(0, "East Elm Street", []), new(3_000, "West Elm Street", []), new(9_000, "Oak Avenue", [])];
        Assert.Equal("Elm St to Oak Ave", RouteNaming.Summarize(roads, 10_000));
    }

    [Fact]
    public void Summarize_OnRampJoinsTheMotorwayAndKeepsTheSignedDirection()
    {
        RouteRoad[] roads =
        [
            new(0, "Elm Street", []),
            new(1_000, null, ["WI-175 S"]),
            new(1_300, "State Highway 175", ["WI-175"], Motorway: true),
            new(2_000, null, ["I-90 W"], Motorway: true),
            new(2_500, null, ["I-90 W"], Motorway: true),
        ];
        Assert.Equal("Elm St to WI-175 S to I-90 W", RouteNaming.Summarize(roads, 6_000));
    }

    [Fact]
    public void Summarize_DifferentStreetsSharingAHighwayNumberStayApart()
    {
        RouteRoad[] roads = [new(0, "Elm Street", ["US-12"]), new(4_000, "Oak Avenue", ["US-12"])];
        Assert.Equal("Elm St to Oak Ave", RouteNaming.Summarize(roads, 8_000));
    }

    [Fact]
    public void Summarize_LetteredRoutesAreNotReadAsDirections()
    {
        RouteRoad[] roads = [new(0, null, ["County N"]), new(4_000, null, ["County E"])];
        Assert.Equal("County N to County E", RouteNaming.Summarize(roads, 8_000));
    }

    [Fact]
    public void Summarize_ShortTurnsAndRepeatedRoadsAreLeftOut()
    {
        RouteRoad[] roads =
        [
            new(0, "Elm Street", []),
            new(5_000, "Oak Avenue", []),
            new(5_200, "Elm Street", []),
            new(9_000, "Pine Road", []),
        ];
        Assert.Equal("Elm St", RouteNaming.Summarize(roads, 9_300));
    }

    [Fact]
    public void NameRoutes_SameMainRoads_EachWorksInTheRoadOnlyItTakes()
    {
        RouteRoad[] viaOak = [new(0, "Elm Street", []), new(4_000, "Oak Avenue", []), new(4_300, null, ["I-90 W"], true)];
        RouteRoad[] viaPine = [new(0, "Elm Street", []), new(4_000, "Pine Road", []), new(4_200, null, ["I-90 W"], true)];
        RouteRoad[] elsewhere = [new(0, "Lake Drive", [])];
        Assert.Equal("Elm St to I-90 W", RouteNaming.Summarize(viaOak, 10_000));
        Assert.Equal("Elm St to I-90 W", RouteNaming.Summarize(viaPine, 10_000));

        Assert.Equal(["Elm St to Oak Ave to I-90 W", "Elm St to Pine Rd to I-90 W", "Lake Dr"],
            RouteNaming.NameRoutes([(viaOak, 10_000), (viaPine, 10_000), (elsewhere, 10_000)]).AsEnumerable());
    }

    [Fact]
    public void NameRoutes_RouteWhoseRoadsAreAllOnAnother_KeepsThePlainName()
    {
        RouteRoad[] direct = [new(0, "Elm Street", []), new(4_000, "Main Street", []), new(5_000, null, ["I-90 W"], true)];
        RouteRoad[] detour =
        [
            new(0, "Elm Street", []),
            new(4_000, "Main Street", []),
            new(4_800, "Pine Road", []),
            new(5_000, null, ["I-90 W"], true),
        ];

        // The detour's own road takes the place of its shortest main road, Main St.
        Assert.Equal(["Elm St to Main St to I-90 W", "Elm St to Pine Rd to I-90 W"],
            RouteNaming.NameRoutes([(direct, 10_000), (detour, 10_000)]).AsEnumerable());
    }

    [Fact]
    public void NameRoutes_SameNamedRoadsThroughout_MarksTheLaterOnesAlt()
    {
        RouteRoad[] first = [new(0, "Elm Street", []), new(5_000, null, []), new(5_100, "Oak Avenue", [])];
        RouteRoad[] second = [new(0, "Elm Street", []), new(5_000, null, []), new(5_300, "Oak Avenue", [])];
        RouteRoad[] third = [new(0, "Elm Street", []), new(5_000, null, []), new(5_500, "Oak Avenue", [])];
        Assert.Equal(["Elm St to Oak Ave", "Elm St to Oak Ave (alt.)", "Elm St to Oak Ave (alt. 2)"],
            RouteNaming.NameRoutes([(first, 10_000), (second, 10_000), (third, 10_000)]).AsEnumerable());
    }

    [Fact]
    public void NameRoutes_RouteWhoseRoadsAreAllOnAnother_AndATwinOfIt_OnlyTheTwinIsAlt()
    {
        RouteRoad[] direct = [new(0, "Elm Street", []), new(5_000, null, ["I-90 W"], true)];
        RouteRoad[] detour = [new(0, "Elm Street", []), new(5_000, "Pine Road", []), new(5_200, null, ["I-90 W"], true)];
        Assert.Equal(["Elm St to I-90 W", "Elm St to Pine Rd to I-90 W", "Elm St to I-90 W (alt.)"],
            RouteNaming.NameRoutes([(direct, 10_000), (detour, 10_000), (direct, 10_000)]).AsEnumerable());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public void Summarize_NoNamedRoadsOrLength_HasNoName(double totalMeters)
    {
        Assert.Null(RouteNaming.Summarize([new(0, null, []), new(500, null, [])], 1_000));
        Assert.Null(RouteNaming.Summarize([new(0, "Elm Street", [])], totalMeters));
        Assert.Null(RouteNaming.Summarize([], 1_000));
    }
}
