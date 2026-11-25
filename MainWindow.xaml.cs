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
            _audioService.NoteDetected += OnNoteDetected;
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
                    TxtNota.Text = "--";
                    TxtDetalle.Text = "Detenido";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al acceder al micrófono: {ex.Message}", "Error de Audio",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnNoteDetected(MusicalNote? note, double rms)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_audioService.IsListening) return;

                if (note.HasValue)
                {
                    TxtNota.Text = note.Value.FullName;
                    TxtDetalle.Text = $"{note.Value.Frequency:F1} Hz  ({note.Value.Cents:+0;-0;0} cents)";
                }
                else
                {
                    TxtNota.Text = "--";
                    TxtDetalle.Text = $"Escuchando... (Nivel: {rms * 100:F1}%)";
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
