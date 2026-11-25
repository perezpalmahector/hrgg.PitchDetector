using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PitchDetectorApp.Audio
{
    public sealed class SongItem
    {
        public int Id { get; init; }
        public string Title { get; init; } = "Sin título";
        public string Tonality { get; init; } = "";
        public string Metric { get; init; } = "";
        public IReadOnlyList<SongLineItem> Lines { get; init; } = Array.Empty<SongLineItem>();
    }

    public sealed class SongLineItem
    {
        public int MelodyLineId { get; init; }
        public string SectionTitle { get; init; } = "";
        public string Chords { get; init; } = "";
        public string MelodyRaw { get; init; } = "";
        public string Lyrics { get; init; } = "";
        public List<string> NoteTokens { get; init; } = new();
        public List<double> NoteDurations { get; init; } = new();
    }

    public readonly record struct MelodyStepFeedback(
        int CurrentNoteIndex,
        string CurrentTargetNoteName,
        string DetectedNoteText,
        string Instruction,
        double NoteHoldProgress,
        bool LineCompleted,
        bool SongCompleted
    );

    public sealed class SongMelodyTrainer
    {
        private static readonly string[] NoteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        private static readonly Dictionary<string, int> BaseSemitones = new(StringComparer.OrdinalIgnoreCase)
        {
            ["C"] = 0,
            ["C#"] = 1,
            ["DB"] = 1,
            ["D"] = 2,
            ["D#"] = 3,
            ["EB"] = 3,
            ["E"] = 4,
            ["F"] = 5,
            ["F#"] = 6,
            ["GB"] = 6,
            ["G"] = 7,
            ["G#"] = 8,
            ["AB"] = 8,
            ["A"] = 9,
            ["A#"] = 10,
            ["BB"] = 10,
            ["B"] = 11
        };

        public IReadOnlyList<SongItem> Songs { get; private set; } = Array.Empty<SongItem>();
        public int CurrentSongIndex { get; private set; }
        public int CurrentLineIndex { get; private set; }
        public int CurrentNoteIndex { get; private set; }
        public int BaseOctave { get; set; } = 3;
        public bool IgnoreOctaveWhenSinging { get; set; } = true;

        private string? _loadedJsonPath;
        private JsonNode? _rootJsonNode;

        private double _holdSeconds;
        private int _missedFrames;

        private const double RequiredHoldPerNote = 0.35;
        private const double CentsTolerance = 45.0;

        public SongItem? CurrentSong =>
            (Songs.Count > 0 && CurrentSongIndex >= 0 && CurrentSongIndex < Songs.Count)
                ? Songs[CurrentSongIndex]
                : null;

        public IReadOnlyList<SongLineItem> Lines => CurrentSong?.Lines ?? Array.Empty<SongLineItem>();

        public SongLineItem? CurrentLine =>
            (Lines.Count > 0 && CurrentLineIndex >= 0 && CurrentLineIndex < Lines.Count)
                ? Lines[CurrentLineIndex]
                : null;

        public void LoadFromFile(string filePath)
        {
            _loadedJsonPath = filePath;
            string json = File.ReadAllText(filePath);
            LoadFromJson(json);
        }

        public void LoadFromJson(string json)
        {
            _rootJsonNode = JsonNode.Parse(json);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            using var doc = JsonDocument.Parse(json);
            List<SongDto> dtos = new();

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                dtos = JsonSerializer.Deserialize<List<SongDto>>(json, options) ?? new();
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("songs", out var songsProp) && songsProp.ValueKind == JsonValueKind.Array)
                    dtos = JsonSerializer.Deserialize<List<SongDto>>(songsProp.GetRawText(), options) ?? new();
                else
                {
                    var single = JsonSerializer.Deserialize<SongDto>(json, options);
                    if (single != null) dtos.Add(single);
                }
            }

            if (dtos.Count == 0)
                throw new InvalidOperationException("No se encontraron cantos válidos en el JSON.");

            Songs = dtos.Select(ParseSongDto).ToList();
            SelectSong(0);
        }
        public void SetNoteDuration(int noteIndex, double seconds)
        {
            var line = CurrentLine;
            if (line == null || noteIndex < 0 || noteIndex >= line.NoteDurations.Count) return;
            line.NoteDurations[noteIndex] = Math.Clamp(Math.Round(seconds, 2), 0.15, 3.00);
        }

        public void ApplyUniformDurationToCurrentLine(double seconds)
        {
            var line = CurrentLine;
            if (line == null) return;
            double clamped = Math.Clamp(Math.Round(seconds, 2), 0.15, 3.00);
            for (int i = 0; i < line.NoteDurations.Count; i++)
                line.NoteDurations[i] = clamped;
        }

        public void SaveDurationsToJsonFile(string? targetPath = null)
        {
            string? path = targetPath ?? _loadedJsonPath;
            if (string.IsNullOrWhiteSpace(path) || _rootJsonNode == null)
                throw new InvalidOperationException("No hay un archivo songs.json vinculado para guardar.");

            var durationsByLineId = Songs
                .SelectMany(s => s.Lines)
                .Where(l => l.MelodyLineId > 0)
                .GroupBy(l => l.MelodyLineId)
                .ToDictionary(g => g.Key, g => g.First().NoteDurations);

            JsonArray? songsArray = _rootJsonNode as JsonArray
                ?? _rootJsonNode["songs"] as JsonArray;

            if (songsArray == null && _rootJsonNode is JsonObject singleObj)
                songsArray = new JsonArray(singleObj);

            if (songsArray != null)
            {
                foreach (var songNode in songsArray.OfType<JsonObject>())
                {
                    if (songNode["sections"] is not JsonArray sections) continue;
                    foreach (var secNode in sections.OfType<JsonObject>())
                    {
                        if (secNode["lines"] is not JsonArray lines) continue;
                        foreach (var lineNode in lines.OfType<JsonObject>())
                        {
                            int id = lineNode["id"]?.GetValue<int>() ?? 0;
                            string type = lineNode["type"]?.GetValue<string>() ?? "";
                            if (type.Equals("M", StringComparison.OrdinalIgnoreCase) &&
                                durationsByLineId.TryGetValue(id, out var editedDurations))
                            {
                                var arr = new JsonArray();
                                foreach (double d in editedDurations)
                                    arr.Add(d);
                                lineNode["durations"] = arr;
                            }
                        }
                    }
                }
            }

            var writeOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            File.WriteAllText(path, _rootJsonNode.ToJsonString(writeOptions));
        }

        public void SelectSong(int songIndex)
        {
            if (songIndex >= 0 && songIndex < Songs.Count)
            {
                CurrentSongIndex = songIndex;
                CurrentLineIndex = 0;
                ResetCurrentLine();
            }
        }

        public void SelectLine(int lineIndex)
        {
            if (lineIndex >= 0 && lineIndex < Lines.Count)
            {
                CurrentLineIndex = lineIndex;
                ResetCurrentLine();
            }
        }

        public void ResetCurrentLine()
        {
            CurrentNoteIndex = 0;
            _holdSeconds = 0;
            _missedFrames = 0;
        }

        public int GetMidiForToken(string token)
        {
            if (!BaseSemitones.TryGetValue(token, out int semitone))
                semitone = 0;
            return (BaseOctave + 1) * 12 + semitone;
        }

        public string GetFullNoteNameForToken(string token)
        {
            int midi = GetMidiForToken(token);
            int idx = ((midi % 12) + 12) % 12;
            int oct = (midi / 12) - 1;
            return $"{NoteNames[idx]}{oct}";
        }
        public MelodyStepFeedback Update(MusicalNote? detectedNote, double deltaSeconds = 0.045)
        {
            var line = CurrentLine;
            if (line == null || line.NoteTokens.Count == 0)
                return new MelodyStepFeedback(0, "--", "--", "Este canto no tiene melodía", 0, false, true);

            string targetToken = line.NoteTokens[CurrentNoteIndex];
            int targetMidi = GetMidiForToken(targetToken);
            string targetFullName = GetFullNoteNameForToken(targetToken);

            if (!detectedNote.HasValue)
            {
                _missedFrames++;
                if (_missedFrames > 6)
                    _holdSeconds = Math.Max(0, _holdSeconds - deltaSeconds * 0.3);

                return new MelodyStepFeedback(
                    CurrentNoteIndex, targetFullName, "--",
                    $"Canta nota {CurrentNoteIndex + 1}/{line.NoteTokens.Count}: {targetFullName}",
                    _holdSeconds / RequiredHoldPerNote, false, false);
            }

            _missedFrames = 0;
            var note = detectedNote.Value;
            int detectedMidi = (note.Octave + 1) * 12 + Array.IndexOf(NoteNames, note.Name);
            string detectedText = $"{note.FullName} ({note.Cents:+0;-0;0} cents)";

            bool isSamePitch = IgnoreOctaveWhenSinging
                ? ((detectedMidi % 12 + 12) % 12) == ((targetMidi % 12 + 12) % 12)
                : detectedMidi == targetMidi;

            if (isSamePitch && Math.Abs(note.Cents) <= CentsTolerance)
            {
                _holdSeconds += deltaSeconds;
                if (_holdSeconds >= RequiredHoldPerNote)
                {
                    _holdSeconds = 0;
                    CurrentNoteIndex++;

                    if (CurrentNoteIndex >= line.NoteTokens.Count)
                    {
                        if (CurrentLineIndex + 1 < Lines.Count)
                        {
                            CurrentLineIndex++;
                            ResetCurrentLine();
                            var nextLine = CurrentLine!;
                            return new MelodyStepFeedback(
                                0, GetFullNoteNameForToken(nextLine.NoteTokens[0]), detectedText,
                                "✅ ¡Línea completada! Avanzando...", 1.0, true, false);
                        }

                        CurrentNoteIndex = line.NoteTokens.Count - 1;
                        return new MelodyStepFeedback(
                            CurrentNoteIndex, targetFullName, detectedText,
                            "🎉 ¡Completaste todo el canto!", 1.0, true, true);
                    }

                    string nextTarget = GetFullNoteNameForToken(line.NoteTokens[CurrentNoteIndex]);
                    return new MelodyStepFeedback(
                        CurrentNoteIndex, nextTarget, detectedText,
                        $"Bien ✓ Siguiente nota: {nextTarget}", 0, false, false);
                }

                return new MelodyStepFeedback(
                    CurrentNoteIndex, targetFullName, detectedText,
                    $"🎯 Afinado ({targetFullName})...",
                    _holdSeconds / RequiredHoldPerNote, false, false);
            }

            _holdSeconds = Math.Max(0, _holdSeconds - deltaSeconds * 0.25);
            int diff = detectedMidi - targetMidi;
            string hint = diff < 0
                ? $"⬆️ Sube hacia {targetFullName}"
                : diff > 0
                    ? $"⬇️ Baja hacia {targetFullName}"
                    : $"Afina {targetFullName} ({note.Cents:+0;-0;0} cents)";

            return new MelodyStepFeedback(
                CurrentNoteIndex, targetFullName, detectedText,
                hint, _holdSeconds / RequiredHoldPerNote, false, false);
        }

        private static SongItem ParseSongDto(SongDto song)
        {
            var parsedLines = new List<SongLineItem>();
            List<SongLineItem>? firstVerseTemplate = null;

            foreach (var section in song.Sections.OrderBy(s => s.Order))
            {
                string secLabel = section.Type?.ToLowerInvariant() == "chorus"
                    ? "Coro"
                    : $"Estrofa {section.Number ?? section.Order}";

                var orderedLines = section.Lines.OrderBy(l => l.Order).ToList();
                var sectionItems = new List<SongLineItem>();

                string pendingChords = "";
                string pendingMelody = "";
                int pendingMelodyId = 0;
                List<double>? pendingDurations = null;

                foreach (var line in orderedLines)
                {
                    string type = line.Type?.Trim().ToUpperInvariant() ?? "";
                    string text = line.Text ?? "";

                    if (type == "H") pendingChords = text.Trim();
                    else if (type == "M")
                    {
                        pendingMelody = text.Trim();
                        pendingMelodyId = line.Id;
                        pendingDurations = line.Durations;
                    }
                    else if (type == "L")
                    {
                        var tokens = ParseMelodyTokens(pendingMelody);
                        var durations = BuildDurationsList(tokens.Count, pendingDurations);

                        sectionItems.Add(new SongLineItem
                        {
                            MelodyLineId = pendingMelodyId,
                            SectionTitle = secLabel,
                            Chords = pendingChords,
                            MelodyRaw = pendingMelody,
                            Lyrics = text.Trim(),
                            NoteTokens = tokens,
                            NoteDurations = durations
                        });
                        pendingChords = "";
                        pendingMelody = "";
                        pendingMelodyId = 0;
                        pendingDurations = null;
                    }
                }

                if (section.Type?.ToLowerInvariant() == "verse")
                {
                    if (firstVerseTemplate == null && sectionItems.Any(x => x.NoteTokens.Count > 0))
                        firstVerseTemplate = sectionItems;
                    else if (firstVerseTemplate != null && sectionItems.All(x => x.NoteTokens.Count == 0))
                    {
                        for (int i = 0; i < sectionItems.Count && i < firstVerseTemplate.Count; i++)
                        {
                            sectionItems[i] = new SongLineItem
                            {
                                MelodyLineId = firstVerseTemplate[i].MelodyLineId,
                                SectionTitle = sectionItems[i].SectionTitle,
                                Chords = firstVerseTemplate[i].Chords,
                                MelodyRaw = firstVerseTemplate[i].MelodyRaw,
                                Lyrics = sectionItems[i].Lyrics,
                                NoteTokens = firstVerseTemplate[i].NoteTokens,
                                NoteDurations = firstVerseTemplate[i].NoteDurations
                            };
                        }
                    }
                }

                parsedLines.AddRange(sectionItems.Where(x => x.NoteTokens.Count > 0));
            }

            return new SongItem
            {
                Id = song.Id,
                Title = song.Name ?? $"Canto #{song.Id}",
                Tonality = song.Tonality ?? "",
                Metric = song.Metric ?? "",
                Lines = parsedLines
            };
        }

        private static List<double> BuildDurationsList(int tokenCount, List<double>? savedDurations)
        {
            var result = new List<double>(tokenCount);
            for (int i = 0; i < tokenCount; i++)
            {
                if (savedDurations != null && i < savedDurations.Count && savedDurations[i] > 0)
                    result.Add(savedDurations[i]);
                else
                    result.Add(0.50);
            }
            return result;
        }

        private static List<string> ParseMelodyTokens(string rawMelody)
        {
            if (string.IsNullOrWhiteSpace(rawMelody))
                return new List<string>();

            string cleaned = Regex.Replace(rawMelody, @"\([^)]*\)", " ");
            return cleaned
                .Split(new[] { ' ', '\t', '-', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => BaseSemitones.ContainsKey(t))
                .ToList();
        }

        private sealed class SongDto
        {
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("tonality")] public string? Tonality { get; set; }
            [JsonPropertyName("metric")] public string? Metric { get; set; }
            [JsonPropertyName("sections")] public List<SectionDto> Sections { get; set; } = new();
        }

        private sealed class SectionDto
        {
            [JsonPropertyName("type")] public string? Type { get; set; }
            [JsonPropertyName("order")] public int Order { get; set; }
            [JsonPropertyName("number")] public int? Number { get; set; }
            [JsonPropertyName("lines")] public List<LineDto> Lines { get; set; } = new();
        }

        private sealed class LineDto
        {
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("type")] public string? Type { get; set; }
            [JsonPropertyName("order")] public int Order { get; set; }
            [JsonPropertyName("text")] public string? Text { get; set; }
            [JsonPropertyName("durations")] public List<double>? Durations { get; set; }
        }
    }
}
