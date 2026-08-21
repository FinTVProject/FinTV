using System.Globalization;
using System.Text;

namespace FinTv.News;

public static class NewsAssBuilder
{
    public static string Build(
        string header,
        IReadOnlyList<NewsArticle> articles,
        int width,
        int height,
        int durationSeconds,
        bool showHeader)
    {
        var playX = width;
        var playY = height;
        var lines = new StringBuilder();
        if (showHeader && !string.IsNullOrWhiteSpace(header))
        {
            lines.Append(@"{\b1\c&H481DE1&}").Append(Escape(header)).Append(@"{\b0}\N\N");
        }

        foreach (var article in articles)
        {
            lines.Append(@"{\b1\c&HFFFFFF&}").Append(Escape(article.Title)).Append(@"{\b0}\N");
            if (!string.IsNullOrWhiteSpace(article.Summary))
            {
                lines.Append(@"{\c&HBBBBBB&}").Append(Escape(article.Summary)).Append(@"\N");
            }

            lines.Append(@"\N");
        }

        if (articles.Count == 0)
        {
            lines.Append(@"{\b1}Add RSS feeds on the News tab.");
        }

        var lineCount = Math.Max(4, articles.Count * 3 + 2);
        var blockHeight = lineCount * 36 + 80;
        var y1 = playY + blockHeight;
        var y2 = -blockHeight / 2;
        var x = playX / 2;
        var end = FormatAssTime(durationSeconds);
        var text = $"{{\\move({x},{y1},{x},{y2})}}" + lines;

        return $"""
            [Script Info]
            Title: FinTV News
            ScriptType: v4.00+
            WrapStyle: 0
            PlayResX: {playX}
            PlayResY: {playY}

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default, Arial, 28, &H00FFFFFF, &H000000FF, &H00000000, &H80000000, 0, 0, 0, 0, 100, 100, 0, 0, 1, 1, 0, 8, 48, 48, 40, 1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,{end},Default,,0,0,0,,{text}

            """;
    }

    public static string EscapeAssFilterPath(string path)
        => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("{", "(").Replace("}", ")").Replace("\r", "").Replace("\n", "\\N");

    private static string FormatAssTime(int seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(1, seconds));
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1:00}:{2:00}.00",
            (int)span.TotalHours,
            span.Minutes,
            span.Seconds);
    }
}
