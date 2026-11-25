using MathNet.Numerics.IntegralTransforms;
using System.Numerics;

namespace PitchDetectorApp.Audio
{
    public readonly record struct ChordResult(string ChordName, string NotesList, double Score);

    public sealed class ChordDetector
    {
        private const int ChromaBins = 12;

        private readonly int _sampleRate;
        private readonly int _fftSize;
        private readonly Complex[] _fftBuffer;
        private readonly double[] _window;

        private static readonly string[] NoteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        private static readonly (string Suffix, int[] Intervals, double[] Weights)[] ChordTemplates =
        {
            (" Mayor", new[] { 0, 4, 7 },     new[] { 1.5, 1.2, 1.0 }),
            ("m",      new[] { 0, 3, 7 },     new[] { 1.5, 1.2, 1.0 }),
            ("7",      new[] { 0, 4, 7, 10 }, new[] { 1.4, 1.1, 0.9, 1.0 }),
            ("maj7",   new[] { 0, 4, 7, 11 }, new[] { 1.4, 1.1, 0.9, 1.0 }),
            ("m7",     new[] { 0, 3, 7, 10 }, new[] { 1.4, 1.1, 0.9, 1.0 }),
            ("sus4",   new[] { 0, 5, 7 },     new[] { 1.5, 1.1, 1.0 }),
            ("sus2",   new[] { 0, 2, 7 },     new[] { 1.5, 1.1, 1.0 })
        };

        public ChordDetector(int sampleRate = 44100, int fftSize = 8192)
        {
            _sampleRate = sampleRate;
            _fftSize = fftSize;
            _fftBuffer = new Complex[fftSize];
            _window = new double[fftSize];

            for (int i = 0; i < fftSize; i++)
                _window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (fftSize - 1)));
        }

        public ChordResult? DetectChord(ReadOnlySpan<float> audioBuffer)
        {
            if (audioBuffer.Length < _fftSize) return null;

            double rms = 0;
            for (int i = 0; i < _fftSize; i++)
            {
                float s = audioBuffer[i];
                rms += s * s;
                _fftBuffer[i] = new Complex(s * _window[i], 0);
            }

            if (Math.Sqrt(rms / _fftSize) < 0.005) return null;

            Fourier.Forward(_fftBuffer, FourierOptions.Matlab);

            double[] chroma = new double[ChromaBins];
            int minBin = (int)(75.0 * _fftSize / _sampleRate);
            int maxBin = (int)(900.0 * _fftSize / _sampleRate);

            for (int b = minBin + 1; b < maxBin - 1; b++)
            {
                double mag = _fftBuffer[b].Magnitude;
                if (mag > _fftBuffer[b - 1].Magnitude && mag > _fftBuffer[b + 1].Magnitude)
                {
                    double freq = (double)b * _sampleRate / _fftSize;
                    double midiExact = 12.0 * Math.Log2(freq / 440.0) + 69.0;
                    int roundedMidi = (int)Math.Round(midiExact);

                    if (Math.Abs(midiExact - roundedMidi) <= 0.35)
                    {
                        int pitchClass = ((roundedMidi % ChromaBins) + ChromaBins) % ChromaBins;
                        double weight = freq > 400.0 ? 400.0 / freq : 1.0;
                        chroma[pitchClass] += mag * weight;
                    }
                }
            }

            double maxChroma = chroma.Max();
            if (maxChroma <= 0) return null;
            for (int i = 0; i < ChromaBins; i++) chroma[i] /= maxChroma;

            // Exigir al menos 3 notas fuertes (> 50% de la fundamental más potente) para ignorar armónicos de notas sueltas
            var activePitchClasses = Enumerable.Range(0, ChromaBins)
                .Where(i => chroma[i] > 0.50)
                .OrderByDescending(i => chroma[i])
                .ToList();

            if (activePitchClasses.Count < 3) return null;

            string bestChord = "";
            double bestScore = -1;

            for (int root = 0; root < ChromaBins; root++)
            {
                foreach (var (suffix, intervals, weights) in ChordTemplates)
                {
                    double positiveScore = 0;
                    double totalWeight = 0;

                    for (int k = 0; k < intervals.Length; k++)
                    {
                        int noteIdx = (root + intervals[k]) % ChromaBins;
                        positiveScore += chroma[noteIdx] * weights[k];
                        totalWeight += weights[k];
                    }

                    double penalty = 0;
                    for (int n = 0; n < ChromaBins; n++)
                    {
                        int relInterval = ((n - root) % ChromaBins + ChromaBins) % ChromaBins;
                        if (!intervals.Contains(relInterval))
                            penalty += chroma[n] * 0.5;
                    }

                    double finalScore = (positiveScore / totalWeight) - (penalty / 10.0);
                    if (finalScore > bestScore)
                    {
                        bestScore = finalScore;
                        bestChord = $"{NoteNames[root]}{suffix}";
                    }
                }
            }

            if (bestScore < 0.62) return null;

            string notesDisplay = string.Join(" • ", activePitchClasses.Take(4).Select(i => NoteNames[i]));
            return new ChordResult(bestChord, notesDisplay, bestScore);
        }
    }
}
