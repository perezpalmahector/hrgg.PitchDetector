namespace PitchDetectorApp.Audio
{
    public sealed class PitchSmoother
    {
        private readonly double[] _history;
        private readonly double[] _sortBuffer;
        private int _count;
        private int _index;
        private int _silentFrames;
        private readonly int _maxSilentFrames;

        private double _lockedFrequency = -1;
        private double _candidateFrequency = -1;
        private int _candidateCount;
        private readonly int _framesToConfirmChange;

        public PitchSmoother(int windowSize = 7, int maxSilentFrames = 5, int framesToConfirmChange = 3)
        {
            _history = new double[windowSize];
            _sortBuffer = new double[windowSize];
            _maxSilentFrames = maxSilentFrames;
            _framesToConfirmChange = framesToConfirmChange;
        }

        public double Process(double rawFrequency)
        {
            // Manejo de silencio
            if (rawFrequency <= 0)
            {
                _silentFrames++;
                if (_silentFrames >= _maxSilentFrames)
                {
                    Reset();
                }
                return _lockedFrequency;
            }

            _silentFrames = 0;

            // Primera lectura válida tras silencio
            if (_lockedFrequency <= 0)
            {
                AddSample(rawFrequency);
                _lockedFrequency = GetMedian();
                return _lockedFrequency;
            }

            // Verificar si la lectura está dentro de la misma nota (~ +/- 45 cents)
            double ratio = rawFrequency / _lockedFrequency;
            if (ratio >= 0.974 && ratio <= 1.026)
            {
                _candidateCount = 0;
                _candidateFrequency = -1;
                AddSample(rawFrequency);
                _lockedFrequency = GetMedian();
                return _lockedFrequency;
            }

            // Si es una nota distinta, exigir confirmación consecutiva antes de cambiar
            if (_candidateFrequency > 0 && (rawFrequency / _candidateFrequency) >= 0.974 && (rawFrequency / _candidateFrequency) <= 1.026)
            {
                _candidateCount++;
                _candidateFrequency = (_candidateFrequency + rawFrequency) / 2.0;
            }
            else
            {
                _candidateFrequency = rawFrequency;
                _candidateCount = 1;
            }

            // Confirmado: realmente cambiaste de nota
            if (_candidateCount >= _framesToConfirmChange)
            {
                Reset();
                AddSample(_candidateFrequency);
                _lockedFrequency = _candidateFrequency;
            }

            return _lockedFrequency;
        }

        private void AddSample(double freq)
        {
            _history[_index] = freq;
            _index = (_index + 1) % _history.Length;
            if (_count < _history.Length) _count++;
        }

        private double GetMedian()
        {
            Array.Copy(_history, _sortBuffer, _count);
            Array.Sort(_sortBuffer, 0, _count);
            return _sortBuffer[_count / 2];
        }

        public void Reset()
        {
            _count = 0;
            _index = 0;
            _silentFrames = 0;
            _lockedFrequency = -1;
            _candidateFrequency = -1;
            _candidateCount = 0;
        }
    }
}
