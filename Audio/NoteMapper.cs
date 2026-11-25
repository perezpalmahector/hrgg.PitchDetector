namespace PitchDetectorApp.Audio
{
    public readonly record struct MusicalNote(string Name, int Octave, double Frequency, double Cents)
    {
        public string FullName => $"{Name}{Octave}";
    }

    public static class NoteMapper
    {
        private static readonly string[] NoteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public static MusicalNote? FromFrequency(double frequency)
        {
            if (frequency <= 20 || frequency > 4200)
                return null;

            double midiNote = 12.0 * Math.Log2(frequency / 440.0) + 69.0;
            int roundedMidi = (int)Math.Round(midiNote);

            int noteIndex = ((roundedMidi % 12) + 12) % 12;
            int octave = (roundedMidi / 12) - 1;
            double cents = (midiNote - roundedMidi) * 100.0;

            return new MusicalNote(NoteNames[noteIndex], octave, frequency, cents);
        }
    }
}
