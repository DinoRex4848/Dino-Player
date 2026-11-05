using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace DinoPlayer.MP.Playback;

/// <summary>
/// An audio mix in two parts. <see cref="Complex"/> (mpv's lavfi-complex) places every
/// track side by side as stereo pairs; <see cref="Filters"/> (mpv's af chain) applies
/// each track's gain and mixes them down to stereo. Unchecked tracks have gain 0.
/// </summary>
internal sealed record AudioMix(string Complex, string Filters, ImmutableArray<long> TrackIds, ImmutableArray<double> Gains)
{
    /// <summary>True when only gains differ, so they can change without rebuilding the graph.</summary>
    public bool HasSameTracks(AudioMix other) => TrackIds.SequenceEqual(other.TrackIds);
}

// Changing lavfi-complex makes mpv restart its audio decoders and refresh-seek,
// which stalls playback. Filters in the af chain accept af-command instead, so the
// gains live there and a volume change is a command, not a rebuild. Unchecked tracks
// stay in the graph at gain 0, so checking a track is a volume change too. The graph
// is only rebuilt when the file's tracks change.
internal static class AudioMixGraph
{
    /// <summary>mpv label of the af graph, used by af-command.</summary>
    public const string FilterLabel = "dinotracks";

    public static AudioMix Build(IEnumerable<AudioTrack> tracks)
    {
        var all = tracks.ToArray();
        var ids = all.Select(track => track.Id).ToImmutableArray();
        var gains = all.Select(track => track.Enabled ? Gain(track.VolumePercent) : 0).ToImmutableArray();
        if (all.Length == 0)
            return new(string.Empty, string.Empty, ids, gains);

        // Each track becomes a stereo pair: track i is in channels 2i and 2i+1.
        var complex = new StringBuilder();
        for (var index = 0; index < all.Length; index++)
        {
            complex.Append("[aid").Append(all[index].Id).Append("]aformat=channel_layouts=stereo");
            complex.Append(all.Length == 1 ? "[ao]" : $"[s{index}];");
        }
        if (all.Length > 1)
        {
            for (var index = 0; index < all.Length; index++) complex.Append("[s").Append(index).Append(']');
            complex.Append("amerge=inputs=").Append(all.Length).Append("[ao]");
        }

        var filters = new StringBuilder("@").Append(FilterLabel).Append(":lavfi=[");
        if (all.Length == 1)
            filters.Append(VolumeFilter(0, gains[0]));
        else
        {
            filters.Append("asplit=").Append(all.Length);
            for (var index = 0; index < all.Length; index++) filters.Append("[i").Append(index).Append(']');
            filters.Append(';');
            for (var index = 0; index < all.Length; index++)
            {
                filters.Append("[i").Append(index).Append("]pan=stereo|c0=c").Append(index * 2)
                    .Append("|c1=c").Append(index * 2 + 1).Append(',')
                    .Append(VolumeFilter(index, gains[index])).Append("[m").Append(index).Append("];");
            }
            for (var index = 0; index < all.Length; index++) filters.Append("[m").Append(index).Append(']');
            filters.Append("amix=inputs=").Append(all.Length).Append(":normalize=0");
        }
        filters.Append(']');
        return new(complex.ToString(), filters.ToString(), ids, gains);
    }

    /// <summary>Instance name of the volume filter for the track at <paramref name="index"/>.</summary>
    public static string VolumeFilterName(int index) => $"volume@t{index.ToString(CultureInfo.InvariantCulture)}";

    public static string FormatGain(double gain) => gain.ToString("0.###", CultureInfo.InvariantCulture);

    private static double Gain(double volumePercent) =>
        Math.Round(Math.Clamp(double.IsFinite(volumePercent) ? volumePercent : 0, 0, 150) / 100d, 3);

    private static string VolumeFilter(int index, double gain) => $"{VolumeFilterName(index)}={FormatGain(gain)}";
}
