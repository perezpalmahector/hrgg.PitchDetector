namespace PitchDetectorApp.Audio
{
    public enum VoiceType
    {
        Bajo,
        Baritono,
        Tenor,
        Contralto,
        Mezzosoprano,
        Soprano
    }

    public readonly record struct VocalRangeInfo(
        VoiceType Type,
        string DisplayName,
        int MinMidi,
        int MaxMidi,
        int StartMidi
    );

    public readonly record struct TrainingFeedback(
        string TargetNoteName,
        string DetectedNoteText,
        string Instruction,
        double HoldProgress,
        bool IsOnPitch,
        bool JustCompleted,
        bool RangeFinished
    );

    public sealed class VocalTrainer
    {
        private static readonly string[] NoteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public static readonly IReadOnlyList<VocalRangeInfo> Ranges = new[]
        {
            new VocalRangeInfo(VoiceType.Bajo,         "👨 Hombre - Bajo (E2 – E4)",          40, 64, 45),
            new VocalRangeInfo(VoiceType.Baritono,     "👨 Hombre - Barítono (A2 – A4)",      45, 69, 48),
            new VocalRangeInfo(VoiceType.Tenor,        "👨 Hombre - Tenor (C3 – C5)",         48, 72, 52),
            new VocalRangeInfo(VoiceType.Contralto,    "👩 Mujer - Contralto (F3 – F5)",      53, 77, 57),
            new VocalRangeInfo(VoiceType.Mezzosoprano, "👩 Mujer - Mezzosoprano (A3 – A5)",   57, 81, 60),
            new VocalRangeInfo(VoiceType.Soprano,      "👩 Mujer - Soprano (C4 – C6)",        60, 84, 64)
        };

        private VocalRangeInfo _currentRange;
        private int _targetMidi;
        private double _holdSeconds;
        private int _missedFrames;

        private const double RequiredHoldSeconds = 1.0;
        private const double CentsTolerance = 40.0;

        public VocalRangeInfo CurrentRange => _currentRange;
        public int CurrentTargetMidi => _targetMidi;
        public string CurrentTargetName => MidiToName(_targetMidi);

        public VocalTrainer(VoiceType initialVoice = VoiceType.Baritono)
        {
            SetVoiceType(initialVoice);
        }

        public void SetVoiceType(VoiceType type)
        {
            _currentRange = Ranges.First(r => r.Type == type);
            ResetProgress();
        }

        public void ResetProgress()
        {
            _targetMidi = _currentRange.StartMidi;
            _holdSeconds = 0;
            _missedFrames = 0;
        }

        public TrainingFeedback Update(MusicalNote? detectedNote, double deltaSeconds = 0.045)
        {
            string targetName = MidiToName(_targetMidi);

            if (!detectedNote.HasValue)
            {
                _missedFrames++;
                if (_missedFrames > 8)
                    _holdSeconds = Math.Max(0, _holdSeconds - deltaSeconds * 0.25);

                return new TrainingFeedback(
                    targetName,
                    "--",
                    $"Canta y mantén la nota {targetName}",
                    _holdSeconds / RequiredHoldSeconds,
                    false,
                    false,
                    false
                );
            }

            _missedFrames = 0;
            var note = detectedNote.Value;
            int detectedMidi = (note.Octave + 1) * 12 + Array.IndexOf(NoteNames, note.Name);
            int semitoneDiff = detectedMidi - _targetMidi;
            string detectedText = $"{note.FullName} ({note.Cents:+0;-0;0} cents)";

            if (semitoneDiff == 0 && Math.Abs(note.Cents) <= CentsTolerance)
            {
                _holdSeconds += deltaSeconds;

                if (_holdSeconds >= RequiredHoldSeconds)
                {
                    _holdSeconds = 0;
                    if (_targetMidi < _currentRange.MaxMidi)
                    {
                        _targetMidi++;
                        string nextNote = MidiToName(_targetMidi);
                        return new TrainingFeedback(
                            nextNote,
                            detectedText,
                            $"✅ ¡Excelente! Ahora sube a: {nextNote}",
                            0,
                            true,
                            true,
                            false
                        );
                    }

                    return new TrainingFeedback(
                        targetName,
                        detectedText,
                        "🎉 ¡Felicidades! Completaste todo tu rango vocal.",
                        1.0,
                        true,
                        true,
                        true
                    );
                }

                return new TrainingFeedback(
                    targetName,
                    detectedText,
                    $"🎯 ¡Afinado! Mantén firme... ({_holdSeconds:F1}s / {RequiredHoldSeconds:F1}s)",
                    _holdSeconds / RequiredHoldSeconds,
                    true,
                    false,
                    false
                );
            }

            _holdSeconds = Math.Max(0, _holdSeconds - deltaSeconds * 0.25);

            string hint;
            if (semitoneDiff < 0)
                hint = $"⬆️ Muy grave: sube {Math.Abs(semitoneDiff)} semitono(s)";
            else if (semitoneDiff > 0)
                hint = $"⬇️ Muy agudo: baja {semitoneDiff} semitono(s)";
            else
                hint = note.Cents < 0
                    ? $"⬆️ Casi listo: afina un poco más arriba ({note.Cents:F0} cents)"
                    : $"⬇️ Casi listo: afina un poco más abajo (+{note.Cents:F0} cents)";

            return new TrainingFeedback(
                targetName,
                detectedText,
                hint,
                _holdSeconds / RequiredHoldSeconds,
                false,
                false,
                false
            );
        }

        public static string MidiToName(int midi)
        {
            int noteIndex = ((midi % 12) + 12) % 12;
            int octave = (midi / 12) - 1;
            return $"{NoteNames[noteIndex]}{octave}";
        }
    }
}
