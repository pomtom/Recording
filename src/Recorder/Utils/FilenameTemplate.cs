using System.Globalization;
using System.IO;
using System.Text;

namespace Recorder.Utils;

/// <summary>
/// Expands the user's filename template into a recording's file stem.
/// </summary>
/// <remarks>
/// <para>A deliberately small, closed token set rather than a raw <see cref="DateTime"/> format
/// string. Handing the format string straight to <c>ToString</c> would look more flexible, but the
/// characters people reach for — <c>:</c> and <c>/</c> in particular — are exactly the ones Windows
/// forbids in a filename, so the natural attempt would fail in a way that is hard to explain. A
/// token set sidesteps that entirely and still covers what anyone actually wants.</para>
///
/// <para>Expansion is total: an unknown token is left alone as literal text, illegal characters are
/// stripped, and a template that ends up producing nothing usable falls back to the default scheme
/// rather than throwing on the way to starting a recording.</para>
/// </remarks>
public static class FilenameTemplate
{
    /// <summary>The original hard-coded scheme, used whenever a template cannot be honoured.</summary>
    public const string Default = "{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}";

    /// <summary>Tokens shown in the settings hint, in the order they are most likely to be wanted.</summary>
    public static readonly IReadOnlyList<string> Tokens =
    [
        "{yyyy}", "{MM}", "{dd}", "{HH}", "{mm}", "{ss}", "{date}", "{time}", "{monitor}", "{counter}",
    ];

    /// <summary>
    /// Expands <paramref name="template"/> for one recording.
    /// </summary>
    /// <param name="counter">Value for <c>{counter}</c>; the de-duplication index.</param>
    /// <returns>A file stem with no extension, never empty.</returns>
    public static string Expand(string? template, DateTime localTime, string? monitorLabel, int counter)
    {
        var expanded = ExpandCore(template, localTime, monitorLabel, counter);
        var sanitized = Sanitize(expanded);

        return string.IsNullOrWhiteSpace(sanitized)
            ? Sanitize(ExpandCore(Default, localTime, monitorLabel, counter))
            : sanitized;
    }

    /// <summary>True if the template expands to something that can be used as a filename.</summary>
    public static bool IsUsable(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return false;

        var probe = Sanitize(ExpandCore(template, new DateTime(2026, 1, 2, 3, 4, 5), "Display 1", 1));
        return !string.IsNullOrWhiteSpace(probe);
    }

    private static string ExpandCore(string? template, DateTime localTime, string? monitorLabel, int counter)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;

        var invariant = CultureInfo.InvariantCulture;
        var builder = new StringBuilder(template.Length + 24);

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                builder.Append(template[i]);
                continue;
            }

            var end = template.IndexOf('}', i + 1);
            if (end < 0)
            {
                // An unterminated brace is literal text; the rest of the template is too.
                builder.Append(template, i, template.Length - i);
                break;
            }

            var token = template.Substring(i + 1, end - i - 1);
            var replacement = token switch
            {
                "yyyy" => localTime.ToString("yyyy", invariant),
                "yy" => localTime.ToString("yy", invariant),
                "MM" => localTime.ToString("MM", invariant),
                "dd" => localTime.ToString("dd", invariant),
                "HH" => localTime.ToString("HH", invariant),
                "mm" => localTime.ToString("mm", invariant),
                "ss" => localTime.ToString("ss", invariant),
                "date" => localTime.ToString("yyyy-MM-dd", invariant),
                "time" => localTime.ToString("HH-mm-ss", invariant),
                "monitor" => monitorLabel ?? string.Empty,
                "counter" => counter.ToString(invariant),

                // Unknown token: keep it verbatim so a typo is visible in the filename rather than
                // silently vanishing.
                _ => "{" + token + "}",
            };

            builder.Append(replacement);
            i = end;
        }

        return builder.ToString();
    }

    /// <summary>Removes everything Windows will not accept in a filename.</summary>
    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (Array.IndexOf(invalid, c) >= 0) continue;
            if (char.IsControl(c)) continue;
            builder.Append(c);
        }

        // Trailing dots and spaces are legal to write but are silently trimmed by the shell, which
        // makes the file hard to open afterwards.
        return builder.ToString().Trim().TrimEnd('.', ' ');
    }
}
