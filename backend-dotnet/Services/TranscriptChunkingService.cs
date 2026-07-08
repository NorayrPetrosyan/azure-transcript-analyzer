using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed class TranscriptChunkingService(ConfigurationService configuration)
{
    public IReadOnlyList<TranscriptChunk> Split(string transcript, bool includeOverlap = true)
    {
        var chunkSize = Math.Max(500, configuration.TranscriptChunkSize);
        var overlapLines = includeOverlap
            ? Math.Clamp(configuration.TranscriptChunkOverlap, 0, 25)
            : 0;

        if (transcript.Length <= chunkSize)
        {
            return [new TranscriptChunk { Index = 0, Start = 0, End = transcript.Length, Text = transcript }];
        }

        var lineChunks = SplitByLines(transcript, chunkSize, overlapLines);
        if (lineChunks.Count > 0)
        {
            return lineChunks;
        }

        var chunks = new List<TranscriptChunk>();
        var start = 0;

        while (start < transcript.Length)
        {
            var targetEnd = Math.Min(start + chunkSize, transcript.Length);
            var end = targetEnd == transcript.Length
                ? targetEnd
                : FindBoundary(transcript, start, targetEnd);

            if (end <= start)
            {
                end = targetEnd;
            }

            chunks.Add(new TranscriptChunk
            {
                Index = chunks.Count,
                Start = start,
                End = end,
                Text = transcript[start..end].Trim()
            });

            if (end >= transcript.Length)
            {
                break;
            }

            start = end;
        }

        return chunks
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
            .ToList();
    }

    private static IReadOnlyList<TranscriptChunk> SplitByLines(string transcript, int chunkSize, int overlapLines)
    {
        var lineMatches = transcript.Split('\n');
        if (lineMatches.Length <= 1)
        {
            return [];
        }

        var chunks = new List<TranscriptChunk>();
        var currentLines = new List<string>();
        var currentStart = 0;
        var cursor = 0;

        foreach (var rawLine in lineMatches)
        {
            var line = rawLine.TrimEnd('\r');
            var lineWithNewline = cursor + rawLine.Length < transcript.Length ? $"{line}\n" : line;

            if (currentLines.Count > 0 && CurrentLength(currentLines) + lineWithNewline.Length > chunkSize)
            {
                AddLineChunk(chunks, currentLines, currentStart, cursor);

                var overlap = overlapLines > 0
                    ? currentLines.TakeLast(overlapLines).ToList()
                    : [];

                currentStart = Math.Max(0, cursor - CurrentLength(overlap));
                currentLines = overlap;
            }

            if (lineWithNewline.Length > chunkSize && currentLines.Count == 0)
            {
                return [];
            }

            currentLines.Add(lineWithNewline);
            cursor += rawLine.Length + (cursor + rawLine.Length < transcript.Length ? 1 : 0);
        }

        AddLineChunk(chunks, currentLines, currentStart, transcript.Length);

        return chunks
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
            .ToList();
    }

    private static void AddLineChunk(List<TranscriptChunk> chunks, IReadOnlyList<string> lines, int start, int end)
    {
        var text = string.Concat(lines).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        chunks.Add(new TranscriptChunk
        {
            Index = chunks.Count,
            Start = start,
            End = end,
            Text = text
        });
    }

    private static int CurrentLength(IEnumerable<string> lines) => lines.Sum(line => line.Length);

    private static int FindBoundary(string text, int start, int targetEnd)
    {
        var minBoundary = start + Math.Max(100, (targetEnd - start) / 2);
        var boundaryChars = new[] { '\n', '.', '!', '?', '։', ' ' };

        for (var i = targetEnd - 1; i >= minBoundary; i--)
        {
            if (boundaryChars.Contains(text[i]))
            {
                return i + 1;
            }
        }

        return targetEnd;
    }
}
