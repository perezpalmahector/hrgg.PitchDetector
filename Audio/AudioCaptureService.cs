using NAudio.Wave;

namespace PitchDetectorApp.Audio
{
    public readonly record struct AudioAnalysisState(
        MusicalNote? SingleNote,
        ChordResult? Chord,
        List<SoundSource> Sources,
        double Rms
    );

    public sealed class AudioCaptureService : IDisposable
    {
        private const int SampleRate = 44100;
        private const int RingBufferCapacity = 65536;
        private const int YinBufferSize = 4096;
        private const int ChordBufferSize = 8192;
        private const int YamnetRawSamples = 43000;

        private WaveInEvent? _waveIn;
        private readonly YinPitchDetector _yinDetector;
        private readonly PitchSmoother _smoother;
        private readonly ChordDetector _chordDetector;
        private readonly YamnetClassifier _yamnet;

        private readonly float[] _ringBuffer = new float[RingBufferCapacity];
        private int _writePos;
        private long _totalSamplesWritten;

        private CancellationTokenSource? _cts;
        private List<SoundSource> _latestSources = new();
        private bool _yamnetBusy;

        public event Action<AudioAnalysisState>? StateUpdated;
        public bool IsListening { get; private set; }
        public bool MonophonicOnly { get; set; }


        public AudioCaptureService()
        {
            _yinDetector = new YinPitchDetector(SampleRate, YinBufferSize, threshold: 0.14);
            _smoother = new PitchSmoother(windowSize: 5, maxSilentFrames: 5, framesToConfirmChange: 2);
            _chordDetector = new ChordDetector(SampleRate, ChordBufferSize);
            _yamnet = new YamnetClassifier("yamnet.onnx");
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
            Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
            _writePos = 0;
            _totalSamplesWritten = 0;
            _latestSources = new List<SoundSource>();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 30
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
            IsListening = true;

            Task.Run(() => ProcessingLoop(token), token);
        }

        public void Stop()
        {
            if (!IsListening) return;

            _cts?.Cancel();
            if (_waveIn != null)
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;
            }

            _smoother.Reset();
            IsListening = false;
            StateUpdated?.Invoke(new AudioAnalysisState(null, null, new List<SoundSource>(), 0));
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            int samples = e.BytesRecorded / 2;
            for (int i = 0; i < samples; i++)
            {
                float sample = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                _ringBuffer[_writePos] = sample;
                _writePos = (_writePos + 1) % RingBufferCapacity;
                _totalSamplesWritten++;
            }
        }

        private async Task ProcessingLoop(CancellationToken token)
        {
            float[] yinWindow = new float[YinBufferSize];
            float[] chordWindow = new float[ChordBufferSize];
            float[] yamnetWindow16k = new float[YamnetClassifier.RequiredSamples];
            int yamnetCounter = 0;

            string? lastChordName = null;
            int chordStreak = 0;
            ChordResult? heldChord = null;
            int chordHoldFrames = 0;

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(45, token);

                if (_totalSamplesWritten < ChordBufferSize) continue;

                ReadLatestSamples(chordWindow, ChordBufferSize);
                Array.Copy(chordWindow, ChordBufferSize - YinBufferSize, yinWindow, 0, YinBufferSize);

                // 1. Detectar Nota (YIN)
                double rawFreq = _yinDetector.DetectPitch(yinWindow);
                double stableFreq = _smoother.Process(rawFreq);
                var singleNote = stableFreq > 0 ? NoteMapper.FromFrequency(stableFreq) : null;

                // 2. Detectar Acorde (Cromagrama FFT)
                var rawChord = _chordDetector.DetectChord(chordWindow);
                ChordResult? confirmedChord = null;

                if (rawChord.HasValue)
                {
                    if (rawChord.Value.ChordName == lastChordName)
                    {
                        chordStreak++;
                        if (chordStreak >= 2)
                        {
                            confirmedChord = rawChord;
                            heldChord = rawChord;
                            chordHoldFrames = 10;
                        }
                    }
                    else
                    {
                        lastChordName = rawChord.Value.ChordName;
                        chordStreak = 1;
                    }
                }
                else
                {
                    lastChordName = null;
                    chordStreak = 0;
                }

                // 3. Gestión de retención (Chord Hold)
                if (!confirmedChord.HasValue && chordHoldFrames > 0)
                {
                    if (_yinDetector.LastRms < 0.006)
                    {
                        chordHoldFrames = 0;
                        heldChord = null;
                    }
                    else
                    {
                        chordHoldFrames--;
                        confirmedChord = heldChord;
                    }
                }

                // 4. Árbitro: Notas limpias (> 0.91 de pureza YIN) nunca son reemplazadas por acordes
                // 4. Árbitro: Si MonophonicOnly es true, nunca evalúa acordes
                bool showChord = !MonophonicOnly && confirmedChord.HasValue && _yinDetector.LastConfidence < 0.91;


                StateUpdated?.Invoke(new AudioAnalysisState(
                    showChord ? null : singleNote,
                    showChord ? confirmedChord : null,
                    _latestSources,
                    _yinDetector.LastRms
                ));

                // 5. Alimentar YAMNet cada ~450 ms con ventana completa de 1 segundo
                yamnetCounter++;
                if (yamnetCounter >= 10 && !_yamnetBusy && _totalSamplesWritten >= YamnetRawSamples)
                {
                    yamnetCounter = 0;
                    _yamnetBusy = true;
                    float[] raw44k = new float[YamnetRawSamples];
                    ReadLatestSamples(raw44k, YamnetRawSamples);

                    _ = Task.Run(() =>
                    {
                        try
                        {
                            double step = (double)YamnetRawSamples / YamnetClassifier.RequiredSamples;
                            for (int i = 0; i < YamnetClassifier.RequiredSamples; i++)
                                yamnetWindow16k[i] = raw44k[(int)(i * step)];

                            var detected = _yamnet.Predict(yamnetWindow16k, minConfidence: 0.18f);
                            if (detected.Count > 0 || _yinDetector.LastRms < 0.005)
                                _latestSources = detected;
                        }
                        finally
                        {
                            _yamnetBusy = false;
                        }
                    }, token);
                }
            }
        }

        private void ReadLatestSamples(float[] destination, int count)
        {
            int start = (_writePos - count + RingBufferCapacity) % RingBufferCapacity;
            for (int i = 0; i < count; i++)
            {
                destination[i] = _ringBuffer[(start + i) % RingBufferCapacity];
            }
        }

        public void Dispose()
        {
            Stop();
            _yamnet.Dispose();
        }
    }
}
