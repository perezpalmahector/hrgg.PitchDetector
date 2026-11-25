using PitchDetectorApp.Audio;
using System.Windows;
using System.Windows.Media;

namespace PitchDetectorApp
{
    public partial class MainWindow : Window
    {
        private readonly AudioCaptureService _audioService;

        public MainWindow()
        {
            InitializeComponent();
            _audioService = new AudioCaptureService();
            _audioService.StateUpdated += OnStateUpdated;
        }

        private void BtnToggle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _audioService.Toggle();

                if (_audioService.IsListening)
                {
                    BtnToggle.Content = "Desactivar";
                    BtnToggle.Background = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                    TxtDetalle.Text = "Escuchando micrófono...";
                }
                else
                {
                    BtnToggle.Content = "Activar";
                    BtnToggle.Background = new SolidColorBrush(Color.FromRgb(46, 125, 50));
                    TxtPrincipal.Text = "--";
                    TxtDetalle.Text = "Detenido";
                    LstFuentes.ItemsSource = null;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error de Audio",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnStateUpdated(AudioAnalysisState state)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_audioService.IsListening) return;

                if (state.Sources.Count > 0)
                {
                    LstFuentes.ItemsSource = state.Sources
                        .Select(s => $"{s.DisplayName} • {s.Confidence * 100:F0}%")
                        .ToList();
                }
                else
                {
                    LstFuentes.ItemsSource = new List<string> { "🔇 Esperando fuente..." };
                }

                if (state.SingleNote.HasValue)
                {
                    var n = state.SingleNote.Value;
                    TxtPrincipal.FontSize = 72;
                    TxtPrincipal.Foreground = new SolidColorBrush(Color.FromRgb(76, 194, 255));
                    TxtPrincipal.Text = n.FullName;
                    TxtDetalle.Text = $"{n.Frequency:F1} Hz  ({n.Cents:+0;-0;0} cents)";
                }
                else if (state.Chord.HasValue)
                {
                    var c = state.Chord.Value;
                    TxtPrincipal.FontSize = 52;
                    TxtPrincipal.Foreground = new SolidColorBrush(Color.FromRgb(129, 199, 132));
                    TxtPrincipal.Text = c.ChordName;
                    TxtDetalle.Text = c.NotesList;
                }
                else
                {
                    TxtPrincipal.FontSize = 72;
                    TxtPrincipal.Foreground = new SolidColorBrush(Color.FromRgb(76, 194, 255));
                    TxtPrincipal.Text = "--";
                    TxtDetalle.Text = $"Escuchando... (Nivel: {state.Rms * 100:F1}%)";
                }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _audioService.Dispose();
            base.OnClosed(e);
        }
    }
}
