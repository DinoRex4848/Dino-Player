using System.Globalization;
using System.Text;

namespace DinoPlayer.MP.Playback;

internal static class AudioMixGraph
{
    public static string Build(IEnumerable<AudioTrack> tracks)
    {
        var enabled = tracks.Where(track => track.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            return string.Empty;
        }

        var graph = new StringBuilder();
        for (var index = 0; index < enabled.Length; index++)
        {
            var track = enabled[index];
            var gain = Math.Clamp(track.VolumePercent, 0, 150) / 100d;
            graph.Append("[aid").Append(track.Id).Append("]volume=")
                .Append(gain.ToString("0.###", CultureInfo.InvariantCulture))
                .Append("[mix").Append(index).Append("];");
        }

        if (enabled.Length == 1)
        {
            graph.Append("[mix0]anull[ao]");
            return graph.ToString();
        }

        for (var index = 0; index < enabled.Length; index++)
        {
            graph.Append("[mix").Append(index).Append(']');
        }

        graph.Append("amix=inputs=").Append(enabled.Length).Append(":normalize=0[ao]");
        return graph.ToString();
    }
}
