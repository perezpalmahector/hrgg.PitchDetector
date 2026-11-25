using MathNet.Numerics.IntegralTransforms;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Numerics;

namespace PitchDetectorApp.Audio
{
    public readonly record struct SoundSource(string DisplayName, float Confidence);

    public sealed class YamnetClassifier : IDisposable
    {
        public const int RequiredSampleRate = 16000;
        public const int RequiredSamples = 15600; // 0.975s a 16kHz

        // Parámetros exactos del espectrograma Log-Mel de YAMNet
        private const int FrameLength = 400; // 25 ms a 16 kHz
        private const int HopLength = 160;   // 10 ms a 16 kHz
        private const int FftSize = 512;
        private const int NumFrames = 96;
        private const int NumMelBins = 64;

        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly float[] _hannWindow;
        private readonly float[,] _melFilterbank;
        private readonly Complex[] _fftBuffer;
        private readonly float[] _melSpectrogram;

        private static readonly Dictionary<int, string> MusicalLabels = new()
        {
            [0] = "🗣️ Voz hablada",
            [1] = "🗣️ Voz masculina",
            [2] = "🗣️ Voz femenina",
            [3] = "🗣️ Voz infantil",
            [27] = "🎤 Canto / Voz",
            [28] = "🎤 Coro",
            [29] = "🎤 Canto lírico",
            [32] = "🎤 Tarareo",
            [35] = "😗 Silbido",
            [134] = "🎸 Instrumento de cuerda",
            [135] = "🎸 Guitarra",
            [136] = "⚡ Guitarra eléctrica",
            [137] = "🎸 Bajo eléctrico",
            [138] = "🎸 Guitarra acústica",
            [148] = "🎹 Piano",
            [149] = "🎹 Piano eléctrico",
            [150] = "🎹 Órgano",
            [151] = "🎹 Sintetizador",
            [152] = "🥁 Percusión / Batería",
            [186] = "🎻 Violín / Fiddle",
            [188] = "🎻 Violonchelo",
            [189] = "🎻 Contrabajo",
            [190] = "🎺 Instrumento de viento",
            [191] = "🎷 Saxofón",
            [192] = "🎺 Trompeta",
            [193] = "🎶 Flauta",
            [194] = "🎶 Clarinete",
            [200] = "🪗 Acordeón",
            [201] = "🎶 Armónica"
        };

        public YamnetClassifier(string modelPath = "yamnet.onnx")
        {
            _session = new InferenceSession(modelPath);
            _inputName = _session.InputMetadata.Keys.First();

            _hannWindow = new float[FrameLength];
            for (int i = 0; i < FrameLength; i++)
                _hannWindow[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / FrameLength));

            _melFilterbank = CreateMelFilterbank(RequiredSampleRate, FftSize, NumMelBins, 125.0, 7500.0);
            _fftBuffer = new Complex[FftSize];
            _melSpectrogram = new float[NumFrames * NumMelBins];
        }

        public List<SoundSource> Predict(float[] audioWindow16kHz, float minConfidence = 0.22f)
        {
            if (audioWindow16kHz.Length < RequiredSamples)
                return new List<SoundSource>();

            ComputeLogMelSpectrogram(audioWindow16kHz);

            // Detectar si el modelo espera [1, 1, 96, 64] (NCHW) o [1, 96, 64, 1] (NHWC)
            var dims = _session.InputMetadata[_inputName].Dimensions;
            int[] tensorShape = (dims.Length == 4 && dims[3] == 1)
                ? new[] { 1, NumFrames, NumMelBins, 1 }
                : new[] { 1, 1, NumFrames, NumMelBins };

            var inputTensor = new DenseTensor<float>(_melSpectrogram, tensorShape);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
            };

            using var results = _session.Run(inputs);
            var scores = results.First().AsEnumerable<float>().ToArray();

            const int numClasses = 521;
            int outFrames = Math.Max(1, scores.Length / numClasses);
            var bestByLabel = new Dictionary<string, float>();

            foreach (var kvp in MusicalLabels)
            {
                int classIdx = kvp.Key;
                string label = kvp.Value;

                float sum = 0f;
                for (int f = 0; f < outFrames; f++)
                {
                    int idx = f * numClasses + classIdx;
                    if (idx < scores.Length)
                        sum += scores[idx];
                }

                float avgScore = sum / outFrames;
                if (avgScore >= minConfidence)
                {
                    if (!bestByLabel.TryGetValue(label, out float existing) || avgScore > existing)
                        bestByLabel[label] = avgScore;
                }
            }

            return bestByLabel
                .Select(x => new SoundSource(x.Key, x.Value))
                .OrderByDescending(x => x.Confidence)
                .Take(3)
                .ToList();
        }

        private void ComputeLogMelSpectrogram(float[] audio)
        {
            int numSpectrumBins = FftSize / 2 + 1;
            Span<float> magnitudes = stackalloc float[numSpectrumBins];

            for (int frame = 0; frame < NumFrames; frame++)
            {
                int offset = frame * HopLength;
                Array.Clear(_fftBuffer, 0, FftSize);

                for (int i = 0; i < FrameLength && (offset + i) < audio.Length; i++)
                    _fftBuffer[i] = new Complex(audio[offset + i] * _hannWindow[i], 0);

                Fourier.Forward(_fftBuffer, FourierOptions.Matlab);

                for (int k = 0; k < numSpectrumBins; k++)
                    magnitudes[k] = (float)_fftBuffer[k].Magnitude;

                int rowOffset = frame * NumMelBins;
                for (int m = 0; m < NumMelBins; m++)
                {
                    float melEnergy = 0f;
                    for (int k = 0; k < numSpectrumBins; k++)
                        melEnergy += magnitudes[k] * _melFilterbank[m, k];

                    _melSpectrogram[rowOffset + m] = MathF.Log(melEnergy + 0.001f);
                }
            }
        }

        private static float[,] CreateMelFilterbank(int sampleRate, int fftSize, int numMels, double minHz, double maxHz)
        {
            int numBins = fftSize / 2 + 1;
            var filterbank = new float[numMels, numBins];

            double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
            double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

            double minMel = HzToMel(minHz);
            double maxMel = HzToMel(maxHz);

            double[] melPoints = new double[numMels + 2];
            for (int i = 0; i < melPoints.Length; i++)
                melPoints[i] = minMel + (maxMel - minMel) * i / (numMels + 1);

            int[] binPoints = melPoints
                .Select(m => (int)Math.Floor((fftSize + 1) * MelToHz(m) / sampleRate))
                .ToArray();

            for (int m = 1; m <= numMels; m++)
            {
                int left = binPoints[m - 1];
                int center = binPoints[m];
                int right = binPoints[m + 1];

                for (int k = left; k < center; k++)
                    if (k >= 0 && k < numBins && center > left)
                        filterbank[m - 1, k] = (float)(k - left) / (center - left);

                for (int k = center; k <= right; k++)
                    if (k >= 0 && k < numBins && right > center)
                        filterbank[m - 1, k] = (float)(right - k) / (right - center);
            }

            return filterbank;
        }

        public void Dispose() => _session.Dispose();
    }
}
