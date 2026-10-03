namespace TrayTraffic.Core;

public static class MapTiles
{
    public static string BasemapTemplate(bool dark) =>
        "https://api.tomtom.com/maps/orbis/display/raster/tile/{z}/{x}/{y}" +
        $"?apiVersion=2&tileSize=256&style=street-{(dark ? "dark" : "light")}";

    public static string TrafficTemplate(bool dark) =>
        "https://api.tomtom.com/maps/orbis/traffic/flow/raster/tile/{z}/{x}/{y}" +
        $"?apiVersion=2&tileSize=256&style={(dark ? "dark" : "light")}";

    public static (int X, int Y) Coordinate(GeoPoint location, int zoom)
    {
        if (!location.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(location));
        }
        if (zoom is < 0 or > 22)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom));
        }

        int size = 1 << zoom;
        double latitude = Math.Clamp(location.Latitude, -85.05112878, 85.05112878) * Math.PI / 180;
        int x = (int)Math.Floor((location.Longitude + 180) / 360 * size);
        int y = (int)Math.Floor((1 - Math.Log(Math.Tan(latitude) + 1 / Math.Cos(latitude)) / Math.PI) / 2 * size);
        return (Math.Clamp(x, 0, size - 1), Math.Clamp(y, 0, size - 1));
    }

    public static string At(string template, GeoPoint location, int zoom = 12)
    {
        (int x, int y) = Coordinate(location, zoom);
        return template.Replace("{z}", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
