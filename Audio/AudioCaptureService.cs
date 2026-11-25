using NAudio.Wave;

namespace PitchDetectorApp.Audio
{
    public sealed class AudioCaptureService : IDisposable
    {
        private const int SampleRate = 44100;
        private const int BufferSize = 4096;

        private WaveInEvent? _waveIn;
        private readonly YinPitchDetector _detector;
        private readonly PitchSmoother _smoother;
        private readonly float[] _accumulationBuffer;
        private int _accumulatedSamples;

        public event Action<MusicalNote?, double>? NoteDetected;
        public bool IsListening { get; private set; }

        public AudioCaptureService()
        {
            _detector = new YinPitchDetector(SampleRate, BufferSize, threshold: 0.12);
            _smoother = new PitchSmoother(windowSize: 7, maxSilentFrames: 5, framesToConfirmChange: 3);
            _accumulationBuffer = new float[BufferSize];
        }


        public void Toggle()
        {
            if (IsListening) Stop();
            else Start();
        }

        public void Start()
        {
            if (IsListening) return;

            if (WaveInEvent.DeviceCount == 0)
                throw new InvalidOperationException("No se detectó ningún micrófono conectado en Windows.");

            _smoother.Reset();
            _accumulatedSamples = 0;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = 0, // Micrófono predeterminado
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 50
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
            IsListening = true;
        }

        public void Stop()
        {
            if (!IsListening) return;

            if (_waveIn != null)
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }

            _smoother.Reset();
            _accumulatedSamples = 0;
            IsListening = false;
            NoteDetected?.Invoke(null, 0);
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            int incomingSamples = e.BytesRecorded / 2;

            for (int i = 0; i < incomingSamples; i++)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i * 2);
                _accumulationBuffer[_accumulatedSamples++] = sample / 32768f;

                // Procesar exactamente cuando tengamos las 4096 muestras completas
                if (_accumulatedSamples == BufferSize)
                {
                    double rawFrequency = _detector.DetectPitch(_accumulationBuffer);
                    double stableFrequency = _smoother.Process(rawFrequency);

                    var note = stableFrequency > 0 ? NoteMapper.FromFrequency(stableFrequency) : null;
                    NoteDetected?.Invoke(note, _detector.LastRms);

                    // Desplazar la mitad del búfer (50% overlap) para una respuesta el doble de rápida
                    Array.Copy(_accumulationBuffer, BufferSize / 2, _accumulationBuffer, 0, BufferSize / 2);
                    _accumulatedSamples = BufferSize / 2;
                }
            }
        }

        public void Dispose() => Stop();
    }
}
