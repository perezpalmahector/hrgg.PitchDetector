using Microsoft.Win32;
using PitchDetectorApp.Audio;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PitchDetectorApp
{
    public partial class SongTrainerWindow : Window
    {
        public sealed record NoteChipViewModel(
            int NoteIndex,
            string Label,
            string DurationLabel,
            SolidColorBrush Background,
            SolidColorBrush BorderBrush);

        private readonly AudioCaptureService _audioService;
        private readonly SongMelodyTrainer _songTrainer;
        private readonly NoteTonePlayer _tonePlayer;

        private CancellationTokenSource? _playbackCts;
        private bool _isPracticing;
        private string? _activeJsonPath;

        private static readonly SolidColorBrush BrushPendingBg = CreateBrush(42, 45, 52);
        private static readonly SolidColorBrush BrushPendingBorder = CreateBrush(62, 68, 81);
        private static readonly SolidColorBrush BrushActiveBg = CreateBrush(21, 101, 192);
        private static readonly SolidColorBrush BrushActiveBorder = CreateBrush(76, 194, 255);
        private static readonly SolidColorBrush BrushDoneBg = CreateBrush(46, 125, 50);
        private static readonly SolidColorBrush BrushDoneBorder = CreateBrush(129, 199, 132);
        private static readonly SolidColorBrush BrushStart = CreateBrush(46, 125, 50);
        private static readonly SolidColorBrush BrushStop = CreateBrush(198, 40, 40);

        public SongTrainerWindow(AudioCaptureService audioService)
        {
            InitializeComponent();
            _audioService = audioService;
            _songTrainer = new SongMelodyTrainer();
            _tonePlayer = new NoteTonePlayer();

            _activeJsonPath = FindSongsJsonPath();
            if (_activeJsonPath != null)
            {
                _songTrainer.LoadFromFile(_activeJsonPath);
                PopulateSongsCombo();
            }
        }

        private static string? FindSongsJsonPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string inOutput = Path.Combine(baseDir, "songs.json");
            if (File.Exists(inOutput))
                return inOutput;

            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 4 && dir?.Parent != null; i++)
            {
                dir = dir.Parent;
                string candidate = Path.Combine(dir.FullName, "songs.json");
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        private void PopulateSongsCombo()
        {
            CmbSongs.ItemsSource = _songTrainer.Songs
                .Select(s => string.IsNullOrWhiteSpace(s.Tonality)
                    ? s.Title
                    : $"{s.Title} (Tono: {s.Tonality})")
                .ToList();

            if (_songTrainer.Songs.Count > 0)
                CmbSongs.SelectedIndex = 0;
        }

        private void BtnLoadJson_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Seleccionar archivo JSON de cantos",
                Filter = "Archivos JSON (*.json)|*.json|Todos los archivos (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    StopPlayback();
                    StopPractice();
                    _activeJsonPath = dlg.FileName;
                    _songTrainer.LoadFromFile(_activeJsonPath);
                    PopulateSongsCombo();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error al leer el JSON: {ex.Message}", "Error",
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnSaveJson_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _songTrainer.SaveDurationsToJsonFile(_activeJsonPath);
                TxtInstruction.Text = "💾 ¡Tiempos guardados correctamente en songs.json!";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo guardar: {ex.Message}", "Error al guardar",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void NoteChip_LeftClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: int idx } && _songTrainer.CurrentLine != null)
            {
                double current = _songTrainer.CurrentLine.NoteDurations[idx];
                _songTrainer.SetNoteDuration(idx, current + 0.10);
                RefreshLineUI(activeIndex: _songTrainer.CurrentNoteIndex);
            }
        }

        private void NoteChip_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: int idx } && _songTrainer.CurrentLine != null)
            {
                double current = _songTrainer.CurrentLine.NoteDurations[idx];
                _songTrainer.SetNoteDuration(idx, current - 0.10);
                RefreshLineUI(activeIndex: _songTrainer.CurrentNoteIndex);
            }
        }

        private void NoteChip_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is Border { Tag: int idx } && _songTrainer.CurrentLine != null)
            {
                double delta = e.Delta > 0 ? 0.10 : -0.10;
                double current = _songTrainer.CurrentLine.NoteDurations[idx];
                double next = Math.Round((current + delta) * 10) / 10.0;
                _songTrainer.SetNoteDuration(idx, next);
                RefreshLineUI(activeIndex: _songTrainer.CurrentNoteIndex);
                e.Handled = true;
            }
        }


        private void CmbSongs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbSongs.SelectedIndex < 0) return;

            StopPlayback();
            StopPractice();
            _songTrainer.SelectSong(CmbSongs.SelectedIndex);

            CmbLines.ItemsSource = _songTrainer.Lines
                .Select((l, idx) => $"{idx + 1}. [{l.SectionTitle}] {l.Lyrics}")
                .ToList();

            if (_songTrainer.Lines.Count > 0)
                CmbLines.SelectedIndex = 0;
        }

        private void CmbOctave_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_songTrainer == null) return;
            if (CmbOctave.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int oct))
            {
                _songTrainer.BaseOctave = oct;
                RefreshLineUI(activeIndex: _songTrainer.CurrentNoteIndex);
            }
        }

        private void SldDuration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_songTrainer == null) return;
            double val = Math.Round(e.NewValue, 2);
            if (TxtDurationLabel != null)
                TxtDurationLabel.Text = $"Base todas: {val:F2}s";

            _songTrainer.ApplyUniformDurationToCurrentLine(val);
            RefreshLineUI(activeIndex: _songTrainer.CurrentNoteIndex);
        }

        private void CmbLines_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLines.SelectedIndex < 0) return;

            StopPlayback();
            _songTrainer.SelectLine(CmbLines.SelectedIndex);
            PrgNoteHold.Value = 0;
            RefreshLineUI(activeIndex: 0);
        }
        private async void BtnPlayMelody_Click(object sender, RoutedEventArgs e)
        {
            var line = _songTrainer.CurrentLine;
            if (line == null) return;

            if (_isPracticing) StopPractice();

            StopPlayback();
            _playbackCts = new CancellationTokenSource();
            var token = _playbackCts.Token;

            BtnPlayMelody.IsEnabled = false;
            TxtInstruction.Text = "🔊 Escuchando secuencia de la línea...";

            try
            {
                for (int i = 0; i < line.NoteTokens.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    RefreshLineUI(activeIndex: i);

                    double dur = line.NoteDurations[i];
                    int midi = _songTrainer.GetMidiForToken(line.NoteTokens[i]);
                    _tonePlayer.PlayMidiNote(midi, dur * 0.92);

                    await Task.Delay(TimeSpan.FromSeconds(dur), token);
                }

                RefreshLineUI(activeIndex: 0);
                TxtInstruction.Text = "Ahora presiona 'Cantar Línea' e imita la melodía";
            }
            catch (OperationCanceledException) { }
            finally
            {
                BtnPlayMelody.IsEnabled = true;
            }
        }

        private void BtnPractice_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_isPracticing)
                {
                    StopPlayback();
                    _songTrainer.ResetCurrentLine();
                    RefreshLineUI(activeIndex: 0);

                    _audioService.MonophonicOnly = true;
                    _audioService.StateUpdated += OnAudioStateUpdated;
                    _audioService.Start();
                    _isPracticing = true;

                    BtnPractice.Content = "⏹ Detener Práctica";
                    BtnPractice.Background = BrushStop;
                }
                else
                {
                    StopPractice();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error de micrófono: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnNextLine_Click(object sender, RoutedEventArgs e)
        {
            if (CmbLines.SelectedIndex + 1 < _songTrainer.Lines.Count)
                CmbLines.SelectedIndex++;
            else if (_songTrainer.Lines.Count > 0)
                CmbLines.SelectedIndex = 0;
        }

        private void OnAudioStateUpdated(AudioAnalysisState state)
        {
            var feedback = _songTrainer.Update(state.SingleNote, deltaSeconds: 0.045);

            Dispatcher.BeginInvoke(() =>
            {
                if (!_isPracticing) return;

                TxtLivePitch.Text = $"Tu voz: {feedback.DetectedNoteText}";
                TxtInstruction.Text = feedback.Instruction;
                PrgNoteHold.Value = feedback.NoteHoldProgress * 100.0;

                if (feedback.LineCompleted && !feedback.SongCompleted)
                    CmbLines.SelectedIndex = _songTrainer.CurrentLineIndex;
                else if (feedback.SongCompleted)
                {
                    RefreshLineUI(activeIndex: int.MaxValue);
                    StopPractice();
                }
                else
                    RefreshLineUI(activeIndex: feedback.CurrentNoteIndex);
            });
        }

        private void RefreshLineUI(int activeIndex)
        {
            var line = _songTrainer.CurrentLine;
            if (line == null) return;

            TxtSectionBadge.Text = line.SectionTitle.ToUpperInvariant();
            TxtChords.Text = string.IsNullOrWhiteSpace(line.Chords) ? " " : line.Chords;
            TxtLyrics.Text = line.Lyrics;

            var chips = new List<NoteChipViewModel>(line.NoteTokens.Count);
            for (int i = 0; i < line.NoteTokens.Count; i++)
            {
                string fullName = _songTrainer.GetFullNoteNameForToken(line.NoteTokens[i]);
                string durLabel = $"{line.NoteDurations[i]:F2}s";

                if (i < activeIndex)
                    chips.Add(new NoteChipViewModel(i, fullName, durLabel, BrushDoneBg, BrushDoneBorder));
                else if (i == activeIndex)
                    chips.Add(new NoteChipViewModel(i, fullName, durLabel, BrushActiveBg, BrushActiveBorder));
                else
                    chips.Add(new NoteChipViewModel(i, fullName, durLabel, BrushPendingBg, BrushPendingBorder));
            }

            LstMelodyNotes.ItemsSource = chips;
        }

        private void StopPlayback()
        {
            _playbackCts?.Cancel();
            _tonePlayer.Stop();
        }

        private void StopPractice()
        {
            if (!_isPracticing) return;

            _audioService.StateUpdated -= OnAudioStateUpdated;
            _audioService.Stop();
            _audioService.MonophonicOnly = false;
            _isPracticing = false;

            BtnPractice.Content = "🎤 Cantar Línea";
            BtnPractice.Background = BrushStart;
            TxtLivePitch.Text = "Tu voz: --";
            PrgNoteHold.Value = 0;
        }

        protected override void OnClosed(EventArgs e)
        {
            StopPlayback();
            StopPractice();
            _tonePlayer.Dispose();
            base.OnClosed(e);
        }

        private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
        {
            var bsh = new SolidColorBrush(Color.FromRgb(r, g, b));
            bsh.Freeze();
            return bsh;
        }
    }
}
