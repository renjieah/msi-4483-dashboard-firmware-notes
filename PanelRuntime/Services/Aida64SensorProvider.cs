using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PanelRuntime.Models;

namespace PanelRuntime.Services;

public sealed partial class Aida64SensorProvider
{
    private const string MappingName = "AIDA64_SensorValues";
    private const int MaxReadBytes = 256 * 1024;

    public SensorSnapshot ReadSnapshot()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var count = (int)Math.Min(accessor.Capacity, MaxReadBytes);
            var bytes = new byte[count];
            accessor.ReadArray(0, bytes, 0, bytes.Length);
            var zero = Array.IndexOf(bytes, (byte)0);
            if (zero >= 0)
            {
                count = zero;
            }

            var text = Encoding.UTF8.GetString(bytes, 0, count);
            return Parse(text, online: true, error: null);
        }
        catch (Exception ex)
        {
            return new SensorSnapshot(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                false,
                ex.Message,
                new Dictionary<string, SensorValue>());
        }
    }

    private static SensorSnapshot Parse(string text, bool online, string? error)
    {
        var sensors = new Dictionary<string, SensorValue>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SensorRegex().Matches(text))
        {
            var kind = match.Groups["kind"].Value;
            var id = WebUtility.HtmlDecode(match.Groups["id"].Value);
            var label = WebUtility.HtmlDecode(match.Groups["label"].Value);
            var raw = WebUtility.HtmlDecode(match.Groups["value"].Value);
            double? number = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
            sensors[id] = new SensorValue(id, kind, label, raw, number);
        }

        return new SensorSnapshot(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            online,
            error,
            sensors);
    }

    [GeneratedRegex("<(?<kind>sys|temp|fan|cooler|volt|curr|pwr|power|duty)><id>(?<id>.*?)</id><label>(?<label>.*?)</label><value>(?<value>.*?)</value></\\k<kind>>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SensorRegex();
}
