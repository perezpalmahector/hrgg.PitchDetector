using PitchDetectorApp.Audio;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PitchDetectorApp
{
    public partial class VocalTrainerWindow : Window
    {
        private readonly AudioCaptureService _audioService;
        private readonly VocalTrainer _trainer;
        private readonly NoteTonePlayer _tonePlayer;
        private bool _isTraining;
        private DateTime _muteDetectionUntil = DateTime.MinValue;

        private static readonly SolidColorBrush BrushGreen = CreateFrozenBrush(76, 175, 80);
        private static readonly SolidColorBrush BrushAmber = CreateFrozenBrush(255, 183, 77);
        private static readonly SolidColorBrush BrushNeutral = CreateFrozenBrush(224, 230, 237);
        private static readonly SolidColorBrush BrushStart = CreateFrozenBrush(46, 125, 50);
        private static readonly SolidColorBrush BrushStop = CreateFrozenBrush(198, 40, 40);

        public VocalTrainerWindow(AudioCaptureService audioService)
        {
            InitializeComponent();
            _audioService = audioService;
            _trainer = new VocalTrainer(VoiceType.Baritono);
            _tonePlayer = new NoteTonePlayer();

            CmbVoiceType.ItemsSource = VocalTrainer.Ranges;
            CmbVoiceType.SelectedIndex = 1;
        }

        private void PlayCurrentTargetNote(double durationSeconds = 1.2)
        {
            _muteDetectionUntil = DateTime.UtcNow.AddSeconds(durationSeconds + 0.15);
            _tonePlayer.PlayMidiNote(_trainer.CurrentTargetMidi, durationSeconds);
        }

        private void BtnPlayNote_Click(object sender, RoutedEventArgs e)
        {
            PlayCurrentTargetNote(1.2);
        }

        private void CmbVoiceType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbVoiceType.SelectedItem is VocalRangeInfo selected)
            {
                _trainer.SetVoiceType(selected.Type);
                TxtTargetNote.Text = _trainer.CurrentTargetName;
                TxtDetectedNote.Text = "--";
                TxtDetectedNote.Foreground = BrushNeutral;
                PrgHold.Value = 0;
                TxtInstruction.Text = $"Escucha y canta {_trainer.CurrentTargetName}";
                PlayCurrentTargetNote(1.1);
            }
        }

        private void BtnStartStop_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_isTraining)
                {
                    _audioService.MonophonicOnly = true;
                    _audioService.StateUpdated += OnAudioStateUpdated;
                    _audioService.Start();
                    _isTraining = true;

                    BtnStartStop.Content = "Pausar Entrenamiento";
                    BtnStartStop.Background = BrushStop;
                    TxtInstruction.Text = $"Escucha y canta {_trainer.CurrentTargetName}";
                    PlayCurrentTargetNote(1.2);
                }
                else
                {
                    StopTraining();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al iniciar micrófono: {ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            _trainer.ResetProgress();
            TxtTargetNote.Text = _trainer.CurrentTargetName;
            TxtDetectedNote.Text = "--";
            TxtDetectedNote.Foreground = BrushNeutral;
            PrgHold.Value = 0;
            TxtInstruction.Text = $"Reiniciado. Canta {_trainer.CurrentTargetName}";
            PlayCurrentTargetNote(1.1);
        }

        private void OnAudioStateUpdated(AudioAnalysisState state)
        {
            var noteToProcess = DateTime.UtcNow < _muteDetectionUntil ? null : state.SingleNote;
            var feedback = _trainer.Update(noteToProcess, deltaSeconds: 0.045);

            Dispatcher.BeginInvoke(() =>
            {
                if (!_isTraining) return;

                TxtTargetNote.Text = feedback.TargetNoteName;
                TxtDetectedNote.Text = feedback.DetectedNoteText;
                TxtInstruction.Text = feedback.Instruction;
                PrgHold.Value = feedback.HoldProgress * 100.0;

                if (feedback.DetectedNoteText == "--")
                    TxtDetectedNote.Foreground = BrushNeutral;
                else
                    TxtDetectedNote.Foreground = feedback.IsOnPitch ? BrushGreen : BrushAmber;

                if (feedback.JustCompleted && !feedback.RangeFinished)
                {
                    PlayCurrentTargetNote(1.2);
                }

                if (feedback.RangeFinished)
                    StopTraining();
            });
        }

        private void StopTraining()
        {
            if (!_isTraining) return;

            _tonePlayer.Stop();
            _audioService.StateUpdated -= OnAudioStateUpdated;
            _audioService.Stop();
            _audioService.MonophonicOnly = false;
            _isTraining = false;

            BtnStartStop.Content = "Iniciar Entrenamiento";
            BtnStartStop.Background = BrushStart;
        }

        protected override void OnClosed(EventArgs e)
        {
            StopTraining();
            _tonePlayer.Dispose();
            base.OnClosed(e);
        }

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
