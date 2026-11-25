using NAudio.Wave;

namespace PitchDetectorApp.Audio
{
    public sealed class NoteTonePlayer : IDisposable
    {
        private WaveOutEvent? _waveOut;
        private CancellationTokenSource? _cts;

        public void PlayMidiNote(int midiNote, double durationSeconds = 1.2)
        {
            Stop();

            double frequency = 440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0);
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    var provider = new WarmPianoProvider(frequency, durationSeconds);
                    var waveOut = new WaveOutEvent();
                    _waveOut = waveOut;

                    waveOut.Init(provider);
                    waveOut.Play();

                    await Task.Delay(TimeSpan.FromSeconds(durationSeconds + 0.1), token);
                    waveOut.Stop();
                    waveOut.Dispose();
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        public void Stop()
        {
            _cts?.Cancel();
            if (_waveOut != null)
            {
                try
                {
                    _waveOut.Stop();
                    _waveOut.Dispose();
                }
                catch { }
                _waveOut = null;
            }
        }

        public void Dispose() => Stop();

        private sealed class WarmPianoProvider : ISampleProvider
        {
            private readonly double _frequency;
            private readonly int _totalSamples;
            private int _sampleIndex;

            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);

            public WarmPianoProvider(double frequency, double durationSeconds)
            {
                _frequency = frequency;
                _totalSamples = (int)(44100 * durationSeconds);
            }

            public int Read(float[] buffer, int offset, int count)
            {
                return Read(buffer.AsSpan(offset, count));
            }

            public int Read(Span<float> buffer)
            {
                int remaining = _totalSamples - _sampleIndex;
                if (remaining <= 0) return 0;

                int samplesToWrite = Math.Min(buffer.Length, remaining);
                for (int i = 0; i < samplesToWrite; i++)
                {
                    double t = (_sampleIndex + i) / 44100.0;

                    double attack = Math.Min(1.0, t / 0.02);
                    double decay = Math.Exp(-t * 2.2);
                    double env = attack * decay * 0.35;

                    double wave = Math.Sin(2.0 * Math.PI * _frequency * t) * 0.75
                                + Math.Sin(2.0 * Math.PI * _frequency * 2.0 * t) * 0.20
                                + Math.Sin(2.0 * Math.PI * _frequency * 3.0 * t) * 0.05;

                    buffer[i] = (float)(wave * env);
                }

                _sampleIndex += samplesToWrite;
                return samplesToWrite;
            }
        }
    }
}
