namespace TrayTraffic.Core;

/// <summary>The road a route takes from one guidance instruction to the next.</summary>
/// <param name="OffsetMeters">How far along the route the road begins.</param>
/// <param name="Name">The street name, such as "West Elm Street"; null for unnamed roads and most ramps.</param>
/// <param name="Numbers">Road numbers such as "I-90 W" (signposted, with a direction) or "US-12".</param>
/// <param name="Motorway">A motorway or controlled-access road, which people call by number.</param>
public sealed record RouteRoad(double OffsetMeters, string? Name, string[] Numbers, bool Motorway = false);

/// <summary>
/// Names a route the way people describe one: its few longest roads in driving order, such as
/// "Elm St to I-94 E to I-90 W".
/// </summary>
public static class RouteNaming
{
    public const int MaxRoads = 3;

    /// <summary>Roads shorter than this share of the trip are turns along the way, not the route.</summary>
    private const double MinimumShare = 0.05;

    private static readonly HashSet<string> s_directions = new(StringComparer.OrdinalIgnoreCase)
    {
        "N", "S", "E", "W", "NE", "NW", "SE", "SW",
        "North", "South", "East", "West", "Northeast", "Northwest", "Southeast", "Southwest",
    };

    private static readonly Dictionary<string, string> s_streetTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Avenue"] = "Ave", ["Boulevard"] = "Blvd", ["Circle"] = "Cir", ["Court"] = "Ct", ["Drive"] = "Dr",
        ["Expressway"] = "Expy", ["Freeway"] = "Fwy", ["Highway"] = "Hwy", ["Lane"] = "Ln", ["Parkway"] = "Pkwy",
        ["Place"] = "Pl", ["Road"] = "Rd", ["Street"] = "St", ["Terrace"] = "Ter", ["Trail"] = "Trl",
    };

    /// <summary>Names a single route by its longest few roads in driving order.</summary>
    /// <returns>The route's name, or null when guidance named too little of it.</returns>
    public static string? Summarize(IReadOnlyList<RouteRoad> roads, double totalMeters) =>
        NameRoutes([(roads, totalMeters)])[0];

    /// <summary>
    /// Names each of a trip's routes. Alternatives often share their longest roads, so routes that
    /// would get the same name each work in the longest road the others don't take, in place of
    /// their shortest main road: "Elm St to Oak Ave to I-90 W" beside "Elm St to Pine Rd to I-90 W".
    /// A route whose roads are all on the others keeps the plain name, which then tells it apart.
    /// Routes on the same named roads throughout differ only in unnamed ramps or how long they stay
    /// on each, so the later ones in <paramref name="routes"/> are marked "(alt.)", "(alt. 2)".
    /// </summary>
    /// <returns>Each route's name, or null where guidance named too little of it.</returns>
    public static string?[] NameRoutes(IReadOnlyList<(IReadOnlyList<RouteRoad> Roads, double TotalMeters)> routes)
    {
        List<Stretch>[] stretches = [.. routes.Select(route => Stretches(route.Roads, route.TotalMeters))];
        string?[] names = [.. routes.Select((route, index) => Compose(stretches[index], route.TotalMeters, null))];
        foreach (int[] group in SameNames(names))
        {
            foreach (int index in group)
            {
                HashSet<string> others = new(group.Where(other => other != index)
                    .SelectMany(other => stretches[other]).Select(stretch => stretch.Label).OfType<string>(),
                    StringComparer.OrdinalIgnoreCase);
                if (stretches[index].Where(stretch => stretch.Label is { } label && !others.Contains(label))
                    .MaxBy(stretch => stretch.Meters) is { } distinct)
                {
                    names[index] = Compose(stretches[index], routes[index].TotalMeters, distinct);
                }
            }
        }
        foreach (int[] group in SameNames(names))
        {
            for (int position = 1; position < group.Length; position++)
            {
                names[group[position]] += position == 1 ? " (alt.)" : $" (alt. {position})";
            }
        }
        return names;
    }

    /// <summary>Groups the indexes of routes that share a name, each group in route order.</summary>
    private static int[][] SameNames(string?[] names) =>
        [.. names.Select((name, index) => (name, index))
            .Where(route => route.name is not null)
            .GroupBy(route => route.name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Select(route => route.index).ToArray())];

    /// <summary>
    /// Joins each instruction's road into continuous stretches: a street that changes from East to
    /// West, or an on-ramp and the motorway it joins.
    /// </summary>
    private static List<Stretch> Stretches(IReadOnlyList<RouteRoad> roads, double totalMeters)
    {
        if (!double.IsFinite(totalMeters) || totalMeters <= 0)
        {
            return [];
        }

        RouteRoad[] ordered = [.. roads
            .Where(road => double.IsFinite(road.OffsetMeters) && road.OffsetMeters >= 0 && road.OffsetMeters < totalMeters)
            .OrderBy(road => road.OffsetMeters)];
        List<Stretch> stretches = [];
        for (int index = 0; index < ordered.Length; index++)
        {
            RouteRoad road = ordered[index];
            double end = index + 1 < ordered.Length ? ordered[index + 1].OffsetMeters : totalMeters;
            Stretch next = new(road.OffsetMeters, end - road.OffsetMeters,
                road.Name is { } name ? ShortStreetName(name) : null, road.Numbers, road.Motorway);
            if (stretches.Count > 0 && stretches[^1].SameRoad(next))
            {
                stretches[^1] = stretches[^1].Extend(next);
            }
            else
            {
                stretches.Add(next);
            }
        }
        return stretches;
    }

    /// <summary>Names the longest few stretches in driving order.</summary>
    /// <param name="required">A road to name however short it is, in place of the shortest main road.</param>
    private static string? Compose(List<Stretch> stretches, double totalMeters, Stretch? required)
    {
        IEnumerable<Stretch> named = stretches
            .Where(stretch => stretch.Label is not null && stretch.Meters >= totalMeters * MinimumShare &&
                !string.Equals(stretch.Label, required?.Label, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(stretch => stretch.Meters)
            .DistinctBy(stretch => stretch.Label, StringComparer.OrdinalIgnoreCase)
            .Take(required is null ? MaxRoads : MaxRoads - 1);
        string[] labels = [.. (required is null ? named : named.Append(required))
            .OrderBy(stretch => stretch.Start)
            .Select(stretch => stretch.Label!)];
        return labels.Length > 0 ? string.Join(" to ", labels) : null;
    }

    /// <summary>
    /// Shortens a street name the way people say it: "West Elm Street" becomes "Elm St". A direction
    /// stays when it is the name itself, as in "West North Avenue" to "North Ave".
    /// </summary>
    public static string ShortStreetName(string name)
    {
        List<string> words = [.. name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        if (words.Count > 2 && s_directions.Contains(words[0].TrimEnd('.')))
        {
            words.RemoveAt(0);
        }
        if (words.Count > 2 && s_directions.Contains(words[^1].TrimEnd('.')))
        {
            words.RemoveAt(words.Count - 1);
        }
        if (words.Count > 1 && s_streetTypes.TryGetValue(words[^1], out string? abbreviation))
        {
            words[^1] = abbreviation;
        }
        return string.Join(' ', words);
    }

    /// <summary>
    /// "I-90 W" without its direction. Only a numbered road has one, so lettered routes such as
    /// "County N" keep their letter.
    /// </summary>
    private static string BaseNumber(string number)
    {
        int space = number.LastIndexOf(' ');
        return space > 0 && s_directions.Contains(number[(space + 1)..]) && number[..space].Any(char.IsAsciiDigit)
            ? number[..space]
            : number;
    }

    private sealed record Stretch(double Start, double Meters, string? Name, string[] Numbers, bool Motorway)
    {
        /// <summary>
        /// Motorways go by number, as do roads without a name; a signposted direction ("I-90 W") is
        /// preferred over the bare number.
        /// </summary>
        public string? Label
        {
            get
            {
                string? number = Numbers.FirstOrDefault(number => BaseNumber(number) != number) ?? Numbers.FirstOrDefault();
                return Motorway || Name is null ? number ?? Name : Name;
            }
        }

        /// <summary>
        /// The same street, or the same numbered road where one part has no name or is a motorway
        /// (an on-ramp joining it). Differently named streets that share a highway number stay apart.
        /// </summary>
        public bool SameRoad(Stretch next) =>
            (Name is not null && string.Equals(Name, next.Name, StringComparison.OrdinalIgnoreCase)) ||
            ((Name is null || next.Name is null || Motorway || next.Motorway) &&
                Numbers.Select(BaseNumber).Intersect(next.Numbers.Select(BaseNumber), StringComparer.OrdinalIgnoreCase).Any());

        public Stretch Extend(Stretch next) => this with
        {
            Meters = Meters + next.Meters,
            Name = Name ?? next.Name,
            Numbers = [.. Numbers.Union(next.Numbers, StringComparer.OrdinalIgnoreCase)],
            Motorway = Motorway || next.Motorway,
        };
    }
}
