namespace PitchDetectorApp.Audio
{
    public sealed class YinPitchDetector
    {
        private readonly int _sampleRate;
        private readonly double _threshold;
        private readonly float[] _yinBuffer;

        public double LastRms { get; private set; }
        public double LastConfidence { get; private set; }

        public YinPitchDetector(int sampleRate = 44100, int bufferSize = 4096, double threshold = 0.15)
        {
            _sampleRate = sampleRate;
            _threshold = threshold;
            _yinBuffer = new float[bufferSize / 2];
        }

        public double DetectPitch(ReadOnlySpan<float> audioBuffer)
        {
            int halfBufferSize = _yinBuffer.Length;
            if (audioBuffer.Length < halfBufferSize * 2)
                return -1;

            double rms = 0;
            for (int i = 0; i < halfBufferSize * 2; i++)
                rms += audioBuffer[i] * audioBuffer[i];

            LastRms = Math.Sqrt(rms / (halfBufferSize * 2));
            if (LastRms < 0.004)
            {
                LastConfidence = 0;
                return -1;
            }

            Array.Clear(_yinBuffer, 0, halfBufferSize);
            for (int tau = 1; tau < halfBufferSize; tau++)
            {
                for (int i = 0; i < halfBufferSize; i++)
                {
                    float delta = audioBuffer[i] - audioBuffer[i + tau];
                    _yinBuffer[tau] += delta * delta;
                }
            }

            _yinBuffer[0] = 1f;
            float runningSum = 0f;
            for (int tau = 1; tau < halfBufferSize; tau++)
            {
                runningSum += _yinBuffer[tau];
                _yinBuffer[tau] *= tau / runningSum;
            }

            int tauEstimate = -1;
            int minTau = _sampleRate / 2000;
            int maxTau = Math.Min(halfBufferSize - 1, _sampleRate / 30);

            for (int tau = minTau; tau < maxTau; tau++)
            {
                if (_yinBuffer[tau] < _threshold)
                {
                    while (tau + 1 < maxTau && _yinBuffer[tau + 1] < _yinBuffer[tau])
                    {
                        tau++;
                    }
                    tauEstimate = tau;
                    break;
                }
            }

            if (tauEstimate == -1)
            {
                LastConfidence = 0;
                return -1;
            }

            // Entre menor es _yinBuffer[tauEstimate], más pura y monofónica es la nota
            LastConfidence = 1.0 - _yinBuffer[tauEstimate];
            double betterTau = ParabolicInterpolation(tauEstimate);
            return _sampleRate / betterTau;
        }

        private double ParabolicInterpolation(int tauEstimate)
        {
            int x0 = tauEstimate < 1 ? tauEstimate : tauEstimate - 1;
            int x2 = tauEstimate + 1 < _yinBuffer.Length ? tauEstimate + 1 : tauEstimate;

            if (x0 == tauEstimate)
                return _yinBuffer[tauEstimate] <= _yinBuffer[x2] ? tauEstimate : x2;

            if (x2 == tauEstimate)
                return _yinBuffer[tauEstimate] <= _yinBuffer[x0] ? tauEstimate : x0;

            float s0 = _yinBuffer[x0];
            float s1 = _yinBuffer[tauEstimate];
            float s2 = _yinBuffer[x2];

            return tauEstimate + (s2 - s0) / (2.0 * (2.0 * s1 - s2 - s0));
        }
    }
}
